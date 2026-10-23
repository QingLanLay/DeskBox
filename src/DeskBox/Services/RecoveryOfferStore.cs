using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Services;

// Local persistence machinery (the atomic marker file), so this store sits in
// the Core.Persistence namespace beside WidgetLayoutStore — the module
// boundary keeps destructive file operations out of plain Services code.
namespace DeskBox.Core.Persistence;

/// <summary>
/// Marker written when a launch detects a wiped configuration with a valid
/// recovery snapshot to offer. The boot-session toast is one-shot and easy to
/// miss, and the first unattended boot already writes default settings.json —
/// so the wipe is only detectable on that single launch. The marker keeps the
/// offer alive across launches until the user answers it, and lives in the
/// data directory on purpose: a full restore swaps that directory away (taking
/// the marker with the fresh-profile data), while another wipe removes it
/// together with the settings the detection keys on — both directions
/// self-consistent without extra bookkeeping.
/// </summary>
internal sealed record RecoveryOfferMarker(
    string SnapshotPath,
    DateTimeOffset SnapshotCreatedAtUtc,
    string? SnapshotAppVersion,
    int SnapshotWidgetCount,
    DateTimeOffset DetectedAtUtc);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(RecoveryOfferMarker), TypeInfoPropertyName = "RecoveryOfferMarker")]
internal sealed partial class RecoveryOfferJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Owns <c>recovery-offer-pending.json</c> in the data directory. Advisory
/// state: a lost or unreadable marker only costs the one-click offer (the
/// settings page restore entry stays available), so reads never throw.
/// </summary>
internal sealed class RecoveryOfferStore
{
    internal const string MarkerFileName = "recovery-offer-pending.json";

    // The marker is a few hundred bytes; anything larger is foreign garbage
    // and reading it wholesale (an OOM outside the catch list) buys nothing.
    private const long MaxMarkerFileBytes = 64 * 1024;

    private readonly string _markerPath;

    public RecoveryOfferStore()
        : this(DeskBoxDataPathService.Current.DataDirectory)
    {
    }

    internal RecoveryOfferStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _markerPath = Path.Combine(dataDirectory, MarkerFileName);
    }

    internal string MarkerPath => _markerPath;

    public RecoveryOfferMarker? TryRead()
    {
        try
        {
            if (!File.Exists(_markerPath) ||
                new FileInfo(_markerPath).Length > MaxMarkerFileBytes)
            {
                return null;
            }

            return JsonSerializer.Deserialize(
                File.ReadAllText(_markerPath),
                RecoveryOfferJsonContext.Default.RecoveryOfferMarker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public Task WriteAsync(RecoveryOfferMarker marker, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marker);
        return WriteCoreAsync(
            JsonSerializer.Serialize(marker, RecoveryOfferJsonContext.Default.RecoveryOfferMarker),
            cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryDelete();
        return Task.CompletedTask;
    }

    private async Task WriteCoreAsync(string json, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_markerPath)!);
        string tempPath = $"{_markerPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, _markerPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }

    private void TryDelete()
    {
        try
        {
            if (File.Exists(_markerPath))
            {
                File.Delete(_markerPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log($"[RecoveryOffer] Marker delete failed: {ex.Message}");
        }
    }
}
