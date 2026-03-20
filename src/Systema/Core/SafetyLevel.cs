// ════════════════════════════════════════════════════════════════════════════
// SafetyLevel.cs  ·  Enum for categorizing tweaks by risk level
// ════════════════════════════════════════════════════════════════════════════
//
// Three-value enum (Safe / Caution / Expert) attached to service entries and
// tweak operations. Used by the UI to colour-code badge chips so users can
// quickly assess the risk of enabling or disabling a particular item.
//
// RELATED FILES
//   Models/ServiceInfo.cs                       — carries a SafetyLevel property
//   Core/Converters/SafetyLevelToColorConverter.cs — converts enum to brush for badges
// ════════════════════════════════════════════════════════════════════════════

namespace Systema.Core;

public enum SafetyLevel
{
    Safe,
    ModeratelySafe,
    Advanced
}
