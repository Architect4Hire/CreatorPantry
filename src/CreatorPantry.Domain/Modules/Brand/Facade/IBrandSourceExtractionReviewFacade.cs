using System.Security.Cryptography;
using System.Text;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Brand.Business;
using CreatorPantry.Domain.Modules.Brand.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Brand.Facade;

/// <summary>
/// The application boundary for a creator reading and correcting a document version's extracted text.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A separate boundary from <see cref="IBrandSourceExtractionFacade"/>, deliberately.</strong> That one is
/// the Worker's, and is documented as checking no role because its only caller runs as the workspace's service
/// identity at <c>Viewer</c>. Folding a creator route onto it would put an operation that must check <c>Editor</c>
/// behind a comment saying it checks nothing. The data layer below is shared; the authorization is not.
/// </para>
/// <para>
/// Nothing on this boundary accepts a workspace id, and nothing it returns carries an object key, a container or a
/// URL. The text it returns and the text it accepts are both untrusted content (ai.md).
/// </para>
/// </remarks>
public interface IBrandSourceExtractionReviewFacade
{
    /// <summary>
    /// Reads one numbered version's current extracted text.
    /// </summary>
    /// <param name="documentId">The document, from the route.</param>
    /// <param name="versionNumber">The version, as the document's metadata numbers it. Any version reads.</param>
    /// <returns>
    /// The artifact and its text, a <c>NotExtracted</c> state for a version nothing has read yet, or
    /// <c>brand.source.not_found</c> / <c>brand.source.storage.unavailable</c>.
    /// </returns>
    /// <remarks>
    /// Any member may read, like the detail and the download. No idempotency key and nothing to validate: both
    /// arguments are route-bound.
    /// </remarks>
    Task<OperationResult<BrandSourceExtractionServiceModel>> GetAsync(
        Guid documentId, int versionNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the current version's extracted text with the creator's own, as a new artifact.
    /// </summary>
    /// <param name="model">The corrected text, the creator's reason, and the id of the artifact it was composed against.</param>
    /// <param name="idempotencyKey">
    /// Required. Without it a retry of a request that did commit would arrive quoting an artifact that has since
    /// been superseded — by itself — and be told it conflicted.
    /// </param>
    /// <remarks>
    /// <para>
    /// Editor or above, as for the upload and the replacement: this writes creator-owned source material.
    /// </para>
    /// <para>
    /// <strong>Nothing is overwritten.</strong> The correction is a new artifact at the next ordinal; the uploaded
    /// file, the parser's artifact and every earlier correction all stay exactly where they are and stay readable.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Asks for a version's text to be read again. Editor or above, and an idempotency key is required: it names
    /// one click, so a request repeated after the new read has settled does not queue a second one.
    /// </summary>
    Task<IdempotentOutcome<BrandSourceExtractionServiceModel>> RetryAsync(
        string userId,
        Guid documentId,
        int versionNumber,
        RetryBrandSourceExtractionViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    Task<IdempotentOutcome<BrandSourceExtractionServiceModel>> CorrectAsync(
        string userId,
        Guid documentId,
        int versionNumber,
        CorrectBrandSourceExtractionViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);
}

internal sealed class BrandSourceExtractionReviewFacade(
    IValidator<CorrectBrandSourceExtractionViewModel> validator,
    IBrandSourceExtractionReviewBusiness business,
    IWorkspaceContext workspace,
    IIdempotentCommandExecutor idempotency) : IBrandSourceExtractionReviewFacade
{
    /// <summary>A stable operation name for the idempotency scope. Changing it orphans in-flight keys.</summary>
    private const string CorrectOperation = "brand.source.extraction.correct";

    private const string RetryOperation = "brand.source.extraction.retry";

    /// <remarks>
    /// Straight through, and deliberately: there is nothing to validate because both arguments are route-bound,
    /// nothing to cache because the text changes whenever anybody corrects it, and no role above Viewer to check.
    /// A facade that adds nothing is still the boundary — workers and AI plugins reach this seam here.
    /// </remarks>
    public Task<OperationResult<BrandSourceExtractionServiceModel>> GetAsync(
        Guid documentId, int versionNumber, CancellationToken cancellationToken) =>
        business.GetAsync(documentId, versionNumber, cancellationToken);

    public async Task<IdempotentOutcome<BrandSourceExtractionServiceModel>> RetryAsync(
        string userId,
        Guid documentId,
        int versionNumber,
        RetryBrandSourceExtractionViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (workspace.Role < WorkspaceRole.Editor)
        {
            return Refused(new OperationError(
                BrandErrorCodes.SourceForbidden,
                "You do not have permission to read extracted text again in this workspace.",
                new Dictionary<string, string[]>()));
        }

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId,
                workspace.WorkspaceId,
                RetryOperation,
                idempotencyKey,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["documentId"] = documentId,
                    ["versionNumber"] = versionNumber,
                    ["expectedExtractionId"] = model.ExpectedExtractionId,
                }),
            token => business.RetryAsync(userId, documentId, versionNumber, model.ExpectedExtractionId, token),
            cancellationToken);
    }

    public async Task<IdempotentOutcome<BrandSourceExtractionServiceModel>> CorrectAsync(
        string userId,
        Guid documentId,
        int versionNumber,
        CorrectBrandSourceExtractionViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Checked here as well as at the route, because workers and AI plugins reach this boundary without
        // passing an [Authorize].
        if (workspace.Role < WorkspaceRole.Editor)
        {
            return Refused(new OperationError(
                BrandErrorCodes.SourceForbidden,
                "You do not have permission to correct extracted text in this workspace.",
                new Dictionary<string, string[]>()));
        }

        var validation = await validator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return Refused(OperationError.Validation(
                BrandErrorCodes.SourceInvalidRequest,
                "That correction could not be accepted.",
                [.. validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))]));
        }

        // The validator has already established that this succeeds. Called again rather than carried out of the
        // validator because a FluentValidation rule reports failures and does not return values — and the
        // normalized form has to be the thing both the fingerprint and the write see, or a retry sent with
        // different line endings would read as a different request.
        if (!BrandSourceCorrectionText.TryNormalize(model.Text, out var text, out _))
        {
            throw new InvalidOperationException("A validated correction failed normalization.");
        }

        var reason = model.Reason!.Trim();

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId,
                workspace.WorkspaceId,
                CorrectOperation,
                idempotencyKey,
                Fingerprint(documentId, versionNumber, model.ExpectedExtractionId!.Value, text, reason)),
            token => business.CorrectAsync(
                userId, documentId, versionNumber, model.ExpectedExtractionId!.Value, text, reason, token),
            cancellationToken);
    }

    /// <summary>
    /// What makes two corrections the same request: the same text, for the same reason, offered for the same
    /// version against the same read of its text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The expected artifact is part of it deliberately — the same text sent again after a collaborator's
    /// correction is a different request about a different state, and replaying the earlier answer for it would
    /// be a lie about which artifact the version is on. The same reasoning the replacement's fingerprint records.
    /// </para>
    /// <para>
    /// The text is hashed rather than included: it is up to four megabytes, and a fingerprint is compared, not
    /// read. The digest is of the normalized form, so two submissions differing only in line endings or trailing
    /// blank lines are correctly the same request.
    /// </para>
    /// </remarks>
    private static object Fingerprint(
        Guid documentId, int versionNumber, Guid expectedExtractionId, string text, string reason) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["documentId"] = documentId,
            ["versionNumber"] = versionNumber,
            ["expectedExtractionId"] = expectedExtractionId,
            ["textChecksum"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
            ["reason"] = reason,
        };

    private static IdempotentOutcome<BrandSourceExtractionServiceModel> Refused(OperationError error) =>
        new(OperationResult<BrandSourceExtractionServiceModel>.Failure(error), Replayed: false);
}
