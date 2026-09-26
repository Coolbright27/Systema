using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Systema.Core;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// "Recommended" is decided per PC by RecommendationRules, and the pages, Auto Pilot and Home
/// all read the same rules. Before, pages printed it as fixed text: the Graphics page said
/// "Recommended" on Disable MPO for NVIDIA PCs, where Auto Pilot keeps MPO on for VSync.
/// </summary>
public class RecommendationRulesTests
{
    private static readonly PcProfile Desktop       = new(IsLaptop: false, RamMb: 32_768, HasNvidiaGpu: false, HasIntelIgpu: true, IsHybridCpu: false);
    private static readonly PcProfile NvidiaDesktop = Desktop with { HasNvidiaGpu = true };
    private static readonly PcProfile Laptop        = new(IsLaptop: true, RamMb: 16_384, HasNvidiaGpu: false, HasIntelIgpu: true, IsHybridCpu: false);
    private static readonly PcProfile NvidiaLaptop  = Laptop with { HasNvidiaGpu = true };   // like a Precision 5560
    private static readonly PcProfile[] All = { Desktop, NvidiaDesktop, Laptop, NvidiaLaptop, Laptop with { RamMb = 8_192 }, Desktop with { IsHybridCpu = true } };

    private static string? Badge(string key, PcProfile pc) => RecommendationRules.For(key, pc).Badge;

    // ── The rules ─────────────────────────────────────────────────────────

    [Fact]
    public void Mpo_IsKeptOnForNvidia()
    {
        Assert.Equal("Recommended", Badge("DisableMpo", Desktop));
        Assert.Equal("Recommended: Off", Badge("DisableMpo", NvidiaDesktop));
        Assert.Equal("Recommended: Off", Badge("DisableMpo", NvidiaLaptop));
        Assert.Contains("NVIDIA", RecommendationRules.For("DisableMpo", NvidiaLaptop).Why);
    }

    [Theory]
    [InlineData("PerformanceMode")]
    [InlineData("TimerResolution")]
    [InlineData("BoostHighPerfPlan")]
    public void FullSpeedPowerSettings_AreForDesktopsOnly(string key)
    {
        Assert.Equal("Recommended", Badge(key, Desktop));
        Assert.Null(Badge(key, Laptop));
        Assert.Null(Badge(key, NvidiaLaptop));
    }

    [Fact]
    public void Intel_DesktopsGoFullSpeed_LaptopsSavePower()
    {
        Assert.Equal("Recommended: Max Performance", Badge("IntelPowerPolicy", Desktop));
        Assert.Equal("Recommended: Default", Badge("IntelPowerPolicy", Laptop));
        foreach (var key in new[] { "IntelRc6", "IntelDpst", "IntelDrrs" })
        {
            Assert.Equal("Recommended: Off", Badge(key, Desktop));
            Assert.Equal("Recommended: On", Badge(key, Laptop));
        }
        Assert.Equal("Recommended: On", Badge("IntelFbc", Desktop));
        Assert.Equal("Recommended: On", Badge("IntelFbc", Laptop));
    }

    [Fact]
    public void Nvidia_DesktopsHoldFullClocks_LaptopsDont()
    {
        Assert.Equal("Recommended: Off", Badge("NvidiaPowerManagement", NvidiaDesktop));
        Assert.Equal("Recommended: Prefer maximum performance", Badge("NvidiaModePluggedIn", NvidiaDesktop));
        Assert.Null(Badge("NvidiaModeBattery", NvidiaDesktop));
        Assert.Null(Badge("NvidiaFpsCap", NvidiaDesktop));

        Assert.Equal("Recommended: On", Badge("NvidiaPowerManagement", NvidiaLaptop));
        Assert.Null(Badge("NvidiaModePluggedIn", NvidiaLaptop));
        Assert.Equal("Recommended: Optimal power", Badge("NvidiaModeBattery", NvidiaLaptop));
        Assert.Equal("Recommended", Badge("NvidiaFpsCap", NvidiaLaptop));
    }

    [Fact]
    public void KeepKernelInRam_Needs16GB()
    {
        Assert.Null(Badge("KeepKernelInRam", Laptop with { RamMb = 8_192 }));
        Assert.Equal("Recommended", Badge("KeepKernelInRam", Laptop));        // 16 GB installed
        Assert.Contains("32 GB", RecommendationRules.For("KeepKernelInRam", Desktop).Why);
    }

