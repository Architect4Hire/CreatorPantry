using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand.Data;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Business;

public interface IBrandSetupSessionBusiness
{
    /// <summary>The caller's own session, or a success carrying null when they have none.</summary>
    Task<OperationResult<BrandSetupSessionServiceModel?>> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates the caller's session (no <paramref name="ifMatch"/>) or updates it (matching token). Conflict on a
    /// missing or stale token against an existing session, or a token against none; a distinct conflict for a
    /// completed session. Returns whether the session was created.
    /// </summary>
    Task<OperationResult<(BrandSetupSessionServiceModel Session, bool Created)>> SaveAsync(
        SaveBrandSetupSessionViewModel model, string? ifMatch, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the session completed. Requires the token and furthest step = finish. Activates nothing and
    /// changes no guide, document, profile or publication.
    /// </summary>
    Task<OperationResult<BrandSetupSessionServiceModel>> CompleteAsync(string? ifMatch, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the caller's own session. Idempotent when none exists; a conflict failure when concurrent writes
    /// left the row in place.
    /// </summary>
    Task<OperationResult<bool>> DeleteAsync(CancellationToken cancellationToken);
}

internal sealed class BrandSetupSessionBusiness(
    IBrandSetupSessionDataLayer dataLayer,
    IWorkspaceContext workspace,
    IClock clock) : IBrandSetupSessionBusiness
{
    public async Task<OperationResult<BrandSetupSessionServiceModel?>> GetAsync(CancellationToken cancellationToken)
    {
        var session = await dataLayer.GetAsync(workspace.AccountId, cancellationToken);

        return OperationResult<BrandSetupSessionServiceModel?>.Success(session is null ? null : ToServiceModel(session));
    }

    public async Task<OperationResult<(BrandSetupSessionServiceModel Session, bool Created)>> SaveAsync(
        SaveBrandSetupSessionViewModel model, string? ifMatch, CancellationToken cancellationToken)
    {
        // Domain backstop for any caller that reaches Business without the facade's validator.
        if (BrandSetupSteps.Check(model.CurrentStep, model.FurthestStep, model.CompletedSteps, model.SkippedSteps).Any()
            || BrandSetupSessionDraft.Problem(model.DraftJson) is not null)
        {
            return Failure(InvalidRequest());
        }

        var userId = workspace.AccountId;
        var now = clock.UtcNow;
        var existing = await dataLayer.GetForUpdateAsync(userId, cancellationToken);

        if (existing is null)
        {
            if (!string.IsNullOrEmpty(ifMatch))
            {
                // The caller holds a token for a row that is gone (started over elsewhere): stale.
                return Failure(Conflict());
            }

            // WorkspaceId is left unset: WorkspaceOwnershipInterceptor stamps it from the resolved context.
            var session = new BrandSetupSession
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Status = BrandSetupSessionStatus.InProgress,
                CreatedUtc = now,
                UpdatedUtc = now,
            };

            Apply(session, model);

            var created = await dataLayer.CreateAsync(
                session, Audit(userId, BrandAuditActions.SetupSessionStarted, session, "Started a voice setup session."),
                cancellationToken);

            if (!created)
            {
                return Failure(Conflict());
            }

            return await Reload(created: true, cancellationToken);
        }

        if (existing.Status == BrandSetupSessionStatus.Completed)
        {
            return Failure(CompletedError());
        }

        if (!BrandConcurrencyToken.Matches(Unquote(ifMatch), existing.RowVersion))
        {
            return Failure(Conflict());
        }

        Apply(existing, model);
        existing.UpdatedUtc = now;

        var saved = await dataLayer.UpdateAsync(existing, audit: null, cancellationToken);

        return saved ? await Reload(created: false, cancellationToken) : Failure(Conflict());
    }

    public async Task<OperationResult<BrandSetupSessionServiceModel>> CompleteAsync(
        string? ifMatch, CancellationToken cancellationToken)
    {
        var userId = workspace.AccountId;
        var existing = await dataLayer.GetForUpdateAsync(userId, cancellationToken);

        if (existing is null)
        {
            return OperationResult<BrandSetupSessionServiceModel>.Failure(new OperationError(
                BrandErrorCodes.SetupSessionNotFound,
                "There is no voice setup session to complete.",
                new Dictionary<string, string[]>()));
        }

        if (existing.Status == BrandSetupSessionStatus.Completed)
        {
            return OperationResult<BrandSetupSessionServiceModel>.Failure(CompletedError());
        }

        if (!BrandConcurrencyToken.Matches(Unquote(ifMatch), existing.RowVersion))
        {
            return OperationResult<BrandSetupSessionServiceModel>.Failure(Conflict());
        }

        if (existing.FurthestStep != BrandSetupSteps.Finish)
        {
            return OperationResult<BrandSetupSessionServiceModel>.Failure(new OperationError(
                BrandErrorCodes.SetupSessionNotFinished,
                "The voice setup has not reached its last step.",
                new Dictionary<string, string[]>()));
        }

        var now = clock.UtcNow;
        existing.Status = BrandSetupSessionStatus.Completed;
        existing.CompletedUtc = now;
        existing.UpdatedUtc = now;

        var saved = await dataLayer.UpdateAsync(
            existing,
            Audit(userId, BrandAuditActions.SetupSessionCompleted, existing, "Completed the voice setup session."),
            cancellationToken);

        if (!saved)
        {
            return OperationResult<BrandSetupSessionServiceModel>.Failure(Conflict());
        }

        var reread = await dataLayer.GetAsync(userId, cancellationToken);

        return reread is null
            ? OperationResult<BrandSetupSessionServiceModel>.Failure(Conflict())
            : OperationResult<BrandSetupSessionServiceModel>.Success(ToServiceModel(reread));
    }

    public async Task<OperationResult<bool>> DeleteAsync(CancellationToken cancellationToken)
    {
        var userId = workspace.AccountId;

        var outcome = await dataLayer.DeleteAsync(
            userId,
            session => Audit(userId, BrandAuditActions.SetupSessionReset, session, "Started the voice setup over."),
            cancellationToken);

        return outcome == BrandSetupSessionDeleteOutcome.Conflict
            ? OperationResult<bool>.Failure(Conflict())
            : OperationResult<bool>.Success(outcome == BrandSetupSessionDeleteOutcome.Deleted);
    }

    private async Task<OperationResult<(BrandSetupSessionServiceModel, bool)>> Reload(
        bool created, CancellationToken cancellationToken)
    {
        var session = await dataLayer.GetAsync(workspace.AccountId, cancellationToken);

        return session is null
            ? Failure(Conflict())
            : OperationResult<(BrandSetupSessionServiceModel, bool)>.Success((ToServiceModel(session), created));
    }

    private static void Apply(BrandSetupSession session, SaveBrandSetupSessionViewModel model)
    {
        session.CurrentStep = model.CurrentStep!;
        session.FurthestStep = model.FurthestStep!;
        session.CompletedSteps = InWizardOrder(model.CompletedSteps!);
        session.SkippedSteps = InWizardOrder(model.SkippedSteps!);
        session.DraftJson = model.DraftJson!;
    }

    private static List<string> InWizardOrder(IReadOnlyList<string?> steps) =>
        steps.Select(step => step!).OrderBy(BrandSetupSteps.IndexOf).ToList();

    // ETag-shaped values arrive quoted (and possibly weak); the token itself is bare base64.
    private static string? Unquote(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("W/", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"' ? trimmed[1..^1] : trimmed;
    }

    private static BrandSetupSessionServiceModel ToServiceModel(BrandSetupSession session) => new(
        BrandSetupSessionStatuses.ToWire(session.Status),
        session.CurrentStep,
        session.FurthestStep,
        session.CompletedSteps.ToList(),
        session.SkippedSteps.ToList(),
        session.DraftJson,
        session.CreatedUtc,
        session.UpdatedUtc,
        session.CompletedUtc,
        BrandConcurrencyToken.From(session.RowVersion));

    private static OperationResult<(BrandSetupSessionServiceModel, bool)> Failure(OperationError error) =>
        OperationResult<(BrandSetupSessionServiceModel, bool)>.Failure(error);

    private static OperationError InvalidRequest() => new(
        BrandErrorCodes.SetupSessionInvalidRequest,
        "The voice setup session could not be saved.",
        new Dictionary<string, string[]>());

    private static OperationError Conflict() => new(
        BrandErrorCodes.SetupSessionConflict,
        "Your voice setup session changed since you loaded it. Reload it and try again.",
        new Dictionary<string, string[]>());

    private static OperationError CompletedError() => new(
        BrandErrorCodes.SetupSessionCompleted,
        "This voice setup session is already completed. Start over to begin a new one.",
        new Dictionary<string, string[]>());

    // References are the status only: the draft is creator content and never enters an audit row.
    private static AuditEntry Audit(string actorUserId, string action, BrandSetupSession session, string summary) => new(
        actorUserId,
        action,
        BrandAuditActions.SetupSessionResourceType,
        session.Id.ToString("D"),
        CorrelationId(),
        summary,
        BeforeReference: null,
        AfterReference: session.Status.ToString());

    private static Guid CorrelationId()
    {
        var traceId = System.Diagnostics.Activity.Current?.TraceId;

        return traceId is { } id && id != default ? Guid.ParseExact(id.ToHexString(), "N") : Guid.NewGuid();
    }
}
