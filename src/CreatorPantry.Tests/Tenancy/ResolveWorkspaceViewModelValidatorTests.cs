using CreatorPantry.Domain.Models.ViewModels.Tenancy;
using CreatorPantry.Domain.Tenancy;
using CreatorPantry.Domain.Validation.Tenancy;

namespace CreatorPantry.Tests.Tenancy;

public class ResolveWorkspaceViewModelValidatorTests
{
    private readonly ResolveWorkspaceViewModelValidator _validator = new();

    [Theory]
    [InlineData("sams-kitchen")]
    [InlineData("a")]
    [InlineData("workspace-123")]
    public void Well_formed_slugs_pass(string slug) =>
        Assert.True(_validator.Validate(new ResolveWorkspaceViewModel(slug)).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Sams-Kitchen")] // uppercase
    [InlineData("sams_kitchen")] // underscore
    [InlineData("-sams-kitchen")] // leading hyphen
    [InlineData("sams-kitchen-")] // trailing hyphen
    [InlineData("sams--kitchen")] // doubled hyphen
    [InlineData("sams kitchen")] // space
    public void Malformed_slugs_fail(string slug) =>
        Assert.False(_validator.Validate(new ResolveWorkspaceViewModel(slug)).IsValid);

    [Fact]
    public void A_slug_at_the_maximum_length_passes()
    {
        var slug = new string('a', WorkspacePolicy.SlugMaxLength);

        Assert.True(_validator.Validate(new ResolveWorkspaceViewModel(slug)).IsValid);
    }

    [Fact]
    public void A_slug_over_the_maximum_length_fails()
    {
        var slug = new string('a', WorkspacePolicy.SlugMaxLength + 1);

        Assert.False(_validator.Validate(new ResolveWorkspaceViewModel(slug)).IsValid);
    }

    [Fact]
    public void Surrounding_whitespace_is_ignored()
    {
        Assert.True(_validator.Validate(new ResolveWorkspaceViewModel("  sams-kitchen  ")).IsValid);
    }
}
