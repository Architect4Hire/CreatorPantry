using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Publishing.Managers;

/// <summary>
/// One creator's confirmation that a piece of content should go out to one profile at one time (PUB-002).
/// </summary>
/// <remarks>
/// <para>
/// <strong>It carries no workspace and no time zone.</strong> The workspace is the resolved context's, and the
/// zone is the workspace's own scheduling zone, read from its brand profile — a request that could name either
/// would be a request that could schedule into somebody else's workspace or at an hour the creator did not
/// pick (tenancy.md, publishing.md).
/// </para>
/// <para>
/// <strong><see cref="Confirmed"/> is the explicit confirmation</strong> publishing.md requires, not a
/// formality: there is no automation policy that can stand in for it yet, so a body without it is refused.
/// </para>
/// </remarks>
public sealed class SchedulePublishingViewModel
{
    /// <summary>The board card this publication is for.</summary>
    public Guid ContentCardId { get; init; }

    /// <summary>CreatorPantry's own id for the profile, as the profiles route gave it. Never a provider's.</summary>
    public Guid PublishingProfileId { get; init; }

    /// <summary>
    /// The network the caller believes that profile posts to, when it wants that checked.
    /// </summary>
    /// <remarks>
    /// Optional, and a guard rather than an instruction: the platform that is recorded is always the
    /// profile's. A caller that sends one it read from a stale profile list is told its copy is out of date
    /// instead of publishing to a network it did not mean.
    /// </remarks>
    public PublishingPlatform? ExpectedPlatform { get; init; }

    /// <summary>The text to publish, exactly as it should go out.</summary>
    public string? Text { get; init; }

    /// <summary>A library asset to publish with it, and the exact version. Both or neither.</summary>
    public Guid? MediaAssetId { get; init; }

    /// <inheritdoc cref="MediaAssetId"/>
    public int? MediaAssetVersionNumber { get; init; }

    /// <summary>When it should be published, as an instant. Must be in the future.</summary>
    public DateTimeOffset? ScheduledAt { get; init; }

    /// <summary>True when the creator has confirmed this publication. Anything else is refused.</summary>
    public bool Confirmed { get; init; }
}

/// <summary>Shape checks for a confirmation: what can be judged without a clock, a database or a provider.</summary>
public static class SchedulePublishingInputChecks
{
    public static IEnumerable<(string Field, string Error)> Confirmation(SchedulePublishingViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.ContentCardId == Guid.Empty)
        {
            yield return (nameof(model.ContentCardId), "Say which card this publication is for.");
        }

        if (model.PublishingProfileId == Guid.Empty)
        {
            yield return (nameof(model.PublishingProfileId), "Say which profile to publish to.");
        }

        if (string.IsNullOrWhiteSpace(model.Text))
        {
            yield return (nameof(model.Text), "There is nothing to publish.");
        }
        else if (model.Text.Length > PublishingLedgerPolicy.ContentTextMaxLength)
        {
            yield return (
                nameof(model.Text),
                $"That is longer than the {PublishingLedgerPolicy.ContentTextMaxLength:N0} characters a publication can carry.");
        }

        // A version pin without its asset names a version of nothing, and an asset without one is a picture
        // nobody can reproduce. The ledger's own check constraint refuses the pair; this says which field.
        if (model.MediaAssetId is null && model.MediaAssetVersionNumber is not null)
        {
            yield return (nameof(model.MediaAssetId), "Say which picture that version belongs to.");
        }

        if (model.MediaAssetId is not null && model.MediaAssetVersionNumber is null)
        {
            yield return (nameof(model.MediaAssetVersionNumber), "Say which version of that picture to publish.");
        }

        if (model.MediaAssetVersionNumber is { } version && version < 1)
        {
            yield return (nameof(model.MediaAssetVersionNumber), "That is not a version number.");
        }

        if (model.ScheduledAt is null)
        {
            yield return (nameof(model.ScheduledAt), "Say when this should be published.");
        }

        if (model.ExpectedPlatform is { } platform && !Enum.IsDefined(platform))
        {
            yield return (nameof(model.ExpectedPlatform), "That is not a network this server knows.");
        }

        if (!model.Confirmed)
        {
            yield return (nameof(model.Confirmed), "Confirm the publication before it can be scheduled.");
        }
    }

    /// <summary>
    /// The idempotency key, which this command requires.
    /// </summary>
    /// <remarks>
    /// Required rather than optional, as every command with an irreversible effect outside this server
    /// requires one: the provider offers no key of its own, so a retry with no key would be a second post
    /// nobody asked for (api-contract.md).
    /// </remarks>
    public static OperationError? Key(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return new OperationError(
                IdempotencyPolicy.KeyRequiredCode,
                $"This request requires an {IdempotencyPolicy.KeyHeader} header, so that a retry cannot publish twice.",
                new Dictionary<string, string[]>());
        }

        // The shared policy's own rule, to the character: a key this command accepted but the rest of the
        // product would refuse would be a key that means one thing here and another everywhere else.
        var key = idempotencyKey.Trim();

        return key.Length > IdempotencyPolicy.KeyMaxLength || !key.All(character => character is >= '!' and <= '~')
            ? new OperationError(
                IdempotencyPolicy.KeyInvalidCode,
                $"{IdempotencyPolicy.KeyHeader} must be 1–{IdempotencyPolicy.KeyMaxLength} printable ASCII characters.",
                new Dictionary<string, string[]>())
            : null;
    }
}

public sealed class SchedulePublishingViewModelValidator : AbstractValidator<SchedulePublishingViewModel>
{
    public SchedulePublishingViewModelValidator() =>
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in SchedulePublishingInputChecks.Confirmation(model))
            {
                context.AddFailure(field, message);
            }
        });
}
