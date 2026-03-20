// ════════════════════════════════════════════════════════════════════════════
// DnsProfile.cs  ·  Named DNS configuration (primary + secondary server pair)
// ════════════════════════════════════════════════════════════════════════════
//
// Simple data record holding a ProfileName, PrimaryDNS, and SecondaryDNS string.
// DnsService ships a built-in list of well-known profiles (Cloudflare, Google,
// etc.) and applies the chosen one by writing directly to registry adapter keys.
//
// RELATED FILES
//   DnsService.cs          — owns the profile list and applies profiles via registry
//   NetworkViewModel.cs    — profile picker ComboBox is bound to DnsProfile items
// ════════════════════════════════════════════════════════════════════════════

namespace Systema.Models;

public class DnsProfile
{
    public string Name { get; set; } = string.Empty;
    public string Primary { get; set; } = string.Empty;
    public string Secondary { get; set; } = string.Empty;
    public bool SupportsDoH { get; set; }
}
