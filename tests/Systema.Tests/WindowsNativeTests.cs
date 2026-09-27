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
