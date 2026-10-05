using System.ComponentModel;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// What a client sends to ask for a reading of a reference image (IMG-004).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The image is named, not uploaded.</strong> It is one of the workspace's own brand source
/// documents, which the creator uploaded through the route that exists for uploading — where its signature,
/// decoded format, dimensions and size were inspected and its workspace established. Accepting bytes here
/// would be a second upload surface with a second set of those checks to keep in step, and a second place to
/// get authorization wrong.
/// </para>
/// <para>
/// <strong>What is absent:</strong> no bytes, no media type (the server established one from the file), no
/// scope, no model, no provider, no rendering setting, no observations or prompt (those are the answer), no
/// schema version and no workspace.
/// </para>
/// </remarks>
public sealed class RequestReferenceImageAnalysisViewModel
{
    /// <summary>
    /// The uploaded image to read, as a brand source document of this workspace.
    /// </summary>
    /// <remarks>
    /// Resolved through the brand module's facade under the workspace filter, so a neighbour's document is
    /// refused exactly as one that does not exist.
    /// </remarks>
    public Guid ReferenceDocumentId { get; set; }

    /// <summary>
    /// A note about what the creator is looking for in the reference. Optional, and untrusted.
    /// </summary>
    /// <remarks>
    /// Carried at <c>UntrustedText</c> trust beside the image rather than folded into the task instructions:
    /// it is the creator's own text, and a note that reads like an instruction is still a note (ai.md).
    /// </remarks>
    [Description("What you are looking for in this reference. Optional.")]
    public string? Note { get; set; }
}

/// <summary>Shape validation for an IMG-004 request.</summary>
public sealed class RequestReferenceImageAnalysisViewModelValidator
    : AbstractValidator<RequestReferenceImageAnalysisViewModel>
{
    public RequestReferenceImageAnalysisViewModelValidator()
    {
        RuleFor(model => model.ReferenceDocumentId)
            .NotEmpty().WithMessage("Name the uploaded image to read.");

        RuleFor(model => model.Note)
            .MaximumLength(AiPolicy.PhotographyCreatorConceptMaxLength)
            .WithMessage(
                $"Keep the note to {AiPolicy.PhotographyCreatorConceptMaxLength} characters or fewer.");
    }
}

/// <summary>Stable error codes this seam introduces. Renaming one is a breaking API change.</summary>
public static class AiReferenceImageRequestErrors
{
    public const string RequestInvalid = "ai.referenceImage.invalid_request";

    public const string TaskNotEnabled = "ai.referenceImage.not_enabled";

    /// <summary>
    /// The document this request names cannot be read as a reference image.
    /// </summary>
    /// <remarks>
    /// One answer for every cause: no such document, another workspace's, and one whose current version is
    /// not an image. A creator picks a reference from their own library, so each is a client naming something
    /// it was not shown — and distinguishing them would say which of a neighbour's documents exist
    /// (tenancy.md).
    /// </remarks>
    public const string ReferenceNotFound = "ai.referenceImage.not_found";

    public const string RequestNotFound = "ai.referenceImageRequest.not_found";
}

/// <summary>
/// The keys an IMG-004 request travels under in <c>AiOperation.TaskInputsJson</c>.
/// </summary>
public static class ReferenceImageInputs
{
    public const string ReferenceDocumentId = "referenceDocumentId";

    /// <summary>
    /// The image's version number, resolved and pinned when the request was made.
    /// </summary>
    /// <remarks>
    /// Pinned for the reason every other version pin here is: the bytes that reach the model are the ones the
    /// creator attached, not whichever version is current when the operation is claimed minutes later. A
    /// creator who replaces the file in between gets a reading of what they asked about.
    /// </remarks>
    public const string ReferenceVersionNumber = "referenceVersionNumber";

    public const string CreatorNote = "note";

    /// <inheritdoc cref="PhotographyConceptInputs.Read"/>
    public static string? Read(IReadOnlyDictionary<string, string>? inputs, string key) =>
        PhotographyConceptInputs.Read(inputs, key);

    /// <inheritdoc cref="ImagePromptInputs.ReadNumber"/>
    public static int? ReadNumber(IReadOnlyDictionary<string, string>? inputs, string key) =>
        ImagePromptInputs.ReadNumber(inputs, key);

    /// <inheritdoc cref="ImagePromptInputs.ReadId"/>
    public static Guid? ReadId(IReadOnlyDictionary<string, string>? inputs, string key) =>
        ImagePromptInputs.ReadId(inputs, key);
}
