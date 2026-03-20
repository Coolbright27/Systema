// ════════════════════════════════════════════════════════════════════════════
// AdminCheckService.cs  ·  Static utility to verify administrator privilege
// ════════════════════════════════════════════════════════════════════════════
//
// Exposes a single static method AdminCheckService.IsAdmin() that checks the
// current Windows identity for elevation. Called at app startup to gate launch;
// the app manifest also requests elevation so normal users see a UAC prompt.
//
// RELATED FILES
//   App.xaml.cs  — calls IsAdmin() and shows an error dialog then exits if false
// ════════════════════════════════════════════════════════════════════════════

using System.Security.Principal;

namespace Systema.Services;

public static class AdminCheckService
{
    public static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
