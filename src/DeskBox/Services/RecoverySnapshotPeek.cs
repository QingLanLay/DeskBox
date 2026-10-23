using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeskBox.Services;

/// <summary>
/// What a single recovery-snapshot zip revealed through the cheap three-entry
/// peek (manifest + settings + layout) used by the wipe-recovery offer. Every
/// field is answerable without extracting the archive to disk.
/// </summary>
internal sealed record RecoverySnapshotPeek(
    bool ManifestReadable,
    DateTimeOffset? CreatedAtUtc,
    string? AppVersion,
    int BackupSchemaVersion,
    bool HasSettings,
    int SettingsSchemaVersion,
    bool? HasResolvedInitialSetup,
    int LiveWidgetCount)
{
    public static RecoverySnapshotPeek Unreadable { get; } =
        new(false, null, null, 0, false, 0, null, 0);
}

internal enum RecoverySnapshotCandidateVerdict
{
    Valid,
    /// <summary>Manifest unreadable, core entries missing or duplicated, or a
    /// schema stamp this build cannot interpret in either direction — the
    /// same shape the restore validator would refuse, kept out of the offer
    /// so a proposed restore never dies only at confirmation time.</summary>
    RejectUnreadable,
    /// <summary>Stamped by a newer settings schema — restoring it on this build
    /// yields a read-only settings session (the schema-newer guard), which is
    /// too confusing to hand a user who just lost their configuration.</summary>
    RejectSchemaNewer,
    /// <summary>Captured before the profile resolved its first-run widget
    /// setup — the signature of a snapshot taken on a fresh-default profile
    /// right after a wipe, not of the user data the offer exists to recover.</summary>
    RejectUnresolvedSetup,
    /// <summary>No live widgets in the layout — same fresh-default signature
    /// through a different field.</summary>
    RejectNoWidgets,
}

/// <summary>
/// Pure acceptance rules for a recovery snapshot offered automatically after a
/// configuration wipe. Hard gates only, aligned with the restore validator's
/// own checks (entry uniqueness, archive schema bounds, strict numeric
/// parsing): the peek must never propose an archive the restore path would
/// refuse. The hasResolvedInitialFileWidgetSetup + live-widget pair
/// distinguishes a real profile from a fresh-default snapshot without fuzzy
/// "looks default" heuristics that could reject genuine profiles (for example
/// one whose owner never finished onboarding).
/// </summary>
internal static class RecoverySnapshotCandidatePolicy
{
    public static RecoverySnapshotCandidateVerdict Evaluate(RecoverySnapshotPeek peek)
    {
        if (!peek.ManifestReadable ||
            !peek.HasSettings ||
            peek.SettingsSchemaVersion < 1 ||
            peek.BackupSchemaVersion < 1 ||
            peek.BackupSchemaVersion > DeskBoxDataBackupService.CurrentBackupSchemaVersion)
        {
            return RecoverySnapshotCandidateVerdict.RejectUnreadable;
        }

        if (peek.SettingsSchemaVersion > SettingsMigrationPipeline.CurrentSchemaVersion)
        {
            return RecoverySnapshotCandidateVerdict.RejectSchemaNewer;
        }

        if (peek.HasResolvedInitialSetup is not true)
        {
            return RecoverySnapshotCandidateVerdict.RejectUnresolvedSetup;
        }

        if (peek.LiveWidgetCount <= 0)
        {
            return RecoverySnapshotCandidateVerdict.RejectNoWidgets;
        }

        return RecoverySnapshotCandidateVerdict.Valid;
    }
}

/// <summary>
/// A recovery snapshot that passed the peek validation and is ready to be
/// offered to the user (and restored from, on their confirmation).
/// </summary>
public sealed record RecoverySnapshotCandidate(
    string Path,
    DateTimeOffset CreatedAtUtc,
    string? AppVersion,
    int BackupSchemaVersion,
    int WidgetCount,
    bool IsFromNewerAppVersion);

internal enum RecoveryOfferOutcome
{
    /// <summary>No marker, already shown this session, or no valid candidate.</summary>
    NotOffered,
    /// <summary>Window closed without a choice. Decision A: this counts as an
    /// implicit fresh start — the marker is cleared (without the explicit
    /// button's DefaultsAfterFailure policy relaxation) and the first-run
    /// flows proceed in the same activation.</summary>
    Dismissed,
    /// <summary>User chose a fresh start; the marker is cleared and first-run
    /// flows (default widget, onboarding) proceed.</summary>
    FreshStart,
    /// <summary>User chose restore and the restore transaction was prepared,
    /// confirmed and scheduled; the process is shutting down for the relaunch.</summary>
    RestoreScheduled,
}

public enum RecoveryOfferDecision
{
    Restore,
    FreshStart,
    Dismissed,
}

/// <summary>
/// JSON peek helpers for recovery-snapshot archives: decode the three entries
/// the candidate policy needs without extracting anything to disk.
/// </summary>
internal static class RecoverySnapshotPeekReader
{
    // Matches the restore validator's per-entry manifest cap: peeks must not
    // accept (or spend memory on) archives the restore path would refuse.
    private const int MaxPeekEntryBytes = 1024 * 1024;

