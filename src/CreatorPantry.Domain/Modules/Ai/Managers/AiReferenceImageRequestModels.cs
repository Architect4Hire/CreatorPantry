using System.ComponentModel;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>Where the picture to read comes from (AF.3.4).</summary>
/// <remarks>
/// Starts at one: zero is not a source, so a request whose source was never set is refused rather than
/// quietly read as a brand document.
/// </remarks>
public enum AiReferenceImageSource
{
    /// <summary>One of the workspace's brand source documents. The only source before AF.3.4.</summary>
    BrandDocument = 1,

    /// <summary>A library asset, optionally at one of its versions.</summary>
    DamAsset = 2,

    /// <summary>A generated image that is still staged, or was kept.</summary>
    GeneratedImage = 3,
}

/// <summary>
/// What a client sends to ask for a reading of a reference image (IMG-004).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The image is named, not uploaded.</strong> It is a picture this workspace already holds — a brand
/// source document, a library asset, or a generated image — each of which arrived through a route where its
/// signature, format, dimensions and size were inspected and its workspace established. Accepting bytes here
/// would be a second upload surface with a second set of those checks to keep in step, and a second place to
/// get authorization wrong.
/// </para>
/// <para>
/// <strong>Exactly one picture, and one field saying which kind</strong> (AF.3.4). <see cref="Source"/> names
/// the kind and only that kind's ids may be sent. A body carrying <see cref="ReferenceDocumentId"/> and no
/// <see cref="Source"/> is read as a brand document, which is what every request before AF.3.4 was.
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
    /// Which kind of picture this names. Optional only for a request that names a brand document, which is
    /// what it then means.
    /// </summary>
    [Description("Where the picture comes from: BrandDocument, DamAsset or GeneratedImage. Send the ids for that source and no others. Omitted, a request naming referenceDocumentId is a BrandDocument.")]
    public AiReferenceImageSource? Source { get; set; }

    /// <summary>
    /// For <c>BrandDocument</c>: the uploaded image to read, as a brand source document of this workspace.
    /// </summary>
    /// <remarks>
    /// Resolved through the brand module's facade under the workspace filter, so a neighbour's document is
    /// refused exactly as one that does not exist.
    /// </remarks>
    public Guid? ReferenceDocumentId { get; set; }

    /// <summary>For <c>DamAsset</c>: the library asset to read.</summary>
    /// <remarks>Resolved through the media module's facade under the workspace filter.</remarks>
    public Guid? MediaAssetId { get; set; }

    /// <summary>
    /// For <c>DamAsset</c>, optionally: the version to read. Omitted, the asset's current version is read and
    /// pinned when the request is made.
    /// </summary>
    public int? MediaAssetVersionNumber { get; set; }

    /// <summary>For <c>GeneratedImage</c>: the staged or kept generated image to read.</summary>
    /// <remarks>
    /// A declined or expired image is refused exactly as an unknown one is: it is no longer the creator's to
    /// look at, so it is not theirs to have read either.
    /// </remarks>
    public Guid? GeneratedImageId { get; set; }

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
        RuleFor(model => model.Source)
            .Must(source => source is null || Enum.IsDefined(source.Value))
            .WithMessage("Say where the picture comes from: BrandDocument, DamAsset or GeneratedImage.");

        // Exactly one picture. Each rule names the field that is wrong, so a client that mixed two sources is
        // told which id does not belong rather than only that something is off.
        RuleFor(model => model.ReferenceDocumentId)
            .Must((model, id) => AiReferenceImageSources.Of(model) is not AiReferenceImageSource.BrandDocument
                || id is { } value && value != Guid.Empty)
            .WithMessage("Name the uploaded image to read.")
            .Must((model, id) => id is null || AiReferenceImageSources.Of(model) is AiReferenceImageSource.BrandDocument)
            .WithMessage("This belongs to a BrandDocument source. Send only the ids for the source you named.");

        RuleFor(model => model.MediaAssetId)
            .Must((model, id) => AiReferenceImageSources.Of(model) is not AiReferenceImageSource.DamAsset
                || id is { } value && value != Guid.Empty)
            .WithMessage("Name the library asset to read.")
            .Must((model, id) => id is null || AiReferenceImageSources.Of(model) is AiReferenceImageSource.DamAsset)
            .WithMessage("This belongs to a DamAsset source. Send only the ids for the source you named.");

        RuleFor(model => model.MediaAssetVersionNumber)
            .Must((model, number) => number is null || AiReferenceImageSources.Of(model) is AiReferenceImageSource.DamAsset)
            .WithMessage("This belongs to a DamAsset source. Send only the ids for the source you named.")
            .Must(number => number is null or >= 1)
            .WithMessage("Version numbers start at 1.");

        RuleFor(model => model.GeneratedImageId)
            .Must((model, id) => AiReferenceImageSources.Of(model) is not AiReferenceImageSource.GeneratedImage
                || id is { } value && value != Guid.Empty)
            .WithMessage("Name the generated image to read.")
            .Must((model, id) => id is null || AiReferenceImageSources.Of(model) is AiReferenceImageSource.GeneratedImage)
            .WithMessage("This belongs to a GeneratedImage source. Send only the ids for the source you named.");

        // Nothing named at all, and no source to hang the error on.
        RuleFor(model => model.Source)
            .Must((model, _) => AiReferenceImageSources.Of(model) is not null)
            .WithMessage("Say which picture to read: name a source and that source's id.");

        RuleFor(model => model.Note)
            .MaximumLength(AiPolicy.PhotographyCreatorConceptMaxLength)
            .WithMessage(
                $"Keep the note to {AiPolicy.PhotographyCreatorConceptMaxLength} characters or fewer.");
    }
}

