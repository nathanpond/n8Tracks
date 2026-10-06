using System.Text.Json;
using n8Tracks.Application.Jobs;

namespace n8Tracks.Application.Suno.Import;

/// <summary>
/// Runs the <see cref="ExportStagingService.ClassifyJobType"/> job a large export's completion queues:
/// stages and classifies the export named in the payload (<c>{ exportId }</c>). Its result is the
/// export's ID and the state it ended in; a failed classification fails the job too, with no clip
/// content in the message.
/// </summary>
internal sealed class ExportClassifyJobHandler(ExportStagingService exports) : IJobHandler
{
    public async Task<JsonElement?> RunAsync(IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Payload is not { ValueKind: JsonValueKind.Object } payload
            || !payload.TryGetProperty("exportId", out var value)
            || !value.TryGetGuid(out var exportId))
        {
            throw new InvalidOperationException("The job names no export.");
        }

        var outcome = await exports.ClassifyAsync(exportId, cancellationToken).ConfigureAwait(false);
        context.Report(100);
        return outcome switch
        {
            ExportClassification.Failed failed => throw new InvalidOperationException("Classifying the export failed; it is marked failed.", failed.Exception),
            ExportClassification.Ready => JsonSerializer.SerializeToElement(new { exportId, state = "ready" }),
            _ => JsonSerializer.SerializeToElement(new { exportId, state = "abandoned" }),
        };
    }
}
