// ════════════════════════════════════════════════════════════════════════════
// NvapiService.cs  ·  NVIDIA "Max Frame Rate" (FPS cap) via NVAPI DRS
// ════════════════════════════════════════════════════════════════════════════
//
// The NVIDIA frame-rate limiter is NOT a registry value like PowerMizer — it lives
// in the driver settings database (DRS), the same store the NVIDIA app / Control
// Panel writes to. The only supported way to read or write it is NVAPI. This wraps
// the base-profile FRL_FPS setting (0x10835002): 0 / absent = off, else the FPS cap.
//
// Everything is guarded: if nvapi64.dll is missing or init fails, IsAvailable()
// returns false and the UI hides the card. The struct marshaling below was verified
// against a live driver (read 61 → set 120 → read back 120 → restore 61) before
// shipping, which is why the version/size math is trusted.
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Runtime.InteropServices;
using Systema.Core;

namespace Systema.Services;

public class NvapiService
{
    private static readonly LoggerService _log = LoggerService.Instance;

    // nvapi64.dll exposes ONE entry point; every other function is fetched by numeric id.
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr NvAPI_QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Init_t();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Unload_t();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateSession_t(out IntPtr s);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DestroySession_t(IntPtr s);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LoadSettings_t(IntPtr s);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SaveSettings_t(IntPtr s);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetBaseProfile_t(IntPtr s, out IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSetting_t(IntPtr s, IntPtr p, uint id, ref NVDRS_SETTING setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetSetting_t(IntPtr s, IntPtr p, ref NVDRS_SETTING setting);

    private const uint ID_Init = 0x0150E828, ID_Unload = 0xD22BDD7E,
                       ID_CreateSession = 0x0694D52E, ID_DestroySession = 0xDAD9CFF8,
                       ID_LoadSettings = 0x375DBD6B, ID_SaveSettings = 0xFCBC7E14,
                       ID_GetBaseProfile = 0xDA8466A0, ID_GetSetting = 0x73BF8338,
                       ID_SetSetting = 0x577DD202;

    private const uint FRL_FPS_ID = 0x10835002;   // Frame Rate Limiter ("Max Frame Rate"); 0 = off
    private const uint NVDRS_DWORD_TYPE = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NVDRS_SETTING
    {
        public uint version;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2048)] public ushort[] settingName;
        public uint settingId;
        public uint settingType;
        public uint settingLocation;
        public uint isCurrentPredefined;
        public uint isPredefinedValid;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4100)] public byte[] predefinedValue;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4100)] public byte[] currentValue;
    }

    private static readonly uint SettingVersion =
        (uint)(Marshal.SizeOf<NVDRS_SETTING>() | (1 << 16));

    private static NVDRS_SETTING NewSetting() => new()
    {
        version = SettingVersion,
        settingName     = new ushort[2048],
        predefinedValue = new byte[4100],
        currentValue    = new byte[4100],
    };

    private static T Fn<T>(uint id) where T : Delegate
    {
        IntPtr p = NvAPI_QueryInterface(id);
        if (p == IntPtr.Zero) throw new InvalidOperationException($"NVAPI function {id:X8} unavailable");
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    /// <summary>True when NVAPI is present and initializes — i.e. an NVIDIA driver is installed.</summary>
    public bool IsAvailable()
    {
        try
        {
            if (Fn<Init_t>(ID_Init)() != 0) return false;
            try { return true; } finally { Fn<Unload_t>(ID_Unload)(); }
        }
        catch (Exception ex) { _log.Info("NvapiService", $"NVAPI not available: {ex.Message}"); return false; }
    }

    /// <summary>Current global Max Frame Rate in FPS. 0 = no cap / off / unavailable.</summary>
    public int GetMaxFrameRate()
    {
        try
        {
            if (Fn<Init_t>(ID_Init)() != 0) return 0;
            try
            {
                var create = Fn<CreateSession_t>(ID_CreateSession);
                var load   = Fn<LoadSettings_t>(ID_LoadSettings);
                var getPr  = Fn<GetBaseProfile_t>(ID_GetBaseProfile);
                var getSet = Fn<GetSetting_t>(ID_GetSetting);
                var destroy= Fn<DestroySession_t>(ID_DestroySession);

                if (create(out IntPtr session) != 0) return 0;
                try
                {
                    load(session);
                    if (getPr(session, out IntPtr profile) != 0) return 0;
                    var s = NewSetting();
                    int st = getSet(session, profile, FRL_FPS_ID, ref s);
                    if (st != 0) return 0;   // not found = no cap
                    return (int)BitConverter.ToUInt32(s.currentValue, 0);
                }
                finally { destroy(session); }
            }
            finally { Fn<Unload_t>(ID_Unload)(); }
        }
        catch (Exception ex) { _log.Warn("NvapiService", $"GetMaxFrameRate failed: {ex.Message}"); return 0; }
    }

    /// <summary>Sets the global Max Frame Rate. fps &lt;= 0 removes the cap entirely (driver default).
    /// Persists to the DRS base profile — the same place the NVIDIA app writes the global setting.</summary>
    public TweakResult SetMaxFrameRate(int fps)
    {
        try
        {
            if (Fn<Init_t>(ID_Init)() != 0)
                return TweakResult.Fail("NVIDIA driver interface (NVAPI) is unavailable on this PC.");
            try
            {
                var create = Fn<CreateSession_t>(ID_CreateSession);
                var load   = Fn<LoadSettings_t>(ID_LoadSettings);
                var getPr  = Fn<GetBaseProfile_t>(ID_GetBaseProfile);
                var setSet = Fn<SetSetting_t>(ID_SetSetting);
                var save   = Fn<SaveSettings_t>(ID_SaveSettings);
                var destroy= Fn<DestroySession_t>(ID_DestroySession);

                if (create(out IntPtr session) != 0)
                    return TweakResult.Fail("Could not open the NVIDIA settings session.");
                try
                {
                    load(session);
                    if (getPr(session, out IntPtr profile) != 0)
                        return TweakResult.Fail("Could not read the NVIDIA base profile.");

                    // "Remove the limit" (Reset / 0) is FRL_FPS = 0 — the frame limiter's own OFF value,
                    // verified to read back as 0. We deliberately do NOT delete the setting: deleting the
                    // profile setting reverts the base profile to a predefined value instead of clearing
                    // the cap, which is why Reset appeared to "go back to the old number."
                    uint value = fps <= 0 ? 0u : (uint)fps;
                    var s = NewSetting();
                    s.settingId   = FRL_FPS_ID;
                    s.settingType = NVDRS_DWORD_TYPE;
                    BitConverter.GetBytes(value).CopyTo(s.currentValue, 0);
                    int st = setSet(session, profile, ref s);
                    if (st != 0) return TweakResult.Fail($"NVIDIA rejected the setting (code {st}).");

                    st = save(session);
                    if (st != 0) return TweakResult.Fail($"NVIDIA could not save the setting (code {st}).");

                    _log.Info("NvapiService", fps <= 0 ? "Max Frame Rate cleared (no cap)" : $"Max Frame Rate set to {fps} FPS");
                    return TweakResult.Ok(fps <= 0
                        ? "FPS cap removed. NVIDIA is back to no frame limit."
                        : $"FPS cap set to {fps} FPS.");
                }
                finally { destroy(session); }
            }
            finally { Fn<Unload_t>(ID_Unload)(); }
        }
        catch (Exception ex)
        {
            _log.Error("NvapiService", "SetMaxFrameRate failed", ex);
            return TweakResult.FromException(ex);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Power management mode  (PREFERRED_PSTATE)
    // ════════════════════════════════════════════════════════════════════════
    //
    // This is the setting behind "Power Management Mode" in the NVIDIA app's global settings —
    // a DRS base-profile value, exactly like Max Frame Rate above, NOT the PowerMizer registry
    // values in NvidiaGpuService. It applies IMMEDIATELY, with no restart, which is what makes a
    // separate on-battery choice workable: Systema can swap the value on the power-source change.
    private const uint PREFERRED_PSTATE_ID = 0x1057EB71;

    /// <summary>
    /// NVAPI PREFERRED_PSTATE values. Confirmed against the NVIDIA app on a T1200:
    /// 0 really is Adaptive (it was mislabelled "Optimal power" in 0.7.284), 2 is the driver
    /// default the app calls "Normal", and 1 is Prefer maximum performance.
    /// </summary>
    // NVIDIA's PREFERRED_PSTATE enum, matched against the NVIDIA app's own dropdown on a T1200.
    // The app lists exactly five of these — every value below EXCEPT 4 (Prefer minimum power),
    // which NVIDIA defines but does not expose in its UI.
    public const uint PStateAdaptive       = 0;   // "Adaptive"
    public const uint PStateMaxPerf        = 1;   // "Prefer maximum performance"
    public const uint PStateDriverManaged  = 2;   // "NVIDIA driver-controlled (Default)"
    public const uint PStateConsistentPerf = 3;   // "Prefer consistent performance"
    public const uint PStateOptimalPower   = 5;   // "Optimal power"

    // ── NVAPI CANNOT tell you which modes a GPU offers. Proven, don't retry it. ──
    //
    // Probed against a live driver (T1200, 2026-08-11/12):
    //
    //   NvAPI_DRS_GetSettingNameFromId(0x1057EB71)   → "Power management mode"   (id is valid)
    //   NvAPI_DRS_EnumAvailableSettingIds            → rc 0, 125 settings, ours among them
    //                                                  (the session is healthy)
    //   NvAPI_DRS_EnumAvailableSettingValues         → -160 for EVERY setting tried:
    //         Power management mode, Frame Rate Limiter, Vertical Sync, and two others.
    //         Not specific to this setting — the call cannot retrieve value lists here at all.
    //   NvAPI_DRS_SetSetting with values 0..8        → ALL accepted, rc 0, each reading back
    //         verbatim, including 7 and 8 which NVIDIA does not define. No validation exists to
    //         infer support from either.
    //
    // Conclusion: the NVIDIA app builds its dropdown from its own resources, not from the driver.
    // There is no query to write. The UI list must come from modes actually OBSERVED in the
    // NVIDIA app — see AllPowerModes in NvidiaGpuViewModel.
    //
    // An earlier version of this comment claimed the list was identical on every NVIDIA GPU and
    // therefore needed no filtering. That was wrong: it was reasoning from a failed probe rather
    // than from what the app displays.

    // ── GPU architecture — decides how many power modes NVIDIA offers ────────
    //
    // Found by reading the NVIDIA app's own localisation file
    // (NVIDIA app\www\assets\i18n\en_US.json), which carries THREE variants of the power-mode
    // help text and gives away how NVIDIA gates the dropdown:
    //
    //   typicalUsageScenarios            → Adaptive, Prefer maximum performance
    //   typicalUsageScenariosTuring      → + NVIDIA driver-controlled, Prefer consistent performance
    //   typicalUsageScenariosQuadroMode  → the same four, Quadro wording
    //
    // So the extra two modes are Turing-and-newer. Architecture is read straight from the driver
    // rather than parsed out of the card's name, which would misfile rebadged and mobile parts.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumPhysicalGPUs_t([Out] IntPtr[] handles, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetArchInfo_t(IntPtr gpu, IntPtr info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDriverVersion_t(out uint version, IntPtr branch);

    private const uint ID_EnumPhysicalGPUs = 0xE5AC921F,
                       ID_GetArchInfo      = 0xD8265D24,
                       ID_GetDriverVersion = 0x2926AAAD;

    /// <summary>Turing (TU1xx). Anything at or above this gets NVIDIA's full mode list.</summary>
    public const uint ArchTuring = 0x160;

    /// <summary>
    /// The primary GPU's architecture id (Maxwell 0x110, Pascal 0x130, Volta 0x140, Turing 0x160,
    /// Ampere 0x170, Ada 0x190, …), or 0 when it can't be read. Verified on a T1200 → 0x160.
    /// </summary>
    public uint GetGpuArchitecture()
    {
        try
        {
            if (Fn<Init_t>(ID_Init)() != 0) return 0;
            try
            {
                var enumGpus = Fn<EnumPhysicalGPUs_t>(ID_EnumPhysicalGPUs);
                var archInfo = Fn<GetArchInfo_t>(ID_GetArchInfo);

                var gpus = new IntPtr[64];
                if (enumGpus(gpus, out int count) != 0 || count <= 0) return 0;

                IntPtr b = Marshal.AllocHGlobal(16);   // version, architecture, implementation, revision
                try
                {
                    for (int k = 0; k < 16; k += 4) Marshal.WriteInt32(b, k, 0);
                    Marshal.WriteInt32(b, 0, 16 | (2 << 16));
                    if (archInfo(gpus[0], b) != 0) return 0;
                    return unchecked((uint)Marshal.ReadInt32(b, 4));
                }
                finally { Marshal.FreeHGlobal(b); }
            }
            finally { Fn<Unload_t>(ID_Unload)(); }
        }
        catch (Exception ex) { _log.Warn("NvapiService", $"GetGpuArchitecture failed: {ex.Message}"); return 0; }
    }

    /// <summary>Driver version as NVIDIA reports it (e.g. 56312 = 563.12), or 0.</summary>
    public uint GetDriverVersion()
    {
        try
        {
            if (Fn<Init_t>(ID_Init)() != 0) return 0;
            try
            {
                var getVer = Fn<GetDriverVersion_t>(ID_GetDriverVersion);
                IntPtr branch = Marshal.AllocHGlobal(64);
                try { return getVer(out uint v, branch) == 0 ? v : 0; }
                finally { Marshal.FreeHGlobal(branch); }
            }
            finally { Fn<Unload_t>(ID_Unload)(); }
        }
        catch { return 0; }
    }

    /// <summary>
    /// The mode currently in the base profile, or the driver default when the setting has
    /// never been written (which is what the driver falls back to anyway).
    /// </summary>
    public uint GetPowerMode() => GetPowerMode(out _);

    /// <summary>
    /// As above, but <paramref name="present"/> reports whether the value came from the driver or
    /// is our fallback. Callers must not treat a fallback as evidence of anything — doing so let a
    /// default re-add a mode the GPU-architecture filter had just excluded.
    /// </summary>
    public uint GetPowerMode(out bool present)
    {
        present = false;
        try
        {
            if (Fn<Init_t>(ID_Init)() != 0) return PStateOptimalPower;
            try
            {
                var create  = Fn<CreateSession_t>(ID_CreateSession);
                var load    = Fn<LoadSettings_t>(ID_LoadSettings);
                var getPr   = Fn<GetBaseProfile_t>(ID_GetBaseProfile);
                var getSet  = Fn<GetSetting_t>(ID_GetSetting);
                var destroy = Fn<DestroySession_t>(ID_DestroySession);

                if (create(out IntPtr session) != 0) return PStateOptimalPower;
                try
                {
                    load(session);
                    if (getPr(session, out IntPtr profile) != 0) return PStateOptimalPower;

                    var s = NewSetting();
                    int st = getSet(session, profile, PREFERRED_PSTATE_ID, ref s);
                    if (st != 0)
                    {
                        // Not written to the base profile — the card is on NVIDIA's own default,
                        // which is Optimal power. Logged so a machine that clearly HAS a mode
                        // chosen in the NVIDIA app but reads as unset is diagnosable.
                        _log.Info("NvapiService",
                                  $"PREFERRED_PSTATE not set in base profile (status {st}) — assuming Optimal power (NVIDIA's default)");
                        return PStateOptimalPower;
                    }

                    uint v = BitConverter.ToUInt32(s.currentValue, 0);
                    present = true;
                    _log.Info("NvapiService",
                              $"PREFERRED_PSTATE read: {v} (predefined={s.isCurrentPredefined})");
                    return v;
                }
                finally { destroy(session); }
            }
            finally { Fn<Unload_t>(ID_Unload)(); }
        }
        catch (Exception ex)
        {
            _log.Warn("NvapiService", $"GetPowerMode failed: {ex.Message}");
            return PStateOptimalPower;
        }
    }

    /// <summary>Sets the global power management mode. Takes effect immediately — no restart.</summary>
    public TweakResult SetPowerMode(uint pstate)
    {
        try
        {
            if (Fn<Init_t>(ID_Init)() != 0)
                return TweakResult.Fail("NVIDIA driver interface (NVAPI) is unavailable on this PC.");
            try
            {
                var create  = Fn<CreateSession_t>(ID_CreateSession);
                var load    = Fn<LoadSettings_t>(ID_LoadSettings);
                var getPr   = Fn<GetBaseProfile_t>(ID_GetBaseProfile);
                var setSet  = Fn<SetSetting_t>(ID_SetSetting);
                var save    = Fn<SaveSettings_t>(ID_SaveSettings);
                var destroy = Fn<DestroySession_t>(ID_DestroySession);

                if (create(out IntPtr session) != 0)
                    return TweakResult.Fail("Could not open the NVIDIA settings session.");
                try
                {
                    load(session);
                    if (getPr(session, out IntPtr profile) != 0)
                        return TweakResult.Fail("Could not read the NVIDIA base profile.");

                    var s = NewSetting();
                    s.settingId   = PREFERRED_PSTATE_ID;
                    s.settingType = NVDRS_DWORD_TYPE;
                    BitConverter.GetBytes(pstate).CopyTo(s.currentValue, 0);

                    int st = setSet(session, profile, ref s);
                    if (st != 0) return TweakResult.Fail($"NVIDIA rejected the setting (code {st}).");

                    st = save(session);
                    if (st != 0) return TweakResult.Fail($"NVIDIA could not save the setting (code {st}).");

                    _log.Info("NvapiService", $"Power management mode set to pstate {pstate}");
                    return TweakResult.Ok("Power management mode applied.");
                }
                finally { destroy(session); }
            }
            finally { Fn<Unload_t>(ID_Unload)(); }
        }
        catch (Exception ex)
        {
            _log.Error("NvapiService", "SetPowerMode failed", ex);
            return TweakResult.FromException(ex);
        }
    }

    // ── Refresh-rate helpers ──────────────────────────────────────────────────
    // Used both by the Nvidia tab's "Monitor refresh rate" button and by the
    // "Cap FPS to monitor refresh rate" recommendation, so the two always agree.

    /// <summary>The primary monitor's refresh rate snapped to a clean FPS cap target
    /// (nearest multiple of 5, clamped 20–999) — e.g. a 59 Hz panel → 60. 0 if the
    /// refresh rate can't be read.</summary>
    public static int GetRefreshRateFpsTarget()
    {
        int hz = GetPrimaryRefreshHz();
        return hz <= 0 ? 0 : Math.Clamp((int)(Math.Round(hz / 5.0) * 5), 20, 999);
    }

    /// <summary>Primary monitor's current refresh rate in Hz (0 if unreadable).</summary>
    public static int GetPrimaryRefreshHz()
    {
        try
        {
            var dm = new DEVMODE();
            dm.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
            return EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm) ? (int)dm.dmDisplayFrequency : 0;
        }
        catch { return 0; }
    }

    private const int ENUM_CURRENT_SETTINGS = -1;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint   dmFields;
        public int    dmPositionX, dmPositionY;
        public uint   dmDisplayOrientation, dmDisplayFixedOutput;
        public short  dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint   dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint   dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
}
