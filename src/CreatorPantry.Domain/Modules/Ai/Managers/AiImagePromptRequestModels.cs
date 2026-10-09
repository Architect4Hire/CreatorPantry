using System.ComponentModel;
using CreatorPantry.Domain.Managers.Reference;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// What a client sends to ask for a composed image prompt (IMG-002).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The concept is required and the rest is not.</strong> A prompt is composed for one shot of one
/// concept the creator already has, so without those two there is nothing to compose; everything else refines
/// it. The concept is named by the request that produced it plus the concept's own id, which is what IMG-001
/// stored — so a client passes back what it was shown rather than anything it had to construct.
/// </para>
/// <para>
/// <strong>What is absent:</strong> no scope, no model, no provider, no rendering setting, no prompt (that is
/// the answer, not the question), no schema version and no workspace.
/// </para>
/// </remarks>
public sealed class RequestImagePromptViewModel
{
    /// <summary>The IMG-001 request whose proposal holds the concept. Its id, as that route returned it.</summary>
    public Guid ConceptRequestId { get; set; }

    /// <summary>
    /// The concept within that proposal, as its rows identify it.
    /// </summary>
    /// <remarks>
    /// Server-minted when the concept was stored, so a client can only name one it was shown. An id from
    /// another proposal, another workspace or nowhere is one refusal.
    /// </remarks>
    public Guid ConceptId { get; set; }

    /// <summary>Which shot of that concept to compose for.</summary>
    public AiPhotographyShotKind? ShotKind { get; set; }

    /// <summary>
    /// The channel the image is for, or null for the workspace's visual guidance in general.
    /// </summary>
    [Description("The channel the image is for. Optional; a known channel key.")]
    public string? ChannelKey { get; set; }

    /// <summary>The recipe being photographed, or null when the concept names enough on its own.</summary>
    public Guid? RecipeId { get; set; }

    /// <summary>The exact version to describe. Never valid without <see cref="RecipeId"/>.</summary>
    public Guid? RecipeVersionId { get; set; }

    /// <summary>
    /// A brief the creator uploaded, as a brand source document of theirs.
    /// </summary>
    /// <remarks>
    /// Read through the brand module's facade under the workspace filter, so "authorized" is the existing
    /// machinery rather than a check here — and its text is untrusted when it arrives, because a brief is
    /// written to instruct somebody (this capability's RESTRICTION).
    /// </remarks>
    public Guid? BriefDocumentId { get; set; }

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

    /// <summary>Scene elements to include, beyond what the concept already says.</summary>
    [Description("Scene elements to include. Optional.")]
    public IReadOnlyList<string>? SceneOverrides { get; set; }

    /// <summary>Style directions to follow, beyond what the concept already says.</summary>
    [Description("Style directions to follow. Optional.")]
    public IReadOnlyList<string>? StyleOverrides { get; set; }
}

