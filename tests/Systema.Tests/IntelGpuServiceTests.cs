// IntelGpuServiceTests.cs — pure-logic unit tests (no registry I/O).
//   • IsIntelAdapter detection
//   • ParseValue (DWORD / string / REG_BINARY)
//   • ManagedValueNames — the write/revert allow-list is exactly the documented set
//   • Pure helpers (IsSingleRefreshRate, NormalizeRefreshHz, BuildMsiPath)

using System;
using System.Linq;
using Systema.Core;
using Systema.Services;
using Xunit;

namespace Systema.Tests;

public class IntelGpuServiceTests
{
    [Theory]
    [InlineData("Intel(R) UHD Graphics 630", "")]
    [InlineData("Intel(R) Iris(R) Xe Graphics", "Intel Corporation")]
    [InlineData("", "Intel Corporation")]
    [InlineData("intel arc a370m", "")]
    public void IsIntelAdapter_MatchesIntel(string desc, string provider)
        => Assert.True(IntelGpuService.IsIntelAdapter(desc, provider));

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070", "NVIDIA")]
    [InlineData("AMD Radeon RX 6800", "Advanced Micro Devices, Inc.")]
    [InlineData("Microsoft Basic Display Adapter", "Microsoft")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void IsIntelAdapter_RejectsNonIntel(string? desc, string? provider)
        => Assert.False(IntelGpuService.IsIntelAdapter(desc, provider));

    [Fact]
    public void ParseValue_NullIsDefault() => Assert.Null(IntelGpuService.ParseValue(null));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ParseValue_DwordRoundTrips(int v) => Assert.Equal(v, IntelGpuService.ParseValue(v));

    [Theory]
    [InlineData("1", 1)]
    [InlineData("0", 0)]
    [InlineData(" 2 ", 2)]
    public void ParseValue_StringDigitsParse(string s, int expected)
        => Assert.Equal(expected, IntelGpuService.ParseValue(s));

    [Theory]
    [InlineData("on")]
    [InlineData("")]
    [InlineData("default")]
    public void ParseValue_NonNumericStringIsDefault(string s)
        => Assert.Null(IntelGpuService.ParseValue(s));

    [Fact]
    public void ParseValue_UintIsAccepted() => Assert.Equal(1, IntelGpuService.ParseValue(1u));

    [Theory]
    [InlineData(new byte[] { 6, 0, 0, 0 }, 6)]
    [InlineData(new byte[] { 1, 0, 0, 0 }, 1)]
    [InlineData(new byte[] { 0 }, 0)]
    [InlineData(new byte[] { 255, 0 }, 255)]
    public void ParseValue_BinaryLittleEndian(byte[] raw, int expected)
        => Assert.Equal(expected, IntelGpuService.ParseValue(raw));

    [Fact]
    public void ParseValue_EmptyOrOversizedBinaryIsDefault()
    {
        Assert.Null(IntelGpuService.ParseValue(Array.Empty<byte>()));
        Assert.Null(IntelGpuService.ParseValue(new byte[] { 1, 2, 3, 4, 5 }));
    }

    [Fact]
    public void IsDpstSupported_KeyPresent_Supported()
    {
        Assert.True(IntelGpuService.IsDpstSupported(new[] { "DPSTEnable" }, isLaptop: false));
        Assert.True(IntelGpuService.IsDpstSupported(new[] { "PowerDpstAggressivenessLevel" }, isLaptop: false));
    }

    [Fact]
    public void IsDpstSupported_NoKeysOnLaptop_StillSupported()
    {
        Assert.True(IntelGpuService.IsDpstSupported(Array.Empty<string>(), isLaptop: true));
        Assert.True(IntelGpuService.IsDpstSupported(null, isLaptop: true));
    }

    [Fact]
    public void IsDpstSupported_NoKeysNotLaptop_NotSupported()
    {
        Assert.False(IntelGpuService.IsDpstSupported(new[] { "RC6", "PowerPolicy" }, isLaptop: false));
        Assert.False(IntelGpuService.IsDpstSupported(Array.Empty<string>(), isLaptop: false));
    }

    [Fact]
    public void ManagedValueNames_IsExactlyTheDocumentedSet()
    {
        var expected = new[]
        {
            "PowerPolicy",
            "RC6", "RC6_DC",
            "PanelSelfRefreshEnable", "PSR2Disable",
            "DPSTEnable", "PowerDpstAggressivenessLevel", "Dpst6_3ApplyExtraDimming",
            "DRRSEnabled", "Psr2DrrsEnable",
            "FBCEnable"
        };
        Assert.Equal(expected.OrderBy(x => x), IntelGpuService.ManagedValueNames.OrderBy(x => x));
        Assert.DoesNotContain("MSISupported", IntelGpuService.ManagedValueNames);
        Assert.DoesNotContain("RC6p", IntelGpuService.ManagedValueNames);
        Assert.DoesNotContain("RC6pp", IntelGpuService.ManagedValueNames);
        Assert.DoesNotContain("PowerGpsAggressivenessLevel", IntelGpuService.ManagedValueNames);
        foreach (var k in new[] { "RC6p", "RC6p_DC", "RC6pp", "RC6pp_DC", "PowerGpsAggressivenessLevel" })
            Assert.Contains(k, IntelGpuService.AbandonedValueNames);
    }

    [Fact]
    public void ManagedValueNames_DoesNotIncludeOutOfScopeKeys()
    {
        Assert.DoesNotContain("EnableQuickSyncVideoDecoding", IntelGpuService.ManagedValueNames);
        Assert.DoesNotContain("EnableQuickSyncVideoEncoding", IntelGpuService.ManagedValueNames);
        Assert.DoesNotContain("DedicatedSegmentSize", IntelGpuService.ManagedValueNames);
    }

    [Fact]
    public void WriteValue_RejectsUnmanagedName()
    {
        var svc = new IntelGpuService();
        var adapters = new[] { new IntelAdapter { SubKey = "0000", FullPath = "ignored" } };
        Assert.False(svc.WriteValue(adapters, "DedicatedSegmentSize", 0).Success);
    }

    [Fact]
    public void ResetValue_RejectsUnmanagedName()
    {
        var svc = new IntelGpuService();
        var adapters = new[] { new IntelAdapter { SubKey = "0000", FullPath = "ignored" } };
        Assert.False(svc.ResetValue(adapters, "DedicatedSegmentSize").Success);
    }

    [Theory]
    [InlineData(60, 60, true)]
    [InlineData(48, 48, true)]
    [InlineData(47, 59, false)]
    [InlineData(0, 0, false)]
    [InlineData(0, 60, false)]
    public void IsSingleRefreshRate_Works(int min, int max, bool expected)
        => Assert.Equal(expected, IntelGpuService.IsSingleRefreshRate(min, max));

    [Theory]
    [InlineData(59, 60)]
    [InlineData(60, 60)]
    [InlineData(47, 48)]
    [InlineData(144, 144)]
    [InlineData(85, 85)]
    public void NormalizeRefreshHz_SnapsToCommon(int input, int expected)
        => Assert.Equal(expected, IntelGpuService.NormalizeRefreshHz(input));

    [Fact]
    public void BuildMsiPath_FormatsUnderDeviceParameters()
    {
        var p = IntelGpuService.BuildMsiPath(@"PCI\VEN_8086&DEV_9A60\3&11583659&0&10");
        Assert.Equal(
            @"SYSTEM\CurrentControlSet\Enum\PCI\VEN_8086&DEV_9A60\3&11583659&0&10\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties",
            p);
    }

    [Theory]
    // Integrated iGPU (PCI bus 0) → true; dedicated GPU on a non-zero bus → false.
    [InlineData(@"@System32\drivers\pci.sys,#65536;PCI bus %1, device %2, function %3;(0,2,0)", true)]
    [InlineData(@"PCI bus 0, device 2, function 0", true)]
    [InlineData(@"@System32\drivers\pci.sys,#65536;PCI bus %1, device %2, function %3;(1,0,0)", false)]
    [InlineData(@"@System32\drivers\pci.sys,#65536;PCI bus %1, device %2, function %3;(3,0,0)", false)]
    [InlineData("", true)]            // unknown → assume integrated (don't over-exclude)
    [InlineData("garbage", true)]     // unparseable → assume integrated
    public void IsIntegratedLocation_DetectsBusZero(string loc, bool expected)
        => Assert.Equal(expected, IntelGpuService.IsIntegratedLocation(loc));

    [Fact]
    public void CleanupMsiOverride_WithNoPaths_ClearsNothing()
        => Assert.Equal(0, new IntelGpuService().CleanupMsiOverride(Array.Empty<string>()));

    [Fact]
    public void WriteValue_WithNoAdapters_Fails()
        => Assert.False(new IntelGpuService().WriteValue(Array.Empty<IntelAdapter>(), "RC6", 1).Success);
}
