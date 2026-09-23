using CreatorPantry.Domain.Managers.Patching;
using CreatorPantry.Domain.Managers.Reference;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Shape and format validation for <see cref="UpdateRecipeViewModel"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every content rule is gated on <see cref="PatchField{T}.IsSubmitted"/>.</strong> A field the body
/// never mentioned is not being changed, so validating it would refuse a perfectly good edit because of
/// something the caller did not touch — and on a recipe stored before a limit tightened, that would leave
/// the creator unable to edit their own work at all.
/// </para>
/// <para>
/// <strong>What is deliberately not here.</strong> Whether a submitted cuisine, course, technique or unit
/// exists and is still offered needs a lookup, so it stays above, in the facade. Whether the recipe's total
/// time remains coherent, whether a yield unit still has a quantity beside it, and whether that unit is a
/// temperature are all judgements about the recipe <em>as it would end up</em> — a patch names only part of
/// it, so none of them can be answered from this body alone. They belong to Business, which can see the
/// merged result. That is the one structural difference from
/// <see cref="CreateRecipeViewModelValidator"/>, where a complete request made the yield pairing answerable
/// at the edge.
/// </para>
/// <para>
/// Nothing here trusts the binder: every string is treated as nullable and trimmed before it is measured.
/// </para>
/// </remarks>
public sealed class UpdateRecipeViewModelValidator : AbstractValidator<UpdateRecipeViewModel>
{
    public UpdateRecipeViewModelValidator()
    {
        // Required, and worth stating before anything else: an edit with nothing to check against cannot be
        // applied safely whatever else it says.
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Send the recipe's concurrency token with your edit.")
            // Refused here rather than compared later, on purpose. A token that could never have been issued
            // is a client bug, and reporting it as a conflict would send a creator looking for a collaborator
            // who does not exist.
            .Must(RecipeConcurrencyToken.IsWellFormed)
                .WithMessage("That is not a concurrency token this API issued.")
            .OverridePropertyName(nameof(UpdateRecipeViewModel.ExpectedConcurrencyToken));

        RuleFor(model => (model.Reason ?? string.Empty).Trim())
            .MaximumLength(RecipePolicy.NoteMaxLength)
            .OverridePropertyName(nameof(UpdateRecipeViewModel.Reason));

        // The one content field that may be changed but never cleared. Null and whitespace get the same
        // answer, because both would leave the recipe without a name.
        RuleFor(model => model.Title)
            .Cascade(CascadeMode.Stop)
            .Must(field => !string.IsNullOrWhiteSpace(field.Value))
                .WithMessage("A recipe must keep a title.")
            .Must(field => Trimmed(field).Length <= RecipePolicy.TitleMaxLength)
                .WithMessage($"A title can be at most {RecipePolicy.TitleMaxLength} characters.")
            .When(model => model.Title.IsSubmitted)
            .OverridePropertyName(nameof(UpdateRecipeViewModel.Title));

        Text(model => model.Description, RecipePolicy.DescriptionMaxLength, nameof(UpdateRecipeViewModel.Description));
        Text(model => model.Headnote, RecipePolicy.LongTextMaxLength, nameof(UpdateRecipeViewModel.Headnote));
        Text(model => model.Notes, RecipePolicy.LongTextMaxLength, nameof(UpdateRecipeViewModel.Notes));
        Text(model => model.StorageNotes, RecipePolicy.LongTextMaxLength, nameof(UpdateRecipeViewModel.StorageNotes));
        Text(model => model.AttributionText, RecipePolicy.AttributionMaxLength, nameof(UpdateRecipeViewModel.AttributionText));
        Text(model => model.YieldText, RecipePolicy.YieldTextMaxLength, nameof(UpdateRecipeViewModel.YieldText));

        // Absolute http/https only, exactly as on create: a relative path means nothing outside this
        // workspace's own site, and refusing other schemes keeps a `javascript:` or `data:` value from being
        // stored and later rendered as a link. Clearing it is allowed — it is a link, not a name.
        RuleFor(model => model.SourceUrl)
            .Cascade(CascadeMode.Stop)
            .Must(field => Trimmed(field).Length <= RecipePolicy.UrlMaxLength)
                .WithMessage($"A web address can be at most {RecipePolicy.UrlMaxLength} characters.")
            .Must(field => BeAnHttpUrl(field.Value))
                .WithMessage("Enter a full web address beginning with http:// or https://.")
            .When(model => model.SourceUrl.IsSubmitted && !string.IsNullOrWhiteSpace(model.SourceUrl.Value))
            .OverridePropertyName(nameof(UpdateRecipeViewModel.SourceUrl));

        Minutes(model => model.PrepTimeMinutes, nameof(UpdateRecipeViewModel.PrepTimeMinutes));
        Minutes(model => model.CookTimeMinutes, nameof(UpdateRecipeViewModel.CookTimeMinutes));
        Minutes(model => model.RestTimeMinutes, nameof(UpdateRecipeViewModel.RestTimeMinutes));
        Minutes(model => model.TotalTimeMinutes, nameof(UpdateRecipeViewModel.TotalTimeMinutes));

        RuleFor(model => model.YieldQuantity)
            .Must(field => field.Value is null or > 0m)
                .WithMessage("A yield must be greater than zero.")
            .When(model => model.YieldQuantity.IsSubmitted)
            .OverridePropertyName(nameof(UpdateRecipeViewModel.YieldQuantity));

        Reference(model => model.CuisineId, nameof(UpdateRecipeViewModel.CuisineId));
        Reference(model => model.CourseId, nameof(UpdateRecipeViewModel.CourseId));
        Reference(model => model.PrimaryTechniqueId, nameof(UpdateRecipeViewModel.PrimaryTechniqueId));
        Reference(model => model.YieldUnitId, nameof(UpdateRecipeViewModel.YieldUnitId));

        // Changeable, never clearable — a recipe is always somewhere in the creator's workflow. Archived is
        // accepted here, unlike on a create: archiving is a state a recipe is moved to, and this is the
        // route that moves it.
        RuleFor(model => model.Status)
            .Cascade(CascadeMode.Stop)
            .Must(field => field.Value is not null)
                .WithMessage("A recipe must keep an editorial state.")
            // Enum.IsDefined rather than a cast, for the reason the create validator records: a cast accepts
            // 99 happily, so an undefined status would be stored now and refused later — and narrowing an
            // accepted value afterwards is a breaking change (api-contract.md).
            .Must(field => Enum.IsDefined(field.Value!.Value))
                .WithMessage("That is not a recipe status.")
            .When(model => model.Status.IsSubmitted)
            .OverridePropertyName(nameof(UpdateRecipeViewModel.Status));

        // A submitted list replaces the whole set; null and [] both clear it. The same rules as a create,
        // because the resulting set has to satisfy the same things however it got there.
        RuleFor(model => model.Tags)
            .Cascade(CascadeMode.Stop)
            .Must(field => Tags(field).Count <= TagPolicy.MaxTagsPerRecipe)
                .WithMessage($"A recipe can carry at most {TagPolicy.MaxTagsPerRecipe} tags.")
            .Must(field => Tags(field).All(tag => !string.IsNullOrWhiteSpace(tag)))
                .WithMessage("A tag cannot be blank.")
            .Must(field => Tags(field).All(tag => tag!.Trim().Length <= TagPolicy.NameMaxLength))
                .WithMessage($"A tag can be at most {TagPolicy.NameMaxLength} characters.")
            // Normalized, because "Weeknight" and "weeknight" are the same tag and sending both is a request
            // to apply one tag twice — which the primary key would refuse further down with a far worse
            // message than this one.
            .Must(field => HaveNoRepeatedTags(Tags(field)))
                .WithMessage("The same tag is listed more than once.")
            .When(model => model.Tags.IsSubmitted)
            .OverridePropertyName(nameof(UpdateRecipeViewModel.Tags));
    }

