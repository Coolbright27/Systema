// IntelGpuRoundTripTests.cs
//
// END-TO-END test of the real write path against a writable HKCU scratch hive with a
// simulated Intel adapter — no elevation, never touches the machine's real keys.
// Writes/resets/reverts target the PRIMARY adapter; the active-GPU detection resolves to
// null under HKCU (no Enum\<pnp>\Driver), so adapter 0000 stays primary here.

using System;
using System.Collections.Generic;
using Systema.Core;
using Systema.Services;
using Microsoft.Win32;
using Xunit;

namespace Systema.Tests;

public class IntelGpuRoundTripTests : IDisposable
{
    private const string ClassPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string Adapter0000 = ClassPath + @"\0000";
    private const string Adapter0001 = ClassPath + @"\0001"; // NVIDIA — must never be written
    private const string Adapter0002 = ClassPath + @"\0002";

    private readonly IntelGpuService _svc;

    public IntelGpuRoundTripTests()
    {
        Cleanup();
        using (var intel = Registry.CurrentUser.CreateSubKey(Adapter0000, writable: true)!)
        {
            intel.SetValue("DriverDesc", "Intel(R) UHD Graphics");
            intel.SetValue("ProviderName", "Intel Corporation");
            intel.SetValue("PowerPolicy", 2, RegistryValueKind.DWord);
            intel.SetValue("Psr2DrrsEnable", 1, RegistryValueKind.DWord);
            intel.SetValue("PSR2Disable", 0, RegistryValueKind.DWord);
        }
        using (var nv = Registry.CurrentUser.CreateSubKey(Adapter0001, writable: true)!)
        {
            nv.SetValue("DriverDesc", "NVIDIA T1200 Laptop GPU");
            nv.SetValue("ProviderName", "NVIDIA");
        }
        _svc = new IntelGpuService(Registry.CurrentUser);
    }

    private static int? Read(string adapterPath, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(adapterPath, writable: false);
        return IntelGpuService.ParseValue(k?.GetValue(name));
    }

    [Fact]
    public void Detect_FindsIntelOnly()
    {
        var list = _svc.DetectIntelAdapters();
        Assert.Single(list);
        Assert.Equal("0000", list[0].SubKey);
        Assert.Contains("Intel", list[0].DriverDesc);
    }

    [Fact]
    public void WriteValue_ActuallyPersistsToRegistry()
    {
        var adapters = _svc.DetectIntelAdapters();
        var r = _svc.WriteValue(adapters, IntelGpuService.PanelSelfRefreshEnable, 0);
        Assert.True(r.Success);
        Assert.Equal(0, Read(Adapter0000, IntelGpuService.PanelSelfRefreshEnable));
        _svc.WriteValue(adapters, IntelGpuService.PanelSelfRefreshEnable, 1);
        Assert.Equal(1, Read(Adapter0000, IntelGpuService.PanelSelfRefreshEnable));
    }

    [Fact]
    public void ResetValue_DeletesSoDriverDefaultReturns()
    {
        var adapters = _svc.DetectIntelAdapters();
        _svc.WriteValue(adapters, IntelGpuService.RC6, 0);
        Assert.Equal(0, Read(Adapter0000, IntelGpuService.RC6));
        var r = _svc.ResetValue(adapters, IntelGpuService.RC6);
        Assert.True(r.Success);
        Assert.Null(Read(Adapter0000, IntelGpuService.RC6));
    }

    [Fact]
    public void SetRc6_On_WritesRc6AndDc_NoDeepStateKeys()
    {
        var adapters = _svc.DetectIntelAdapters();
        _svc.SetRc6(adapters, on: true);
        Assert.Equal(1, Read(Adapter0000, IntelGpuService.RC6));
        Assert.Equal(1, Read(Adapter0000, IntelGpuService.RC6Dc));
        foreach (var k in new[] { IntelGpuService.RC6p, IntelGpuService.RC6pp, IntelGpuService.RC6pDc, IntelGpuService.RC6ppDc })
            Assert.Null(Read(Adapter0000, k));
    }

    [Fact]
    public void SetRc6_Off_WritesRc6AndDc()
    {
        var adapters = _svc.DetectIntelAdapters();
        _svc.SetRc6(adapters, on: false);
        Assert.Equal(0, Read(Adapter0000, IntelGpuService.RC6));
        Assert.Equal(0, Read(Adapter0000, IntelGpuService.RC6Dc));
    }

    [Fact]
    public void ResetRc6_ClearsDocumentedKeys()
    {
        var adapters = _svc.DetectIntelAdapters();
        _svc.SetRc6(adapters, on: true);
        _svc.ResetRc6(adapters);
        Assert.Null(Read(Adapter0000, IntelGpuService.RC6));
        Assert.Null(Read(Adapter0000, IntelGpuService.RC6Dc));
    }

