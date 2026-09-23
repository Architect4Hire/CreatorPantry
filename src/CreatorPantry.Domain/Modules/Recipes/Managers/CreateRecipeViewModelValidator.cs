using CreatorPantry.Domain.Managers.Reference;
using FluentValidation;

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

        // Archived is a state a recipe is moved to, never one it begins in. Cheap to state here, and it keeps
        // a recipe from being created into a state no interface would show it in.
        RuleFor(model => model.Status)
            .Must(status => status != SettableRecipeStatusViewModel.Archived)
            .WithMessage("A new recipe cannot start out archived.")
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

        RuleFor(model => model.Instructions)
            .Cascade(CascadeMode.Stop)
            .Must(instructions => !Groups(instructions).Any(group => group is null))
                .WithMessage("An instruction group cannot be blank.")
            .Must(instructions => Groups(instructions).Count <= RecipePolicy.MaxInstructionGroupsPerRecipe)
                .WithMessage($"A recipe can carry at most {RecipePolicy.MaxInstructionGroupsPerRecipe} instruction groups.")
            .Must(instructions => Groups(instructions).Sum(group => Steps(group).Count) <= RecipePolicy.MaxInstructionStepsPerRecipe)
                .WithMessage($"A recipe can carry at most {RecipePolicy.MaxInstructionStepsPerRecipe} instruction steps.")
            // Nothing exists yet for an id to name: a create that sends one is a client bug, not a request
            // this could honour by ignoring it silently.
            .Must(instructions => Groups(instructions).All(group => group!.Id is null))
                .WithMessage("A new recipe's instructions cannot name an existing group.")
            .Must(instructions => Groups(instructions).All(group => Trimmed(group!.Title).Length <= RecipePolicy.GroupTitleMaxLength))
                .WithMessage($"An instruction group heading can be at most {RecipePolicy.GroupTitleMaxLength} characters.")
            .Must(instructions => !AllSteps(instructions).Any(step => step is null))
                .WithMessage("An instruction step cannot be blank.")
            .Must(instructions => AllSteps(instructions).All(step => step!.Id is null))
                .WithMessage("A new recipe's instructions cannot name an existing step.")
            .Must(instructions => AllSteps(instructions).All(step => !string.IsNullOrWhiteSpace(step!.Text)))
                .WithMessage("An instruction step cannot be blank.")
            .Must(instructions => AllSteps(instructions).All(step => Trimmed(step!.Text).Length <= RecipePolicy.StepTextMaxLength))
                .WithMessage($"An instruction step can be at most {RecipePolicy.StepTextMaxLength} characters.")
            .Must(instructions => AllSteps(instructions).All(step => Trimmed(step!.Note).Length <= RecipePolicy.NoteMaxLength))
                .WithMessage($"A step note can be at most {RecipePolicy.NoteMaxLength} characters.")
            .Must(instructions => AllSteps(instructions).All(step => step!.DurationMinutes is null or (>= 0 and <= RecipePolicy.MaxTimeMinutes)))
                .WithMessage("A step's duration must be a time in minutes between 0 and one year.")
            .Must(instructions => AllSteps(instructions).All(step => step!.TechniqueId != Guid.Empty))
                .WithMessage("That is not a valid technique reference.")
            .Must(instructions => AllSteps(instructions).All(step => step!.TemperatureUnitId != Guid.Empty))
                .WithMessage("That is not a valid unit reference.")
            // Mirrors CK_RecipeInstructionSteps_Temperature_Dimension: present in both or neither. Whether the
            // unit itself measures temperature needs a lookup, so that half stays in Business.
            .Must(instructions => AllSteps(instructions).All(
                step => (step!.TemperatureValue is null) == (step.TemperatureUnitId is null)))
                .WithMessage("A step's temperature needs both a value and a unit, or neither.")
            .OverridePropertyName(nameof(CreateRecipeViewModel.Instructions));
    }

    private static IReadOnlyList<RecipeInstructionGroupInputViewModel> Groups(IReadOnlyList<RecipeInstructionGroupInputViewModel?>? instructions) =>
        instructions?.OfType<RecipeInstructionGroupInputViewModel>().ToList() ?? [];

    private static IReadOnlyList<RecipeInstructionStepInputViewModel> Steps(RecipeInstructionGroupInputViewModel group) =>
        group.Steps?.OfType<RecipeInstructionStepInputViewModel>().ToList() ?? [];

    private static IEnumerable<RecipeInstructionStepInputViewModel> AllSteps(IReadOnlyList<RecipeInstructionGroupInputViewModel?>? instructions) =>
        Groups(instructions).SelectMany(Steps);

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
