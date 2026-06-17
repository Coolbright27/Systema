namespace Systema.Models;

public class OptionalFeatureInfo
{
    public string Name        { get; set; } = string.Empty;
    public string State       { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Always-populated description for the UI: the curated text when we have
    /// one, otherwise a friendly generic note so no row is ever left bare (many DISM
    /// feature names are cryptic and unknown to most users).</summary>
    public string DisplayDescription =>
        string.IsNullOrWhiteSpace(Description)
            ? "Optional Windows feature. Safe to leave on if you're not sure what it does."
            : Description;
    public bool IsEnabled => State.Contains("Enabled", StringComparison.OrdinalIgnoreCase)
                          && !State.Contains("Disabled", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// When true, the feature is unsafe or obsolete and should be removed.
    /// Surfaces a "REMOVE RECOMMENDED" badge in the Optional Features list.
    /// </summary>
    public bool IsRecommendedToRemove { get; set; }
}
