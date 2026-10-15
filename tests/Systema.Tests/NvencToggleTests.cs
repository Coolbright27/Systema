using System.IO;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// The NVENC encoding redirect feature disables NVIDIA's video encoder DLLs so apps
/// fall back to the iGPU encoder. These tests pin the structural expectations:
///   - The DLL paths are correct Windows system paths
///   - The service has the detection, enable and disable methods
///   - The toggle only shows when both NVENC DLLs and a non-NVIDIA GPU are present
///   - The view binds to the right properties
/// </summary>
public class NvencToggleTests
{
    private static string RepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(NvencToggleTests).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", ".."));
    }

    private static string ReadSource(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts)));

    // ── Service has all the required methods ────────────────────────────────

    [Fact]
    public void Service_HasNvencMethods()
    {
        string src = ReadSource("src", "Systema", "Services", "NvidiaGpuService.cs");
        Assert.Contains("IsNvencPresent()", src);
        Assert.Contains("IsNvencEnabled()", src);
        Assert.Contains("DisableNvenc()", src);
        Assert.Contains("EnableNvenc()", src);
        Assert.Contains("HasNonNvidiaGpu()", src);
    }

    [Fact]
    public void Service_TargetsTheCorrectDlls()
    {
        string src = ReadSource("src", "Systema", "Services", "NvidiaGpuService.cs");
        Assert.Contains("nvEncodeAPI64.dll", src);
        Assert.Contains("nvEncodeAPI.dll", src);
        Assert.Contains(".off", src);
    }

    [Fact]
    public void Service_TakesOwnershipBeforeRenaming()
    {
        string src = ReadSource("src", "Systema", "Services", "NvidiaGpuService.cs");
        int takeown = src.IndexOf("takeown");
        int icacls  = src.IndexOf("icacls");
        int move    = src.IndexOf("File.Move(from, to)");
        Assert.True(takeown > 0, "must take ownership from TrustedInstaller");
        Assert.True(icacls > takeown, "must grant admin access after takeown");
        Assert.True(move > icacls, "must rename after granting access");
    }

    [Fact]
    public void Service_HandlesLockedDllGracefully()
    {
        string src = ReadSource("src", "Systema", "Services", "NvidiaGpuService.cs");
        Assert.Contains("0x80070020", src);
        Assert.Contains("Close apps that record or stream", src);
    }

    // ── ViewModel gates the toggle on iGPU + NVENC presence ─────────────────

    [Fact]
    public void ViewModel_GatesToggleOnBothConditions()
    {
        string src = ReadSource("src", "Systema", "ViewModels", "NvidiaGpuViewModel.cs");
        Assert.Contains("IsNvencPresent()", src);
        Assert.Contains("HasNonNvidiaGpu()", src);
        Assert.Contains("IsNvencToggleAvailable", src);
    }

    [Fact]
    public void ViewModel_ReadsRealStateFromFileSystem()
    {
        string src = ReadSource("src", "Systema", "ViewModels", "NvidiaGpuViewModel.cs");
        Assert.Contains("IsNvencEnabled()", src);
        Assert.Contains("NvencEnabled", src);
    }

    [Fact]
    public void ViewModel_RevertOnFailure()
    {
        string src = ReadSource("src", "Systema", "ViewModels", "NvidiaGpuViewModel.cs");
        Assert.Contains("NvencEnabled = !value", src);
    }

    // ── View only shows when the toggle is available ────────────────────────

    [Fact]
    public void View_SectionGatedOnToggleAvailable()
    {
        string xaml = ReadSource("src", "Systema", "Views", "NvidiaView.xaml");
        Assert.Contains("IsNvencToggleAvailable", xaml);
        Assert.Contains("NvencEnabled", xaml);
        Assert.Contains("Video encoding", xaml);
    }

    [Fact]
    public void View_ShowsIGpuNameInDescription()
    {
        string xaml = ReadSource("src", "Systema", "Views", "NvidiaView.xaml");
        Assert.Contains("IGpuName", xaml);
    }

    [Fact]
    public void View_HasBetaBadge()
    {
        string xaml = ReadSource("src", "Systema", "Views", "NvidiaView.xaml");
        Assert.Contains("Badge=\"Beta\"", xaml);
        Assert.Contains("NVIDIA encoding", xaml);
    }

    [Fact]
    public void View_WarnsAboutAppRestart()
    {
        string xaml = ReadSource("src", "Systema", "Views", "NvidiaView.xaml");
        Assert.Contains("Restart encoding apps", xaml);
    }

    [Fact]
    public void View_HasResetButton()
    {
        string xaml = ReadSource("src", "Systema", "Views", "NvidiaView.xaml");
        Assert.Contains("ResetNvencCommand", xaml);
        Assert.Contains("Reset to default", xaml);
    }

    // ── Re-enforcement on startup ───────────────────────────────────────────

    [Fact]
    public void ViewModel_HasReEnforceMethod()
    {
        string src = ReadSource("src", "Systema", "ViewModels", "NvidiaGpuViewModel.cs");
        Assert.Contains("ReEnforceNvenc(SettingsService", src);
    }

    [Fact]
    public void ReEnforce_OnlyActsWhenSavedPreferenceIsDisabled()
    {
        string src = ReadSource("src", "Systema", "ViewModels", "NvidiaGpuViewModel.cs");
        int method = src.IndexOf("public static void ReEnforceNvenc");
        Assert.True(method > 0);
        string body = src[method..src.IndexOf("\n    }", method)];
        Assert.Contains("saved != true", body);
        Assert.Contains("IsNvencEnabled()", body);
    }

    [Fact]
    public void Startup_CallsReEnforceWhenPreferenceIsSet()
    {
        string app = ReadSource("src", "Systema", "App.xaml.cs");
        Assert.Contains("NvencEncodingDisabled == true", app);
        Assert.Contains("ReEnforceNvenc(settingsService)", app);
    }

    // ── Toggle saves the preference ─────────────────────────────────────────

    [Fact]
    public void Toggle_SavesPreference()
    {
        string src = ReadSource("src", "Systema", "ViewModels", "NvidiaGpuViewModel.cs");
        Assert.Contains("_settings.NvencEncodingDisabled = !value", src);
    }

    [Fact]
    public void Reset_ClearsPreferenceAndReEnables()
    {
        string src = ReadSource("src", "Systema", "ViewModels", "NvidiaGpuViewModel.cs");
        int reset = src.IndexOf("private void ResetNvenc()");
        Assert.True(reset > 0);
        string body = src[reset..src.IndexOf("\n    }", reset)];
        Assert.Contains("NvencEncodingDisabled = null", body);
        Assert.Contains("EnableNvenc()", body);
    }

    // ── Settings stores nullable tri-state ───────────────────────────────────

    [Fact]
    public void Settings_HasNullableNvencPreference()
    {
        string src = ReadSource("src", "Systema", "Services", "SettingsService.cs");
        Assert.Contains("bool? NvencEncodingDisabled", src);
    }

    // ── Search index includes the feature ───────────────────────────────────

    [Fact]
    public void SearchIndex_HasNvencEntry()
    {
        string src = ReadSource("src", "Systema", "Core", "SettingsSearchIndex.cs");
        Assert.Contains("nvenc", src);
        Assert.Contains("encoding", src);
    }
}
