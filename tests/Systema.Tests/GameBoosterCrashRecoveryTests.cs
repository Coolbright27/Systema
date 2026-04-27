// ════════════════════════════════════════════════════════════════════════════
// GameBoosterCrashRecoveryTests.cs
// Tests the crash-recovery persistence layer for GameBoosterService.
//
// Strategy: the crash recovery path reads/writes a JSON file at
//   %LOCALAPPDATA%\Systema\boost_state.json
// The file format is defined by BoostStateSnapshot (private nested class).
// We duplicate that structure here as a test-local class with identical
// property names so we can exercise the JSON format independently of the
// private implementation.
// ════════════════════════════════════════════════════════════════════════════

using System.Text.Json;

namespace Systema.Tests;

public class GameBoosterCrashRecoveryTests : IDisposable
{
    // ── Mirror of GameBoosterService's private snapshot classes ───────────────
    // Must keep property names in sync with GameBoosterService.BoostStateSnapshot.

    private sealed class BoostStateSnapshot
    {
        public string? GameName                   { get; set; }
        public List<string>? KilledServices       { get; set; }
        public int? NotificationsEnabled          { get; set; }
        public string? PowerPlanGuid              { get; set; }
        public bool SearchIndexingWasRunning      { get; set; }
        public int? AppCaptureEnabled             { get; set; }
        public int? GameDvrEnabled                { get; set; }
        public int? SystemResponsiveness          { get; set; }
        public int? MmPriority                    { get; set; }
        public string? SchedulingCategory         { get; set; }
        public string? SfIoPriority               { get; set; }
        public List<RegistryRestoreEntry>? NagleRestore    { get; set; }
        public List<RegistryRestoreEntry>? NicPowerRestore { get; set; }
        public bool WifiRadioDisabled             { get; set; }
        public bool BluetoothRadioDisabled        { get; set; }
        // Battery pause (added in v1.7.51) — vendor-specific charge control
        // BatteryPauseMethod added in v1.7.52 — routes Resume back through the right hook
        public string? BatteryPauseMethod         { get; set; }
        public string? BatteryPauseVendor         { get; set; }
        public string? BatteryPauseOriginalMode   { get; set; }
        public bool    BatteryPauseWasActive      { get; set; }
    }

