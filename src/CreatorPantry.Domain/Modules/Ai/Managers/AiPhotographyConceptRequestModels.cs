using System.ComponentModel;
using CreatorPantry.Domain.Managers.Reference;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// What a client sends to ask for photography concepts (IMG-001).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Everything here is optional, and that is the capability's shape.</strong> A creator may plan a shoot
/// for a recipe they have finished, for one they are still drafting, or for an idea with no recipe behind it at
/// all — so there is no required field, and a request that says nothing at all is a legitimate "give me
/// something for this channel". What is <em>not</em> here: a scope (the server fixes it), a model, a prompt, a
/// schema version, a concept count, and a workspace (the route's).
/// </para>
/// <para>
/// <strong>No brand-guide selection either, deliberately.</strong> The visual guidance is the workspace's
/// active guide for the named channel, assembled server-side — a creator planning a shoot is asking for their
/// own look, not auditioning a guide version, which is what 11A.24 exists for. Adding an explicit version pin
/// later is a compatible addition; defaulting to the active guide is not something a later change could take
/// back.
/// </para>
/// </remarks>
public sealed class RequestPhotographyConceptViewModel
{
    /// <summary>
    /// The channel the photographs are for, or null for the workspace's visual guidance in general.
    /// </summary>
    /// <remarks>
    /// Validated against the catalogue, so an unknown or retired key is refused rather than quietly ignored —
    /// a concept planned for a channel the product does not know would be planned against no guidance at all.
    /// </remarks>
    [Description("The channel the photographs are for. Optional; a known channel key.")]
    public string? ChannelKey { get; set; }

    /// <summary>The recipe being photographed, or null when there is not one yet.</summary>
    public Guid? RecipeId { get; set; }

    /// <summary>
    /// The exact version to shoot, or null for the recipe's current one. Never valid without
    /// <see cref="RecipeId"/>.
    /// </summary>
    public Guid? RecipeVersionId { get; set; }

    /// <summary>
    /// The picture the creator already has in mind, in their own words.
    /// </summary>
    /// <remarks>
    /// Untrusted text. It travels to the model inside an untrusted segment, never folded into the task
    /// instructions, because a creator's description of a photograph can carry an injected instruction as
    /// easily as an imported page can (ai.md).
    /// </remarks>
    [Description("The picture you have in mind. Optional; your own words, not a prompt.")]
    public string? CreatorConcept { get; set; }

    /// <summary>
    /// The name the creator gave what the picture is of, for work with no recipe behind it yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Untrusted text, and it travels in the untrusted segment beside the creator's concept and overrides. It
    /// is a name and nothing more: it says what the subject is called, never how it is cooked, so nothing
    /// downstream may read a cuisine, a method or an ingredient out of it.
    /// </para>
    /// <para>
    /// <strong>Refused alongside <see cref="RecipeId"/>.</strong> A linked recipe is canonical source material
    /// the request reads for itself, and the task says to shoot the dish as that recipe describes it; a name
    /// beside it would be a second, weaker answer to the same question. The client decides that precedence
    /// once, so a request carrying both is a client defect rather than something to arbitrate here.
    /// </para>
    /// </remarks>
    [Description("What the dish is called. Optional; your own words, and not needed when a recipe is named.")]
    public string? DishName { get; set; }

    /// <summary>Scene elements the creator wants — a surface, a season, a time of day, a prop.</summary>
    [Description("Scene elements to include. Optional.")]
    public IReadOnlyList<string>? SceneOverrides { get; set; }

    /// <summary>Style directions the creator wants — a mood, a palette, a treatment.</summary>
    [Description("Style directions to follow. Optional.")]
    public IReadOnlyList<string>? StyleOverrides { get; set; }
}

