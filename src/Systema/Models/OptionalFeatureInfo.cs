namespace Systema.Models;

public class OptionalFeatureInfo
{
    public string Name        { get; set; } = string.Empty;
    public string State       { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsEnabled => State.Contains("Enabled", StringComparison.OrdinalIgnoreCase)
                          && !State.Contains("Disabled", StringComparison.OrdinalIgnoreCase);
}
