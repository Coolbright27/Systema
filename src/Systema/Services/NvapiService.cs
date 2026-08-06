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