    [Fact]
    public void SetDpst_DrivesAggressivenessLevelAndExtraDimming()
    {
        var adapters = _svc.DetectIntelAdapters();
        var off = _svc.SetDpst(adapters, on: false);
        Assert.True(off.Success);
        Assert.Equal(0, Read(Adapter0000, IntelGpuService.DpstEnable));
        Assert.Equal(1, Read(Adapter0000, IntelGpuService.DpstLevel));
        Assert.Equal(0, Read(Adapter0000, IntelGpuService.DpstExtraDimming));
        Assert.Null(Read(Adapter0000, IntelGpuService.DpstGpsLevel));
        var on = _svc.SetDpst(adapters, on: true);
        Assert.True(on.Success);
        Assert.Equal(1, Read(Adapter0000, IntelGpuService.DpstEnable));
        Assert.Equal(6, Read(Adapter0000, IntelGpuService.DpstLevel));
        Assert.Equal(1, Read(Adapter0000, IntelGpuService.DpstExtraDimming));
        Assert.Null(Read(Adapter0000, IntelGpuService.DpstGpsLevel));
    }

    [Fact]
    public void RevertAll_ClearsEveryReinforcementKeyOnPrimary()
    {
        var adapters = _svc.DetectIntelAdapters();
        // Seed PSR keys directly (the app no longer writes them) plus the live toggles —
        // Revert All must still clear ALL of them, including stale PSR overrides.
        _svc.WriteValue(adapters, IntelGpuService.Psr2Disable, 1);
        _svc.WriteValue(adapters, IntelGpuService.PanelSelfRefreshEnable, 0);
        _svc.SetDpst(adapters, on: false);
        _svc.SetDrrs(adapters, on: false);
        _svc.SetFbc(adapters, on: false);
        _svc.RevertAll(adapters);
        foreach (var name in new[] {
            IntelGpuService.Psr2Disable, IntelGpuService.PanelSelfRefreshEnable,
            IntelGpuService.DpstEnable, IntelGpuService.DpstLevel, IntelGpuService.DpstExtraDimming,
            IntelGpuService.DrrsEnabled, IntelGpuService.Psr2DrrsEnable, IntelGpuService.FbcEnable })
            Assert.Null(Read(Adapter0000, name));
    }

    [Fact]
    public void SetDrrs_WritesBothNamingVariants_OnPrimaryAdapter()
    {
        var adapters = _svc.DetectIntelAdapters();
        _svc.SetDrrs(adapters, on: false);
        Assert.Equal(0, Read(Adapter0000, IntelGpuService.DrrsEnabled));
        Assert.Equal(0, Read(Adapter0000, IntelGpuService.Psr2DrrsEnable));
        _svc.SetDrrs(adapters, on: true);
        Assert.Equal(1, Read(Adapter0000, IntelGpuService.DrrsEnabled));
        Assert.Equal(1, Read(Adapter0000, IntelGpuService.Psr2DrrsEnable));
    }

    [Fact]
    public void SetFbc_EnableDisableResetRoundTrips()
    {
        var adapters = _svc.DetectIntelAdapters();
        _svc.SetFbc(adapters, on: false);
        Assert.Equal(0, Read(Adapter0000, IntelGpuService.FbcEnable));
        _svc.SetFbc(adapters, on: true);
        Assert.Equal(1, Read(Adapter0000, IntelGpuService.FbcEnable));
        _svc.ResetValue(adapters, IntelGpuService.FbcEnable);
        Assert.Null(Read(Adapter0000, IntelGpuService.FbcEnable));
    }

    [Fact]
    public void CleanupMsiOverride_DeletesStaleMsiSupported()
    {
        var path = IntelGpuService.BuildMsiPath(@"PCI\VEN_8086&DEV_TEST\3&test&0&10");
        using (var k = Registry.CurrentUser.CreateSubKey(path, writable: true))
            k!.SetValue(IntelGpuService.MsiSupported, 1, RegistryValueKind.DWord);
        Assert.Equal(1, _svc.CleanupMsiOverride(new[] { path }));
        using var check = Registry.CurrentUser.OpenSubKey(path, writable: false);
        Assert.Null(check?.GetValue(IntelGpuService.MsiSupported));
        Assert.Equal(0, _svc.CleanupMsiOverride(new[] { path }));
    }

