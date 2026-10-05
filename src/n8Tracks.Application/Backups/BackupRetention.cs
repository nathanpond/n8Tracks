namespace n8Tracks.Application.Backups;

/// <summary>
/// Which archives retention deletes after a successful scheduled backup: the complete, valid
/// archives whose manifest kind is <c>scheduled</c>, beyond the newest <c>keep</c>, oldest first.
/// Manual and safety backups, invalid archives, and those a newer version made are never counted
/// or deleted.
/// </summary>
public static class BackupRetention
{
    public static IReadOnlyList<BackupArchive> Select(IEnumerable<BackupArchive> archives, int keep)
    {
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, BackupSchedule.MinimumKeep);

        return [.. archives
            .Where(static archive => archive.Validity == BackupValidity.Valid
                && BackupKinds.Parse(archive.Kind) == BackupKind.Scheduled)
            .OrderByDescending(static archive => archive.CreatedUtc)
            .ThenByDescending(static archive => archive.Name, StringComparer.Ordinal)
            .Skip(keep)
            .Reverse()];
    }
}
