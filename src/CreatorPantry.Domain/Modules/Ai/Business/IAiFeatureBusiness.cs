using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Business;

internal interface IAiFeatureBusiness
{
    Task<OperationResult<AiFeaturesServiceModel>> GetAsync(CancellationToken cancellationToken);

    Task<OperationResult<AiFeaturesServiceModel>> ReplaceAsync(
        ReplaceAiFeaturesViewModel model, CancellationToken cancellationToken);
}

/// <summary>
/// Which AI features a workspace has switched on, read and replaced as one value.
/// </summary>
/// <remarks>
/// A read reports the same answer <see cref="IAiTaskGate"/> would give a request, from the same two sources:
/// the workspace's stored choice, else the deployment default. A replace stores an answer for <em>every</em>
/// switchable task, named or not, so once an Owner has saved, nothing in this workspace depends on the
/// deployment default any more and a later change to it cannot move a creator's features under them.
/// </remarks>
internal sealed class AiFeatureBusiness(
    IAiTaskSwitchDataLayer dataLayer,
    IWorkspaceContext workspace,
    AiTaskOptions defaults,
    IClock clock) : IAiFeatureBusiness
{
    public async Task<OperationResult<AiFeaturesServiceModel>> GetAsync(CancellationToken cancellationToken) =>
        OperationResult<AiFeaturesServiceModel>.Success(Describe(await dataLayer.ListAsync(cancellationToken)));

    public async Task<OperationResult<AiFeaturesServiceModel>> ReplaceAsync(
        ReplaceAiFeaturesViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The validator has already refused anything that is not switchable; a name that slips past it is
        // dropped here rather than stored, so the table can only ever hold the catalogue's own spellings.
        var on = (model.Enabled ?? [])
            .Select(AiFeaturePolicy.Canonical)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        var states = AiFeaturePolicy.SwitchableTasks.ToDictionary(task => task, on.Contains, StringComparer.Ordinal);

        var before = Describe(await dataLayer.ListAsync(cancellationToken));

        var saved = await dataLayer.ReplaceAsync(
            states,
            clock.UtcNow,
            new AuditEntry(
                workspace.AccountId,
                AiFeatureAuditActions.Replaced,
                AiFeatureAuditActions.ResourceType,
                workspace.WorkspaceId.ToString("D"),
                CorrelationId(),
                "Changed which AI features are switched on.",

                // Counts only: an audit row stays safe to display, and how many were on says what changed
                // in size without listing a workspace's choices in a second place.
                BeforeReference: before.Features.Count(feature => feature.Enabled).ToString(),
                AfterReference: on.Count.ToString()),
            cancellationToken);

        if (!saved)
        {
            return OperationResult<AiFeaturesServiceModel>.Failure(new OperationError(
                AiFeatureErrors.Conflict,
                "These settings changed while this was saving. Reload them and apply your change again.",
                new Dictionary<string, string[]>()));
        }

        return await GetAsync(cancellationToken);
    }

    private AiFeaturesServiceModel Describe(IReadOnlyDictionary<string, bool> stored) => new(
    [
        .. AiFeaturePolicy.SwitchableTasks.Select(task => stored.TryGetValue(task, out var chosen)
            ? new AiFeatureServiceModel(task, chosen, SetByWorkspace: true)
            : new AiFeatureServiceModel(task, AiTaskCatalog.IsEnabled(defaults, task), SetByWorkspace: false)),
    ]);

    private static Guid CorrelationId()
    {
        // A W3C trace id is sixteen bytes, the same width as a Guid, so an operator can paste the audit row's
        // correlation id into a trace search and find the request.
        var traceId = System.Diagnostics.Activity.Current?.TraceId;

        return traceId is { } id && id != default ? Guid.ParseExact(id.ToHexString(), "N") : Guid.NewGuid();
    }
}
