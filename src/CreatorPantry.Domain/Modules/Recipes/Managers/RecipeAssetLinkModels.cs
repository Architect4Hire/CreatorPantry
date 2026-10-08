using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The body of <c>POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/asset-links</c> (RCPUB-005): one
/// library asset put to one use on one recipe.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No workspace and no owner.</strong> The workspace is resolved from the route and the caller's
/// membership; the asset is resolved inside it before anything is written, and the foreign key behind the link
/// makes a cross-workspace one unrepresentable rather than merely refused (tenancy.md).
/// </para>
/// <para>
/// <strong>Nothing about the asset.</strong> No title, alt text, media type or address: a link records a use,
/// and everything true of the picture stays on the asset, where there is one place to correct it (media.md).
/// <see cref="Caption"/> is the exception because a caption is written for this recipe.
/// </para>
/// <para>
/// <strong>No position.</strong> A new link goes to the end. Reordering is an edit of its own and not part of
/// linking.
/// </para>
/// </remarks>
public sealed record LinkRecipeAssetViewModel
{
    /// <summary>The asset to link. Required, and must be in this workspace's library.</summary>
    public Guid? MediaAssetId { get; init; }

    /// <summary>What the asset is doing on the recipe. Required.</summary>
    public RecipeAssetRole? Role { get; init; }

    /// <summary>
    /// The instruction step the image belongs to. Required when <see cref="Role"/> is
    /// <see cref="RecipeAssetRole.Step"/>, and refused for every other role.
    /// </summary>
    public Guid? InstructionStepId { get; init; }

    /// <summary>
    /// The version of the asset to pin, from 1. Omit it to follow whichever version is current.
    /// </summary>
    public int? VersionNumber { get; init; }

    /// <summary>The caption written for this recipe. Optional. Not alt text, which belongs to the asset.</summary>
    public string? Caption { get; init; }

    /// <summary>
    /// The <c>concurrencyToken</c> from the recipe this link was composed against. Required: a link is an edit
    /// to the recipe, and one made against a recipe that has since changed is refused rather than applied.
    /// </summary>
    public string? ExpectedConcurrencyToken { get; init; }
}

/// <summary>
/// The body of <c>DELETE /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/asset-links/{linkId}</c>.
/// </summary>
/// <remarks>
/// One field, and it is not about the link. Which link is the route's to say; this says what the caller
/// believes the recipe looks like, for the reason <see cref="LinkRecipeAssetViewModel.ExpectedConcurrencyToken"/>
/// gives.
/// </remarks>
public sealed record UnlinkRecipeAssetViewModel
{
    /// <inheritdoc cref="LinkRecipeAssetViewModel.ExpectedConcurrencyToken"/>
    public string? ExpectedConcurrencyToken { get; init; }
}

/// <summary>Shape checks on a link request. What the ids resolve to is the facade's and Business's to decide.</summary>
public sealed class LinkRecipeAssetViewModelValidator : AbstractValidator<LinkRecipeAssetViewModel>
{
    public LinkRecipeAssetViewModelValidator()
    {
        RuleFor(model => model.MediaAssetId)
            .Must(id => id is { } value && value != Guid.Empty)
            .WithMessage("Choose the picture to link.");

        RuleFor(model => model.Role)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Say what the picture is for.")
            .Must(role => role is { } value && Enum.IsDefined(value))
                .WithMessage("That is not something a picture can be linked as.");

        // The two halves of a step image, checked here because either alone is a malformed request rather
        // than a refused one: the database would say the same thing, as a constraint violation nobody reads.
        RuleFor(model => model.InstructionStepId)
            .Must(id => id is { } value && value != Guid.Empty)
            .When(model => model.Role == RecipeAssetRole.Step)
            .WithMessage("Say which step the picture belongs to.");

        RuleFor(model => model.InstructionStepId)
            .Null()
            .When(model => model.Role is { } role && role != RecipeAssetRole.Step)
            .WithMessage("Only a step image belongs to a step.");

        RuleFor(model => model.VersionNumber)
            .GreaterThanOrEqualTo(1)
            .When(model => model.VersionNumber is not null)
            .WithMessage("Version numbers start at 1.");

        RuleFor(model => (model.Caption ?? string.Empty).Trim())
            .MaximumLength(RecipePolicy.CaptionMaxLength)
            .OverridePropertyName(nameof(LinkRecipeAssetViewModel.Caption));

        RuleFor(model => model.ExpectedConcurrencyToken)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Send the recipe's concurrency token with your link.")
            .Must(RecipeConcurrencyToken.IsWellFormed)
                .WithMessage("That is not a concurrency token this API issued.");
    }
}

/// <inheritdoc cref="LinkRecipeAssetViewModelValidator"/>
public sealed class UnlinkRecipeAssetViewModelValidator : AbstractValidator<UnlinkRecipeAssetViewModel>
{
    public UnlinkRecipeAssetViewModelValidator()
    {
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Send the recipe's concurrency token with your unlink.")
            .Must(RecipeConcurrencyToken.IsWellFormed)
                .WithMessage("That is not a concurrency token this API issued.");
    }
}

/// <summary>A link request, validated and normalized: every field is what it claims to be.</summary>
public sealed record CanonicalRecipeAssetLink
{
    public required Guid MediaAssetId { get; init; }

    public required RecipeAssetRole Role { get; init; }

    public Guid? InstructionStepId { get; init; }

    public int? VersionNumber { get; init; }

    public string? Caption { get; init; }

    public required string ExpectedConcurrencyToken { get; init; }

    public static CanonicalRecipeAssetLink From(LinkRecipeAssetViewModel model)
    {
        var caption = model.Caption?.Trim();

        return new CanonicalRecipeAssetLink
        {
            // Non-null by validation, which refuses each of these before anything canonicalizes it.
            MediaAssetId = model.MediaAssetId!.Value,
            Role = model.Role!.Value,
            InstructionStepId = model.InstructionStepId,
            VersionNumber = model.VersionNumber,
            Caption = string.IsNullOrEmpty(caption) ? null : caption,
            ExpectedConcurrencyToken = model.ExpectedConcurrencyToken!,
        };
    }

    /// <summary>
    /// What this request asked for, for the idempotency fingerprint.
    /// </summary>
    /// <remarks>
    /// The recipe and the token are part of it, not only the link: the same link against a different state of
    /// the recipe is a different request, and a caller who re-read before retrying should be told the key was
    /// reused rather than handed the earlier answer.
    /// </remarks>
    public object Fingerprint(Guid recipeId) => new
    {
        RecipeId = recipeId,
        MediaAssetId,
        Role,
        InstructionStepId,
        VersionNumber,
        Caption,
        ExpectedConcurrencyToken,
    };
}
