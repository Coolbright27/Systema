using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Systema.Core;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// Systema follows Windows light/dark mode and the accent colour, live, like Windows' own apps
/// (0.7.354). Colours come from two palettes with the same keys; everything refers to them with
/// DynamicResource so swapping the palette repaints the app.
/// </summary>
public class ThemeTests
{
    private static string Root()
    {
        string dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "src", "Systema"))) dir = Directory.GetParent(dir)!.FullName;
        return Path.Combine(dir, "src", "Systema");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(Root(), Path.Combine(parts)));

    private static HashSet<string> Keys(string xaml) =>
        Regex.Matches(xaml, "x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();

    // The default Windows blue, as Windows stores it (Light3 … Dark3 + a spare, RGBA each).
    private static readonly byte[] DefaultBlue =
    {
        0x99, 0xEB, 0xFF, 0, 0x4C, 0xC2, 0xFF, 0, 0x00, 0x91, 0xF8, 0, 0x00, 0x78, 0xD4, 0,
        0x00, 0x67, 0xC0, 0, 0x00, 0x3E, 0x92, 0, 0x00, 0x1A, 0x68, 0, 0xF7, 0x63, 0x0C, 0,
    };

    [Fact]
    public void Accent_UsesTheShadeWindowsUsesForEachMode()
    {
        Assert.Equal(Color.FromRgb(0x4C, 0xC2, 0xFF), ThemeManager.AccentFromPalette(DefaultBlue, light: false)); // Light2
        Assert.Equal(Color.FromRgb(0x00, 0x67, 0xC0), ThemeManager.AccentFromPalette(DefaultBlue, light: true));  // Dark1
        Assert.Null(ThemeManager.AccentFromPalette(null, light: false));
        Assert.Null(ThemeManager.AccentFromPalette(new byte[8], light: true));
    }

    [Fact]
    public void AccentTints_FollowTheAccent()
    {
        var palette = new ResourceDictionary();
        var purple = Color.FromRgb(0x88, 0x44, 0xCC);
        ThemeManager.ApplyAccent(palette, purple);

        Assert.Equal(purple, ((SolidColorBrush)palette["AccentBlueBrush"]).Color);
        foreach (var (key, alpha) in ThemeManager.AccentTints)
            Assert.Equal(Color.FromArgb(alpha, 0x88, 0x44, 0xCC), ((SolidColorBrush)palette[key]).Color);
    }

    [Fact]
    public void BothPalettes_HaveExactlyTheSameKeys()
    {
        var dark  = Keys(Read("Resources", "Themes", "Palette.Dark.xaml"));
        var light = Keys(Read("Resources", "Themes", "Palette.Light.xaml"));
        Assert.Empty(dark.Except(light));
        Assert.Empty(light.Except(dark));

        // Every tint ThemeManager rebuilds must exist in both.
        foreach (var (key, _) in ThemeManager.AccentTints) Assert.Contains(key, dark);
    }

    [Fact]
    public void LightPalette_UsesWindows11LightValues()
    {
        var p = Read("Resources", "Themes", "Palette.Light.xaml");
        Assert.Contains("<Color x:Key=\"BgPrimaryColor\">#F3F3F3</Color>", p);
        Assert.Contains("<Color x:Key=\"TextPrimaryColor\">#1B1B1B</Color>", p);
        Assert.Contains("<Color x:Key=\"AccentGreenColor\">#0F7B0F</Color>", p);
        Assert.Contains("<Color x:Key=\"AccentYellowColor\">#9D5D00</Color>", p);
        Assert.Contains("<Color x:Key=\"AccentRedColor\">#C42B1C</Color>", p);
        Assert.Contains("<SolidColorBrush x:Key=\"TextOnAccentBrush\"      Color=\"#FFFFFF\"/>", p);
        Assert.Contains("<SolidColorBrush x:Key=\"CardOverMicaBrush\" Color=\"#B3FFFFFF\"/>", p);
    }

    [Fact]
    public void App_LoadsThePaletteFirst()
    {
        var app = Read("App.xaml");
        int palette = app.IndexOf("Resources/Themes/Palette.Dark.xaml", StringComparison.Ordinal);
        int styles  = app.IndexOf("Resources/Themes/Dark.xaml", StringComparison.Ordinal);
        Assert.True(palette > 0 && palette < styles);
        Assert.Contains("ThemeManager.Initialize();", Read("App.xaml.cs"));
    }

    // A StaticResource brush is resolved once and never follows a theme change.
    [Fact]
    public void NoXaml_UsesAFixedBrushReference()
    {
        foreach (var f in Directory.GetFiles(Root(), "*.xaml", SearchOption.AllDirectories)
                                   .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                            && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
            Assert.False(Regex.IsMatch(File.ReadAllText(f), @"\{StaticResource [A-Za-z0-9]+Brush\}"),
                         $"{Path.GetFileName(f)} has a StaticResource brush; use DynamicResource so it follows the theme");
    }

    // The dark-only tints pages used to hard-code would stay dark in light mode.
    [Fact]
    public void Pages_HaveNoHardCodedThemeColours()
    {
        var views = Directory.GetFiles(Path.Combine(Root(), "Views"), "*View.xaml")
                             .Append(Path.Combine(Root(), "Views", "MainWindow.xaml"))
                             .Append(Path.Combine(Root(), "Resources", "Themes", "Dark.xaml"));
        // Allowed: the wallpaper placeholder gradient, the search overlay's dimming (Windows'
        // smoke layer is dark in both themes), the accent button's white hover layer, and the
        // close button's Windows red with its white X.
        var allowed = new HashSet<string> { "#1E3A5F", "#0F1B2D", "#66000000", "#FFFFFF", "#C42B1C", "#A82318" };
        foreach (var f in views)
            foreach (Match m in Regex.Matches(File.ReadAllText(f), "=\"(#[0-9A-Fa-f]{6,8})\""))
                Assert.True(allowed.Contains(m.Groups[1].Value.ToUpperInvariant()),
                            $"{Path.GetFileName(f)} hard-codes {m.Groups[1].Value}; use a palette key");
    }

    [Fact]
    public void ThemeChanges_AreFollowedLive()
    {
        var main = Read("Views", "MainWindow.xaml.cs");
        Assert.Contains("Marshal.PtrToStringUni(lParam) == \"ImmersiveColorSet\"", main);
        Assert.Contains("WM_DWMCOLORIZATIONCOLORCHANGED", main);
        Assert.Contains("ThemeManager.Changed += OnThemeChanged;", main);

        // The dialogs with Windows' own title bar match it too.
        Assert.Contains("ThemeManager.FollowTheme(this)", Read("Views", "DismissedWindow.xaml.cs"));
        Assert.Contains("ThemeManager.FollowTheme(this)", Read("Views", "RestorePointManagerWindow.xaml.cs"));
    }
}
