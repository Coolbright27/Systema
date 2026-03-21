namespace Systema.Models;

public class OptionalFeatureInfo
{
    public string Name        { get; set; } = string.Empty;
    public string State       { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsEnabled => State.Contains("Enabled", StringComparison.OrdinalIgnoreCase)
                          && !State.Contains("Disabled", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// When true, the feature is unsafe or obsolete and should be removed.
    /// Surfaces a "REMOVE RECOMMENDED" badge in the Optional Features list.
    /// </summary>
    public bool IsRecommendedToRemove { get; set; }
}
