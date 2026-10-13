using FluentValidation;

namespace CreatorPantry.Domain.Modules.Publishing.Managers;

/// <summary>
/// A creator's request to stop one publication going out (PUB-003).
/// </summary>
/// <remarks>
/// <para>
/// It carries no record id — that is the route's — and no workspace. The only thing in the body is the token
/// of the state the caller was looking at.
/// </para>
/// <para>
/// <strong>The token is required, and it is not ceremony.</strong> The race it catches is the one that
/// matters: a post going out between the moment a screen was drawn and the moment Cancel was clicked. Quoting
/// what was read turns that into a refusal the creator can understand instead of a cancellation aimed at a
/// state that no longer exists.
/// </para>
/// </remarks>
public sealed class CancelPublishingViewModel
{
    /// <summary>The <c>concurrencyToken</c> of the record as the caller last read it.</summary>
    public string? ExpectedConcurrencyToken { get; init; }
}

/// <summary>Shape checks for a cancellation.</summary>
public static class CancelPublishingInputChecks
{
    public static IEnumerable<(string Field, string Error)> Cancellation(CancelPublishingViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (string.IsNullOrWhiteSpace(model.ExpectedConcurrencyToken))
        {
            yield return (
                nameof(model.ExpectedConcurrencyToken),
                "Quote the concurrencyToken of the record you read.");
        }
        else if (!PublishingRecordConcurrencyToken.IsWellFormed(model.ExpectedConcurrencyToken))
        {
            yield return (nameof(model.ExpectedConcurrencyToken), "That is not a token this API issued.");
        }
    }
}

public sealed class CancelPublishingViewModelValidator : AbstractValidator<CancelPublishingViewModel>
{
    public CancelPublishingViewModelValidator() =>
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in CancelPublishingInputChecks.Cancellation(model))
            {
                context.AddFailure(field, message);
            }
        });
}
