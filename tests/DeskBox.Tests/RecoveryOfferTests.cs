using System.IO.Compression;
using System.Text.Json;
using DeskBox.Contracts;
using DeskBox.Core.Persistence;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Wipe-recovery offer: candidate peek validation, marker lifecycle, snapshot
/// pool protection and the first-run policy relaxation after an explicit
/// fresh start.
/// </summary>
public sealed class RecoveryOfferTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _appDataRoot;

    public RecoveryOfferTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        _appDataRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "app-data")).FullName;
    }

    public void Dispose() => Directory.Delete(_tempRoot, recursive: true);

    // ── Candidate policy (pure) ───────────────────────────────────────────

    [Fact]
    public void Evaluate_RejectsUnreadableManifestOrMissingSettings()
    {
        var verdict = RecoverySnapshotCandidatePolicy.Evaluate(
            RecoverySnapshotPeek.Unreadable);
        Assert.Equal(RecoverySnapshotCandidateVerdict.RejectUnreadable, verdict);

        verdict = RecoverySnapshotCandidatePolicy.Evaluate(new RecoverySnapshotPeek(
            ManifestReadable: true, CreatedAtUtc: DateTimeOffset.UtcNow, AppVersion: "1.5.9",
            BackupSchemaVersion: 2, HasSettings: false, SettingsSchemaVersion: 14,
            HasResolvedInitialSetup: true, LiveWidgetCount: 3));
        Assert.Equal(RecoverySnapshotCandidateVerdict.RejectUnreadable, verdict);
    }

    [Fact]
    public void Evaluate_RejectsSchemaStampsTheRestorePathWouldRefuse()
    {
        // Settings schema 0 = missing/non-integer stamp: the typed restore
        // validator would fail on the same shape, so the offer must refuse it.
        Assert.Equal(
            RecoverySnapshotCandidateVerdict.RejectUnreadable,
            RecoverySnapshotCandidatePolicy.Evaluate(new RecoverySnapshotPeek(
                true, DateTimeOffset.UtcNow, "1.5.9", 2, true, 0,
                HasResolvedInitialSetup: true, LiveWidgetCount: 1)));

        // Archive manifest schema outside the restore validator's accepted
        // range (future backup format on an older build).
        Assert.Equal(
            RecoverySnapshotCandidateVerdict.RejectUnreadable,
            RecoverySnapshotCandidatePolicy.Evaluate(new RecoverySnapshotPeek(
                true, DateTimeOffset.UtcNow, "1.5.9",
                DeskBoxDataBackupService.CurrentBackupSchemaVersion + 1, true, 14,
                HasResolvedInitialSetup: true, LiveWidgetCount: 1)));
    }

    [Fact]
    public void Evaluate_RejectsFreshDefaultSignature()
    {
        // Snapshot taken right after a wipe: initial setup unresolved.
        Assert.Equal(
            RecoverySnapshotCandidateVerdict.RejectUnresolvedSetup,
            RecoverySnapshotCandidatePolicy.Evaluate(new RecoverySnapshotPeek(
                true, DateTimeOffset.UtcNow, "1.5.9", 2, true, 14,
                HasResolvedInitialSetup: false, LiveWidgetCount: 1)));

        // Setup resolved but no widgets ever created.
        Assert.Equal(
            RecoverySnapshotCandidateVerdict.RejectNoWidgets,
            RecoverySnapshotCandidatePolicy.Evaluate(new RecoverySnapshotPeek(
                true, DateTimeOffset.UtcNow, "1.5.9", 2, true, 14,
                HasResolvedInitialSetup: true, LiveWidgetCount: 0)));
    }

    [Fact]
    public void Evaluate_RejectsSchemaNewerThanBuild()
    {
        Assert.Equal(
            RecoverySnapshotCandidateVerdict.RejectSchemaNewer,
            RecoverySnapshotCandidatePolicy.Evaluate(new RecoverySnapshotPeek(
                true, DateTimeOffset.UtcNow, "1.5.9", 2, true,
                SettingsMigrationPipeline.CurrentSchemaVersion + 1,
                HasResolvedInitialSetup: true, LiveWidgetCount: 2)));
    }

    [Fact]
    public void Evaluate_AcceptsRealProfile()
    {
        Assert.Equal(
            RecoverySnapshotCandidateVerdict.Valid,
            RecoverySnapshotCandidatePolicy.Evaluate(new RecoverySnapshotPeek(
                true, DateTimeOffset.UtcNow, "1.5.9", 2, true,
                SettingsMigrationPipeline.CurrentSchemaVersion,
                HasResolvedInitialSetup: true, LiveWidgetCount: 2)));
    }

    // ── Candidate selection over crafted archives ─────────────────────────

    [Fact]
    public async Task GetLatestValidRecoverySnapshotAsync_PicksNewestValidOverNewerJunk()
    {
        string junk = WriteSnapshot(
            "DeskBox-Auto-20261021-090000.zip",
            mtimeUtc: DateTimeOffset.UtcNow.ToUniversalTime() - TimeSpan.FromHours(1),
            settingsJson: SettingsJson(hasResolved: false),
            layoutWidgets: []);
        string valid = WriteSnapshot(
            "DeskBox-Auto-20261020-221100.zip",
            mtimeUtc: DateTimeOffset.UtcNow.ToUniversalTime() - TimeSpan.FromDays(1),
            settingsJson: SettingsJson(hasResolved: true),
            layoutWidgets: ["w1", "w2"]);

        var service = new DeskBoxDataBackupService(_appDataRoot);
        RecoverySnapshotCandidate? candidate = await service.GetLatestValidRecoverySnapshotAsync(
            DateTimeOffset.UtcNow);

        Assert.NotNull(candidate);
        Assert.Equal(valid, candidate!.Path);
        Assert.Equal(2, candidate.WidgetCount);
        Assert.NotEqual(junk, candidate.Path);
    }

    [Fact]
    public async Task GetLatestValidRecoverySnapshotAsync_ExcludesArchivesNewerThanProcessStart()
    {
        WriteSnapshot(
            "DeskBox-Auto-20261021-090500.zip",
            mtimeUtc: DateTimeOffset.UtcNow.ToUniversalTime() + TimeSpan.FromMinutes(5),
            settingsJson: SettingsJson(hasResolved: true),
            layoutWidgets: ["w1"]);

        var service = new DeskBoxDataBackupService(_appDataRoot);
        // The junk archive created moments after the wipe (newer than the
        // process start) must never be offered even though it is the only one.
        Assert.Null(await service.GetLatestValidRecoverySnapshotAsync(DateTimeOffset.UtcNow));
        Assert.True(await service.HasRecoverySnapshotNotAfterAsync(
            DateTimeOffset.UtcNow.ToUniversalTime() + TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public async Task GetLatestValidRecoverySnapshotAsync_RejectsFutureSchema()
    {
        WriteSnapshot(
            "DeskBox-Auto-20261020-221100.zip",
            mtimeUtc: DateTimeOffset.UtcNow.ToUniversalTime() - TimeSpan.FromDays(1),
            settingsJson: SettingsJson(hasResolved: true, schemaVersion: SettingsMigrationPipeline.CurrentSchemaVersion + 5),
            layoutWidgets: ["w1"]);

        var service = new DeskBoxDataBackupService(_appDataRoot);
        Assert.Null(await service.GetLatestValidRecoverySnapshotAsync(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task GetLatestValidRecoverySnapshotAsync_CountsLegacyLayoutInsideSettings()
    {
        // Pre-split archives (v1.3.8–v1.5.4) carry the widget list as
        // TOP-LEVEL settings keys; a "widgetLayout" wrapper never existed in
        // any serialized era (the shape is pinned by hand, not by the live
        // serializer, so facade evolution cannot silently change it).
        WriteSnapshot(
            "DeskBox-Auto-20260101-000000.zip",
            mtimeUtc: DateTimeOffset.UtcNow.ToUniversalTime() - TimeSpan.FromDays(30),
            settingsJson: SettingsJson(
                hasResolved: true,
                extraJson:
                    "\"widgets\":[{\"id\":\"w1\"},{\"id\":\"w2\"},{\"id\":\"w3\"}]," +
                    "\"deletedWidgetIds\":[\"w2\"]"),
            layoutWidgets: null);

        var service = new DeskBoxDataBackupService(_appDataRoot);
        RecoverySnapshotCandidate? candidate = await service.GetLatestValidRecoverySnapshotAsync(
            DateTimeOffset.UtcNow);

        Assert.NotNull(candidate);
        Assert.Equal(2, candidate!.WidgetCount);
    }

    [Fact]
    public async Task GetLatestValidRecoverySnapshotAsync_RejectsManifestMissingRequiredMembers()
    {
        // The restore validator's typed manifest record requires
        // kind/createdAtUtc/appVersion; an archive lacking them must be
        // refused up front instead of being proposed and failing at
        // restore time on every attempt.
        WriteSnapshot(
            "DeskBox-Auto-20260101-000001.zip",
            mtimeUtc: DateTimeOffset.UtcNow.ToUniversalTime() - TimeSpan.FromDays(30),
            settingsJson: SettingsJson(hasResolved: true),
            layoutWidgets: ["w1"],
            manifestJson:
                "{\"schemaVersion\":2,\"kind\":\"automatic\",\"createdAtUtc\":\"2026-10-19T22:11:00Z\"}");

        var service = new DeskBoxDataBackupService(_appDataRoot);
        Assert.Null(await service.GetLatestValidRecoverySnapshotAsync(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task HasRecoverySnapshotNotAfterAsync_FalseWithoutSnapshots()
    {
        var service = new DeskBoxDataBackupService(_appDataRoot);
        Assert.False(await service.HasRecoverySnapshotNotAfterAsync(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task AutomaticSnapshot_ExcludesRecoveryOfferMarker()
    {
        string data = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(data, "settings.json"), "{}");
        var store = new RecoveryOfferStore(data);
        await store.WriteAsync(new RecoveryOfferMarker(
            "C:/recovery/DeskBox-Auto-1.zip",
            DateTimeOffset.UtcNow,
            "1.5.9",
            2,
            DateTimeOffset.UtcNow));

        var service = new DeskBoxDataBackupService(_appDataRoot);
        LocalBackupResult result = await service.CreateAutomaticSnapshotResultAsync(force: true);

        Assert.Equal(DeskBox.Contracts.BackupOutcome.Succeeded, result.Outcome);
        Assert.NotNull(result.ArchivePath);
        using ZipArchive archive = ZipFile.Open(result.ArchivePath!, ZipArchiveMode.Read);
        Assert.DoesNotContain(
            archive.Entries,
            entry => entry.FullName.EndsWith(
                RecoveryOfferStore.MarkerFileName,
                StringComparison.OrdinalIgnoreCase));
    }

    // ── Marker store ──────────────────────────────────────────────────────

    [Fact]
    public async Task RecoveryOfferStore_RoundtripAndClear()
    {
        string data = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        var store = new RecoveryOfferStore(data);
        Assert.Null(store.TryRead());

        var marker = new RecoveryOfferMarker(
            "C:/recovery/DeskBox-Auto-1.zip",
            new DateTimeOffset(2026, 10, 19, 22, 11, 0, TimeSpan.Zero),
            "1.5.9",
            3,
            new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero));
        await store.WriteAsync(marker);

        RecoveryOfferMarker? read = store.TryRead();
        Assert.NotNull(read);
        Assert.Equal(marker.SnapshotPath, read!.SnapshotPath);
        Assert.Equal(marker.SnapshotWidgetCount, read.SnapshotWidgetCount);
        Assert.Equal(
            marker.SnapshotCreatedAtUtc,
            read.SnapshotCreatedAtUtc,
            precision: TimeSpan.FromSeconds(1));

        await store.ClearAsync();
        Assert.Null(store.TryRead());
        // Clear is idempotent.
        await store.ClearAsync();
    }

    [Fact]
    public async Task RecoveryOfferStore_UnreadableMarkerReadsAsNull()
    {
        string data = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        var store = new RecoveryOfferStore(data);
        await File.WriteAllTextAsync(store.MarkerPath, "not json");
        Assert.Null(store.TryRead());
    }

    // ── First-run policy relaxation ───────────────────────────────────────

    [Fact]
    public void InitialSetup_AllowsDefaultWidgetAfterExplicitFreshStartOnly()
    {
        // DefaultsAfterFailure keeps suppressing widget creation by default…
        Assert.Equal(
            InitialFileWidgetSetupDecision.None,
            InitialFileWidgetSetupPolicy.Evaluate(new InitialFileWidgetSetupSnapshot(
                IsInteractiveLaunch: true,
                SettingsLoadState: SettingsLoadRecoveryState.DefaultsAfterFailure,
                HasResolvedSetup: false,
                HasConfiguredFileWidget: false)));

        // …but the explicit "start fresh" answer reopens the first-run path.
        Assert.Equal(
            InitialFileWidgetSetupDecision.CreateDefaultWidget,
            InitialFileWidgetSetupPolicy.Evaluate(new InitialFileWidgetSetupSnapshot(
                IsInteractiveLaunch: true,
                SettingsLoadState: SettingsLoadRecoveryState.DefaultsAfterFailure,
                HasResolvedSetup: false,
                HasConfiguredFileWidget: false,
                AllowDefaultAfterRecoveryFailure: true)));

        // A resolved profile is never re-offered a default widget, fresh start
        // or not.
        Assert.Equal(
            InitialFileWidgetSetupDecision.None,
            InitialFileWidgetSetupPolicy.Evaluate(new InitialFileWidgetSetupSnapshot(
                IsInteractiveLaunch: true,
                SettingsLoadState: SettingsLoadRecoveryState.DefaultsAfterFailure,
                HasResolvedSetup: true,
                HasConfiguredFileWidget: false,
                AllowDefaultAfterRecoveryFailure: true)));
    }

    // ── Source contract: ordering against the first-run flows ─────────────

    [Fact]
    public void Startup_WipeRecoveryOfferPrecedesAndGatesFirstRunFlows()
    {
        string app = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.xaml.cs"));

        string launch = Slice(
            app,
            "recovery-snapshot-notification",
            "\"managed-storage-desktop-shortcut\"");
        int markerWrite = launch.IndexOf("RecoveryOfferStore.WriteAsync", StringComparison.Ordinal);
        int offer = launch.IndexOf("ShowRecoveryOfferIfNeededAsync();", StringComparison.Ordinal);
        int setup = launch.IndexOf("\"initial-file-widget-setup\"", StringComparison.Ordinal);
        Assert.True(markerWrite >= 0, "Detection must arm the offer marker.");
        Assert.True(offer > markerWrite, "The offer must follow the detection that arms it.");
        Assert.True(setup > offer, "The offer must precede first-run widget creation.");
        // A shutdown in flight between the offer and first-run flows (a
        // restore may be scheduled from the activation path during startup)
        // must stop the pipeline before it manufactures widgets.
        int offerReturn = launch.IndexOf(
            "if (recoveryOfferOutcome == RecoveryOfferOutcome.RestoreScheduled)",
            StringComparison.Ordinal);
        int shuttingDownGate = launch.IndexOf(
            "if (IsShuttingDown)",
            offerReturn,
            StringComparison.Ordinal);
        Assert.True(
            shuttingDownGate > offerReturn && shuttingDownGate < setup,
            "An IsShuttingDown gate must sit between the offer and first-run setup.");

        string activation = Slice(
            app,
            "RecoveryOfferOutcome activationRecoveryOffer",
            "if (await EnsureOnboardingAsync(isInteractiveLaunch: true))");
        int activationOffer = activation.IndexOf(
            "await ShowRecoveryOfferIfNeededAsync();",
            StringComparison.Ordinal);
        int activationSetup = activation.IndexOf(
            "await EnsureInitialFileWidgetSetupAsync(",
            StringComparison.Ordinal);
        Assert.True(
            activationOffer >= 0 && activationSetup > activationOffer,
            "External activations must put the wipe-recovery offer before first-run flows.");
        // The in-flight/window guards close the double-window race: a second
        // surface arriving while the offer is being shown must not race it.
        Assert.Contains(
            "_recoveryOfferWindow is not null",
            activation,
            StringComparison.Ordinal);
        Assert.Contains(
            "_recoveryOfferInFlight)",
            activation,
            StringComparison.Ordinal);

        // The startup snapshot gate keys on the deterministic load outcome and
        // covers BOTH wipe arms (missing settings and failed load) — the
        // DefaultsAfterFailure session's snapshot would otherwise hijack the
        // next session's offer with a defaults-plus-layout archive.
        Assert.Contains("await settingsLoadTask;", app, StringComparison.Ordinal);
        string snapshotGate = Slice(
            app,
            "// A defaults launch (missing settings, or settings",
            "await _backupRuntime.RunScheduledLocalAsync();");
        Assert.Contains(
            "SettingsLoadRecoveryState.DefaultsForMissingFile",
            snapshotGate,
            StringComparison.Ordinal);
        Assert.Contains(
            "SettingsLoadRecoveryState.DefaultsAfterFailure",
            snapshotGate,
            StringComparison.Ordinal);
    }

    [Fact]
    public void App_PinsOfferSemanticsAndRaceGuards()
    {
        string app = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.xaml.cs"));

        // Semantics A: closing the window routes through the implicit
        // fresh-start handler WITHOUT the explicit button's policy relaxation.
        Assert.Contains(
            "HandleRecoveryOfferFreshStartAsync(relaxDefaultWidgetPolicy: false)",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "HandleRecoveryOfferFreshStartAsync(relaxDefaultWidgetPolicy: true)",
            app,
            StringComparison.Ordinal);

        // The restore-transaction guard: once a relaunch is scheduled, a
        // later failure must not cancel the confirmed restore.
        Assert.Contains("if (!restartScheduled)", app, StringComparison.Ordinal);

        // Outcome propagation: a second surface in the same session gets the
        // recorded decision back instead of NotOffered.
        Assert.Contains(
            "return _lastRecoveryOfferOutcome;",
            app,
            StringComparison.Ordinal);

        // RestoreScheduled is pre-recorded so activations arriving while the
        // restore transaction is preparing never run the first-run flows.
        Assert.Contains(
            "_lastRecoveryOfferOutcome = outcome;",
            app,
            StringComparison.Ordinal);

        // The pending offer's referenced snapshot is pinned against retention
        // pruning until the decision lands.
        Assert.Contains(
            "SetRecoverySnapshotRetentionPin(",
            app,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScheduledSnapshots_PauseWhileARecoveryDecisionIsPending()
    {
        string data = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(data, "settings.json"), "{}");
        var store = new RecoveryOfferStore(data);
        await store.WriteAsync(new RecoveryOfferMarker(
            "C:/recovery/DeskBox-Auto-1.zip",
            DateTimeOffset.UtcNow,
            "1.5.9",
            2,
            DateTimeOffset.UtcNow));

        var service = new DeskBoxDataBackupService(_appDataRoot);
        service.SetRecoveryOfferPauseProbe(() => store.TryRead() is not null);

        // Scheduled snapshots stay paused: the fresh-default profile they
        // would archive evicts the pre-wipe snapshots the offer restores from.
        LocalBackupResult scheduled = await service.CreateAutomaticSnapshotResultAsync(force: false);
        Assert.Equal(BackupOutcome.Skipped, scheduled.Outcome);

        // Manual "back up now" stays available — the user is looking at the
        // backup page and the action is explicit.
        LocalBackupResult forced = await service.CreateAutomaticSnapshotResultAsync(force: true);
        Assert.Equal(BackupOutcome.Succeeded, forced.Outcome);

        // The forced archive is seconds old; a scheduled leg would be
        // throttled by the interval check (latest snapshot < IntervalMinutes,
        // default 1440 — see CreateAutomaticSnapshotResultAsync). Remove it so
        // the leg below proves the pause lifted, not that the interval
        // elapsed. Use the returned path: same-second collisions get a -2
        // suffix the constructed name would miss.
        File.Delete(forced.ArchivePath!);

        await store.ClearAsync();
        LocalBackupResult resumed = await service.CreateAutomaticSnapshotResultAsync(force: false);
        Assert.Equal(BackupOutcome.Succeeded, resumed.Outcome);
    }

    [Fact]
    public async Task ForcedSnapshots_NeverEvictThePendingRecoveryReference()
    {
        string data = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(data, "settings.json"), "{}");

        // Pre-wipe pool: the pinned newest plus three older archives, mtimes
        // a day apart so ordering never ties with the forced storm (= now).
        string pinned = WriteSnapshot(
            "DeskBox-Auto-20260101-000000.zip",
            mtimeUtc: DateTimeOffset.UtcNow.ToUniversalTime() - TimeSpan.FromDays(4),
            settingsJson: SettingsJson(hasResolved: true),
            layoutWidgets: ["w1", "w2"]);
        string[] older =
        [
            WriteSnapshot(
                "DeskBox-Auto-20251229-000000.zip",
                mtimeUtc: DateTimeOffset.UtcNow.ToUniversalTime() - TimeSpan.FromDays(10),
                settingsJson: SettingsJson(hasResolved: true),
                layoutWidgets: ["w1"]),
            WriteSnapshot(
                "DeskBox-Auto-20251230-000000.zip",
                mtimeUtc: DateTimeOffset.UtcNow.ToUniversalTime() - TimeSpan.FromDays(8),
                settingsJson: SettingsJson(hasResolved: true),
                layoutWidgets: ["w1"]),
            WriteSnapshot(
                "DeskBox-Auto-20251231-000000.zip",
                mtimeUtc: DateTimeOffset.UtcNow.ToUniversalTime() - TimeSpan.FromDays(6),
                settingsJson: SettingsJson(hasResolved: true),
                layoutWidgets: ["w1"])
        ];

        var store = new RecoveryOfferStore(data);
        await store.WriteAsync(new RecoveryOfferMarker(
            pinned,
            DateTimeOffset.UtcNow,
            "1.5.9",
            2,
            DateTimeOffset.UtcNow));

        var service = new DeskBoxDataBackupService(_appDataRoot);
        service.SetRecoveryOfferPauseProbe(() => store.TryRead() is not null);
        service.SetRecoverySnapshotRetentionPin(() => store.TryRead()?.SnapshotPath);

        // A forced-storm of manual backups beyond the default retention (7)
        // must prune the OLDEST pre-wipe archives — never the pinned
        // reference, which does not consume a retention slot.
        for (int i = 0; i < 7; i++)
        {
            LocalBackupResult forced = await service.CreateAutomaticSnapshotResultAsync(force: true);
            Assert.Equal(BackupOutcome.Succeeded, forced.Outcome);
        }

        Assert.True(File.Exists(pinned), "The pinned recovery reference was evicted.");
        string directory = Path.GetDirectoryName(pinned)!;
        Assert.Equal(
            8,
            Directory.EnumerateFiles(directory, "DeskBox-Auto-*.zip").Count());
        Assert.DoesNotContain(older[0], Directory.EnumerateFiles(directory, "DeskBox-Auto-*.zip"));

        // The offer itself still resolves to the pinned archive: the forced
        // snapshots of the fresh-default profile fail the content gates.
        RecoverySnapshotCandidate? candidate = await service.GetLatestValidRecoverySnapshotAsync(
            DateTimeOffset.UtcNow.ToUniversalTime() - TimeSpan.FromDays(1));
        Assert.NotNull(candidate);
        Assert.Equal(pinned, candidate!.Path);

        // After the decision (marker cleared) retention goes back to normal:
        // one more forced run pushes past the quota and evicts the former
        // pin like any other archive — the exemption ends with the marker.
        await store.ClearAsync();
        service.SetRecoverySnapshotRetentionPin(null);
        LocalBackupResult finalForced = await service.CreateAutomaticSnapshotResultAsync(force: true);
        Assert.Equal(BackupOutcome.Succeeded, finalForced.Outcome);
        Assert.False(File.Exists(pinned), "The pin outlived the pending decision.");
    }

    [Fact]
    public async Task RecoveryOfferStore_OversizedMarkerReadsAsNull()
    {
        string data = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        var store = new RecoveryOfferStore(data);
        await File.WriteAllTextAsync(
            store.MarkerPath,
            new string('x', 128 * 1024));
        Assert.Null(store.TryRead());
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string SettingsJson(
        bool hasResolved,
        int? schemaVersion = null,
        string? extraJson = null)
    {
        int version = schemaVersion ?? SettingsMigrationPipeline.CurrentSchemaVersion;
        string json =
            $"{{\"schemaVersion\":{version}," +
            $"\"hasResolvedInitialFileWidgetSetup\":{(hasResolved ? "true" : "false")}";
        if (!string.IsNullOrEmpty(extraJson))
        {
            json += "," + extraJson;
        }

        return json + "}";
    }

    private string WriteSnapshot(
        string fileName,
        DateTimeOffset mtimeUtc,
        string settingsJson,
        string[]? layoutWidgets,
        string? manifestJson = null)
    {
        string directory = Path.Combine(
            Path.GetDirectoryName(_appDataRoot)!,
            "DeskBox-Recovery",
            "automatic");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(
                archive,
                "manifest.json",
                manifestJson ??
                "{\"schemaVersion\":2,\"kind\":\"automatic\",\"createdAtUtc\":\"2026-10-19T22:11:00Z\",\"appVersion\":\"1.5.9\"}");
            WriteEntry(archive, "data/settings.json", settingsJson);
            if (layoutWidgets is not null)
            {
                string widgetsJson = layoutWidgets.Length == 0
                    ? string.Empty
                    : string.Join(",", layoutWidgets.Select(id => $"{{\"id\":\"{id}\"}}"));
                WriteEntry(
                    archive,
                    "data/widget-layout.json",
                    "{\"schemaVersion\":1,\"layout\":{\"widgets\":[" + widgetsJson +
                    "],\"deletedWidgetIds\":[]}}");
            }
        }

        // After dispose: finalizing the zip rewrites the file and would reset
        // the stamp the selection ordering below depends on.
        File.SetLastWriteTimeUtc(path, mtimeUtc.UtcDateTime);
        return path;
    }

    private static void WriteEntry(ZipArchive archive, string entryName, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker not found: {endMarker}");
        return source[start..end];
    }
}
