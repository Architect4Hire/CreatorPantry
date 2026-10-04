using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand.Data;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Business;

public interface IBrandStyleGuideEditSessionBusiness
{
    /// <summary>
    /// The caller's own unsaved edit of one guide, or a success carrying null when they have none.
    /// </summary>
    Task<OperationResult<BrandStyleGuideEditSessionServiceModel?>> GetAsync(
        Guid guideId, CancellationToken cancellationToken);

    /// <summary>
    /// Upsert of the caller's own draft. <paramref name="ifMatch"/> is required once a draft exists.
    /// </summary>
    /// <remarks>
    /// <strong>A draft is never refused for being behind.</strong> The baseline is stored as sent and reported
    /// back as stale when the guide has gained a version since; refusing the save would cost the creator the
    /// words they typed, which is the failure this whole seam exists to prevent. The refusal that matters at
    /// the moment of writing belongs to <c>POST .../versions</c>.
    /// </remarks>
    Task<OperationResult<(BrandStyleGuideEditSessionServiceModel Session, bool Created)>> SaveAsync(
        Guid guideId,
        SaveBrandStyleGuideEditSessionViewModel model,
        string? ifMatch,
        CancellationToken cancellationToken);

    /// <summary>
    /// Discards the caller's own draft of one guide. Idempotent: no draft is a success, because what the
    /// request asked for is already true.
    /// </summary>
    Task<OperationResult<bool>> DeleteAsync(Guid guideId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBrandStyleGuideEditSessionBusiness"/>
internal sealed class BrandStyleGuideEditSessionBusiness(
    IBrandStyleGuideEditSessionDataLayer dataLayer,
    IWorkspaceContext workspace,
    IClock clock) : IBrandStyleGuideEditSessionBusiness
{
    public async Task<OperationResult<BrandStyleGuideEditSessionServiceModel?>> GetAsync(
        Guid guideId, CancellationToken cancellationToken)
    {
        if (await dataLayer.ReadTargetAsync(guideId, cancellationToken) is not { } target)
        {
            return OperationResult<BrandStyleGuideEditSessionServiceModel?>.Failure(NotFound());
        }

        var session = await dataLayer.GetAsync(guideId, workspace.AccountId, cancellationToken);

        // Null in success, not a failure: a creator who has not started editing has no draft, and that is an
        // answer the editor acts on rather than an error it recovers from.
        return OperationResult<BrandStyleGuideEditSessionServiceModel?>.Success(
            session is null ? null : ToServiceModel(session, target));
    }

    public async Task<OperationResult<(BrandStyleGuideEditSessionServiceModel Session, bool Created)>> SaveAsync(
        Guid guideId,
        SaveBrandStyleGuideEditSessionViewModel model,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (await dataLayer.ReadTargetAsync(guideId, cancellationToken) is not { } target)
        {
            return Failure(NotFound());
        }

        var userId = workspace.AccountId;
        var now = clock.UtcNow;
        var existing = await dataLayer.GetForUpdateAsync(guideId, userId, cancellationToken);

        if (existing is null)
        {
            if (!string.IsNullOrEmpty(ifMatch))
            {
                // The caller holds a token for a row that is gone — discarded in another tab, or cleared by a
                // save that wrote a version. Either way their copy is not the one on the server.
                return Failure(Conflict());
            }

            // WorkspaceId is left unset: WorkspaceOwnershipInterceptor stamps it from the resolved context.
            var session = new BrandStyleGuideEditSession
            {
                Id = Guid.NewGuid(),
                BrandStyleGuideId = target.GuideId,
                UserId = userId,
                BaselineVersionNumber = model.BaselineVersionNumber!.Value,
                DraftJson = model.DraftJson!,
                CreatedUtc = now,
                UpdatedUtc = now,
            };

            return await dataLayer.CreateAsync(session, cancellationToken)
                ? await Reload(guideId, target, created: true, cancellationToken)
                : Failure(Conflict());
        }

        if (!BrandConcurrencyToken.Matches(Unquote(ifMatch), existing.RowVersion))
        {
            return Failure(Conflict());
        }

        existing.BaselineVersionNumber = model.BaselineVersionNumber!.Value;
        existing.DraftJson = model.DraftJson!;
        existing.UpdatedUtc = now;

        return await dataLayer.UpdateAsync(existing, cancellationToken)
            ? await Reload(guideId, target, created: false, cancellationToken)
            : Failure(Conflict());
    }

    public async Task<OperationResult<bool>> DeleteAsync(Guid guideId, CancellationToken cancellationToken)
    {
        if (await dataLayer.ReadTargetAsync(guideId, cancellationToken) is null)
        {
            return OperationResult<bool>.Failure(NotFound());
        }

        var outcome = await dataLayer.DeleteAsync(guideId, workspace.AccountId, cancellationToken);

        return outcome == BrandStyleGuideEditSessionDeleteOutcome.Conflict
            ? OperationResult<bool>.Failure(Conflict())
            : OperationResult<bool>.Success(outcome == BrandStyleGuideEditSessionDeleteOutcome.Deleted);
    }

    /// <summary>
    /// Re-reads what was just written, so the reply carries the row version the next autosave has to quote.
    /// </summary>
    private async Task<OperationResult<(BrandStyleGuideEditSessionServiceModel, bool)>> Reload(
        Guid guideId, BrandStyleGuideEditTarget target, bool created, CancellationToken cancellationToken)
    {
        var session = await dataLayer.GetAsync(guideId, workspace.AccountId, cancellationToken);

        return session is null
            ? Failure(Conflict())
            : OperationResult<(BrandStyleGuideEditSessionServiceModel, bool)>.Success(
                (ToServiceModel(session, target), created));
    }

    /// <summary>
    /// <c>IsStale</c> is decided here and nowhere else: one answer about whether the guide has moved, so a
    /// client cannot reach a different one by comparing the numbers itself.
    /// </summary>
    private static BrandStyleGuideEditSessionServiceModel ToServiceModel(
        BrandStyleGuideEditSession session, BrandStyleGuideEditTarget target) =>
        new(
            session.BrandStyleGuideId,
            session.BaselineVersionNumber,
            target.WorkingVersionNumber,
            session.BaselineVersionNumber != target.WorkingVersionNumber,
            session.DraftJson,
            session.CreatedUtc,
            session.UpdatedUtc,
            BrandConcurrencyToken.From(session.RowVersion));

    private static OperationResult<(BrandStyleGuideEditSessionServiceModel, bool)> Failure(OperationError error) =>
        OperationResult<(BrandStyleGuideEditSessionServiceModel, bool)>.Failure(error);

    /// <summary>Answered identically to a guide that was never created (tenancy.md).</summary>
    private static OperationError NotFound() => new(
        BrandErrorCodes.GuideNotFound,
        "There is no such brand style guide.",
        new Dictionary<string, string[]>());

    private static OperationError Conflict() => new(
        BrandErrorCodes.GuideEditSessionConflict,
        "Your unsaved edit of this guide has changed somewhere else. Read it again and decide which copy to keep.",
        new Dictionary<string, string[]>());

    /// <summary>`If-Match` arrives quoted by the HTTP specification; the stored token is not.</summary>
    private static string? Unquote(string? ifMatch) => ifMatch?.Trim().Trim('"');
}
