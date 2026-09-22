using CreatorPantry.Domain.Models.ViewModels.Tenancy;
using CreatorPantry.Domain.Tenancy;
using CreatorPantry.Domain.Validation.Tenancy;

namespace CreatorPantry.Tests.Tenancy;

public sealed class CreateWorkspaceViewModelValidatorTests
{
    private readonly CreateWorkspaceViewModelValidator _validator = new();

    [Fact]
    public void A_reasonable_name_is_valid() =>
        Assert.True(_validator.Validate(new CreateWorkspaceViewModel("Sam's Kitchen")).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_or_blank_name_is_invalid(string name) =>
        Assert.False(_validator.Validate(new CreateWorkspaceViewModel(name)).IsValid);

    [Fact]
    public void A_name_over_the_max_length_is_invalid() =>
        Assert.False(_validator.Validate(new CreateWorkspaceViewModel(new string('a', WorkspacePolicy.NameMaxLength + 1))).IsValid);

    [Fact]
    public void A_name_at_the_max_length_is_valid() =>
        Assert.True(_validator.Validate(new CreateWorkspaceViewModel(new string('a', WorkspacePolicy.NameMaxLength))).IsValid);
}