/// <summary>
/// Shape validation for an IMG-001 request. Takes the channel catalogue, as the brand validators do, because
/// an unknown channel key is a shape failure here rather than a lookup.
/// </summary>
public sealed class RequestPhotographyConceptViewModelValidator
    : AbstractValidator<RequestPhotographyConceptViewModel>
{
    public RequestPhotographyConceptViewModelValidator(IContentChannelCatalog channels)
    {
        ArgumentNullException.ThrowIfNull(channels);

        RuleFor(model => model.ChannelKey)
            .Must(key => string.IsNullOrWhiteSpace(key) || channels.Find(key.Trim()) is not null)
            .WithMessage("That is not a channel CreatorPantry writes for.");

        // A version without its recipe names nothing resolvable, the same rule a prompt record's pins obey.
        RuleFor(model => model.RecipeVersionId)
            .Must((model, _) => model.RecipeVersionId is null || model.RecipeId is not null)
            .WithMessage("Name the recipe the version belongs to.");

        RuleFor(model => model.CreatorConcept)
            .MaximumLength(AiPolicy.PhotographyCreatorConceptMaxLength)
            .WithMessage(
                $"Keep your concept to {AiPolicy.PhotographyCreatorConceptMaxLength} characters or fewer.");

        RuleFor(model => model.DishName)
            .MaximumLength(AiPolicy.PhotographyDishNameMaxLength)
            .WithMessage($"Keep the dish name to {AiPolicy.PhotographyDishNameMaxLength} characters or fewer.");

        // Refused rather than one quietly winning: the recipe is the shoot's source material, and a name sent
        // beside it would be a second answer to what the picture is of.
        RuleFor(model => model.DishName)
            .Must((model, name) => string.IsNullOrWhiteSpace(name) || model.RecipeId is null)
            .WithMessage("A shoot is planned around a linked recipe or a dish name you give, not both.");

        RuleFor(model => model.SceneOverrides)
            .Must(BeWithinLimits)
            .WithMessage(OverrideMessage("scene element"));

        RuleFor(model => model.StyleOverrides)
            .Must(BeWithinLimits)
            .WithMessage(OverrideMessage("style direction"));
    }

    private static bool BeWithinLimits(IReadOnlyList<string>? values) =>
        values is null
        || (values.Count <= AiPolicy.MaxPhotographyOverrideCount
            && values.All(value => (value?.Length ?? 0) <= AiPolicy.PhotographyOverrideMaxLength));

    private static string OverrideMessage(string what) =>
        $"Give at most {AiPolicy.MaxPhotographyOverrideCount} {what}s, each "
            + $"{AiPolicy.PhotographyOverrideMaxLength} characters or fewer.";
}

/// <summary>Stable error codes this seam introduces. Renaming one is a breaking API change.</summary>
public static class AiPhotographyConceptRequestErrors
{
    public const string RequestInvalid = "ai.photographyConcept.invalid_request";

    public const string TaskNotEnabled = "ai.photographyConcept.not_enabled";

    /// <summary>
    /// The request pins a recipe or a version this workspace does not have.
    /// </summary>
    /// <remarks>
    /// Also the answer for another workspace's recipe, in the same words, so a shoot request cannot be used to
    /// ask what a neighbour owns (tenancy.md) — the same discipline <c>PromptRecordBusiness</c> applies to a
    /// prompt's pins.
    /// </remarks>
    public const string RecipeNotFound = "ai.photographyConceptRecipe.not_found";

    public const string RequestNotFound = "ai.photographyConceptRequest.not_found";
}

/// <summary>
/// The keys an IMG-001 request travels under in <c>AiOperation.TaskInputsJson</c>.
/// </summary>
/// <remarks>
/// Written by the request business and read by <see cref="PhotographyConceptAiTaskHandler"/>, which is the only
/// reader. Blank values are omitted on the way in, so an absent key and a blank one mean the same thing.
/// </remarks>
public static class PhotographyConceptInputs
{
    public const string ChannelKey = "channelKey";

    public const string CreatorConcept = "creatorConcept";

    public const string DishName = "dishName";

    public const string SceneOverrides = "sceneOverrides";

    public const string StyleOverrides = "styleOverrides";

    /// <summary>How a list of overrides is joined into one input value, and split back out of it.</summary>
    /// <remarks>
    /// A newline rather than a separator a creator might type. A semicolon or a pipe inside one override would
    /// silently become two.
    /// </remarks>
    public const string ListSeparator = "\n";

    /// <summary>One override list as stored, or an empty list when the key is absent or blank.</summary>
    public static IReadOnlyList<string> ReadList(
        IReadOnlyDictionary<string, string>? inputs, string key) =>
        inputs?.GetValueOrDefault(key) is { } value && !string.IsNullOrWhiteSpace(value)
            ? [.. value.Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : [];

    /// <summary>One single-valued input, or null when absent or blank.</summary>
    public static string? Read(IReadOnlyDictionary<string, string>? inputs, string key) =>
        inputs?.GetValueOrDefault(key) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;
}
