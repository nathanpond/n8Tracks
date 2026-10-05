using System.Globalization;

namespace n8Tracks.Application.Backups;

/// <summary>
/// Validates an archive fully before a restore may start: the manifest is there and readable, the
/// disk has room, every checksum matches, the database passes an integrity check, and its last
/// migration is one this build knows. A backup from a newer version is refused naming the version
/// needed. A valid archive is kept as a validation, by ID, until it is confirmed or expires; an
/// upload that fails is deleted at once. Validation reads; it never changes the live data.
/// </summary>
public sealed class RestoreValidator(
    IBackupStorage storage,
    IRestoreArchives archives,
    IDiskSpace disk,
    RestoreValidations validations,
    RestoreReads reads,
    RestoreOptions options,
    TimeProvider time)
{
    /// <summary>Validates an archive in one of the backup folders.</summary>
    public async Task<RestoreValidationOutcome> ValidateListedAsync(BackupLocation location, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);

        Sweep();
        if (await storage.FindAsync(location, name, cancellationToken).ConfigureAwait(false) is not { } archive)
        {
            return new RestoreValidationOutcome.NotFound();
        }

        var source = new RestoreSource.Listed(archive.Location, archive.Name);
        using (reads.Hold(archive.Location, archive.Name))
        {
            return await ValidateAsync(source, cancellationToken).ConfigureAwait(false) ?? new RestoreValidationOutcome.NotFound();
        }
    }

    /// <summary>
    /// Streams an upload to a temporary file under the data path and validates it. A declared length
    /// over the limit, or over the free space, is refused before anything is written. The upload is
    /// deleted unless it is valid.
    /// </summary>
    public async Task<RestoreValidationOutcome> ValidateUploadAsync(
        Stream content,
        string fileName,
        long? declaredLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(fileName);

        Sweep();
        if (declaredLength > options.MaxUploadBytes)
        {
            return new RestoreValidationOutcome.Refused(TooLarge());
        }

        if (declaredLength is { } length && disk.AvailableForData() is var available && length > available)
        {
            return new RestoreValidationOutcome.Refused(NoSpace(length, available));
        }

        if (await archives.SaveUploadAsync(content, options.MaxUploadBytes, cancellationToken).ConfigureAwait(false) is not { } uploadId)
        {
            return new RestoreValidationOutcome.Refused(TooLarge());
        }

        RestoreValidationOutcome? outcome = null;
        try
        {
            outcome = await ValidateAsync(new RestoreSource.Uploaded(uploadId, SafeName(fileName)), cancellationToken).ConfigureAwait(false);
            return outcome ?? new RestoreValidationOutcome.NotFound();
        }
        finally
        {
            if (outcome is not RestoreValidationOutcome.Valid)
            {
                archives.DeleteUpload(uploadId);
            }
        }
    }

    /// <summary>
    /// Checks a validated archive again, fully, as a restore begins: it may have changed since. Null
    /// when it still passes.
    /// </summary>
    public async Task<RestoreRefusal?> RecheckAsync(RestoreValidation validation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(validation);

        var reading = await archives.ReadAsync(validation.Source, cancellationToken).ConfigureAwait(false);
        return reading switch
        {
            null => new RestoreRefusal(RestoreRefusalReason.MissingManifest, "The backup is no longer there."),
            ArchiveReading.Refused refused => refused.Refusal,
            ArchiveReading.Readable readable => await archives.VerifyAsync(validation.Source, readable.Summary, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unknown archive reading."),
        };
    }

    /// <summary>Drops validations past their lifetime and deletes their uploads; returns how many were dropped.</summary>
    public int Sweep()
    {
        var expired = validations.RemoveExpired(time.GetUtcNow());
        foreach (var validation in expired)
        {
            if (validation.Source is RestoreSource.Uploaded upload)
            {
                archives.DeleteUpload(upload.UploadId);
            }
        }

        return expired.Count;
    }

    /// <summary>The whole check; null when a listed archive went while it was read.</summary>
    private async Task<RestoreValidationOutcome?> ValidateAsync(RestoreSource source, CancellationToken cancellationToken)
    {
        switch (await archives.ReadAsync(source, cancellationToken).ConfigureAwait(false))
        {
            case null:
                return null;

            case ArchiveReading.Refused refused:
                return new RestoreValidationOutcome.Refused(refused.Refusal);

            case ArchiveReading.Readable readable:
                // Room to unpack the archive beside the live data, and for the safety backup of that data.
                var required = readable.UnpackedBytes + archives.LiveDataBytes();
                var available = disk.AvailableForData();
                if (required > available)
                {
                    return new RestoreValidationOutcome.Refused(NoSpace(required, available));
                }

                if (await archives.VerifyAsync(source, readable.Summary, cancellationToken).ConfigureAwait(false) is { } refusal)
                {
                    return new RestoreValidationOutcome.Refused(refusal);
                }

                var validation = new RestoreValidation(Guid.CreateVersion7(), source, readable.Summary, time.GetUtcNow() + options.ValidationLifetime);
                validations.Add(validation);
                return new RestoreValidationOutcome.Valid(validation);

            default:
                throw new InvalidOperationException("Unknown archive reading.");
        }
    }

    private RestoreRefusal TooLarge() =>
        new(RestoreRefusalReason.TooLarge, $"The file is larger than the {Size(options.MaxUploadBytes)} an upload may be.");

    private static RestoreRefusal NoSpace(long required, long available) =>
        new(
            RestoreRefusalReason.InsufficientSpace,
            $"There is not enough free space to restore this backup: it needs {Size(required)}, and {Size(available)} is free.",
            RequiredBytes: required,
            AvailableBytes: available);

    /// <summary>The upload's own name, without any folder a browser might send, cut to a sane length.</summary>
    private static string SafeName(string fileName)
    {
        var name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        name = new string([.. name.Where(static c => !char.IsControl(c))]);
        return name.Length switch
        {
            0 => "upload.zip",
            > 255 => name[..255],
            _ => name,
        };
    }

    /// <summary>A size as people read it, in decimal units: <c>20 GB</c>, <c>1.5 MB</c>.</summary>
    public static string Size(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}

/// <summary>Validations waiting for their confirmation, in memory: a restart drops them (and their uploads).</summary>
public sealed class RestoreValidations
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, RestoreValidation> pending = [];

    public void Add(RestoreValidation validation)
    {
        ArgumentNullException.ThrowIfNull(validation);

        lock (gate)
        {
            pending[validation.Id] = validation;
        }
    }

    /// <summary>The validation, still pending and unexpired at <paramref name="now"/>, or null.</summary>
    public RestoreValidation? Find(Guid id, DateTimeOffset now)
    {
        lock (gate)
        {
            return pending.TryGetValue(id, out var validation) && validation.ExpiresUtc > now ? validation : null;
        }
    }

    /// <summary>Removes and returns the validation, so it can start one restore only; null when there is none.</summary>
    public RestoreValidation? Take(Guid id, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!pending.Remove(id, out var validation))
            {
                return null;
            }

            if (validation.ExpiresUtc > now)
            {
                return validation;
            }

            // Expired: put back for the sweep, which deletes its upload.
            pending[id] = validation;
            return null;
        }
    }

    /// <summary>Removes and returns every validation expired at <paramref name="now"/>.</summary>
    public List<RestoreValidation> RemoveExpired(DateTimeOffset now)
    {
        lock (gate)
        {
            var expired = pending.Values.Where(validation => validation.ExpiresUtc <= now).ToList();
            foreach (var validation in expired)
            {
                pending.Remove(validation.Id);
            }

            return expired;
        }
    }
}

/// <summary>
/// The listed archives a restore is reading now (validating, or restoring from), which
/// <see cref="BackupService.DeleteAsync"/> refuses to delete.
/// </summary>
public sealed class RestoreReads
{
    private readonly Lock gate = new();
    private readonly Dictionary<(BackupLocation, string), int> held = [];

    /// <summary>Marks the archive as being read until the returned handle is disposed.</summary>
    public IDisposable Hold(BackupLocation location, string name)
    {
        var key = (location, name);
        lock (gate)
        {
            held[key] = held.GetValueOrDefault(key) + 1;
        }

        return new Release(this, key);
    }

    /// <summary>Whether a restore is reading the archive now.</summary>
    public bool IsHeld(BackupLocation location, string name)
    {
        lock (gate)
        {
            return held.ContainsKey((location, name));
        }
    }

    private void Drop((BackupLocation, string) key)
    {
        lock (gate)
        {
            if (held.GetValueOrDefault(key) <= 1)
            {
                held.Remove(key);
            }
            else
            {
                held[key]--;
            }
        }
    }

    private sealed class Release(RestoreReads owner, (BackupLocation, string) key) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.Drop(key);
            }
        }
    }
}
