// WindowsUpdateDependencyTests.cs
//
// Regression guard for the v0.7.9 incident where the "Privacy & Background
// Services" one-click toggle disabled DPS (Diagnostic Policy Service), which
// is a prerequisite for WaaSMedicSvc (Windows Update Medic Service). Without
// WaaSMedicSvc, Windows Update's COM components have no auto-repair path and
// the WU client eventually returns 0x80004002 (E_NOINTERFACE) on scans.
//
// Any service that lives in the dependency chain of Windows Update — or that
// is a dependency of WaaSMedicSvc / TrustedInstaller / wuauserv — MUST be
// excluded from the auto-disable list. We can't enforce that via the runtime
// service-manifest because the registry queries it would require are slow
// and the list is rarely changed; instead, this test pins the names so
// adding a new service that breaks WU again triggers an immediate red build.
//
// If a future maintainer legitimately wants to remove one of these from the
// safe list, they need to update both the constant below and this test —
// the friction is the point.

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Systema.Tests;

public class WindowsUpdateDependencyTests
{
    /// <summary>
    /// Services that must never be auto-disabled by the "Privacy &amp; Background
    /// Services" toggle because Windows Update directly or transitively
    /// depends on them.
    /// </summary>
    private static readonly string[] MustStayInNoRecommendedTag =
    {
        "DPS",            // Diagnostic Policy Service — WaaSMedicSvc dependency
        "WdiServiceHost", // Diagnostic Service Host — shares the chain
        "BITS",           // Background Intelligent Transfer — WU's download channel
        "DoSvc",          // Delivery Optimization — Store + WU download transport
    };

    /// <summary>
    /// Reads <c>ServiceControlService.cs</c> as text and asserts every entry in
    /// <see cref="MustStayInNoRecommendedTag"/> appears inside the
    /// <c>_noRecommendedTag</c> HashSet literal. Text-only check so we don't
    /// have to load Systema.dll (which Application Control blocks on this
    /// machine — see other tests for context).
    /// </summary>
    [Fact]
    public void ServicesCriticalForWindowsUpdate_AreInNoRecommendedTag()
    {
        var path = ServiceFilePath();
        Assert.True(File.Exists(path), $"ServiceControlService.cs not found at {path}");
        var src = File.ReadAllText(path);

        // Extract the contents of the `_noRecommendedTag = new(...) { ... };` block.
        // Non-greedy match up to the matching closing brace+semicolon.
        var match = Regex.Match(
            src,
            @"_noRecommendedTag\s*=\s*new\b[^{]*\{(?<body>.*?)\};",
            RegexOptions.Singleline);

        Assert.True(match.Success, "Could not locate _noRecommendedTag HashSet in ServiceControlService.cs");
        var body = match.Groups["body"].Value;

        var missing = MustStayInNoRecommendedTag
            .Where(name => !Regex.IsMatch(body, $@"""{Regex.Escape(name)}""", RegexOptions.IgnoreCase))
            .ToList();

        Assert.True(missing.Count == 0,
            "These services MUST remain in _noRecommendedTag — disabling any of them " +
            "breaks Windows Update (see WindowsUpdateDependencyTests.cs comment): " +
            string.Join(", ", missing));
    }

    private static string ServiceFilePath()
    {
        var asmDir = Path.GetDirectoryName(typeof(WindowsUpdateDependencyTests).Assembly.Location)!;
        // tests/Systema.Tests/bin/<Cfg>/<TFM>/  →  ../../../../../src/Systema/Services/ServiceControlService.cs
        return Path.GetFullPath(Path.Combine(
            asmDir, "..", "..", "..", "..", "..",
            "src", "Systema", "Services", "ServiceControlService.cs"));
    }
}
