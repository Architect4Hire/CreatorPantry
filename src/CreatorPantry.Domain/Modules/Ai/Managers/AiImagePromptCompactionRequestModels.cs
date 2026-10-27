using System.ComponentModel;
using System.Globalization;
using CreatorPantry.Domain.Modules.Media.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// What a client sends to ask for a shorter version of an image prompt, for a picture quality whose model takes
/// fewer characters than the prompt and its avoid list come to (AP.3.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The prompt and avoid list are the ones the picture request was refused for</strong>, as the creator
/// last saw them, and the quality class is the one they chose. The server measures them against that class's
/// model itself — a budget sent by a client would be a number nothing checks — and refuses a prompt that already
/// fits or an avoid list that leaves no room.
/// </para>
/// <para>
/// <strong>What is absent:</strong> no budget, no model, no provider, no schema version and no workspace.
/// </para>
/// </remarks>
public sealed class RequestImagePromptCompactionViewModel
{
    /// <summary>The prompt to shorten, as the creator would send it.</summary>
    [Description("The prompt to shorten, exactly as it would be sent for pictures.")]
    public string? PromptText { get; set; }

    /// <summary>
    /// The avoid list that goes with it. Never shown to the model: it is carried into the shorter version word
    /// for word — trimmed at its ends only, as the picture request trims it — and it is measured because it
    /// shares the model's limit.
    /// </summary>
    [Description("The avoid list that goes with the prompt. Optional. Kept word for word; never shortened.")]
    public string? AvoidText { get; set; }

    /// <summary>The picture quality to fit; null for the host's default.</summary>
    [Description("The picture quality the shorter prompt must fit. Optional; the default quality when omitted.")]
    public GeneratedImageQualityClass? QualityClass { get; set; }
}

/// <summary>Shape validation for a request to shorten an image prompt.</summary>
public sealed class RequestImagePromptCompactionViewModelValidator
    : AbstractValidator<RequestImagePromptCompactionViewModel>
{
    public RequestImagePromptCompactionViewModelValidator()
    {
        RuleFor(model => model.PromptText)
            .Must(text => !string.IsNullOrWhiteSpace(text))
            .WithMessage("Give the prompt to shorten.");

        RuleFor(model => model.PromptText)
            .MaximumLength(AiPolicy.ImagePromptMaxLength)
            .WithMessage($"A prompt may be at most {AiPolicy.ImagePromptMaxLength} characters.");

        RuleFor(model => model.AvoidText)
            .MaximumLength(AiPolicy.ImagePromptCompactionAvoidMaxLength)
            .WithMessage($"An avoid list may be at most {AiPolicy.ImagePromptCompactionAvoidMaxLength} characters.");

        RuleFor(model => model.QualityClass)
            .Must(quality => quality is null || Enum.IsDefined(quality.Value))
            .WithMessage("That is not a picture quality.");
    }
}

/// <summary>Stable error codes this seam introduces. Renaming one is a breaking API change.</summary>
public static class AiImagePromptCompactionErrors
{
    public const string RequestInvalid = "ai.imagePromptCompaction.invalid_request";

    /// <summary>
    /// The prompt and its avoid list already fit the chosen quality's model, so there is nothing to shorten.
    /// Maps to 422, naming <c>promptText</c>. Refused rather than run, because a shorter prompt nobody needed is
    /// a creator's words changed and their allowance spent for nothing.
    /// </summary>
    public const string AlreadyFits = "ai.imagePromptCompaction.prompt.unprocessable";

    /// <summary>
    /// The avoid list alone leaves too little room for any prompt. Maps to 422, naming <c>avoidText</c>, with the
    /// figures the picture request's own refusal carries. A shorter prompt cannot fix it, and the avoid list is
    /// never shortened for the creator.
    /// </summary>
    public const string AvoidTooLong = "ai.imagePromptCompaction.avoid.unprocessable";

    /// <summary>
    /// This host cannot say how many characters the chosen quality's model takes — it has no image deployment,
    /// or one that will not say — so there is no length to shorten to. Maps to 503: a fact about the deployment,
    /// not the request.
    /// </summary>
    public const string LimitUnknown = "ai.imagePromptCompaction.unavailable";

    /// <summary>The prompt and avoid list are too long together to be stored as one request.</summary>
    public const string RequestTooLarge = "ai.imagePromptCompaction.request.unprocessable";
}

/// <summary>
/// The keys a request for a shorter prompt travels under in <c>AiOperation.TaskInputsJson</c>. Their presence
/// is what makes an <see cref="AiTaskType.ImagePrompt"/> operation a shortening rather than a composition.
/// </summary>
public static class ImagePromptCompactionInputs
{
    /// <summary>The creator's prompt to shorten. Shown to the model, fenced as untrusted.</summary>
    public const string SourcePrompt = "compactPrompt";

    /// <summary>
    /// The creator's avoid list, trimmed at its ends as the picture request trims it and otherwise as written. Never
    /// shown to the model; carried into the proposal word for word.
    /// </summary>
    public const string Avoid = "compactAvoid";

    /// <summary>The picture quality the request was measured against, by name. Recorded, not re-resolved.</summary>
    public const string QualityClass = "compactQualityClass";

    /// <summary>The model's prompt character limit at the time of the request.</summary>
    public const string CharacterLimit = "compactCharacterLimit";

    /// <summary>How long the shorter prompt may be with the avoid list kept whole.</summary>
    public const string PromptBudget = "compactPromptBudget";

    /// <summary>
    /// The inputs that describe this deployment rather than what the creator asked, and so are not part of a
    /// request's identity: a retry made after the limit was reconfigured is still the request it retries.
    /// </summary>
    public static IReadOnlyList<string> DeploymentFacts { get; } = [CharacterLimit, PromptBudget];

    /// <summary>Whether these inputs ask for a shorter prompt rather than a composed one.</summary>
    public static bool IsCompaction(IReadOnlyDictionary<string, string>? inputs) =>
        inputs?.ContainsKey(SourcePrompt) is true;

    /// <summary>One stored value, verbatim — never trimmed, because the avoid list must survive as written.</summary>
    public static string? ReadVerbatim(IReadOnlyDictionary<string, string>? inputs, string key) =>
        inputs is not null && inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    /// <summary>One stored positive integer, or null when absent, unparseable or not above nought.</summary>
    public static int? ReadPositive(IReadOnlyDictionary<string, string>? inputs, string key) =>
        ReadVerbatim(inputs, key) is { } value
        && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        && number > 0
            ? number
            : null;
}
