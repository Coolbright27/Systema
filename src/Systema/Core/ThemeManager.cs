// ════════════════════════════════════════════════════════════════════════════
// ThemeManager.cs  ·  Follow Windows' light/dark mode and accent colour, live
// ════════════════════════════════════════════════════════════════════════════
//
// Like every Windows 11 app, Systema takes its look from Settings > Personalization > Colors:
//   • "Choose your mode" (apps)  →  Palette.Dark.xaml or Palette.Light.xaml
//   • the accent colour          →  the Accent* brushes, in the same shade WinUI uses
//                                    (Light2 of the accent palette in dark mode, Dark1 in light)
// and it changes over the moment the user changes them, no restart (MainWindow forwards the
// WM_SETTINGCHANGE "ImmersiveColorSet" / WM_DWMCOLORIZATIONCOLORCHANGED broadcasts to Refresh).
//
// How the swap works: App.xaml's first merged dictionary is the palette. Every style and page
// refers to palette brushes with DynamicResource, so replacing that one dictionary repaints the
// whole app. The palette is always built complete (accent included) BEFORE it goes in, so the UI
// never shows a half-applied theme.
// ════════════════════════════════════════════════════════════════════════════

using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Systema.Services;

namespace Systema.Core;

public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AccentKey      = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

    // WinUI's own defaults, for a PC with no accent palette in the registry.
    internal static readonly Color DefaultDarkAccent  = Color.FromRgb(0x60, 0xCD, 0xFF);
    internal static readonly Color DefaultLightAccent = Color.FromRgb(0x00, 0x5F, 0xB8);

    /// <summary>The accent tints the pages use, by key and alpha (see the palettes).</summary>
    internal static readonly (string Key, byte Alpha)[] AccentTints =
    {
        ("AccentAlpha09Brush", 0x17), ("AccentAlpha10Brush", 0x1A), ("AccentAlpha18Brush", 0x2E),
        ("AccentAlpha20Brush", 0x33), ("AccentAlpha30Brush", 0x4D), ("AccentAlpha40Brush", 0x66),
    };

    public static bool  IsLight { get; private set; }
    public static Color Accent  { get; private set; } = DefaultDarkAccent;

    /// <summary>Raised on the UI thread after the palette changed (mode or accent).</summary>
    public static event Action? Changed;

    private static bool _applied;

    /// <summary>Reads Windows' choice and applies it. UI thread, before the main window is built.</summary>
    public static void Initialize() => Apply(force: true);

    /// <summary>Re-reads after Windows reports a colour change; does nothing if nothing changed.</summary>
    public static void Refresh() => Apply(force: false);

    private static void Apply(bool force)
    {
        var app = Application.Current;
        if (app == null) return;
        try
        {
            bool  light  = ReadAppsUseLightTheme();
            Color accent = AccentFromPalette(ReadAccentPalette(), light) ?? (light ? DefaultLightAccent : DefaultDarkAccent);
            if (!force && _applied && light == IsLight && accent == Accent) return;

            var palette = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Resources/Themes/Palette.{(light ? "Light" : "Dark")}.xaml",
                                 UriKind.Absolute),
            };
            ApplyAccent(palette, accent);

            var dicts = app.Resources.MergedDictionaries;
            int slot = -1;
            for (int i = 0; i < dicts.Count; i++)
                if (dicts[i].Source?.OriginalString.Contains("Palette.", StringComparison.Ordinal) == true) { slot = i; break; }
            if (slot >= 0) dicts[slot] = palette; else dicts.Insert(0, palette);

            bool first = !_applied;
            IsLight = light;
            Accent  = accent;
            _applied = true;
            LoggerService.Instance.Info("ThemeManager",
                $"{(first ? "Theme" : "Theme changed")}: {(light ? "light" : "dark")}, accent #{accent.R:X2}{accent.G:X2}{accent.B:X2}");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            // Worst case the app keeps whatever palette it has (dark from App.xaml at startup).
            LoggerService.Instance.Warn("ThemeManager", $"Could not apply the Windows theme: {ex.Message}");
        }
    }

    /// <summary>Puts the accent and its tints into a palette before it's swapped in.</summary>
    internal static void ApplyAccent(ResourceDictionary palette, Color accent)
    {
        palette["AccentBlueColor"] = accent;
        palette["AccentBlueBrush"] = Frozen(accent);
        foreach (var (key, alpha) in AccentTints)
            palette[key] = Frozen(Color.FromArgb(alpha, accent.R, accent.G, accent.B));
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ── Standard windows' title bars ────────────────────────────────────────
    // A window with Windows' own title bar (the dialogs) gets a light one unless it asks for dark.
    // This keeps it matching the theme, live, for as long as the window is open.

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public static void FollowTheme(Window window)
    {
        void Apply()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                int dark = IsLight ? 0 : 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            }
            catch { /* cosmetic */ }
        }

        Action onChanged = Apply;
        window.SourceInitialized += (_, _) => Apply();
        Changed += onChanged;
        window.Closed += (_, _) => Changed -= onChanged;
        Apply();   // in case the window already has its handle
    }

    // ── Reading Windows' settings ───────────────────────────────────────────

    /// <summary>
    /// The accent shade WinUI uses for fills: Light2 in dark mode, Dark1 in light mode. The
    /// AccentPalette value is 8 RGBA entries: Light3, Light2, Light1, Base, Dark1, Dark2, Dark3, (spare).
    /// </summary>
    internal static Color? AccentFromPalette(byte[]? palette, bool light)
    {
        if (palette == null || palette.Length < 32) return null;
        int i = (light ? 4 : 1) * 4;
        return Color.FromRgb(palette[i], palette[i + 1], palette[i + 2]);
    }

    /// <summary>Apps mode from Settings. Windows' own default (value missing) is light.</summary>
    private static bool ReadAppsUseLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
    }

    private static byte[]? ReadAccentPalette()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AccentKey);
        return key?.GetValue("AccentPalette") as byte[];
    }
}
