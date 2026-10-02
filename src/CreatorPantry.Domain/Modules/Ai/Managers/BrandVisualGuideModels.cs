using System.ComponentModel;
using CreatorPantry.Domain.Modules.Brand.Managers;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>Which image screen is asking what visual guidance would apply (11A.21b).</summary>
/// <param name="Task">
/// <c>photography-concept</c> or <c>image-prompt</c>. A discriminator the API maps to a task itself; a task that is
/// not grounded in the brand's look is refused rather than answered with an empty preview that implies it is.
/// </param>
public sealed record BrandVisualGuideViewModel(
    [property: FromQuery(Name = "task")]
    [property: Description("photography-concept or image-prompt.")]
    string? Task = null);

/// <summary>One library document the creator could name as a visual reference example.</summary>
/// <param name="Title">The creator's own title for it. Never a filename or a storage location.</param>
/// <param name="Usable">
/// True when text from it would reach the generation. False means it would be left out — and its image is never
/// sent in its place.
/// </param>
/// <param name="UnusableReason">A plain-language reason, set exactly when <paramref name="Usable"/> is false.</param>
public sealed record BrandVisualReferenceServiceModel(Guid DocumentId, string Title, bool Usable, string? UnusableReason);

/// <summary>
/// What an image setup screen shows about the brand's look before anything is generated.
/// </summary>
/// <param name="ActiveGuide">
/// Null when the workspace has activated no guide. <c>appliedSections</c> then names the visual parts of it.
/// </param>
/// <param name="HasVisualGuidance">True when the active guide describes at least one visual section for this task.</param>
/// <param name="StyleLines">
/// The look in the creator's words — identity, photography direction, prompt guidance — shortened to a line each.
/// Excludes negative guidance, which is <paramref name="NegativeGuidance"/>, and the guide's Do/Don't rules, which an
/// image task does not receive.
/// </param>
/// <param name="NegativeGuidance">What the guide says to keep out of the picture, shortened. Null when it says none.</param>
/// <param name="References">
/// The workspace's active visual reference documents. Empty when no guide is active: without a guide the server names
/// no documents at all.
/// </param>
/// <param name="ReferencesTruncated">True when there are more references than are listed.</param>
/// <param name="ReferencesAvailable">
/// False when the library could not be read. An empty <paramref name="References"/> is then "could not load", not
/// "you have none" — the look is still returned, and a screen can say which it is.
/// </param>
public sealed record BrandVisualGuideServiceModel(
    BrandWritingGuideActiveServiceModel? ActiveGuide,
    bool HasVisualGuidance,
    IReadOnlyList<BrandWritingGuideRuleServiceModel> StyleLines,
    string? NegativeGuidance,
    IReadOnlyList<BrandVisualReferenceServiceModel> References,
    bool ReferencesTruncated,
    bool ReferencesAvailable);

public static class BrandVisualGuideErrors
{
    public const string RequestInvalid = "ai.brandVisualGuide.invalid_request";
}

/// <summary>The image tasks this preview answers for, as the strings a client sends.</summary>
public static class BrandVisualGuideTasks
{
    public const string PhotographyConcept = "photography-concept";

    public const string ImagePrompt = "image-prompt";

    public static AiTaskType? Parse(string? value) => value?.Trim() switch
    {
        PhotographyConcept => AiTaskType.PhotographyConcept,
        ImagePrompt => AiTaskType.ImagePrompt,
        _ => null,
    };
}

public sealed class BrandVisualGuideViewModelValidator : AbstractValidator<BrandVisualGuideViewModel>
{
    public BrandVisualGuideViewModelValidator()
    {
        RuleFor(model => model.Task)
            .Must(task => BrandVisualGuideTasks.Parse(task) is not null)
            .WithMessage($"Task must be {BrandVisualGuideTasks.PhotographyConcept} or {BrandVisualGuideTasks.ImagePrompt}.");
    }
}

/// <summary>What a creator is told about a reference that would not be used.</summary>
public static class BrandVisualReferenceReasons
{
    /// <summary>Appended to every reason: the guarantee that matters most about an image the creator named.</summary>
    public const string Guarantee = "It will not be used, and its image is never sent.";

    public static string For(BrandSourceExtractionState state) => state switch
    {
        BrandSourceExtractionState.NotExtracted => $"We have not read it yet. {Guarantee}",
        BrandSourceExtractionState.Unsupported => $"We cannot read text from this kind of file. {Guarantee}",
        BrandSourceExtractionState.Failed => $"We could not read it. {Guarantee}",
        _ => $"Its text is not ready to use yet. {Guarantee}",
    };
}
