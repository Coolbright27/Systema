using Systema.ViewModels;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// Home's device header shows the PC's model under its name, like Windows Settings. Custom-built
/// desktops report placeholder strings from the motherboard firmware, which must never be shown.
/// </summary>
public class HomeDeviceHeaderTests
{
    [Theory]
    [InlineData("Dell Inc.", "Precision 5560", "Precision 5560")]
    [InlineData("LENOVO", "20XW004GUS", "20XW004GUS")]
    [InlineData("ASUS", "System Product Name", "ASUS")]
    [InlineData("To Be Filled By O.E.M.", "To Be Filled By O.E.M.", "Windows PC")]
    [InlineData("System manufacturer", "System Product Name", "Windows PC")]
    [InlineData("Default string", "Default string", "Windows PC")]
    [InlineData("", "", "Windows PC")]
    public void Model_NeverShowsFirmwarePlaceholders(string vendor, string model, string expected) =>
        Assert.Equal(expected, DashboardViewModel.CleanModel(vendor, model));

    [Fact]
    public void DeviceName_IsNeverEmpty() =>
        Assert.False(string.IsNullOrWhiteSpace(DashboardViewModel.ReadDeviceName()));
}
