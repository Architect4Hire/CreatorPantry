using CreatorPantry.Domain.Managers.Reference;
using FluentValidation;
using FluentValidation.Results;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Shape and format validation for <see cref="CreateRecipeViewModel"/>.
/// </summary>
/// <remarks>
/// <para>
/// Edge validation only (backend.md). Anything needing a database lookup or a domain judgement stays in
/// Business: that the cuisine, course, technique and unit ids name rows that exist and are still active,
/// that the yield unit is not a temperature, and whether a stated total time is coherent with its parts.
/// The last one is deliberate rather than an omission — a creator's total is a fact in its own right, and
/// deciding when it is implausible is a domain question, not a format one.
/// </para>
/// <para>
/// Nothing here trusts the binder. Every string is treated as nullable and trimmed before it is measured,
/// because a body may send <c>null</c>, whitespace, or padding for any field.
/// </para>
/// </remarks>
public sealed class CreateRecipeViewModelValidator : AbstractValidator<CreateRecipeViewModel>
{
    public CreateRecipeViewModelValidator()
    {
        RuleFor(model => (model.Title ?? string.Empty).Trim())
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Enter a recipe title.")
            .MaximumLength(RecipePolicy.TitleMaxLength)
            .OverridePropertyName(nameof(CreateRecipeViewModel.Title));

        Optional(model => model.Description, RecipePolicy.DescriptionMaxLength, nameof(CreateRecipeViewModel.Description));
        Optional(model => model.Headnote, RecipePolicy.LongTextMaxLength, nameof(CreateRecipeViewModel.Headnote));
        Optional(model => model.Notes, RecipePolicy.LongTextMaxLength, nameof(CreateRecipeViewModel.Notes));
        Optional(model => model.StorageNotes, RecipePolicy.LongTextMaxLength, nameof(CreateRecipeViewModel.StorageNotes));
        Optional(model => model.AttributionText, RecipePolicy.AttributionMaxLength, nameof(CreateRecipeViewModel.AttributionText));
        Optional(model => model.YieldText, RecipePolicy.YieldTextMaxLength, nameof(CreateRecipeViewModel.YieldText));

        RuleFor(model => model.SourceUrl)
            .Cascade(CascadeMode.Stop)
            .MaximumLength(RecipePolicy.UrlMaxLength)
            // Absolute http/https only. A relative path has no meaning outside this workspace's own site, and
            // rejecting other schemes here keeps a `javascript:` or `data:` value from ever being stored and
            // later rendered as a link.
            .Must(BeAnHttpUrl).WithMessage("Enter a full web address beginning with http:// or https://.")
            .When(model => !string.IsNullOrWhiteSpace(model.SourceUrl));

        Minutes(model => model.PrepTimeMinutes, nameof(CreateRecipeViewModel.PrepTimeMinutes));
        Minutes(model => model.CookTimeMinutes, nameof(CreateRecipeViewModel.CookTimeMinutes));
        Minutes(model => model.RestTimeMinutes, nameof(CreateRecipeViewModel.RestTimeMinutes));
        Minutes(model => model.TotalTimeMinutes, nameof(CreateRecipeViewModel.TotalTimeMinutes));

        RuleFor(model => model.YieldQuantity)
            .GreaterThan(0).WithMessage("A yield must be greater than zero.")
            .When(model => model.YieldQuantity.HasValue);

        // Mirrors CK_Recipes_YieldUnit_RequiresQuantity. Validated here as well so the failure is a readable
        // 400 rather than a database error surfacing as a 500. The reverse is allowed: "makes 12" with no
        // unit is how creators routinely write a yield.
        RuleFor(model => model.YieldQuantity)
            .NotNull().WithMessage("Give the yield a number as well as a unit.")
            .When(model => model.YieldUnitId.HasValue)
            .OverridePropertyName(nameof(CreateRecipeViewModel.YieldQuantity));

        RuleFor(model => model.ServingCount)
            .GreaterThan(0).WithMessage("A serving count must be greater than zero.")
            .When(model => model.ServingCount.HasValue);

        RuleFor(model => model.ServingSize)
            .GreaterThan(0).WithMessage("A serving size must be greater than zero.")
            .When(model => model.ServingSize.HasValue);

        // Mirrors CK_Recipes_ServingSize_RequiresYieldUnit, for the reason the pairing above is mirrored: a
        // readable 400 beats a database error surfacing as a 500. The reverse is allowed, and a serving
        // *count* needs no unit at all — "serves 12" is complete on its own.
        RuleFor(model => model.ServingSize)
            .Null().WithMessage("Give the yield a unit as well, so a serving size has something to be measured in.")
            .When(model => !model.YieldUnitId.HasValue)
            .OverridePropertyName(nameof(CreateRecipeViewModel.ServingSize));

        NotEmptyGuid(model => model.CuisineId, nameof(CreateRecipeViewModel.CuisineId));
        NotEmptyGuid(model => model.CourseId, nameof(CreateRecipeViewModel.CourseId));
        NotEmptyGuid(model => model.PrimaryTechniqueId, nameof(CreateRecipeViewModel.PrimaryTechniqueId));
        NotEmptyGuid(model => model.YieldUnitId, nameof(CreateRecipeViewModel.YieldUnitId));

        // Enum.IsDefined rather than a cast, for the reason MeasurementPolicy.ParseDimension already records:
        // a cast accepts 99 happily, and an undefined status would be stored now and refused later —
        // api-contract.md counts narrowing an accepted value as a breaking change, so the narrow reading
        // ships first.
        RuleFor(model => model.Status)
            .Must(status => Enum.IsDefined(status!.Value))
            .WithMessage("That is not a recipe status.")
            .When(model => model.Status.HasValue);

        // Draft is the only state a recipe may be created into. Archived never was allowed — it is a state a
        // recipe is moved to, never one it begins in — and since TESTRUN-005 made editorial state a machine,
        // Approved is not either: a create that reached it would be an approval with no readiness evaluation
        // behind it and no transition recorded, which is the whole thing the machine prevents.
        //
        // Refused here rather than removed from SettableRecipeStatusViewModel, for the reason that type
        // records: leaving a value out of the enum turns this clear field error into a generic
        // unreadable-body 400.
        RuleFor(model => model.Status)
            .Must(status => status == SettableRecipeStatusViewModel.Draft)
            .WithMessage("A new recipe starts out as a draft; move it on with a readiness transition.")
            .When(model => model.Status.HasValue && Enum.IsDefined(model.Status!.Value));

        RuleFor(model => model.Tags!)
            .Cascade(CascadeMode.Stop)
            .Must(tags => tags.Count <= TagPolicy.MaxTagsPerRecipe)
                .WithMessage($"A recipe can carry at most {TagPolicy.MaxTagsPerRecipe} tags.")
            .Must(tags => tags.All(tag => !string.IsNullOrWhiteSpace(tag)))
                .WithMessage("A tag cannot be blank.")
            .Must(tags => tags.All(tag => tag!.Trim().Length <= TagPolicy.NameMaxLength))
                .WithMessage($"A tag can be at most {TagPolicy.NameMaxLength} characters.")
            // Normalized, because "Weeknight" and "weeknight" are the same tag and sending both is a request
            // to apply one tag twice — which the primary key would refuse further down with a far worse
            // message than this one.
            .Must(HaveNoRepeatedTags).WithMessage("The same tag is listed more than once.")
            .When(model => model.Tags is not null)
            .OverridePropertyName(nameof(CreateRecipeViewModel.Tags));

        // List-level only, for the reason the ingredient block below gives.
        RuleFor(model => model.Instructions)
            .Cascade(CascadeMode.Stop)
            .Must(instructions => !Groups(instructions).Any(group => group is null))
                .WithMessage("An instruction group cannot be blank.")
            .Must(instructions => Groups(instructions).Count <= RecipePolicy.MaxInstructionGroupsPerRecipe)
                .WithMessage($"A recipe can carry at most {RecipePolicy.MaxInstructionGroupsPerRecipe} instruction groups.")
            .Must(instructions => Groups(instructions).Sum(group => Steps(group).Count) <= RecipePolicy.MaxInstructionStepsPerRecipe)
                .WithMessage($"A recipe can carry at most {RecipePolicy.MaxInstructionStepsPerRecipe} instruction steps.")
            .Must(instructions => !AllSteps(instructions).Any(step => step is null))
                .WithMessage("An instruction step cannot be blank.")
            .OverridePropertyName(nameof(CreateRecipeViewModel.Instructions));

        // List-level only: a total, or two rows naming one id, is not about any one line. Everything a single
        // group or line can be wrong about on its own is reported at its position instead — see the rule below
        // and RecipeInputPositions.
        RuleFor(model => model.IngredientGroups)
            .Cascade(CascadeMode.Stop)
            .Must(groups => !IngredientGroups(groups).Any(group => group is null))
                .WithMessage("An ingredient group cannot be blank.")
            .Must(groups => IngredientGroups(groups).Count <= RecipePolicy.MaxIngredientGroupsPerRecipe)
                .WithMessage($"A recipe can carry at most {RecipePolicy.MaxIngredientGroupsPerRecipe} ingredient groups.")
            .Must(groups => IngredientGroups(groups).Sum(group => Lines(group).Count) <= RecipePolicy.MaxIngredientLinesPerRecipe)
                .WithMessage($"A recipe can carry at most {RecipePolicy.MaxIngredientLinesPerRecipe} ingredient lines.")
            .Must(groups => !AllLines(groups).Any(line => line is null))
                .WithMessage("An ingredient line cannot be blank.")
            .OverridePropertyName(nameof(CreateRecipeViewModel.IngredientGroups));

        // Each failure keyed to the group or line it is about, so the editor can mark that row. No
        // OverridePropertyName: these rules supply their own paths.
        RuleFor(model => model.IngredientGroups).Custom((groups, context) =>
            RecipeInputPositions.AddIngredientFailures(
                groups, RecipeInputMode.Create, (path, message) => context.AddFailure(new ValidationFailure(path, message))));

        RuleFor(model => model.Instructions).Custom((groups, context) =>
            RecipeInputPositions.AddInstructionFailures(
                groups, RecipeInputMode.Create, (path, message) => context.AddFailure(new ValidationFailure(path, message))));
    }