/// <summary>Shape validation for an IMG-002 request.</summary>
public sealed class RequestImagePromptViewModelValidator : AbstractValidator<RequestImagePromptViewModel>
{
    public RequestImagePromptViewModelValidator(IContentChannelCatalog channels)
    {
        ArgumentNullException.ThrowIfNull(channels);

        RuleFor(model => model.ConceptRequestId)
            .NotEmpty().WithMessage("Name the concept request this prompt is being composed from.");

        RuleFor(model => model.ConceptId)
            .NotEmpty().WithMessage("Name the concept to compose for.");

        RuleFor(model => model.ShotKind)
            .NotNull().WithMessage("Say which shot of the concept to compose for.")
            .Must(kind => kind is null || Enum.IsDefined(kind.Value))
            .WithMessage("That is not a shot a concept plans.");

        RuleFor(model => model.ChannelKey)
            .Must(key => string.IsNullOrWhiteSpace(key) || channels.Find(key.Trim()) is not null)
            .WithMessage("That is not a channel CreatorPantry writes for.");

        RuleFor(model => model.RecipeVersionId)
            .Must((model, _) => model.RecipeVersionId is null || model.RecipeId is not null)
            .WithMessage("Name the recipe the version belongs to.");

        RuleFor(model => model.DishName)
            .MaximumLength(AiPolicy.PhotographyDishNameMaxLength)
            .WithMessage($"Keep the dish name to {AiPolicy.PhotographyDishNameMaxLength} characters or fewer.");

        // Refused rather than one quietly winning, for the reason IMG-001 refuses it.
        RuleFor(model => model.DishName)
            .Must((model, name) => string.IsNullOrWhiteSpace(name) || model.RecipeId is null)
            .WithMessage("A prompt is composed around a linked recipe or a dish name you give, not both.");

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
public static class AiImagePromptRequestErrors
{
    public const string RequestInvalid = "ai.imagePrompt.invalid_request";

    public const string TaskNotEnabled = "ai.imagePrompt.not_enabled";

    /// <summary>
    /// The concept this request names cannot be composed from.
    /// </summary>
    /// <remarks>
    /// One answer for every cause: no such request, another workspace's, one that ran a different task, one
    /// that has not produced a proposal yet, a concept id that proposal does not hold, and a shot that concept
    /// did not plan. A creator picks a concept from what they were shown, so every one of those is a client
    /// naming something it was not shown — and distinguishing them would say which of a neighbour's things
    /// exist (tenancy.md).
    /// </remarks>
    public const string ConceptNotFound = "ai.imagePromptConcept.not_found";

    /// <summary>The request pins a recipe or version this workspace does not have.</summary>
    public const string RecipeNotFound = "ai.imagePromptRecipe.not_found";

    /// <summary>The brief this request names is not a document of this workspace.</summary>
    public const string BriefNotFound = "ai.imagePromptBrief.not_found";

    public const string RequestNotFound = "ai.imagePromptRequest.not_found";
}

/// <summary>
/// The keys an IMG-002 request travels under in <c>AiOperation.TaskInputsJson</c>.
/// </summary>
public static class ImagePromptInputs
{
    public const string ConceptRequestId = "conceptRequestId";

    public const string ConceptId = "conceptId";

    public const string ShotKind = "shotKind";

    public const string ChannelKey = "channelKey";

    public const string BriefDocumentId = "briefDocumentId";

    /// <summary>
    /// The brief's version number, resolved and pinned when the request was made.
    /// </summary>
    /// <remarks>
    /// Pinned rather than resolved in the worker, for the reason every other version pin in this codebase is:
    /// the brief that reaches the model is the one the creator attached, not whichever version happens to be
    /// current when the operation is claimed minutes later. It also means the handler needs only the passage
    /// facade, where resolving the number itself would need the document facade too.
    /// </remarks>
    public const string BriefVersionNumber = "briefVersionNumber";

    /// <inheritdoc cref="PhotographyConceptInputs.DishName"/>
    public const string DishName = PhotographyConceptInputs.DishName;

    public const string SceneOverrides = "sceneOverrides";

    public const string StyleOverrides = "styleOverrides";

    /// <inheritdoc cref="PhotographyConceptInputs.ListSeparator"/>
    public const string ListSeparator = PhotographyConceptInputs.ListSeparator;

    /// <inheritdoc cref="PhotographyConceptInputs.Read"/>
    public static string? Read(IReadOnlyDictionary<string, string>? inputs, string key) =>
        PhotographyConceptInputs.Read(inputs, key);

    /// <inheritdoc cref="PhotographyConceptInputs.ReadList"/>
    public static IReadOnlyList<string> ReadList(IReadOnlyDictionary<string, string>? inputs, string key) =>
        PhotographyConceptInputs.ReadList(inputs, key);

    /// <summary>One stored integer input, or null when absent or unparseable.</summary>
    public static int? ReadNumber(IReadOnlyDictionary<string, string>? inputs, string key) =>
        Read(inputs, key) is { } value && int.TryParse(value, out var number) ? number : null;

    /// <summary>One stored Guid input, or null when absent or unparseable.</summary>
    public static Guid? ReadId(IReadOnlyDictionary<string, string>? inputs, string key) =>
        Read(inputs, key) is { } value && Guid.TryParse(value, out var id) ? id : null;

    /// <summary>The stored shot role, or null when absent or not a role a concept plans.</summary>
    public static AiPhotographyShotKind? ReadShotKind(IReadOnlyDictionary<string, string>? inputs) =>
        Read(inputs, ShotKind) is { } value
        && Enum.TryParse<AiPhotographyShotKind>(value, out var kind)
        && Enum.IsDefined(kind)
            ? kind
            : null;
}
