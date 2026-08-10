// MainWindowCloseTests.cs
//
// Regression guard for the v0.7.280 crash.
//
// Systema lives in the tray, so closing the window is meant to hide it, never destroy it. That was
// only ever true for the title bar's X button, which called Hide() directly. Alt+F4, the taskbar's
// "Close window" and the window system menu all go straight to Window.Close(), and nothing cancelled
// it — so the window really was destroyed while App kept its reference in _mainWindow.
//
// A closed WPF Window can never be shown again. The next open request (tray double-click, Windows
// Search, a second launch signalling the show event) called Show() on the dead window and threw:
//
//   System.InvalidOperationException: Cannot set Visibility or call Show, ShowDialog, or
//   WindowInteropHelper.EnsureHandle after a Window has closed.
//      at Systema.App.ShowMainWindow() in App.xaml.cs:line 829
//
// Reported by a user on 0.7.279 after updating and reopening via Windows Search.
//
// Two independent defences, both pinned here:
//   1. MainWindow.OnClosing cancels every close except the real one (tray → Exit).
//   2. App nulls its reference on Closed, so even an unexpected close just rebuilds the window.
//
// Source-scan (not reflection) because Application Control blocks loading Systema.dll into the
// test host on this machine — same constraint as the other *SourceTests.

using System.IO;
using System.Linq;
using Xunit;

namespace Systema.Tests;

public class MainWindowCloseTests
{
    private static string RepoRoot()
    {
        var asmDir = Path.GetDirectoryName(typeof(MainWindowCloseTests).Assembly.Location)!;
        // tests/Systema.Tests/bin/<Cfg>/<TFM>/  →  ../../../../../
        return Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
    }

    private static string Read(params string[] parts)
    {
        var path = Path.GetFullPath(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));
        Assert.True(File.Exists(path), $"Expected file not found: {path}");
        return File.ReadAllText(path);
    }

    private static string Window() => Read("src", "Systema", "Views", "MainWindow.xaml.cs");
    private static string App()    => Read("src", "Systema", "App.xaml.cs");

    [Fact]
    public void EveryCloseIsCancelledUnlessItIsTheRealExit()
    {
        var src = Window();

        Assert.Contains("protected override void OnClosing(", src);
        Assert.Contains("if (!AllowClose)", src);
        Assert.Contains("e.Cancel = true;", src);

        // The hide must still do the full tray hand-off, or Ghost Mode and the refresh timer
        // desync from the window's real state.
        Assert.Contains("SetTrayOnly(true)", src);
        Assert.Contains("NotifyWindowHidden()", src);
    }

    [Fact]
    public void OnlyExplicitShutdownIsAllowedToActuallyClose()
    {
        Assert.Contains("public bool AllowClose { get; set; }", Window());
        Assert.Contains("_mainWindow.AllowClose = true", App());
    }

    [Fact]
    public void AppDropsItsReferenceWhenTheWindowCloses()
    {
        // Without this, App holds a dead Window and Show() throws instead of rebuilding.
        Assert.Contains("_mainWindow.Closed += (_, _) => _mainWindow = null;", App());
    }

    [Fact]
    public void CloseButtonGoesThroughTheSamePathAsEveryOtherClose()
    {
        var src = Window();

        // The bug was two behaviours for one action: the X hid, everything else destroyed.
        // The X must now route through Close() so OnClosing is the single decision point.
        Assert.Contains("private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();", src);
    }
}