    [Fact]
    public void RevertAll_ClearsEveryManagedValue()
    {
        var adapters = _svc.DetectIntelAdapters();
        _svc.WriteValue(adapters, IntelGpuService.RC6, 0);
        _svc.WriteValue(adapters, IntelGpuService.PanelSelfRefreshEnable, 0);
        _svc.WriteValue(adapters, IntelGpuService.PowerPolicy, 1);
        var r = _svc.RevertAll(adapters);
        Assert.True(r.Success);
        foreach (var name in IntelGpuService.ManagedValueNames)
            Assert.Null(Read(Adapter0000, name));
    }

    [Fact]
    public void WriteValue_PreservesExistingBinaryKind()
    {
        var adapters = _svc.DetectIntelAdapters();
        using (var k = Registry.CurrentUser.OpenSubKey(Adapter0000, writable: true)!)
            k.SetValue(IntelGpuService.DpstLevel, new byte[] { 6, 0, 0, 0 }, RegistryValueKind.Binary);
        _svc.WriteValue(adapters, IntelGpuService.DpstLevel, 1);
        using var k2 = Registry.CurrentUser.OpenSubKey(Adapter0000, writable: false)!;
        Assert.Equal(RegistryValueKind.Binary, k2.GetValueKind(IntelGpuService.DpstLevel));
        Assert.Equal(1, IntelGpuService.ParseValue(k2.GetValue(IntelGpuService.DpstLevel)));
    }

    [Fact]
    public void WriteValue_NewKeyDefaultsToDword()
    {
        var adapters = _svc.DetectIntelAdapters();
        _svc.WriteValue(adapters, IntelGpuService.RC6, 1);
        using var k = Registry.CurrentUser.OpenSubKey(Adapter0000, writable: false)!;
        Assert.Equal(RegistryValueKind.DWord, k.GetValueKind(IntelGpuService.RC6));
    }

    [Fact]
    public void Writes_NeverTouchNonIntelAdapter()
    {
        var adapters = _svc.DetectIntelAdapters();
        _svc.WriteValue(adapters, IntelGpuService.RC6, 0);
        _svc.WriteValue(adapters, IntelGpuService.PowerPolicy, 1);
        foreach (var name in IntelGpuService.ManagedValueNames)
            Assert.Null(Read(Adapter0001, name));
    }

    [Fact]
    public void Write_Reset_Revert_NeverTouchTheActiveAdapter()
    {
        // Second Intel instance with driver-owned defaults the driver re-creates on the
        // active adapter. Systema must never write to NOR delete from it.
        using (var second = Registry.CurrentUser.CreateSubKey(Adapter0002, writable: true)!)
        {
            second.SetValue("DriverDesc", "Intel(R) UHD Graphics");
            second.SetValue("ProviderName", "Intel Corporation");
            second.SetValue(IntelGpuService.Psr2Disable, 0, RegistryValueKind.DWord);
            second.SetValue(IntelGpuService.Psr2DrrsEnable, 1, RegistryValueKind.DWord);
            second.SetValue(IntelGpuService.DpstExtraDimming, 1, RegistryValueKind.DWord);
        }
        var list = _svc.DetectIntelAdapters();
        Assert.Equal(2, list.Count);
        Assert.Equal("0000", list[0].SubKey);

        _svc.WriteValue(list, IntelGpuService.RC6, 0);
        Assert.Equal(0, Read(Adapter0000, IntelGpuService.RC6));
        Assert.Null(Read(Adapter0002, IntelGpuService.RC6));

        _svc.ResetValue(list, IntelGpuService.RC6);
        Assert.Null(Read(Adapter0000, IntelGpuService.RC6));

        _svc.WriteValue(list, IntelGpuService.PowerPolicy, 2);
        _svc.RevertAll(list);
        Assert.Null(Read(Adapter0000, IntelGpuService.PowerPolicy));
        Assert.Equal(0, Read(Adapter0002, IntelGpuService.Psr2Disable));
        Assert.Equal(1, Read(Adapter0002, IntelGpuService.Psr2DrrsEnable));
        Assert.Equal(1, Read(Adapter0002, IntelGpuService.DpstExtraDimming));
    }

    [Fact]
    public void ResolveFeature_PrefersPresentAlias()
    {
        var (name, value) = _svc.ResolveFeature(Adapter0000,
            new[] { IntelGpuService.DrrsEnabled, IntelGpuService.Psr2DrrsEnable });
        Assert.Equal(IntelGpuService.Psr2DrrsEnable, name);
        Assert.Equal(1, value);
    }

    [Fact]
    public void GetValueNames_SeesCapabilityKeys()
    {
        var names = _svc.GetValueNames(Adapter0000);
        Assert.Contains("PSR2Disable", names);
        Assert.Contains("Psr2DrrsEnable", names);
    }

    public void Dispose() => Cleanup();

    private static void Cleanup()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(@"SYSTEM\CurrentControlSet", throwOnMissingSubKey: false); }
        catch { /* best-effort */ }
    }
}
