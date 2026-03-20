// ════════════════════════════════════════════════════════════════════════════
// TweakResult.cs  ·  Simple result record returned by all tweak operations
// ════════════════════════════════════════════════════════════════════════════
//
// Lightweight record with a Success bool and a Message string. All service
// methods that apply OS changes return TweakResult so ViewModels can surface
// a consistent success/failure toast or status message without catching raw
// exceptions in the UI layer.
//
// RELATED FILES
//   (returned by tweak methods across most Services in src/Systema/Services/)
// ════════════════════════════════════════════════════════════════════════════

namespace Systema.Core;

public record TweakResult(bool Success, string Message)
{
    public static TweakResult Ok(string message = "Done") => new(true, message);
    public static TweakResult Fail(string message) => new(false, message);
    public static TweakResult FromException(Exception ex) => new(false, ex.Message);
}