/// <summary>Which source a request names, with the one rule for a request that does not say.</summary>
public static class AiReferenceImageSources
{
    /// <summary>
    /// The source a request is for, or null when it names none.
    /// </summary>
    /// <remarks>
    /// A stated source is taken as stated — whether its ids are right is the validator's question. An unstated
    /// one is a brand document exactly when that is the only id sent, which is the shape every client sent
    /// before there was a choice; an unstated source beside any other id is nothing, because guessing which of
    /// two pictures was meant is how a creator gets a reading of the wrong one.
    /// </remarks>
    public static AiReferenceImageSource? Of(RequestReferenceImageAnalysisViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Source is { } stated)
        {
            return stated;
        }

        return model.ReferenceDocumentId is not null
            && model.MediaAssetId is null
            && model.MediaAssetVersionNumber is null
            && model.GeneratedImageId is null
                ? AiReferenceImageSource.BrandDocument
                : null;
    }
}

/// <summary>Stable error codes this seam introduces. Renaming one is a breaking API change.</summary>
public static class AiReferenceImageRequestErrors
{
    public const string RequestInvalid = "ai.referenceImage.invalid_request";

    public const string TaskNotEnabled = "ai.referenceImage.not_enabled";

    /// <summary>
    /// The picture this request names cannot be read as a reference image.
    /// </summary>
    /// <remarks>
    /// One answer for every cause, whichever source was named: no such picture, another workspace's, a removed
    /// asset, a version it does not have, a declined or expired generated image, one that is not an image, and
    /// one too large to send. A creator picks a reference from their own library, so each is a client naming
    /// something it was not shown — and distinguishing them would say which of a neighbour's pictures exist
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
    /// <summary>
    /// Which source the request named, by <see cref="AiReferenceImageSource"/> name. Absent on an operation
    /// requested before AF.3.4, which is read as a brand document.
    /// </summary>
    public const string Source = "source";

    public const string ReferenceDocumentId = "referenceDocumentId";

    public const string MediaAssetId = "mediaAssetId";

    /// <summary>The asset version, resolved and pinned when the request was made, as the document's is.</summary>
    public const string MediaAssetVersionNumber = "mediaAssetVersionNumber";

    public const string GeneratedImageId = "generatedImageId";

    /// <summary>The source an operation's inputs name. A brand document where none is written.</summary>
    public static AiReferenceImageSource ReadSource(IReadOnlyDictionary<string, string>? inputs) =>
        Enum.TryParse<AiReferenceImageSource>(Read(inputs, Source), ignoreCase: false, out var source)
            && Enum.IsDefined(source)
                ? source
                : AiReferenceImageSource.BrandDocument;

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