    /// <summary>
    /// A length limit on a clearable text field, applied only when the field was submitted.
    /// </summary>
    /// <remarks>
    /// The lambda is a plain <see cref="Func{T, TResult}"/> rather than a member expression, so
    /// FluentValidation cannot infer a name from it — hence the explicit <paramref name="name"/>.
    /// <see cref="CreateRecipeViewModelValidator"/> validates computed expressions the same way.
    /// </remarks>
    private void Text(Func<UpdateRecipeViewModel, PatchField<string?>> field, int maxLength, string name) =>
        RuleFor(model => field(model))
            .Must(submitted => Trimmed(submitted).Length <= maxLength)
                .WithMessage($"That can be at most {maxLength} characters.")
            .When(model => field(model).IsSubmitted)
            .OverridePropertyName(name);

    private void Minutes(Func<UpdateRecipeViewModel, PatchField<int?>> field, string name) =>
        RuleFor(model => field(model))
            .Must(submitted => submitted.Value is null or (>= 0 and <= RecipePolicy.MaxTimeMinutes))
                .WithMessage("Enter a time in minutes between 0 and one year.")
            .When(model => field(model).IsSubmitted)
            .OverridePropertyName(name);

    private void Reference(Func<UpdateRecipeViewModel, PatchField<Guid?>> field, string name) =>
        RuleFor(model => field(model))
            .Must(submitted => submitted.Value != Guid.Empty)
                .WithMessage("That is not a valid reference.")
            .When(model => field(model).IsSubmitted)
            .OverridePropertyName(name);

    private static string Trimmed(PatchField<string?> field) => (field.Value ?? string.Empty).Trim();

    private static IReadOnlyList<string?> Tags(PatchField<IReadOnlyList<string?>?> field) => field.Value ?? [];

    private static bool BeAnHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool HaveNoRepeatedTags(IReadOnlyList<string?> tags)
    {
        var normalized = tags
            .Select(tag => NameNormalization.NormalizeName(tag ?? string.Empty))
            .ToList();

        return normalized.Distinct(StringComparer.Ordinal).Count() == normalized.Count;
    }
}