    public static RecoverySnapshotPeek Read(ZipArchive archive)
    {
        DateTimeOffset? createdAtUtc = null;
        string? appVersion = null;
        int backupSchemaVersion = 0;
        bool manifestRead = false;

        ZipArchiveEntry? manifestEntry = FindEntry(archive, "manifest.json");
        if (manifestEntry is not null)
        {
            JsonObject? manifest = ParseObject(manifestEntry);
            if (manifest is not null)
            {
                createdAtUtc = ReadDateTimeOffset(manifest["createdAtUtc"]);
                appVersion = ReadString(manifest["appVersion"]);
                backupSchemaVersion = ReadInt32(manifest["schemaVersion"]) ?? 0;
                // The restore validator's typed manifest record requires
                // kind/createdAtUtc/appVersion as positional members; an
                // archive lacking any of them would pass the peek, get
                // proposed, and then fail the restore on every attempt —
                // refuse it up front by treating the manifest as unreadable.
                manifestRead =
                    ReadString(manifest["kind"]) is not null &&
                    createdAtUtc is not null &&
                    appVersion is not null;
            }
        }

        bool hasSettings = false;
        int settingsSchemaVersion = 0;
        bool? hasResolvedInitialSetup = null;
        int liveWidgetCount = 0;

        ZipArchiveEntry? settingsEntry = FindEntry(archive, "data/settings.json");
        JsonObject? settings = settingsEntry is null ? null : ParseObject(settingsEntry);
        if (settings is not null)
        {
            hasSettings = true;
            settingsSchemaVersion = ReadInt32(settings["schemaVersion"]) ?? 0;
            hasResolvedInitialSetup = ReadBoolean(settings["hasResolvedInitialFileWidgetSetup"]);

            // Pre-split archives (and post-split ones whose layout store was
            // quarantined) carry the widget list as TOP-LEVEL settings keys —
            // a "widgetLayout" wrapper never existed in any serialized era.
            liveWidgetCount = CountLiveWidgets(
                settings["widgets"] as JsonArray,
                ReadStringSet(settings["deletedWidgetIds"] as JsonArray));
        }

        ZipArchiveEntry? layoutEntry = FindEntry(archive, "data/widget-layout.json");
        JsonObject? layoutDocument = layoutEntry is null ? null : ParseObject(layoutEntry);
        JsonObject? layout = layoutDocument?["layout"] as JsonObject;
        if (layout is not null)
        {
            liveWidgetCount = CountLiveWidgets(
                layout["widgets"] as JsonArray,
                ReadStringSet(layout["deletedWidgetIds"] as JsonArray));
        }

        if (!manifestRead && !hasSettings)
        {
            return RecoverySnapshotPeek.Unreadable;
        }

        return new RecoverySnapshotPeek(
            manifestRead,
            createdAtUtc,
            appVersion,
            backupSchemaVersion,
            hasSettings,
            settingsSchemaVersion,
            hasResolvedInitialSetup,
            liveWidgetCount);
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string fullName)
    {
        ZipArchiveEntry? match = null;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (!string.Equals(entry.FullName, fullName, StringComparison.Ordinal))
            {
                continue;
            }

            // A duplicate core entry is ambiguous the same way the restore
            // validator treats it (SingleOrDefault / extracted-path reject):
            // the offer refuses what the restore would refuse.
            if (match is not null)
            {
                return null;
            }

            match = entry;
        }

        return match;
    }

    private static JsonObject? ParseObject(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxPeekEntryBytes)
        {
            return null;
        }

        try
        {
            using Stream stream = entry.Open();
            using var buffered = new MemoryStream();
            CopyCapped(stream, buffered);
            buffered.Position = 0;
            return JsonNode.Parse(buffered) as JsonObject;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    private static void CopyCapped(Stream source, MemoryStream destination)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaxPeekEntryBytes)
            {
                throw new InvalidDataException("Peek entry exceeds the size cap.");
            }

            destination.Write(buffer, 0, read);
        }
    }

    private static int CountLiveWidgets(JsonArray? widgets, HashSet<string> deletedIds)
    {
        if (widgets is null)
        {
            return 0;
        }

        int live = 0;
        foreach (JsonNode? widget in widgets)
        {
            string? id = ReadString(widget?["id"]);
            if (id is null || !deletedIds.Contains(id))
            {
                live++;
            }
        }

        return live;
    }

    private static HashSet<string> ReadStringSet(JsonArray? array)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (array is null)
        {
            return result;
        }

        foreach (JsonNode? node in array)
        {
            if (ReadString(node) is { } value)
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static string? ReadString(JsonNode? node) => (node as JsonValue)?.GetValue<string>();

    private static int? ReadInt32(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out int parsed) ? parsed : null;

    private static bool? ReadBoolean(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out bool parsed) ? parsed : null;

    private static DateTimeOffset? ReadDateTimeOffset(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<DateTimeOffset>(out DateTimeOffset parsed)
            ? parsed
            : null;
}
