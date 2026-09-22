using CreatorPantry.Domain.Models.ViewModels.Auth;
using CreatorPantry.Domain.Validation.Auth;

namespace CreatorPantry.Tests.Auth;

public class RegisterUserViewModelValidatorTests
{
    private readonly RegisterUserViewModelValidator _validator = new();

    [Fact]
    public void Valid_request_passes()
    {
        var result = _validator.Validate(Valid());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Surrounding_whitespace_is_ignored()
    {
        var model = Valid();
        model.Email = "  cook@example.com  ";
        model.DisplayName = "  Sam  ";

        Assert.True(_validator.Validate(model).IsValid);
    }

    [Theory]
    [InlineData(12, true)]
    [InlineData(11, false)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void Password_length_is_bounded(int length, bool valid)
    {
        var model = Valid();
        model.Password = new string('a', length); // no composition rules

        Assert.Equal(valid, _validator.Validate(model).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData(null)]
    public void Invalid_email_is_rejected_on_the_email_field(string? email)
    {
        var model = Valid();
        model.Email = email!;

        var result = _validator.Validate(model);

        Assert.Contains(result.Errors, error => error.PropertyName == nameof(RegisterUserViewModel.Email));
    }

    [Fact]
    public void Email_longer_than_256_characters_is_rejected()
    {
        var model = Valid();
        model.Email = new string('a', 245) + "@example.com"; // 257

        Assert.False(_validator.Validate(model).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Display_name_is_required(string? displayName)
    {
        var model = Valid();
        model.DisplayName = displayName!;

        var result = _validator.Validate(model);

        Assert.Contains(result.Errors, error => error.PropertyName == nameof(RegisterUserViewModel.DisplayName));
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void Display_name_is_at_most_100_characters(int length, bool valid)
    {
        var model = Valid();
        model.DisplayName = new string('d', length);

        Assert.Equal(valid, _validator.Validate(model).IsValid);
    }

    [Fact]
    public void Null_password_is_rejected_on_the_password_field()
    {
        var model = Valid();
        model.Password = null!;

        var result = _validator.Validate(model);

        Assert.Contains(result.Errors, error => error.PropertyName == nameof(RegisterUserViewModel.Password));
    }

    internal static RegisterUserViewModel Valid() => new()
    {
        Email = "cook@example.com",
        Password = "correct horse battery",
        DisplayName = "Sam",
    };
}