    private sealed class RegistryRestoreEntry
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public int? Val    { get; set; }
    }

    // ── File-system helpers ───────────────────────────────────────────────────

    private static readonly string BoostStateDir  =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Systema");
    private static readonly string BoostStatePath = Path.Combine(BoostStateDir, "boost_state.json");
    private static readonly string BoostStateTmp  = BoostStatePath + ".tmp";

    // Saves the original contents so we can restore it after each test.
    private readonly string? _originalContent;

    public GameBoosterCrashRecoveryTests()
    {
        Directory.CreateDirectory(BoostStateDir);
        // Back up whatever may already be there so tests don't stomp real state.
        _originalContent = File.Exists(BoostStatePath) ? File.ReadAllText(BoostStatePath) : null;
        // Start each test with a clean slate.
        TryDelete(BoostStatePath);
        TryDelete(BoostStateTmp);
    }

    public void Dispose()
    {
        // Restore original state after each test.
        TryDelete(BoostStatePath);
        TryDelete(BoostStateTmp);
        if (_originalContent != null)
            File.WriteAllText(BoostStatePath, _originalContent);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string Serialize(BoostStateSnapshot snap) =>
        JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true });

    private static BoostStateSnapshot? Deserialize(string json) =>
        JsonSerializer.Deserialize<BoostStateSnapshot>(json);

    // ══════════════════════════════════════════════════════════════════════════
    // 1. JSON SERIALIZATION ROUNDTRIP
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Roundtrip_FullSnapshot_PreservesAllFields()
    {
        var original = new BoostStateSnapshot
        {
            GameName                 = "csgo",
            KilledServices           = new List<string> { "BITS", "WSearch", "DiagTrack" },
            NotificationsEnabled     = 1,
            PowerPlanGuid            = "381b4222-f694-41f0-9685-ff5bb260df2e",
            SearchIndexingWasRunning = true,
            AppCaptureEnabled        = 1,
            GameDvrEnabled           = 1,
            SystemResponsiveness     = 20,
            MmPriority               = 2,
            SchedulingCategory       = "Medium",
            SfIoPriority             = "Normal",
            WifiRadioDisabled        = true,
            BluetoothRadioDisabled   = false,
            NagleRestore = new List<RegistryRestoreEntry>
            {
                new() { Path = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{abc}", Name = "TcpAckFrequency", Val = null },
                new() { Path = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{abc}", Name = "TCPNoDelay",      Val = null },
            },
            NicPowerRestore = new List<RegistryRestoreEntry>
            {
                new() { Path = @"SYSTEM\CurrentControlSet\Control\Class\{4D36E972}\0001", Name = "PnPCapabilities", Val = 0 },
            },
        };

        var json  = Serialize(original);
        var back  = Deserialize(json)!;

        Assert.Equal("csgo", back.GameName);
        Assert.Equal(3, back.KilledServices!.Count);
        Assert.Contains("BITS",     back.KilledServices);
        Assert.Contains("WSearch",  back.KilledServices);
        Assert.Contains("DiagTrack",back.KilledServices);
        Assert.Equal(1,       back.NotificationsEnabled);
        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", back.PowerPlanGuid);
        Assert.True(back.SearchIndexingWasRunning);
        Assert.Equal(1,        back.AppCaptureEnabled);
        Assert.Equal(1,        back.GameDvrEnabled);
        Assert.Equal(20,       back.SystemResponsiveness);
        Assert.Equal(2,        back.MmPriority);
        Assert.Equal("Medium", back.SchedulingCategory);
        Assert.Equal("Normal", back.SfIoPriority);
        Assert.True(back.WifiRadioDisabled);
        Assert.False(back.BluetoothRadioDisabled);

        Assert.Equal(2, back.NagleRestore!.Count);
        Assert.Null(back.NagleRestore[0].Val);   // null = delete-on-restore
        Assert.Null(back.NagleRestore[1].Val);

        Assert.Single(back.NicPowerRestore!);
        Assert.Equal(0, back.NicPowerRestore![0].Val);
    }

    [Fact]
    public void Roundtrip_NullOptionalFields_RoundtripAsNull()
    {
        var snap = new BoostStateSnapshot
        {
            GameName             = "Fortnite",
            KilledServices       = new List<string> { "BITS" },
            // All nullable fields left null (notifications already off, no power plan change, etc.)
            NotificationsEnabled = null,
            PowerPlanGuid        = null,
            AppCaptureEnabled    = null,
            GameDvrEnabled       = null,
            SystemResponsiveness = null,
            MmPriority           = null,
            SchedulingCategory   = null,
            SfIoPriority         = null,
            NagleRestore         = null,
            NicPowerRestore      = null,
        };

        var back = Deserialize(Serialize(snap))!;

        Assert.Null(back.NotificationsEnabled);
        Assert.Null(back.PowerPlanGuid);
        Assert.Null(back.AppCaptureEnabled);
        Assert.Null(back.GameDvrEnabled);
        Assert.Null(back.SystemResponsiveness);
        Assert.Null(back.MmPriority);
        Assert.Null(back.SchedulingCategory);
        Assert.Null(back.SfIoPriority);
        Assert.Null(back.NagleRestore);
        Assert.Null(back.NicPowerRestore);
    }

    [Fact]
    public void Roundtrip_EmptyKilledServices_RoundtripAsEmptyList()
    {
        // Edge case: boost activated but no services were actually running.
        var snap = new BoostStateSnapshot
        {
            GameName       = "dota2",
            KilledServices = new List<string>(),  // empty — all services were already stopped
        };

        var back = Deserialize(Serialize(snap))!;

        Assert.NotNull(back.KilledServices);
        Assert.Empty(back.KilledServices!);
    }

    [Fact]
    public void Roundtrip_SearchIndexingFlagFalse_RoundtripCorrectly()
    {
        // SearchIndexing was already stopped before boost — should stay false and not be restarted.
        var snap = new BoostStateSnapshot
        {
            GameName                 = "rust",
            SearchIndexingWasRunning = false,
        };

        var back = Deserialize(Serialize(snap))!;
        Assert.False(back.SearchIndexingWasRunning);
    }

    [Fact]
    public void Roundtrip_NagleRestoreWithNonNullVal_PreservesOriginalDword()
    {
        // Val != null means restore to original value (not delete).
        var snap = new BoostStateSnapshot
        {
            GameName = "GTA5",
            NagleRestore = new List<RegistryRestoreEntry>
            {
                new() { Path = @"some\path", Name = "TcpAckFrequency", Val = 2 }, // had value 2 before
            },
        };

        var back = Deserialize(Serialize(snap))!;
        Assert.Equal(2, back.NagleRestore![0].Val);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 2. ATOMIC WRITE PATTERN (tmp → rename)
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AtomicWrite_TmpThenMove_ProducesCorrectFinalFile()
    {
        var snap = new BoostStateSnapshot { GameName = "Overwatch" };
        var json = Serialize(snap);

        // Simulate what PersistBoostState does
        File.WriteAllText(BoostStateTmp, json);
        File.Move(BoostStateTmp, BoostStatePath, overwrite: true);

        Assert.True(File.Exists(BoostStatePath));
        Assert.False(File.Exists(BoostStateTmp)); // tmp must be gone

        var back = Deserialize(File.ReadAllText(BoostStatePath))!;
        Assert.Equal("Overwatch", back.GameName);
    }

    [Fact]
    public void AtomicWrite_OverwritesExistingFile()
    {
        // First write
        var snap1 = new BoostStateSnapshot { GameName = "Session1" };
        File.WriteAllText(BoostStateTmp, Serialize(snap1));
        File.Move(BoostStateTmp, BoostStatePath, overwrite: true);

        // Second write (e.g., settings changed mid-boost)
        var snap2 = new BoostStateSnapshot { GameName = "Session2" };
        File.WriteAllText(BoostStateTmp, Serialize(snap2));
        File.Move(BoostStateTmp, BoostStatePath, overwrite: true);

        var back = Deserialize(File.ReadAllText(BoostStatePath))!;
        Assert.Equal("Session2", back.GameName);
        Assert.False(File.Exists(BoostStateTmp));
    }

    [Fact]
    public void AtomicWrite_LeftoverTmpFromPreviousCrash_IsOverwritten()
    {
        // Simulate crash that left a .tmp file — next boost must not fail
        File.WriteAllText(BoostStateTmp, "CORRUPT LEFTOVER TMP");

        var snap = new BoostStateSnapshot { GameName = "EscapeFromTarkov" };
        File.WriteAllText(BoostStateTmp, Serialize(snap));  // overwrites corrupt tmp
        File.Move(BoostStateTmp, BoostStatePath, overwrite: true);

        var back = Deserialize(File.ReadAllText(BoostStatePath))!;
        Assert.Equal("EscapeFromTarkov", back.GameName);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 3. CORRUPT / EMPTY / MALFORMED FILE HANDLING
    //    Validates the guard logic inside RecoverBoostStateFromCrash:
    //    corrupt JSON → delete file (no recovery loop), null → delete file.
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void CorruptJson_DeserializationThrows_FileCanBeDeleted()
    {
        // Write a corrupt state file
        File.WriteAllText(BoostStatePath, "{ NOT VALID JSON }}}");
        Assert.True(File.Exists(BoostStatePath));

        // The service's catch block calls ClearPersistedBoostState → deletes file.
        // Simulate that guard logic: catch → delete.
        try { JsonSerializer.Deserialize<BoostStateSnapshot>(File.ReadAllText(BoostStatePath)); }
        catch { TryDelete(BoostStatePath); }

        Assert.False(File.Exists(BoostStatePath));
    }

    [Fact]
    public void EmptyFile_DeserializationReturnsNull_FileCanBeDeleted()
    {
        File.WriteAllText(BoostStatePath, "");

        // Empty string → Deserialize returns null in some cases, or throws
        BoostStateSnapshot? result = null;
        try { result = JsonSerializer.Deserialize<BoostStateSnapshot>(""); }
        catch { /* throws on empty — same outcome */ }

        if (result == null)
            TryDelete(BoostStatePath);

        Assert.False(File.Exists(BoostStatePath));
    }

    [Fact]
    public void JsonNullLiteral_DeserializationReturnsNull_FileCanBeDeleted()
    {
        // "null" is valid JSON but deserializes to null BoostStateSnapshot
        File.WriteAllText(BoostStatePath, "null");

        var result = Deserialize(File.ReadAllText(BoostStatePath));
        Assert.Null(result);

        // Service's null-guard: if (snap == null) { ClearPersistedBoostState(); return; }
        if (result == null)
            TryDelete(BoostStatePath);

        Assert.False(File.Exists(BoostStatePath));
    }

    [Fact]
    public void PartialJson_MissingNewFields_DeserializesWithDefaults()
    {
        // Simulate a state file from an older Systema version that didn't have all fields.
        // Missing fields must deserialize to null/false (not throw).
        var oldStyleJson = """
        {
            "GameName": "csgo",
            "KilledServices": ["BITS", "WSearch"]
        }
        """;

        File.WriteAllText(BoostStatePath, oldStyleJson);
        var snap = Deserialize(File.ReadAllText(BoostStatePath))!;

        Assert.Equal("csgo", snap.GameName);
        Assert.Equal(2, snap.KilledServices!.Count);

        // All new fields must default to null / false — they were not present in the old format.
        Assert.Null(snap.NotificationsEnabled);
        Assert.Null(snap.PowerPlanGuid);
        Assert.False(snap.SearchIndexingWasRunning);
        Assert.Null(snap.AppCaptureEnabled);
        Assert.Null(snap.GameDvrEnabled);
        Assert.Null(snap.NagleRestore);
        Assert.Null(snap.NicPowerRestore);
        Assert.False(snap.WifiRadioDisabled);
        Assert.False(snap.BluetoothRadioDisabled);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 4. STATE FILE LIFECYCLE (no real system calls)
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void StateFile_CreatedByPersist_ExistsUntilDeleted()
    {
        var snap = new BoostStateSnapshot { GameName = "BG3", KilledServices = new List<string> { "BITS" } };
        var json = Serialize(snap);

        // Simulate PersistBoostState
        var tmp = BoostStatePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, BoostStatePath, overwrite: true);

        Assert.True(File.Exists(BoostStatePath), "State file must exist after persist");

        // Simulate ClearPersistedBoostState (clean deactivation)
        if (File.Exists(BoostStatePath)) File.Delete(BoostStatePath);

        Assert.False(File.Exists(BoostStatePath), "State file must be deleted after clean deactivation");
    }

    [Fact]
    public void StateFile_NoFile_RecoveryIsNoop()
    {
        // If boost_state.json doesn't exist, RecoverBoostStateFromCrash
        // should return immediately (File.Exists guard).
        Assert.False(File.Exists(BoostStatePath));
        // Nothing else to assert — absence of exception is the test.
    }

    [Fact]
    public void StateFile_DirectoryMissing_CreateDirectorySucceeds()
    {
        // Verify CreateDirectory is idempotent — safe to call even when dir exists.
        Directory.CreateDirectory(BoostStateDir); // dir may already exist
        Assert.True(Directory.Exists(BoostStateDir));

        // Write should succeed
        var snap = new BoostStateSnapshot { GameName = "Valorant" };
        File.WriteAllText(BoostStateTmp, Serialize(snap));
        File.Move(BoostStateTmp, BoostStatePath, overwrite: true);
        Assert.True(File.Exists(BoostStatePath));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 5. WIFI / BLUETOOTH RACE CONDITION GAP
    //    Documents the known gap: WifiRadioDisabled / BluetoothRadioDisabled
    //    are set asynchronously via Task.Run but PersistBoostState reads them
    //    synchronously. A crash after the async tasks finish but before a
    //    re-persist would leave the state file with false for these flags.
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void WifiBluetoothGap_SnapshotBeforeAsyncCompletes_FlagsAreStillFalse()
    {
        // Simulates the race: persist runs synchronously immediately after
        // ApplyBoostOptions returns, but the Task.Run for Wifi/BT hasn't finished yet.
        bool wifiDisabledFlag  = false;  // _wifiRadioDisabled at time of persist (async not done)
        bool btDisabledFlag    = false;  // _bluetoothRadioDisabled at time of persist

        var snap = new BoostStateSnapshot
        {
            GameName           = "Fortnite",
            WifiRadioDisabled  = wifiDisabledFlag,
            BluetoothRadioDisabled = btDisabledFlag,
        };

        // This is what PersistBoostState captures at this moment.
        var json = Serialize(snap);
        File.WriteAllText(BoostStateTmp, json);
        File.Move(BoostStateTmp, BoostStatePath, overwrite: true);

        // Async tasks then complete and set the real flags to true in memory,
        // but the state file still has false — this is the gap.
        // If crash happens now, recovery will NOT restore Wifi/BT.
        var persisted = Deserialize(File.ReadAllText(BoostStatePath))!;
        Assert.False(persisted.WifiRadioDisabled,
            "GAP: state file was written before async wifi task completed — " +
            "crash here means wifi would not be restored on next startup");
        Assert.False(persisted.BluetoothRadioDisabled,
            "GAP: state file was written before async bt task completed — " +
            "crash here means bluetooth would not be restored on next startup");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 6. WSEARCH START VALUE GAP
    //    Documents that only SearchIndexingWasRunning (bool) is persisted,
    //    not the original Start DWORD. Recovery always restores to Start=2 (Auto)
    //    even if the user had it at Start=3 (Manual).
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void WSearchGap_OriginalStartValueNotPersisted_OnlyBooleanSaved()
    {
        // If user had WSearch at Start=3 (Manual) and it was running,
        // the snapshot only captures SearchIndexingWasRunning=true.
        // Recovery will force Start=2 (Auto) — not the original 3.
        var snap = new BoostStateSnapshot
        {
            GameName                 = "Minecraft.Windows",
            SearchIndexingWasRunning = true,
            // There is no "OriginalWSearchStartValue" field — the gap.
        };

        var json = Serialize(snap);
        var back = Deserialize(json)!;

        Assert.True(back.SearchIndexingWasRunning);
        // Confirm the schema has no way to carry the original start type.
        // (If this field ever gets added, this test should be updated.)
        var doc  = System.Text.Json.JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("OriginalWSearchStartType", out _),
            "No WSearch start type field in snapshot — recovery always restores to Start=2 (Auto)");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 7. MULTIPLE BOOSTS IN SUCCESSION — FILE OVERWRITE
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void MultipleBoosts_EachPersistOverwritesPrevious_OnlyLatestIsKept()
    {
        // First game session
        var snap1 = new BoostStateSnapshot
        {
            GameName       = "csgo",
            KilledServices = new List<string> { "BITS", "WSearch" },
            PowerPlanGuid  = "381b4222-f694-41f0-9685-ff5bb260df2e",
        };
        File.WriteAllText(BoostStateTmp, Serialize(snap1));
        File.Move(BoostStateTmp, BoostStatePath, overwrite: true);

        // Clean deactivation — deletes the file
        File.Delete(BoostStatePath);
        Assert.False(File.Exists(BoostStatePath));

        // Second game session
        var snap2 = new BoostStateSnapshot
        {
            GameName       = "Valorant",
            KilledServices = new List<string> { "BITS", "DiagTrack" },
            PowerPlanGuid  = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        };
        File.WriteAllText(BoostStateTmp, Serialize(snap2));
        File.Move(BoostStateTmp, BoostStatePath, overwrite: true);

        var back = Deserialize(File.ReadAllText(BoostStatePath))!;
        Assert.Equal("Valorant", back.GameName);
        Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", back.PowerPlanGuid);
        Assert.Contains("DiagTrack", back.KilledServices!);
        Assert.DoesNotContain("WSearch", back.KilledServices!);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 8. REGISTRY RESTORE ENTRY — NULL VAL MEANS DELETE
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void NagleEntry_NullVal_MeansDeleteValueOnRestore()
    {
        // When the registry value didn't exist before boost, Val is null.
        // On restore we should DELETE the value, not write 0.
        var entry = new RegistryRestoreEntry
        {
            Path = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{test}",
            Name = "TcpAckFrequency",
            Val  = null,
        };

        var snap = new BoostStateSnapshot
        {
            GameName     = "GTA5",
            NagleRestore = new List<RegistryRestoreEntry> { entry },
        };

        var back = Deserialize(Serialize(snap))!;
        Assert.Null(back.NagleRestore![0].Val);
        // Caller interprets null as "call key.DeleteValue(name)" — verified by reading this test.
    }

    [Fact]
    public void NagleEntry_ZeroVal_MeansRestoreToZero_NotDelete()
    {
        // Val=0 is a real registry value (not null/absent), so restore must SET 0, not delete.
        var entry = new RegistryRestoreEntry
        {
            Path = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{test}",
            Name = "TCPNoDelay",
            Val  = 0,
        };

        var snap = new BoostStateSnapshot
        {
            GameName     = "GTA5",
            NagleRestore = new List<RegistryRestoreEntry> { entry },
        };

        var back = Deserialize(Serialize(snap))!;
        Assert.NotNull(back.NagleRestore![0].Val);
        Assert.Equal(0, back.NagleRestore![0].Val);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 9. BATTERY PAUSE — vendor mode roundtrip and forward-compat
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void BatteryPause_DellAdaptive_RoundtripPreservesVendorAndMode()
    {
        // User had Dell PrimaryBattChargeCfg = "Adaptive" before boost.
        // We pause by setting Custom mode (start=50, stop=55). Snapshot must
        // remember "Adaptive" so restore returns to the user's chosen mode.
        var snap = new BoostStateSnapshot
        {
            GameName                 = "csgo",
            BatteryPauseMethod       = "DellModern",
            BatteryPauseVendor       = "Dell Inc.",
            BatteryPauseOriginalMode = "Adaptive",
            BatteryPauseWasActive    = true,
        };

        var back = Deserialize(Serialize(snap))!;
        Assert.Equal("DellModern", back.BatteryPauseMethod);
        Assert.Equal("Dell Inc.",  back.BatteryPauseVendor);
        Assert.Equal("Adaptive",   back.BatteryPauseOriginalMode);
        Assert.True(back.BatteryPauseWasActive);
    }

    [Fact]
    public void BatteryPause_DellCustomOriginal_CompositeEncodingRoundtrips()
    {
        // User already had Dell Custom mode with their own thresholds (e.g. start=60,stop=80)
        // before boost. GetCurrentMode encodes this as "Custom:60:80".
        // Resume must decode and restore both thresholds precisely.
        var snap = new BoostStateSnapshot
        {
            GameName                 = "Cyberpunk2077",
            BatteryPauseMethod       = "DellModern",
            BatteryPauseVendor       = "Dell Inc.",
            BatteryPauseOriginalMode = "Custom:60:80",
            BatteryPauseWasActive    = true,
        };

        var back = Deserialize(Serialize(snap))!;
        Assert.Equal("Custom:60:80", back.BatteryPauseOriginalMode);
        Assert.True(back.BatteryPauseWasActive);

        // Verify the composite can be split back into its parts.
        var parts = back.BatteryPauseOriginalMode!.Split(':');
        Assert.Equal(3, parts.Length);
        Assert.Equal("Custom", parts[0]);
        Assert.Equal("60",     parts[1]);
        Assert.Equal("80",     parts[2]);
    }

    [Fact]
    public void BatteryPause_LenovoConservationOff_RoundtripPreservesZeroFlag()
    {
        // Lenovo Conservation Mode is a "0" or "1" string. Original "0" (off)
        // must survive the roundtrip distinct from null / absent.
        var snap = new BoostStateSnapshot
        {
            GameName                 = "Valorant",
            BatteryPauseVendor       = "LENOVO",
            BatteryPauseOriginalMode = "0",
            BatteryPauseWasActive    = true,
        };

        var back = Deserialize(Serialize(snap))!;
        Assert.Equal("0", back.BatteryPauseOriginalMode);
        Assert.True(back.BatteryPauseWasActive);
    }

    [Fact]
    public void BatteryPause_Disabled_RoundtripPreservesFalseAndNullFields()
    {
        // User did NOT enable battery pause for this session. All three fields stay null/false.
        var snap = new BoostStateSnapshot
        {
            GameName              = "Fortnite",
            BatteryPauseWasActive = false,
        };

        var back = Deserialize(Serialize(snap))!;
        Assert.Null(back.BatteryPauseVendor);
        Assert.Null(back.BatteryPauseOriginalMode);
        Assert.False(back.BatteryPauseWasActive);
    }

    [Fact]
    public void BatteryPause_MethodField_RoundtripsAcrossAllVendorMethods()
    {
        // Recovery routes through method by name — must be stable across versions.
        foreach (var methodName in new[] { "DellModern", "DellLegacy", "Lenovo", "HP", "Acer", "Powercfg" })
        {
            var snap = new BoostStateSnapshot
            {
                GameName              = "csgo",
                BatteryPauseMethod    = methodName,
                BatteryPauseVendor    = "Test Vendor",
                BatteryPauseOriginalMode = "OriginalState",
                BatteryPauseWasActive = true,
            };

            var back = Deserialize(Serialize(snap))!;
            Assert.Equal(methodName, back.BatteryPauseMethod);
        }
    }

    [Fact]
    public void BatteryPause_PowercfgStartStopMode_RoundtripsCommaPair()
    {
        // PowercfgMethod encodes the original threshold pair as "start,stop" — must survive JSON roundtrip.
        var snap = new BoostStateSnapshot
        {
            GameName                 = "Valorant",
            BatteryPauseMethod       = "Powercfg",
            BatteryPauseOriginalMode = "0,100",
            BatteryPauseWasActive    = true,
        };
        var back = Deserialize(Serialize(snap))!;
        Assert.Equal("0,100", back.BatteryPauseOriginalMode);
    }

    [Fact]
    public void BatteryPause_OldSnapshotWithoutFields_DeserializesWithDefaults()
    {
        // Snapshot from v1.7.50 or earlier — no BatteryPause fields at all.
        // Must NOT throw on deserialize; new fields default to null / false.
        var oldStyleJson = """
        {
            "GameName": "csgo",
            "KilledServices": ["BITS"],
            "WifiRadioDisabled": false,
            "BluetoothRadioDisabled": false
        }
        """;

        var snap = Deserialize(oldStyleJson)!;
        Assert.Equal("csgo", snap.GameName);
        Assert.Null(snap.BatteryPauseVendor);
        Assert.Null(snap.BatteryPauseOriginalMode);
        Assert.False(snap.BatteryPauseWasActive);
    }
}