    private static IReadOnlyList<RecipeInstructionGroupInputViewModel> Groups(IReadOnlyList<RecipeInstructionGroupInputViewModel?>? instructions) =>
        instructions?.OfType<RecipeInstructionGroupInputViewModel>().ToList() ?? [];

    private static IReadOnlyList<RecipeInstructionStepInputViewModel> Steps(RecipeInstructionGroupInputViewModel group) =>
        group.Steps?.OfType<RecipeInstructionStepInputViewModel>().ToList() ?? [];

    private static IEnumerable<RecipeInstructionStepInputViewModel> AllSteps(IReadOnlyList<RecipeInstructionGroupInputViewModel?>? instructions) =>
        Groups(instructions).SelectMany(Steps);

    private static IReadOnlyList<RecipeIngredientGroupInputViewModel> IngredientGroups(IReadOnlyList<RecipeIngredientGroupInputViewModel?>? groups) =>
        groups?.OfType<RecipeIngredientGroupInputViewModel>().ToList() ?? [];

    private static IReadOnlyList<RecipeIngredientInputViewModel> Lines(RecipeIngredientGroupInputViewModel group) =>
        group.Ingredients?.OfType<RecipeIngredientInputViewModel>().ToList() ?? [];

    private static IEnumerable<RecipeIngredientInputViewModel> AllLines(IReadOnlyList<RecipeIngredientGroupInputViewModel?>? groups) =>
        IngredientGroups(groups).SelectMany(Lines);

    private static string Trimmed(string? value) => (value ?? string.Empty).Trim();

    private void Optional(
        System.Linq.Expressions.Expression<Func<CreateRecipeViewModel, string?>> property,
        int maxLength,
        string name) =>
        RuleFor(property)
            .MaximumLength(maxLength)
            .OverridePropertyName(name);

    private void Minutes(
        System.Linq.Expressions.Expression<Func<CreateRecipeViewModel, int?>> property,
        string name) =>
        RuleFor(property)
            .InclusiveBetween(0, RecipePolicy.MaxTimeMinutes)
            .WithMessage("Enter a time in minutes between 0 and one year.")
            .OverridePropertyName(name);

    private void NotEmptyGuid(
        System.Linq.Expressions.Expression<Func<CreateRecipeViewModel, Guid?>> property,
        string name) =>
        RuleFor(property)
            .Must(value => value != Guid.Empty)
            .WithMessage("That is not a valid reference.")
            .OverridePropertyName(name);

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
