// UiOverhaulGuardTests.cs
//
// Safety net for the Windows 11 UI overhaul. WPF can't be instantiated or previewed in this
// environment (Smart App Control blocks the WPF dependencies in tests), so a restyle that
// renames a resource key, changes a shared style's target type, or drops something from the
// shell would compile cleanly and only crash or vanish on the user's machine. These text scans
// turn each of those failure modes into a failing build instead.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Systema.Tests;

public class UiOverhaulGuardTests
{
    private static string RepoRoot()
    {
        var asmDir = Path.GetDirectoryName(typeof(UiOverhaulGuardTests).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
    }

    private static string Src(params string[] parts) =>
        Path.Combine(new[] { RepoRoot(), "src", "Systema" }.Concat(parts).ToArray());

    private static string ReadSrc(params string[] parts) => File.ReadAllText(Src(parts));

    private static string StripXmlComments(string xaml) =>
        Regex.Replace(xaml, "<!--.*?-->", "", RegexOptions.Singleline);

    private static readonly Regex KeyDef = new(@"x:Key=""([^""]+)""");
    private static readonly Regex KeyRef = new(@"\{(?:StaticResource|DynamicResource)\s+([A-Za-z0-9_.]+)\s*\}");

    private static HashSet<string> KeysIn(string xaml) =>
        KeyDef.Matches(StripXmlComments(xaml)).Select(m => m.Groups[1].Value).ToHashSet();

    // ── 1. Every resource a view asks for must exist ────────────────────────────
    // A {StaticResource X} whose key was renamed or removed throws XamlParseException the
    // moment the page loads, which kills the whole page. The restyle edits the shared theme,
    // so this is the main way it could break things.
    [Fact]
    public void EveryResourceKeyUsedInXaml_IsDefined()
    {
        var global = KeysIn(ReadSrc("Resources", "Themes", "Dark.xaml"));
        global.UnionWith(KeysIn(ReadSrc("App.xaml")));
        var hostKeys = KeysIn(ReadSrc("Views", "MainWindow.xaml"));

        var missing = new List<string>();
        foreach (var file in Directory.GetFiles(Src("Views"), "*.xaml"))
        {
            string xaml = StripXmlComments(File.ReadAllText(file));
            var allowed = new HashSet<string>(global);
            allowed.UnionWith(KeysIn(xaml));
            // Pages are hosted inside MainWindow, so they can see its resources. The separate
            // dialog windows cannot.
            if (xaml.TrimStart().StartsWith("<UserControl", StringComparison.Ordinal))
                allowed.UnionWith(hostKeys);

            foreach (Match m in KeyRef.Matches(xaml))
                if (!allowed.Contains(m.Groups[1].Value))
                    missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
        }

        Assert.True(missing.Count == 0,
            "Resource keys referenced but not defined anywhere reachable:\n  " +
            string.Join("\n  ", missing.Distinct()));
    }

    // ── 2. Shared styles keep the control type every page uses them on ──────────
    // Seven local styles derive from ToggleSwitch and 92 CheckBoxes use it. Changing its
    // TargetType, or Card's, would make every one of those fail to apply at runtime.
    [Theory]
    [InlineData("ToggleSwitch", "CheckBox")]
    [InlineData("Card", "Border")]
    [InlineData("PrimaryButton", "Button")]
    [InlineData("DangerButton", "Button")]
    [InlineData("GhostButton", "Button")]
    [InlineData("ExpanderButton", "Button")]
    [InlineData("NavButton", "Button")]
    [InlineData("CaptionCloseButton", "Button")]
    [InlineData("DarkComboBox", "ComboBox")]
    [InlineData("DarkTextBox", "TextBox")]
    [InlineData("DarkListView", "ListView")]
    [InlineData("DarkListViewItem", "ListViewItem")]
    [InlineData("AccentProgressBar", "ProgressBar")]
    [InlineData("ScrollThumb", "Thumb")]
    [InlineData("HeadingLarge", "TextBlock")]
    [InlineData("HeadingMedium", "TextBlock")]
    [InlineData("HeadingSmall", "TextBlock")]
    [InlineData("BodyText", "TextBlock")]
    [InlineData("CaptionText", "TextBlock")]
    [InlineData("SectionLabel", "TextBlock")]
    [InlineData("HelperText", "TextBlock")]
    [InlineData("StatusPillGreen", "Border")]
    [InlineData("StatusPillRed", "Border")]
    [InlineData("StatusPillGray", "Border")]
    [InlineData("SettingsGroupHeader", "TextBlock")]
    [InlineData("CardDescription", "TextBlock")]
    [InlineData("CardNote", "TextBlock")]
    [InlineData("CardNoteWarn", "TextBlock")]
    [InlineData("CardNoteGood", "TextBlock")]
    [InlineData("Win11CheckBox", "CheckBox")]
    [InlineData("ListRowCard", "Border")]
    public void SharedStyle_KeepsItsTargetType(string key, string targetType)
    {
        string theme = StripXmlComments(ReadSrc("Resources", "Themes", "Dark.xaml"));
        var tag = Regex.Match(theme, $@"<Style\b[^>]*x:Key=""{Regex.Escape(key)}""[^>]*>");
        Assert.True(tag.Success, $"Shared style '{key}' is missing from Dark.xaml");
        Assert.Contains($@"TargetType=""{targetType}""", tag.Value);
    }

    // ── 3. The shell still reaches every page ───────────────────────────────────
    public static readonly string[] Sections =
    {
        "Dashboard", "Memory", "Services", "Visual", "GameBooster", "Tools", "TaskSleep",
        "Bloatware", "Graphics", "Audio", "Intel", "Nvidia", "Dell", "Settings"
    };

    [Fact]
    public void Shell_KeepsANavEntryForEverySection()
    {
        string shell = StripXmlComments(ReadSrc("Views", "MainWindow.xaml"));
        foreach (var s in Sections)
            Assert.True(Regex.IsMatch(shell, $@"CommandParameter=""{s}"""), $"Nav entry for '{s}' is gone");

        // The hardware sections only appear on machines that have the hardware.
        Assert.Contains("IsIntelGpuPresent", shell);
        Assert.Contains("IsNvidiaGpuPresent", shell);
        Assert.Contains("IsDellPresent", shell);
        Assert.Contains("HasOnDeviceSections", shell);
    }

    [Theory]
    [InlineData("DashboardViewModel", "DashboardView")]
    [InlineData("MemoryViewModel", "MemoryView")]
    [InlineData("ServicesViewModel", "ServicesView")]
    [InlineData("VisualViewModel", "VisualView")]
    [InlineData("GameBoosterViewModel", "GameBoosterView")]
    [InlineData("SettingsViewModel", "SettingsView")]
    [InlineData("ToolsViewModel", "ToolsView")]
    [InlineData("TaskSleepViewModel", "TaskSleepView")]
    [InlineData("BloatwareViewModel", "BloatwareView")]
    [InlineData("GraphicsViewModel", "GraphicsView")]
    [InlineData("AudioViewModel", "AudioView")]
    [InlineData("IntelGpuViewModel", "IntelView")]
    [InlineData("NvidiaGpuViewModel", "NvidiaView")]
    [InlineData("DellViewModel", "DellView")]
    public void Shell_KeepsTheTemplateThatRendersEachPage(string vm, string view)
    {
        string shell = StripXmlComments(ReadSrc("Views", "MainWindow.xaml"));
        var t = Regex.Match(shell, $@"<DataTemplate DataType=""\{{x:Type vm:{vm}\}}"">\s*<views:{view}\s*/>");
        Assert.True(t.Success, $"The DataTemplate mapping {vm} to {view} is gone, so that page would render as a type name");
    }

    // ── 4. The shell keeps everything it does, not just how it looks ────────────
    [Theory]
    [InlineData("{Binding CurrentView")]            // the page host
    [InlineData("SettingsVm.UpdateStatus")]          // update status line
    [InlineData("SettingsVm.CheckForUpdatesCommand")]
    [InlineData("SettingsVm.CanCheckNow")]
    [InlineData("SettingsVm.InstallNowCommand")]
    [InlineData("SettingsVm.IsReadyToInstall")]
    [InlineData("DownloadButton_Click")]
    [InlineData("DiscordButton_Click")]
    [InlineData("CloseButton_Click")]
    [InlineData("TitleBar_MouseLeftButtonDown")]
    [InlineData("Window_Activated")]
    [InlineData("Window_Deactivated")]
    [InlineData("NavigateCommand")]
    [InlineData("Systema v0.")]                      // one of the five version strings
    public void Shell_KeepsItsFunctionalPieces(string needle)
    {
        string shell = StripXmlComments(ReadSrc("Views", "MainWindow.xaml"));
        Assert.Contains(needle, shell);
    }

    // ── 5. Never the VSync-breaking window modes ────────────────────────────────
    // AllowsTransparency makes the HWND layered, which disables MPO and Independent Flip for
    // every window on the desktop. Still banned.
    [Fact]
    public void MainWindow_NeverUsesLayeredTransparency()
    {
        string shell = StripXmlComments(ReadSrc("Views", "MainWindow.xaml"));
        Assert.DoesNotContain(@"AllowsTransparency=""True""", shell);

        string code = ReadSrc("Views", "MainWindow.xaml.cs");
        Assert.DoesNotContain("AllowsTransparency = true", code);
        Assert.DoesNotContain("DWMWA_USE_HOSTBACKDROPBRUSH", code, StringComparison.OrdinalIgnoreCase);
    }

    // Mica was explicitly requested on 2026-09-26 with the VSync rule waived for it (0.7.351).
    // What must hold: it's the Windows 11 22H2+ system backdrop, dark, and the window only goes
    // see-through after DWM accepted it. Otherwise an older build would show a black window.
    [Fact]
    public void MainWindow_MicaIsSafeWhereItIsNotAvailable()
    {
        string code = ReadSrc("Views", "MainWindow.xaml.cs");
        Assert.Contains("Environment.OSVersion.Version.Build >= 22621", code);
        Assert.Contains("DWMWA_SYSTEMBACKDROP_TYPE     = 38", code);
        Assert.Contains("DWMSBT_MAINWINDOW             = 2", code);

        int apply = code.IndexOf("private void ApplyMica", StringComparison.Ordinal);
        Assert.True(apply > 0);
        string body = code[apply..];
        int dark   = body.IndexOf("DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark", StringComparison.Ordinal);
        int mica   = body.IndexOf("DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop", StringComparison.Ordinal);
        int check  = body.IndexOf("if (hr != 0)", StringComparison.Ordinal);
        int clear  = body.IndexOf("Background                = Media.Brushes.Transparent", StringComparison.Ordinal);
        Assert.True(dark > 0 && dark < mica, "dark mode must be set before the backdrop, or Mica comes out light");
        Assert.True(mica < check && check < clear, "the window must only go see-through after DWM accepted Mica");

        foreach (var surface in new[] { "RootSurface", "TitleBarSurface", "SidebarSurface" })
            Assert.Contains($"{surface}.Background", body);
        Assert.Contains("Resources[\"CardLayerBrush\"] = CardOverMica", body);
    }

    // With the glass extended, DWM paints Windows' own caption buttons, and its close X showed
    // through under ours as a doubled X (0.7.351). WS_SYSMENU must go whenever Mica is on, and stay
    // gone if the style is rewritten later.
    [Fact]
    public void MainWindow_MicaHidesTheNativeCaptionButtons()
    {
        string code = ReadSrc("Views", "MainWindow.xaml.cs");
        int apply = code.IndexOf("private void ApplyMica", StringComparison.Ordinal);
        string body = code[apply..];
        int check = body.IndexOf("if (hr != 0)", StringComparison.Ordinal);
        int hide  = body.IndexOf("HideNativeCaptionButtons(hwnd);", StringComparison.Ordinal);
        Assert.True(check > 0 && hide > check, "caption buttons must be hidden once Mica is on");

        Assert.Contains("SetWindowLong(hwnd, GWL_STYLE, style & ~WS_SYSMENU)", code);
        Assert.Contains("msg == WM_STYLECHANGING && IsMicaOn", code);
    }

    // The card styles read CardLayerBrush dynamically so the main window's Mica swap reaches them;
    // everything else (other windows, the Dell overlay banner) keeps a solid card.
    [Fact]
    public void Cards_ReadTheSwappableLayerBrush()
    {
        string dark = ReadSrc("Resources", "Themes", "Dark.xaml");
        Assert.Contains("<SolidColorBrush x:Key=\"CardLayerBrush\" Color=\"{StaticResource BgCardColor}\"/>", dark);
        Assert.Equal(4, Regex.Matches(dark, Regex.Escape("<Setter Property=\"Background\" Value=\"{DynamicResource CardLayerBrush}\"/>")).Count);
        Assert.DoesNotContain("Value=\"{StaticResource BgCardBrush}\"", dark);

        // The Dell banner floats over other content and must stay opaque.
        Assert.Contains("Background=\"{StaticResource BgCardBrush}\" Opacity=\"0.96\"", ReadSrc("Views", "DellView.xaml"));
    }

    // ── 6. The Windows 11 design itself ─────────────────────────────────────────

    // The sliding indicator finds the active item by Tag and compares it with ActiveSection,
    // which Navigate() sets from CommandParameter. A nav button whose Tag drifts from its
    // CommandParameter would navigate fine but never show the indicator.
    [Fact]
    public void EveryNavButton_HasTagMatchingItsCommandParameter()
    {
        string shell = StripXmlComments(ReadSrc("Views", "MainWindow.xaml"));
        var buttons = Regex.Matches(shell, @"<Button\s+Command=""\{Binding NavigateCommand\}""\s+CommandParameter=""([^""]+)""\s+Tag=""([^""]+)""");
        Assert.Equal(Sections.Length, buttons.Count);
        foreach (Match b in buttons)
            Assert.Equal(b.Groups[1].Value, b.Groups[2].Value);
    }

    [Fact]
    public void Nav_UsesOneSlidingIndicator()
    {
        string shell = StripXmlComments(ReadSrc("Views", "MainWindow.xaml"));
        Assert.Contains(@"x:Name=""NavList""", shell);
        Assert.Contains(@"x:Name=""NavIndicator""", shell);
        Assert.Contains(@"x:Name=""NavIndicatorShift""", shell);

        string code = ReadSrc("Views", "MainWindow.xaml.cs");
        Assert.Contains("UpdateNavIndicator", code);
        Assert.Contains("nameof(MainViewModel.ActiveSection)", code);
        // Cosmetic code must never be able to take the window down.
        int at = code.IndexOf("private void UpdateNavIndicator", StringComparison.Ordinal);
        Assert.True(at > 0);
        Assert.Contains("catch", code[at..]);

        // The per-button accent bar is gone, or there would be two indicators.
        string theme = ReadSrc("Resources", "Themes", "Dark.xaml");
        Assert.DoesNotContain(@"x:Name=""AccentBar""", theme);
    }

    [Fact]
    public void PageChanges_Animate()
    {
        string shell = StripXmlComments(ReadSrc("Views", "MainWindow.xaml"));
        // The page host binds CurrentView (through PageViewCache) and still announces changes.
        Assert.Contains("{Binding CurrentView, NotifyOnTargetUpdated=True", shell);
        Assert.Contains("Converter={StaticResource PageViews}", shell);
        Assert.Contains(@"RoutedEvent=""Binding.TargetUpdated""", shell);
    }

    [Fact]
    public void Theme_UsesWindows11Values()
    {
        string theme = StripXmlComments(ReadSrc("Resources", "Themes", "Dark.xaml"));
        Assert.Contains(@"<Color x:Key=""AccentBlueColor"">#60CDFF</Color>", theme);
        Assert.Contains(@"<Color x:Key=""BgPrimaryColor"">#202020</Color>", theme);
        Assert.Contains(@"<Color x:Key=""BgCardColor"">#2B2B2B</Color>", theme);

        // WinUI toggle geometry: 40x20 track with a 12px thumb.
        int t = theme.IndexOf(@"x:Key=""ToggleSwitch""", StringComparison.Ordinal);
        Assert.True(t > 0);
        string toggle = theme[t..theme.IndexOf("</Style>", t, StringComparison.Ordinal)];
        Assert.Contains(@"x:Name=""Track"" Width=""40"" Height=""20""", toggle);
        Assert.Contains(@"x:Name=""Thumb"" Width=""12"" Height=""12""", toggle);

        // Every tooltip is styled, not only the ones that remember to ask.
        Assert.Matches(@"<Style TargetType=""ToolTip"">", theme);
    }

    // Status colours are hand-tinted in pages (#1A22C55E and friends), so changing them in the
    // theme would leave pages mismatched. The overhaul deliberately leaves them alone.
    [Fact]
    public void StatusColours_AreUnchanged()
    {
        string theme = StripXmlComments(ReadSrc("Resources", "Themes", "Dark.xaml"));
        Assert.Contains(@"<Color x:Key=""AccentGreenColor"">#22C55E</Color>", theme);
        Assert.Contains(@"<Color x:Key=""AccentYellowColor"">#F59E0B</Color>", theme);
        Assert.Contains(@"<Color x:Key=""AccentRedColor"">#EF4444</Color>", theme);
    }
}
