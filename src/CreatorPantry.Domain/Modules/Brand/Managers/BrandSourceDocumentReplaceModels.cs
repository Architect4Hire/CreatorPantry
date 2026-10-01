using FluentValidation;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// What a replacement carries besides the file: the token of the read it was composed against, and nothing
/// else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no metadata here on purpose.</strong> A replacement replaces the creator's file, not their
/// description of it: title, type, purpose, channel, audience and tags stay exactly as they were, so a
/// creator who meant to re-upload a file cannot quietly reclassify the document at the same time. Editing
/// the description is its own operation on its own route.
/// </para>
/// <para>
/// No workspace, no document id, no version number and no media type: the first two come from the route and
/// the resolved membership, the version number is the server's counter, and what the file is comes from its
/// bytes.
/// </para>
/// </remarks>
public sealed record ReplaceBrandSourceDocumentViewModel
{
    /// <summary>The <c>concurrencyToken</c> of the document read this replacement was composed against.</summary>
    public string? ExpectedConcurrencyToken { get; init; }
}

/// <summary>
/// A replacement file that has passed inspection and the malware scan and has not been stored. Its version id
/// is fixed here, once per request, so a re-run of the storing step names the same object.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <see cref="BrandSourcePreparedUpload"/> with one difference that matters:
/// <paramref name="DocumentId"/> is an <em>existing</em> document's, read from the route, where an upload's is
/// generated. The version id is new either way, which is what makes the object key new — and therefore what
/// makes an in-place overwrite of the previous version's bytes unrepresentable rather than merely avoided.
/// </para>
/// <para>
/// <strong>No version number.</strong> The number is read from the tracked document inside the idempotent
/// unit, not carried from the pre-check, so a re-run cannot write a number settled against a row it has since
/// re-read.
/// </para>
/// </remarks>
/// <param name="MediaType">Established from the bytes, exactly as on an upload.</param>
public sealed record BrandSourcePreparedReplacement(
    Guid DocumentId,
    Guid VersionId,
    string MediaType,
    long SizeBytes,
    string ContentChecksum,
    string OriginalFileName,
    Stream Content);

public sealed class ReplaceBrandSourceDocumentViewModelValidator : AbstractValidator<ReplaceBrandSourceDocumentViewModel>
{
    public ReplaceBrandSourceDocumentViewModelValidator()
    {
        // Required, and the same two rules the profile's edit uses: a replacement with nothing to check
        // against cannot be applied safely, and a token this API could never have issued is a client bug
        // rather than a collaborator's edit. Both are refused before the file is read, let alone scanned.
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Send the document's concurrency token with the replacement file.")
            .Must(BrandConcurrencyToken.IsWellFormed).WithMessage("That is not a concurrency token this API issued.")
            .OverridePropertyName(nameof(ReplaceBrandSourceDocumentViewModel.ExpectedConcurrencyToken));
    }
}
