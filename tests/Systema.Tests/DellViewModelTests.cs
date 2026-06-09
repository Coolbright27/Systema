using Systema.ViewModels;
using Xunit;

namespace Systema.Tests;

public class DellViewModelTests
{
    [Theory]
    [InlineData("Dell Inc.")]
    [InlineData("Dell")]
    [InlineData("DELL INC.")]   // case-insensitive
    [InlineData("Alienware (Dell)")]
    public void IsDellManufacturer_MatchesDell(string manufacturer)
    {
        Assert.True(DellViewModel.IsDellManufacturer(manufacturer));
    }

    [Theory]
    [InlineData("LENOVO")]
    [InlineData("HP")]
    [InlineData("ASUSTeK COMPUTER INC.")]
    [InlineData("Micro-Star International Co., Ltd.")]
    [InlineData("")]
    [InlineData(null)]
    public void IsDellManufacturer_RejectsNonDell(string? manufacturer)
    {
        Assert.False(DellViewModel.IsDellManufacturer(manufacturer));
    }
}