    [Fact]
    public void PowerThrottling_ForLaptopsAndHybridCpus()
    {
        Assert.Equal("Recommended", Badge("PowerThrottling", Laptop));
        Assert.Equal("Recommended", Badge("PowerThrottling", Desktop with { IsHybridCpu = true }));
        Assert.Null(Badge("PowerThrottling", Desktop));
    }

    [Fact]
    public void EveryPill_SaysWhy_ReadsHuman_AndStartsWithRecommended()
    {
        foreach (var key in RecommendationRules.Keys)
        foreach (var pc in All)
        {
            var a = RecommendationRules.For(key, pc);
            if (a.Badge == null) { Assert.Null(a.Why); continue; }
            Assert.StartsWith("Recommended", a.Badge);
            Assert.False(string.IsNullOrWhiteSpace(a.Why), $"{key} has a pill but no reason");
            Assert.DoesNotContain("—", a.Badge + a.Why);
        }
    }

    [Fact]
    public void UnknownKey_HasNoPill()
    {
        Assert.Equal(Advice.None, RecommendationRules.For("NoSuchSetting", Desktop));
    }

    // ── What XAML binds to ────────────────────────────────────────────────

    [Fact]
    public void Recommend_IsEmptyUntilTheProfileArrives_ThenRefreshesEveryPill()
    {
        var r = new Recommend();
        Assert.Null(r["DisableMpo"]);

        var raised = new List<string?>();
        r.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        r.SetProfile(NvidiaLaptop);

        Assert.Equal(new[] { "Item[]" }, raised);
        Assert.Equal("Recommended: Off", r["DisableMpo"]);
        Assert.Contains("NVIDIA", r["DisableMpo.Why"]);
        Assert.Null(r["NoSuchSetting"]);
        Assert.Null(r["TimerResolution"]);
    }

    // ── The pages ─────────────────────────────────────────────────────────

    private static string ViewsDir()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string p = Path.Combine(dir, "src", "Systema", "Views");
            if (Directory.Exists(p)) return p;
            dir = Directory.GetParent(dir)?.FullName!;
        }
        throw new DirectoryNotFoundException("src/Systema/Views");
    }

    private static IEnumerable<(string File, string Text)> Views() =>
        Directory.GetFiles(ViewsDir(), "*.xaml").Select(f => (Path.GetFileName(f), File.ReadAllText(f)));

    private static string ReadSource(params string[] parts) =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(ViewsDir())!, Path.Combine(parts)));

    [Fact]
    public void Pages_UseOnlyKeysTheRulesKnow_AndEveryRuleIsShownSomewhere()
    {
        var used = new HashSet<string>();
        foreach (var (file, text) in Views())
            foreach (Match m in Regex.Matches(text, "RecommendKey=\"([^\"]*)\""))
            {
                Assert.True(RecommendationRules.Keys.Contains(m.Groups[1].Value),
                            $"{file} asks for unknown rule '{m.Groups[1].Value}'");
                used.Add(m.Groups[1].Value);
            }

        var unused = RecommendationRules.Keys.Except(used).ToList();
        Assert.True(unused.Count == 0, "Rules no page shows: " + string.Join(", ", unused));
    }

    [Fact]
    public void NoPageHandWritesARecommendedPill()
    {
        foreach (var (file, text) in Views())
        {
            Assert.DoesNotContain("Badge=\"Recommended", text);
            Assert.False(Regex.IsMatch(text, "Value=\"Recommended"), $"{file} sets a Recommended badge in a style");
        }
    }

    // The Intel and NVIDIA pages used to say "Recommended: On" for power saving that Home tells
    // desktops to turn off. The pill says it now, per PC.
    [Theory]
    [InlineData("IntelView.xaml")]
    [InlineData("NvidiaView.xaml")]
    public void GpuPages_DontHardCodeARecommendation(string file)
    {
        var text = File.ReadAllText(Path.Combine(ViewsDir(), file));
        Assert.DoesNotContain("Recommended:", text);
        Assert.DoesNotContain("Recommended On", text);
    }

    [Fact]
    public void AutoPilotAndHome_UseTheSameRules()
    {
        var dash = ReadSource("ViewModels", "DashboardViewModel.cs");
        foreach (var rule in new[]
        {
            "WantsHighPerformancePlan(pc)", "WantsTimerResolution(pc)", "WantsMpoDisabled(pc)", "WantsFpsCap(pc)",
            "WantsNvidiaFullClocks(pc)", "WantsNvidiaMaxPerformanceMode(pc)", "WantsIntelMaxPerformance(pc)",
        })
            Assert.Contains("RecommendationRules." + rule, dash);
    }
}
