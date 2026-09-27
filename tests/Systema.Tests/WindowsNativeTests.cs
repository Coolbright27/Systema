using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// Small things that make Systema behave like a Windows 11 app rather than a themed one (0.7.353).
/// </summary>
public class WindowsNativeTests
{
    private static string Root()
    {
        string dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "src", "Systema"))) dir = Directory.GetParent(dir)!.FullName;
        return Path.Combine(dir, "src", "Systema");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(Root(), Path.Combine(parts)));

    private static string Dark() =>
        Regex.Replace(Read("Resources", "Themes", "Dark.xaml"), "<!--.*?-->", "", RegexOptions.Singleline);

    // Without these, the window blurs on a second monitor with different scaling, and message
    // boxes get Windows 95 buttons.
    [Fact]
    public void Manifest_DeclaresPerMonitorDpiAndModernCommonControls()
    {
        var m = Read("app.manifest");
        Assert.Contains("<dpiAwareness xmlns=\"http://schemas.microsoft.com/SMI/2016/WindowsSettings\">PerMonitorV2</dpiAwareness>", m);
        Assert.Contains("name=\"Microsoft.Windows.Common-Controls\" version=\"6.0.0.0\"", m);
        Assert.Contains("level=\"requireAdministrator\"", m);   // unchanged
    }

    // Windows 11 uses the arrow on buttons; the hand is for links. Systema has no links.
    [Fact]
    public void Buttons_UseTheArrowCursor()
    {
        foreach (var f in Directory.GetFiles(Root(), "*.xaml", SearchOption.AllDirectories)
                                   .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                            && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var x = File.ReadAllText(f);
            Assert.False(Regex.IsMatch(x, "Cursor\"\\s+Value=\"Hand\"|Cursor=\"Hand\""), $"{Path.GetFileName(f)} uses the hand cursor");
        }
    }

    // Text boxes' built-in Cut / Copy / Paste menu used to open in the classic light WPF style.
    [Fact]
    public void RightClickMenus_AreStyledDark()
    {
        var d = Dark();
        Assert.Contains("<Style TargetType=\"ContextMenu\">", d);
        Assert.Contains("<Style TargetType=\"MenuItem\">", d);
        Assert.Contains("x:Key=\"{x:Static MenuItem.SeparatorStyleKey}\"", d);
        Assert.Contains("Text=\"{TemplateBinding InputGestureText}\"", d);   // "Ctrl+C" on the right
    }

    // Tabbing through Systema showed nothing on buttons, toggles or the nav: they hid WPF's focus
    // visual outright. They now show the Windows 11 ring (keyboard focus only, never on a click).
    [Fact]
    public void KeyboardFocus_ShowsTheWindows11Ring()
    {
        var d = Dark();
        int ring = d.IndexOf("x:Key=\"Win11FocusVisual\"", StringComparison.Ordinal);
        Assert.True(ring > 0);
        foreach (var style in new[] { "PrimaryButton", "GhostButton", "ToggleSwitch", "CaptionCloseButton", "NavButton", "Win11CheckBox" })
        {
            int s = d.IndexOf($"x:Key=\"{style}\"", StringComparison.Ordinal);
            Assert.True(s > ring, $"{style} must come after Win11FocusVisual (StaticResource)");
            int end = d.IndexOf("</Style>", s, StringComparison.Ordinal);
            Assert.Contains("<Setter Property=\"FocusVisualStyle\" Value=\"{StaticResource Win11FocusVisual}\"/>", d[s..end]);
        }
    }

    // At 100% scaling WPF's default ("Ideal") text sits between pixels and, over Mica, is drawn
    // with grey smoothing: soft and faint next to Settings. Display mode snaps it to the pixel
    // grid like Windows' own text (0.7.355). Popups are separate windows, so they set it too.
    [Fact]
    public void EveryWindow_DrawsCrispText()
    {
        foreach (var w in new[] { "MainWindow.xaml", "DismissedWindow.xaml", "RestorePointManagerWindow.xaml",
                                  "CrashReportWindow.xaml", "DiagnosticsReportWindow.xaml" })
            Assert.Contains("TextOptions.TextFormattingMode=\"Display\"", Read("Views", w));
        Assert.Contains("SetTextFormattingMode(this, System.Windows.Media.TextFormattingMode.Display)",
                        Read("Views", "RestorePointManagerWindow.xaml.cs"));

        var d = Dark();
        foreach (var popup in new[] { "<Style TargetType=\"ToolTip\">", "<Style TargetType=\"ContextMenu\">" })
        {
            int s = d.IndexOf(popup, StringComparison.Ordinal);
            Assert.True(s > 0);
            Assert.Contains("TextOptions.TextFormattingMode\" Value=\"Display\"", d[s..d.IndexOf("</Style>", s, StringComparison.Ordinal)]);
        }
    }

    // Settings writes "On" / "Off" before every switch.
    [Fact]
    public void Switches_SayOnOrOff()
    {
        var d = Dark();
        int s = d.IndexOf("x:Key=\"ToggleSwitch\"", StringComparison.Ordinal);
        var toggle = d[s..d.IndexOf("</ControlTemplate>", s, StringComparison.Ordinal)];
        Assert.Contains("x:Name=\"StateText\" Text=\"Off\"", toggle);
        Assert.Contains("<Setter TargetName=\"StateText\" Property=\"Text\" Value=\"On\"/>", toggle);

        // Home's Auto Pilot switch used to hand-build its own label; now it would say it twice.
        var home = Read("Views", "DashboardView.xaml");
        int sw = home.IndexOf("<CheckBox Style=\"{StaticResource ToggleSwitch}\" VerticalAlignment=\"Center\"", StringComparison.Ordinal);
        Assert.True(sw > 0);
        Assert.DoesNotContain("Value=\"Off\"", home[Math.Max(0, sw - 400)..sw]);
    }

    // Windows uses sentence case for every setting name and button ("Choose your mode",
    // "Install all"), keeping capitals only for names.
    [Fact]
    public void HeadingsAndButtons_UseSentenceCase()
    {
        var names = new HashSet<string>
        {
            "Windows", "Systema", "Start", "Game", "Bar", "Boost", "Booster", "Engine", "Auto", "Pilot", "Launch",
            "Telemetry", "Pro", "Intel", "Dell", "Discord", "Store", "Control", "Panel", "Bluetooth", "Nagle's",
            "Realtek", "Wi-Fi", "High", "Max", "Off", "On", "ClearType",
        };
        var bad = new List<string>();
        foreach (var f in Directory.GetFiles(Path.Combine(Root(), "Views"), "*.xaml"))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), "(?:Header|Content)=\"([^\"{]+)\""))
            {
                var words = m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var w in words.Skip(1))
                {
                    var word = w.Trim('(', ')', ',', '.');
                    if (Regex.IsMatch(word, "^[A-Z][a-z]") && !names.Contains(word))
                        bad.Add($"{Path.GetFileName(f)}: \"{m.Groups[1].Value}\"");
                }
            }
        Assert.True(bad.Count == 0, "Title Case left:\n  " + string.Join("\n  ", bad.Distinct()));
    }

    // Windows buttons and notes are plain text: no emoji, no fallback-font symbols, and no
    // "Status: ON ✓" beside a switch that already says On.
    [Fact]
    public void Text_HasNoSymbolsOrRedundantStatus()
    {
        foreach (var f in Directory.GetFiles(Path.Combine(Root(), "Views"), "*.xaml"))
        {
            var x = File.ReadAllText(f);
            Assert.DoesNotContain("⟳", x);
            Assert.DoesNotContain("\"Status: ON", x);
            Assert.DoesNotContain("\"Status: OFF", x);
            Assert.False(Regex.IsMatch(x, "Content=\"(➕|🔄|↻ |↑)"), $"{Path.GetFileName(f)} has an emoji button");
        }
    }

    // WPF only takes clicks where something has a background. The expander headers' template root
    // had none and their hover layer ignores the mouse, so the gap between the title and the
    // chevron did nothing when clicked (0.7.357). Every clickable template's root now has one.
    [Fact]
    public void ClickableTemplates_TakeClicksAcrossTheirWholeArea()
    {
        var files = Directory.GetFiles(Path.Combine(Root(), "Views"), "*.xaml")
                             .Append(Path.Combine(Root(), "Resources", "Themes", "Dark.xaml"));
        var clickable = new Regex("Button|ListBoxItem|ComboBoxItem|RadioButton|CheckBox|Thumb");
        var bad = new List<string>();
        foreach (var f in files)
        {
            var doc = System.Xml.Linq.XDocument.Load(f);
            foreach (var t in doc.Descendants().Where(e => e.Name.LocalName == "ControlTemplate"))
            {
                var type = (string?)t.Attribute("TargetType") ?? "";
                if (!clickable.IsMatch(type)) continue;
                var root = t.Elements().FirstOrDefault(e => !e.Name.LocalName.EndsWith(".Triggers"));
                if (root != null && root.Attribute("Background") == null)
                    bad.Add($"{Path.GetFileName(f)}: {type} template root <{root.Name.LocalName}> has no Background");
            }
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    // The tray menu kept the pre-redesign blue-grey palette and square corners.
    [Fact]
    public void TrayMenu_UsesTheWindows11Look()
    {
        var tray = Read("Services", "TrayService.cs");
        Assert.DoesNotContain("FromArgb(0x1E, 0x22, 0x27)", tray);   // old background
        Assert.DoesNotContain("FromArgb(0x38, 0xBD, 0xF8)", tray);   // old accent
        Assert.Contains("Rgb(0x2C, 0x2C, 0x2C)", tray);              // PopupBrush, dark
        Assert.Contains("Rgb(0xF9, 0xF9, 0xF9)", tray);              // PopupBrush, light
        Assert.Contains("menu.HandleCreated += (_, _) => RoundCorners(menu)", tray);
        Assert.Contains("DWMWA_WINDOW_CORNER_PREFERENCE", tray);

        // Tray tooltips are UI copy too: no em dashes.
        Assert.DoesNotContain("Systema —", tray);
        Assert.DoesNotContain("SetTooltip($\"Systema —", Read("Services", "GameBoosterService.cs"));
        Assert.DoesNotContain("SetTooltip(\"Systema —", Read("Services", "GameBoosterService.cs"));
    }
}
