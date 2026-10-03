using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// Shape validation for an approval request. Whether the guide exists, is archived, has that version or says
/// anything at all are facts about the workspace's data, and Business decides those — so the boundaries here
/// are the confirmation and the length of the column the reason is stored in.
/// </summary>
public sealed class BrandStyleGuideApprovalValidatorTests
{
    private static readonly ApproveBrandStyleGuideVersionViewModelValidator Validator = new();

    private static IReadOnlyList<string> Fields(ApproveBrandStyleGuideVersionViewModel model) =>
        [.. Validator.Validate(model).Errors.Select(error => error.PropertyName)];

    [Fact]
    public void A_confirmed_request_is_valid() =>
        Assert.True(Validator.Validate(new ApproveBrandStyleGuideVersionViewModel { Confirmed = true }).IsValid);

    [Fact]
    public void A_reason_is_optional() =>
        Assert.True(Validator.Validate(
            new ApproveBrandStyleGuideVersionViewModel { Confirmed = true, Reason = null }).IsValid);

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void An_unconfirmed_request_is_refused(bool? confirmed) =>
        Assert.Contains("Confirmed", Fields(new ApproveBrandStyleGuideVersionViewModel { Confirmed = confirmed }));

    [Fact]
    public void A_reason_of_exactly_the_column_length_is_valid() =>
        Assert.True(Validator.Validate(new ApproveBrandStyleGuideVersionViewModel
        {
            Confirmed = true,
            Reason = new string('x', BrandPolicy.ReasonMaxLength),
        }).IsValid);

    [Fact]
    public void A_reason_one_character_longer_is_refused() =>
        Assert.Contains("Reason", Fields(new ApproveBrandStyleGuideVersionViewModel
        {
            Confirmed = true,
            Reason = new string('x', BrandPolicy.ReasonMaxLength + 1),
        }));

    /// <summary>
    /// An empty reason is a reason, and stored as one. Only null means "no note", which is what the column
    /// being nullable records — so the validator does not quietly turn blank words into an absent note.
    /// </summary>
    [Fact]
    public void An_empty_reason_is_accepted_as_given() =>
        Assert.True(Validator.Validate(
            new ApproveBrandStyleGuideVersionViewModel { Confirmed = true, Reason = string.Empty }).IsValid);
}
