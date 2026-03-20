// ════════════════════════════════════════════════════════════════════════════
// AnimationService.cs  ·  Granular Windows visual effect toggles via registry
// ════════════════════════════════════════════════════════════════════════════
//
// Reads and writes individual visual effect flags (animations, shadows, smooth
// scroll, etc.) under HKCU\Control Panel\Desktop and related keys. After any
// change, broadcasts WM_SETTINGCHANGE via P/Invoke SendMessageTimeout so the
// shell picks up the new settings without a logoff.
//
// RELATED FILES
//   VisualViewModel.cs      — exposes per-effect toggles to the Visual tab
//   HealthScoreService.cs   — reads visual effect state for the Security sub-score
// ════════════════════════════════════════════════════════════════════════════

using System.Runtime.InteropServices;
using Microsoft.Win32;
using Systema.Core;

namespace Systema.Services;

/// <summary>
/// Controls individual Windows visual effect settings via registry.
/// Each tweak targets a specific key/value for granular control.
/// After writing, broadcasts WM_SETTINGCHANGE so the OS picks up changes immediately.
/// </summary>
public class AnimationService
{
    private static readonly LoggerService Log = LoggerService.Instance;

    // ── P/Invoke for immediate OS notification ──────────────────────────────
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref ANIMATIONINFO pvParam, uint fWinIni);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [StructLayout(LayoutKind.Sequential)]
    private struct ANIMATIONINFO
    {
        public uint cbSize;
        public int  iMinAnimate;
    }

    private const uint SPI_SETANIMATION           = 0x0049;
    private const uint SPI_SETCLIENTAREAANIMATION  = 0x1043;
    private const uint SPI_SETDRAGFULLWINDOWS      = 0x0025;
    private const uint SPI_SETFONTSMOOTHING        = 0x004B;
    private const uint SPI_SETCURSORSHADOW         = 0x101B;
    private const uint SPI_SETDROPSHADOW           = 0x1025;
    private const uint SPI_SETTOOLTIPANIMATION     = 0x1017;
    private const uint SPI_SETMENUANIMATION        = 0x1003;
    private const uint SPI_SETCOMBOBOXANIMATION     = 0x1005;
    private const uint SPI_SETLISTBOXSMOOTHSCROLLING = 0x1007;
    private const uint SPIF_UPDATEINIFILE          = 0x01;
    private const uint SPIF_SENDCHANGE             = 0x02;
    private const uint WM_SETTINGCHANGE            = 0x001A;
    private static readonly IntPtr HWND_BROADCAST  = new IntPtr(0xFFFF);

    // ── Registry paths ─────────────────────────────────────────────────────────
    private const string DesktopKey        = @"Control Panel\Desktop";
    private const string WindowMetricsKey  = @"Control Panel\Desktop\WindowMetrics";
    private const string ExplorerAdvKey    = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string VisualFxKey       = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";
    private const string DwmKey            = @"SOFTWARE\Microsoft\Windows\DWM";

    // ── Broadcast helper ────────────────────────────────────────────────────────
    private static void BroadcastSettingChange()
    {
        try
        {
            SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "WindowMetrics",
                0x0002 /*SMTO_ABORTIFHUNG*/, 1000, out _);
        }
        catch { }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ORIGINAL 5 PROPERTIES (with SystemParametersInfo calls added)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Animate controls/elements inside windows (ListviewAlphaSelect, MenuShowDelay).</summary>
    public bool AnimateControlsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ExplorerAdvKey);
                return (int)(key?.GetValue("ListviewAlphaSelect") ?? 1) != 0;
            }
            catch { return true; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvKey, true);
                key.SetValue("ListviewAlphaSelect", value ? 1 : 0, RegistryValueKind.DWord);
                using var desk = Registry.CurrentUser.CreateSubKey(DesktopKey, true);
                desk.SetValue("MenuShowDelay", value ? "400" : "0", RegistryValueKind.String);
                SystemParametersInfo(SPI_SETCLIENTAREAANIMATION, 0, (IntPtr)(value ? 1 : 0), SPIF_SENDCHANGE);
                BroadcastSettingChange();
                Log.Info("AnimationService", $"AnimateControls = {value}");
            }
            catch (Exception ex) { Log.Warn("AnimationService", "AnimateControls set failed", ex); }
        }
    }

    /// <summary>Animate windows when minimizing/maximizing (MinAnimate).</summary>
    public bool AnimateWindowsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(WindowMetricsKey);
                return (key?.GetValue("MinAnimate")?.ToString() ?? "1") != "0";
            }
            catch { return true; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(WindowMetricsKey, true);
                key.SetValue("MinAnimate", value ? "1" : "0", RegistryValueKind.String);
                using var expl = Registry.CurrentUser.CreateSubKey(ExplorerAdvKey, true);
                expl.SetValue("TaskbarAnimations", value ? 1 : 0, RegistryValueKind.DWord);
                var ai = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>(), iMinAnimate = value ? 1 : 0 };
                SystemParametersInfo(SPI_SETANIMATION, ai.cbSize, ref ai, SPIF_SENDCHANGE);
                BroadcastSettingChange();
                Log.Info("AnimationService", $"AnimateWindows = {value}");
            }
            catch (Exception ex) { Log.Warn("AnimationService", "AnimateWindows set failed", ex); }
        }
    }

    /// <summary>Fade/slide menus into view (UserPreferencesMask bit for menu fades).</summary>
    public bool FadeMenusEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(DesktopKey);
                var mask = key?.GetValue("UserPreferencesMask") as byte[];
                if (mask == null || mask.Length < 1) return true;
                return (mask[0] & 0x02) != 0; // bit 1 = menu fade
            }
            catch { return true; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(DesktopKey, true);
                var mask = key.GetValue("UserPreferencesMask") as byte[] ?? GetDefaultMask();
                if (value)
                    mask[0] |= 0x02;
                else
                    mask[0] &= unchecked((byte)~0x02);
                key.SetValue("UserPreferencesMask", mask, RegistryValueKind.Binary);
                SystemParametersInfo(SPI_SETMENUANIMATION, 0, (IntPtr)(value ? 1 : 0), SPIF_SENDCHANGE);
                BroadcastSettingChange();
                Log.Info("AnimationService", $"FadeMenus = {value}");
            }
            catch (Exception ex) { Log.Warn("AnimationService", "FadeMenus set failed", ex); }
        }
    }

    /// <summary>Show window contents while dragging (DragFullWindows).</summary>
    public bool ShowWindowContentsWhileDraggingEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(DesktopKey);
                return (key?.GetValue("DragFullWindows")?.ToString() ?? "1") != "0";
            }
            catch { return true; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(DesktopKey, true);
                key.SetValue("DragFullWindows", value ? "1" : "0", RegistryValueKind.String);
                SystemParametersInfo(SPI_SETDRAGFULLWINDOWS, (uint)(value ? 1 : 0), IntPtr.Zero, SPIF_SENDCHANGE);
                Log.Info("AnimationService", $"DragFullWindows = {value}");
            }
            catch (Exception ex) { Log.Warn("AnimationService", "DragFullWindows set failed", ex); }
        }
    }

    /// <summary>Smooth edges of screen fonts (ClearType / FontSmoothing).</summary>
    public bool SmoothFontsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(DesktopKey);
                return (key?.GetValue("FontSmoothing")?.ToString() ?? "2") != "0";
            }
            catch { return true; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(DesktopKey, true);
                key.SetValue("FontSmoothing", value ? "2" : "0", RegistryValueKind.String);
                key.SetValue("FontSmoothingType", value ? 2 : 0, RegistryValueKind.DWord);
                SystemParametersInfo(SPI_SETFONTSMOOTHING, (uint)(value ? 1 : 0), IntPtr.Zero, SPIF_SENDCHANGE);
                Log.Info("AnimationService", $"SmoothFonts = {value}");
            }
            catch (Exception ex) { Log.Warn("AnimationService", "SmoothFonts set failed", ex); }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  10 NEW ANIMATION PROPERTIES
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Fade or slide tooltips into view.</summary>
    public bool TooltipAnimationEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ExplorerAdvKey);
                var val = key?.GetValue("TooltipAnimation");
                return val == null || (int)val != 0;
            }
            catch { return true; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvKey, true);
                key.SetValue("TooltipAnimation", value ? 1 : 0, RegistryValueKind.DWord);
                SystemParametersInfo(SPI_SETTOOLTIPANIMATION, 0, (IntPtr)(value ? 1 : 0), SPIF_SENDCHANGE);
                BroadcastSettingChange();
                Log.Info("AnimationService", $"TooltipAnimation = {value}");
            }
            catch (Exception ex) { Log.Warn("AnimationService", "TooltipAnimation set failed", ex); }
        }
    }

    /// <summary>Fade out menu items after clicking.</summary>
    public bool FadeOutMenuItemsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ExplorerAdvKey);
                var val = key?.GetValue("SelectionFade");
                return val == null || (int)val != 0;
            }
            catch { return true; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvKey, true);
                key.SetValue("SelectionFade", value ? 1 : 0, RegistryValueKind.DWord);
                BroadcastSettingChange();
                Log.Info("AnimationService", $"FadeOutMenuItems = {value}");
            }
            catch (Exception ex) { Log.Warn("AnimationService", "FadeOutMenuItems set failed", ex); }
        }
    }

    /// <summary>Show shadows under mouse pointer.</summary>
    public bool CursorShadowEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ExplorerAdvKey);
                var val = key?.GetValue("CursorShadow");
                return val == null || (int)val != 0;
            }
            catch { return true; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvKey, true);
                key.SetValue("CursorShadow", value ? 1 : 0, RegistryValueKind.DWord);
                SystemParametersInfo(SPI_SETCURSORSHADOW, 0, (IntPtr)(value ? 1 : 0), SPIF_SENDCHANGE);
                BroadcastSettingChange();
                Log.Info("AnimationService", $"CursorShadow = {value}");
            }
            catch (Exception ex) { Log.Warn("AnimationService", "CursorShadow set failed", ex); }
        }
    }

    /// <summary>Show shadows under windows.</summary>
    public bool WindowShadowEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ExplorerAdvKey);
                var val = key?.GetValue("ListviewShadow");
                return val == null || (int)val != 0;
            }
            catch { return true; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvKey, true);
                key.SetValue("ListviewShadow", value ? 1 : 0, RegistryValueKind.DWord);
                SystemParametersInfo(SPI_SETDROPSHADOW, 0, (IntPtr)(value ? 1 : 0), SPIF_SENDCHANGE);
                BroadcastSettingChange();
                Log.Info("AnimationService", $"WindowShadow = {value}");
            }
            catch (Exception ex) { Log.Warn("AnimationService", "WindowShadow set failed", ex); }
        }
    }

    /// <summary>Slide open combo boxes.</summary>
    public bool ComboBoxAnimationEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ExplorerAdvKey);
                var val = key?.GetValue("ComboBoxAnimation");
                return val == null || (int)val != 0;
            }
            catch { return true; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvKey, true);
                key.SetValue("ComboBoxAnimation", value ? 1 : 0, RegistryValueKind.DWord);
                SystemParametersInfo(SPI_SETCOMBOBOXANIMATION, 0, (IntPtr)(value ? 1 : 0), SPIF_SENDCHANGE);
                BroadcastSettingChange();
                Log.Info("AnimationService", $"ComboBoxAnimation = {value}");
            }
            catch (Exception ex) { Log.Warn("AnimationService", "ComboBoxAnimation set failed", ex); }
        }
    }

    /// <summary>Smooth-scroll list boxes.</summary>
    public bool ListboxSmoothScrollingEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ExplorerAdvKey);
                var val = key?.GetValue("ListboxSmoothScrolling");
                return val == null || (int)val != 0;
            }
            catch { return true; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvKey, true);
                key.SetValue("ListboxSmoothScrolling", value ? 1 : 0, RegistryValueKind.DWord);
                SystemParametersInfo(SPI_SETLISTBOXSMOOTHSCROLLING, 0, (IntPtr)(value ? 1 : 0), SPIF_SENDCHANGE);
                BroadcastSettingChange();
                Log.Info("AnimationService", $"ListboxSmoothScrolling = {value}");
            }
            catch (Exception ex) { Log.Warn("AnimationService", "ListboxSmoothScrolling set failed", ex); }
        }
    }

    /// <summary>Use a background image for each folder type.</summary>
    public bool ListviewWatermarkEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ExplorerAdvKey);
                var val = key?.GetValue("ListviewWatermark");
                return val == null || (int)val != 0;
            }
            catch { return true; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvKey, true);
                key.SetValue("ListviewWatermark", value ? 1 : 0, RegistryValueKind.DWord);
                BroadcastSettingChange();
                Log.Info("AnimationService", $"ListviewWatermark = {value}");
            }
            catch (Exception ex) { Log.Warn("AnimationService", "ListviewWatermark set failed", ex); }
        }
    }

    /// <summary>Use drop shadows for icon labels on the desktop.</summary>
    public bool IconLabelShadowEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ExplorerAdvKey);
                var val = key?.GetValue("IconsOnly");
                // IconsOnly = 0 means shadows enabled (confusing Windows naming)
                return val == null || (int)val == 0;
            }
            catch { return true; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvKey, true);
                key.SetValue("IconsOnly", value ? 0 : 1, RegistryValueKind.DWord);
                BroadcastSettingChange();
                Log.Info("AnimationService", $"IconLabelShadow = {value}");
            }
            catch (Exception ex) { Log.Warn("AnimationService", "IconLabelShadow set failed", ex); }
        }
    }

    /// <summary>Enable Peek (preview desktop on taskbar hover).</summary>
    public bool AeroPeekEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ExplorerAdvKey);
                var val = key?.GetValue("EnableAeroPeek");
                return val == null || (int)val != 0;
            }
            catch { return true; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvKey, true);
                key.SetValue("EnableAeroPeek", value ? 1 : 0, RegistryValueKind.DWord);
                BroadcastSettingChange();
                Log.Info("AnimationService", $"AeroPeek = {value}");
            }
            catch (Exception ex) { Log.Warn("AnimationService", "AeroPeek set failed", ex); }
        }
    }

    /// <summary>Save taskbar thumbnail previews.</summary>
    public bool TaskbarThumbnailPreviewsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ExplorerAdvKey);
                var val = key?.GetValue("ExtendedUIHoverTime");
                // Default (null or 400) = enabled; large value (30000) = effectively disabled
                if (val == null) return true;
                return (int)val <= 1000;
            }
            catch { return true; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvKey, true);
                key.SetValue("ExtendedUIHoverTime", value ? 400 : 30000, RegistryValueKind.DWord);
                BroadcastSettingChange();
                Log.Info("AnimationService", $"TaskbarThumbnailPreviews = {value}");
            }
            catch (Exception ex) { Log.Warn("AnimationService", "TaskbarThumbnailPreviews set failed", ex); }
        }
    }

    // ── Preset methods ────────────────────────────────────────────────────────

    /// <summary>Preset: disable ALL animations for maximum speed.</summary>
    public TweakResult ApplyNoAnimations()
    {
        Log.Info("AnimationService", "Applying preset: No Animations");
        try
        {
            AnimateControlsEnabled                 = false;
            AnimateWindowsEnabled                  = false;
            FadeMenusEnabled                       = false;
            ShowWindowContentsWhileDraggingEnabled = false;
            SmoothFontsEnabled                     = false;
            TooltipAnimationEnabled                = false;
            FadeOutMenuItemsEnabled                = false;
            CursorShadowEnabled                    = false;
            WindowShadowEnabled                    = false;
            ComboBoxAnimationEnabled               = false;
            ListboxSmoothScrollingEnabled          = false;
            ListviewWatermarkEnabled               = false;
            IconLabelShadowEnabled                 = false;
            AeroPeekEnabled                        = false;
            TaskbarThumbnailPreviewsEnabled        = false;

            SetVisualFxMode(2); // Custom
            return TweakResult.Ok("All animations disabled.");
        }
        catch (Exception ex) { return TweakResult.FromException(ex); }
    }

    /// <summary>Preset: keep only what helps usability (animate controls, font smoothing + drag).</summary>
    public TweakResult ApplyOptimizeForSpeed()
    {
        Log.Info("AnimationService", "Applying preset: Optimize For Speed");
        try
        {
            AnimateControlsEnabled                 = true;
            AnimateWindowsEnabled                  = false;
            FadeMenusEnabled                       = false;
            ShowWindowContentsWhileDraggingEnabled = true;
            SmoothFontsEnabled                     = true;
            TooltipAnimationEnabled                = false;
            FadeOutMenuItemsEnabled                = false;
            CursorShadowEnabled                    = false;
            WindowShadowEnabled                    = false;
            ComboBoxAnimationEnabled               = false;
            ListboxSmoothScrollingEnabled          = false;
            ListviewWatermarkEnabled               = false;
            IconLabelShadowEnabled                 = true;
            AeroPeekEnabled                        = true;
            TaskbarThumbnailPreviewsEnabled        = true;

            SetVisualFxMode(2);
            return TweakResult.Ok("Optimized for speed. Animate controls, font smoothing, and drag content preserved.");
        }
        catch (Exception ex) { return TweakResult.FromException(ex); }
    }

    /// <summary>Preset: restore all Windows defaults.</summary>
    public TweakResult ApplyWindowsDefault()
    {
        Log.Info("AnimationService", "Applying preset: Windows Default");
        try
        {
            AnimateControlsEnabled                 = true;
            AnimateWindowsEnabled                  = true;
            FadeMenusEnabled                       = true;
            ShowWindowContentsWhileDraggingEnabled = true;
            SmoothFontsEnabled                     = true;
            TooltipAnimationEnabled                = true;
            FadeOutMenuItemsEnabled                = true;
            CursorShadowEnabled                    = true;
            WindowShadowEnabled                    = true;
            ComboBoxAnimationEnabled               = true;
            ListboxSmoothScrollingEnabled          = true;
            ListviewWatermarkEnabled               = true;
            IconLabelShadowEnabled                 = true;
            AeroPeekEnabled                        = true;
            TaskbarThumbnailPreviewsEnabled        = true;

            SetVisualFxMode(0); // Let Windows decide
            return TweakResult.Ok("All animations restored to Windows defaults.");
        }
        catch (Exception ex) { return TweakResult.FromException(ex); }
    }

    // ── Backward-compat helpers (used by existing code) ──────────────────────

    /// <summary>Legacy: disable all animations (same as ApplyOptimizeForSpeed).</summary>
    public TweakResult DisableAnimations() => ApplyOptimizeForSpeed();

    /// <summary>Legacy: restore all animations (same as ApplyWindowsDefault).</summary>
    public TweakResult RestoreAnimations() => ApplyWindowsDefault();

    /// <summary>Legacy: are animations globally disabled?</summary>
    public bool AreAnimationsDisabled() => !AnimateWindowsEnabled && !AnimateControlsEnabled;

    // ── Private helpers ───────────────────────────────────────────────────────

    private static void SetVisualFxMode(int mode)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(VisualFxKey, true);
            key.SetValue("VisualFXSetting", mode, RegistryValueKind.DWord);
        }
        catch { }
    }

    private static byte[] GetDefaultMask()
        => new byte[] { 0x9E, 0x1E, 0x07, 0x80, 0x12, 0x00, 0x00, 0x00 };
}
