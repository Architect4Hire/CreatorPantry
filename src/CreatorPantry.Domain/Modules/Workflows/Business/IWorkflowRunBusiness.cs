using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Workflows.Data;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Domain.Modules.Workflows.Business;

public interface IWorkflowRunBusiness
{
    /// <summary>Starts a run of the newest version of one workflow, owned by the caller (13A.5).</summary>
    Task<OperationResult<WorkflowRunServiceModel>> StartAsync(StartWorkflowRunViewModel model, CancellationToken cancellationToken);

    /// <summary>Reads one run as it stands, against the version it is pinned to, for the caller (13A.6).</summary>
    Task<OperationResult<WorkflowRunServiceModel>> GetAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Points the run's current step at the record its unfinished work is kept in (13A.7).</summary>
    Task<OperationResult<WorkflowRunServiceModel>> SaveDraftAsync(
        Guid runId, string stepKey, SaveWorkflowStepDraftViewModel model, CancellationToken cancellationToken);

    /// <summary>Completes the run's current step, once the server has found its completion rule met (13A.8).</summary>
    Task<OperationResult<WorkflowRunServiceModel>> CompleteStepAsync(
        Guid runId, string stepKey, CompleteWorkflowStepViewModel model, CancellationToken cancellationToken);

    /// <summary>Goes back from the run's current step to the one before it, reopening that step (13A.9).</summary>
    Task<OperationResult<WorkflowRunServiceModel>> BackAsync(
        Guid runId, string stepKey, WorkflowStepBackViewModel model, CancellationToken cancellationToken);

    /// <summary>Passes over the run's current step, which its workflow must mark optional (13A.9a).</summary>
    Task<OperationResult<WorkflowRunServiceModel>> SkipAsync(
        Guid runId, string stepKey, SkipWorkflowStepViewModel model, CancellationToken cancellationToken);

    /// <summary>Stops a run, keeping it whole, and starts the same journey afresh in its place (13A.9b).</summary>
    Task<OperationResult<WorkflowRunRestart>> RestartAsync(
        Guid runId, RestartWorkflowRunViewModel model, CancellationToken cancellationToken);
}

/// <summary>The run a Start over leaves the creator on, and whether this request started it or found it started.</summary>
public sealed record WorkflowRunRestart(WorkflowRunServiceModel Run, bool Started);

/// <remarks>
/// <para>
/// <strong>Orchestration only.</strong> Starting a run records where the creator is; it never performs the first
/// step's work, and never makes a record to stand in for one that is not there. A record is checked through the
/// module that owns it — its facade, never its tables — so "not found" means exactly what it means on that
/// module's own routes, and another workspace's record answers like an unknown one.
/// </para>
/// <para>
/// <strong>What a start can prove.</strong> When the first step's whole job is to link a record of the kind the
/// run starts from, that step is recorded complete: the record was just checked, so the fact is the server's.
/// Nothing else is ticked off — every later step's completion is 13A.8's to evaluate.
/// </para>
/// <para>
/// <strong>A read changes nothing</strong> and never moves a run onto a newer version: it renders the version the
/// run was started on, and says when a newer one exists. Who is reading decides only what the next action says
/// they can do.
/// </para>
/// </remarks>
internal sealed class WorkflowRunBusiness(
    IWorkflowRunDataLayer data,
    IWorkflowCatalogue catalogue,
    IWorkspaceContext workspace,
    IClock clock,
    IWorkflowFacts facts) : IWorkflowRunBusiness
{
    /// <inheritdoc cref="WorkflowFacts.CheckableKinds"/>
    internal static IReadOnlySet<WorkflowResourceKind> CheckableKinds => WorkflowFacts.CheckableKinds;

    public async Task<OperationResult<WorkflowRunServiceModel>> StartAsync(
        StartWorkflowRunViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The validator has already held the key to the catalogue.
        var definition = catalogue.Latest().Single(candidate => candidate.Key.Value == model.DefinitionKey);

        if (!definition.IsAvailable)
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.DefinitionUnavailable,
                definition.UnavailableReason!,
                new Dictionary<string, string[]>(),
                new Dictionary<string, object?> { ["reason"] = definition.UnavailableReason }));
        }

        var links = new Dictionary<WorkflowResourceKind, Guid>();
        if (model.StartingResource is { Kind: { } kind, Id: var id })
        {
            if (!await facts.ExistsAsync(kind, id, cancellationToken))
            {
                return Fail(new OperationError(
                    WorkflowErrorCodes.StartingResourceUnprocessable,
                    "That record could not be found in this workspace.",
                    new Dictionary<string, string[]>()));
            }

            links[kind] = id;
        }

        var unmet = await UnmetAsync(definition.Prerequisites, definition, new HashSet<string>(), links, cancellationToken);
        if (unmet.Count > 0)
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.PrerequisitesUnmet,
                "This journey needs something else first.",
                new Dictionary<string, string[]>(),
                new Dictionary<string, object?> { ["waitingFor"] = unmet }));
        }

        var (prepared, refused) = await PrepareAsync(
            definition,
            model.Title?.Trim() is { Length: > 0 } title ? title : definition.Goal,
            links,
            createdBy: workspace.MembershipId,
            assignedTo: null,
            cancellationToken);
        if (refused is not null)
        {
            return Fail(refused);
        }

        await data.CreateAsync(prepared!.Run, prepared.Steps, prepared.Links, cancellationToken);

        return OperationResult<WorkflowRunServiceModel>.Success(
            WorkflowRunMapper.ToServiceModel(definition, prepared.Run, prepared.Steps, prepared.Links, prepared.NextAction));
    }

    public async Task<OperationResult<WorkflowRunServiceModel>> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (await data.GetAsync(runId, cancellationToken) is not { } stored)
        {
            return Fail(NotFound());
        }

        var (run, steps, links) = stored;

        // The version it started on, never the newest. One the catalogue no longer has is an invariant broken —
        // the reviewed snapshot refuses to drop a shipped version — so it is a fault, not a guess.
        var definition = catalogue.Find(run.DefinitionKey, run.DefinitionVersion)
            ?? throw new InvalidOperationException(
                $"Workflow run {run.Id} is pinned to {run.DefinitionKey}@{run.DefinitionVersion}, which the catalogue does not have.");

        // Each record the run holds, re-checked through the module that owns it. One that is gone — or was never
        // this workspace's, which reads the same — is said once, and is not used to fill an address.
        var warnings = new List<WorkflowRunWarningServiceModel>();
        var missing = new HashSet<Guid>();
        var missingIds = new HashSet<Guid>();

        foreach (var link in links)
        {
            if (CheckableKinds.Contains(link.ResourceKind) && !await facts.ExistsAsync(link.ResourceKind, link.ResourceId, cancellationToken))
            {
                missing.Add(link.Id);
                missingIds.Add(link.ResourceId);
                warnings.Add(new WorkflowRunWarningServiceModel
                {
                    Code = WorkflowRunWarningCodes.LinkMissing,
                    Message = $"The {Noun(link.ResourceKind, article: false)} this run was using can't be found any more.",
                    Kind = link.ResourceKind,
                    ResourceId = link.ResourceId,
                    StepKey = link.StepKey,
                });
            }
        }

        // A step's saved draft is a pointer too, and can go the same way.
        foreach (var step in steps.Where(step => step.DraftResourceKind is { } kind && CheckableKinds.Contains(kind)))
        {
            if (!await facts.ExistsAsync(step.DraftResourceKind!.Value, step.DraftResourceId!.Value, cancellationToken))
            {
                warnings.Add(new WorkflowRunWarningServiceModel
                {
                    Code = WorkflowRunWarningCodes.DraftMissing,
                    Message = $"The {Noun(step.DraftResourceKind.Value, article: false)} saved as this step's draft can't be found any more.",
                    Kind = step.DraftResourceKind,
                    ResourceId = step.DraftResourceId,
                    StepKey = step.StepKey,
                });
            }
        }

        // The most recent link of each kind that is still there is the one an address is filled from; with none, the
        // address is null rather than one that leads to a page saying "not found".
        var latest = links
            .Where(link => !missing.Contains(link.Id))
            .GroupBy(link => link.ResourceKind)
            .ToDictionary(group => group.Key, group => group.Last().ResourceId);

        WorkflowNextActionServiceModel? nextAction = null;
        if (run.Status == WorkflowRunStatus.InProgress)
        {
            // CK_WorkflowRuns_CurrentStep_Status makes a run in progress always be on a step; one that is not is a
            // fault to report, not a run with nothing to do.
            var current = run.CurrentStepKey
                ?? throw new InvalidOperationException($"Workflow run {run.Id} is in progress on no step.");
            var step = definition.Steps.Single(candidate => candidate.Key.Value == current);

            // A run stays on its last step once every step is done; with nothing left to do, there is no next action,
            // and readyToComplete says the run can be finished (13A.10a).
            if (!Done(steps).Contains(current))
            {
                nextAction = await NextActionAsync(definition, step, Done(steps), latest, cancellationToken);
            }
        }

        var newest = catalogue.Latest().FirstOrDefault(candidate => candidate.Key.Value == run.DefinitionKey);
        if (run.Status == WorkflowRunStatus.InProgress && newest is not null && newest.Version > run.DefinitionVersion)
        {
            warnings.Add(new WorkflowRunWarningServiceModel
            {
                Code = WorkflowRunWarningCodes.NewerVersion,
                Message = "This journey has been updated since you started. You'll finish on the version you began with.",
                LatestVersion = newest.Version,
            });
        }

        if (RoleNeeded(nextAction) is { } needed)
        {
            warnings.Add(new WorkflowRunWarningServiceModel
            {
                Code = WorkflowRunWarningCodes.NextStepNeedsRole,
                Message = $"Someone with {needed} access needs to do the next step.",
            });
        }

        return OperationResult<WorkflowRunServiceModel>.Success(
            WorkflowRunMapper.ToServiceModel(definition, run, steps, links, nextAction, warnings, missingIds));
    }

    /// <remarks>
    /// <para>
    /// <strong>A pointer, never the work.</strong> The record is checked through the module that owns it and only its
    /// kind and id are kept. Which kinds a step takes is its actions' <see cref="WorkflowCapability.DraftKind"/>.
    /// </para>
    /// <para>
    /// <strong>Safe to repeat, and never over a newer edit.</strong> Saving the pointer the step already holds writes
    /// nothing and keeps the token, so a debounced client or a retry after a timeout always lands. Any other change
    /// must quote the token the caller read: one made against a run that has since changed is refused with what the
    /// run holds now, both here and — for a change that lands between this read and the write — in the database.
    /// </para>
    /// <para>
    /// <strong>Nothing claims success before it is committed.</strong> The answer is read back after the save.
    /// </para>
    /// </remarks>
    public async Task<OperationResult<WorkflowRunServiceModel>> SaveDraftAsync(
        Guid runId, string stepKey, SaveWorkflowStepDraftViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (await data.GetForUpdateAsync(runId, cancellationToken) is not { } stored)
        {
            return Fail(NotFound());
        }

        var (run, steps, _) = stored;

        if (!MayChange(run))
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.RunForbidden,
                "Only the person who started this run, the person it's for, or an Editor can change it.",
                new Dictionary<string, string[]>()));
        }

        var definition = catalogue.Find(run.DefinitionKey, run.DefinitionVersion)
            ?? throw new InvalidOperationException(
                $"Workflow run {run.Id} is pinned to {run.DefinitionKey}@{run.DefinitionVersion}, which the catalogue does not have.");

        if (definition.Steps.FirstOrDefault(candidate => candidate.Key.Value == stepKey) is not { } step)
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.StepNotFound, "This run has no such step.", new Dictionary<string, string[]>()));
        }

        var kind = model.Kind!.Value;
        var row = steps.Single(candidate => candidate.StepKey == stepKey);

        // The pointer the step already holds: nothing to write, so nothing to refuse — not even because the run has
        // moved on or finished since. A retry of a save that landed is answered as the success it was.
        if (row.DraftResourceKind == kind && row.DraftResourceId == model.Id)
        {
            return await GetAsync(runId, cancellationToken);
        }

        if (run.Status != WorkflowRunStatus.InProgress)
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.RunNotInProgress,
                "This run is finished, so nothing in it changes any more.",
                new Dictionary<string, string[]>()));
        }

        if (run.CurrentStepKey != stepKey)
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.StepNotCurrent,
                "This isn't the step the run is on.",
                new Dictionary<string, string[]>(),
                new Dictionary<string, object?> { ["currentStepKey"] = run.CurrentStepKey }));
        }

        // The run waits on its last step once every step is done or passed over; that step keeps no new draft.
        if (row.Status == WorkflowStepRunStatus.Skipped)
        {
            return Fail(PassedOver());
        }

        if (row.Status == WorkflowStepRunStatus.Completed)
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.StepAlreadyDone,
                "This step is already done. Go back to it to change it.",
                new Dictionary<string, string[]>()));
        }

        var takes = new[] { step.Capability.Primary }.Concat(step.Capability.Alternatives)
            .Select(key => WorkflowCapabilities.ByKey[key].DraftKind)
            .OfType<WorkflowResourceKind>()
            .ToHashSet();
        if (!takes.Contains(kind))
        {
            return Fail(OperationError.Validation(
                WorkflowErrorCodes.StepDraftInvalid,
                "This step doesn't keep its work in that kind of record.",
                [("kind", takes.Count == 0 ? "This step has no draft to save." : $"This step's draft is a {string.Join(" or a ", takes)}.")]));
        }

        WorkflowRunConcurrencyToken.TryParse(model.ExpectedConcurrencyToken, out var expected);
        if (!expected.AsSpan().SequenceEqual(run.RowVersion))
        {
            return Fail(Stale(run, row));
        }

        if (!await facts.ExistsAsync(kind, model.Id, cancellationToken))
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.StepDraftUnprocessable,
                "That record could not be found in this workspace.",
                new Dictionary<string, string[]>()));
        }

        var now = clock.UtcNow;
        row.DraftResourceKind = kind;
        row.DraftResourceId = model.Id;
        row.DraftSavedAt = now;
        if (row.Status == WorkflowStepRunStatus.NotStarted)
        {
            row.Status = WorkflowStepRunStatus.InProgress;
            row.StartedAt = now;
        }

        // Every change to a step is a change to its run, so the run's one token covers it.
        run.UpdatedAt = now;

        if (await data.SaveAsync(run, expected, cancellationToken) == WorkflowRunWrite.Stale)
        {
            if (await data.GetAsync(runId, cancellationToken) is not { } current)
            {
                return Fail(NotFound());
            }

            // Lost to a request that saved this same draft — a debounced double: it landed, and so did this.
            var held = current.Steps.Single(candidate => candidate.StepKey == stepKey);

            return held.DraftResourceKind == kind && held.DraftResourceId == model.Id
                ? await GetAsync(runId, cancellationToken)
                : Fail(Stale(current.Run, held));
        }

        return await GetAsync(runId, cancellationToken);
    }

    /// <remarks>
    /// <para>
    /// <strong>The server decides, from the owning modules.</strong> The step's rule is evaluated through
    /// <see cref="IWorkflowFacts"/> — a fresh readiness evaluation, a status as stored, the publishing ledger — and a
    /// client's say-so is never proof: a result counts only as a record checked to exist. Nothing here changes a
    /// status, so a step cannot be completed that the domain would not already call done.
    /// </para>
    /// <para>
    /// <strong>One write.</strong> The step, its result links, the run's progress and its current step move together,
    /// against the token the caller read, and the answer is read back after the commit. The run stays on the last step
    /// when none is left open; finishing it is 13A.10a's.
    /// </para>
    /// <para>
    /// <strong>Safe to repeat.</strong> A step that is already complete answers as it stands and writes nothing,
    /// whatever was sent and however far the run has moved since.
    /// </para>
    /// </remarks>
    public async Task<OperationResult<WorkflowRunServiceModel>> CompleteStepAsync(
        Guid runId, string stepKey, CompleteWorkflowStepViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (await data.GetForUpdateAsync(runId, cancellationToken) is not { } stored)
        {
            return Fail(NotFound());
        }

        var (run, steps, links) = stored;

        if (!MayChange(run))
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.RunForbidden,
                "Only the person who started this run, the person it's for, or an Editor can change it.",
                new Dictionary<string, string[]>()));
        }

        var definition = catalogue.Find(run.DefinitionKey, run.DefinitionVersion)
            ?? throw new InvalidOperationException(
                $"Workflow run {run.Id} is pinned to {run.DefinitionKey}@{run.DefinitionVersion}, which the catalogue does not have.");

        if (definition.Steps.FirstOrDefault(candidate => candidate.Key.Value == stepKey) is not { } step)
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.StepNotFound, "This run has no such step.", new Dictionary<string, string[]>()));
        }

        var row = steps.Single(candidate => candidate.StepKey == stepKey);

        // Already done: the answer it would have had, with nothing written.
        if (row.Status == WorkflowStepRunStatus.Completed)
        {
            return await GetAsync(runId, cancellationToken);
        }

        if (run.Status != WorkflowRunStatus.InProgress)
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.RunNotInProgress,
                "This run is finished, so nothing in it changes any more.",
                new Dictionary<string, string[]>()));
        }

        if (run.CurrentStepKey != stepKey)
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.StepNotCurrent,
                "This isn't the step the run is on.",
                new Dictionary<string, string[]>(),
                new Dictionary<string, object?> { ["currentStepKey"] = run.CurrentStepKey }));
        }

        // A step passed over is not completed where it stands — the record cannot be both. Back reopens it to be done.
        if (row.Status == WorkflowStepRunStatus.Skipped)
        {
            return Fail(PassedOver());
        }

        // Results are what a "link a record" step made; no other rule is met by naming a record.
        var linkedKinds = step.CompletionRule is WorkflowCompletionRule.ResourceLinked linked ? linked.Kinds : [];
        var results = model.Results ?? [];
        if (results.FirstOrDefault(result => !linkedKinds.Contains(result.Kind!.Value)) is { } stray)
        {
            return Fail(OperationError.Validation(
                WorkflowErrorCodes.StepCompletionInvalid,
                "That isn't something this step makes.",
                [("results", linkedKinds.Count == 0
                    ? "This step is done when the work is, not when a record is named."
                    : $"This step makes {string.Join(" and ", linkedKinds.Select(kind => Noun(kind, article: true)))}, not {Noun(stray.Kind!.Value, article: true)}.")]));
        }

        WorkflowRunConcurrencyToken.TryParse(model.ExpectedConcurrencyToken, out var expected);
        if (!expected.AsSpan().SequenceEqual(run.RowVersion))
        {
            return Fail(Stale(run, row));
        }

        // What this completion records: each kind the step links, from the results or else the step's own draft.
        var recorded = new Dictionary<WorkflowResourceKind, Guid>();
        var missing = new List<string>();
        foreach (var kind in linkedKinds)
        {
            // Named, else the step's draft, else the record of that kind the run already holds — which is how a
            // step reopened by Back completes again on what it made the first time.
            var named = results.FirstOrDefault(result => result.Kind == kind)?.Id;
            var drafted = row.DraftResourceKind == kind ? row.DraftResourceId : null;
            Guid? id = named ?? drafted ?? links.Where(link => link.ResourceKind == kind).OrderBy(link => link.LinkedAt).LastOrDefault()?.ResourceId;

            if (id is null)
            {
                missing.Add($"Say which {Noun(kind, article: false)} this step made.");
                continue;
            }

            // A kind nothing can check yet is never taken on trust.
            if (!CheckableKinds.Contains(kind) || !await facts.ExistsAsync(kind, id.Value, cancellationToken))
            {
                return Fail(new OperationError(
                    WorkflowErrorCodes.StepResultUnprocessable,
                    named is not null
                        ? "That record could not be found in this workspace."
                        : drafted is not null
                            ? $"The {Noun(kind, article: false)} this step's draft points at could not be found in this workspace."
                            : $"The {Noun(kind, article: false)} this run was using can't be found any more.",
                    new Dictionary<string, string[]>()));
            }

            recorded[kind] = id.Value;
        }

        // The run's most recent record of each kind, with what this completion records taking precedence.
        var latest = links
            .GroupBy(link => link.ResourceKind)
            .ToDictionary(group => group.Key, group => group.Last().ResourceId);
        foreach (var (kind, id) in recorded)
        {
            latest[kind] = id;
        }

        missing.AddRange(await facts.UnmetAsync(step.CompletionRule, run, latest, cancellationToken));
        if (missing.Count > 0)
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.StepIncomplete,
                "This step isn't finished yet.",
                new Dictionary<string, string[]>(),
                new Dictionary<string, object?> { ["missing"] = missing }));
        }

        var now = clock.UtcNow;
        row.Status = WorkflowStepRunStatus.Completed;
        row.StartedAt ??= now;
        row.CompletedAt = now;
        row.CompletedByMembershipId = workspace.MembershipId;

        // A record the run already holds is linked once; naming it again makes it the run's latest of its kind, so
        // the steps after this one read the record this step named rather than whichever was linked most recently.
        foreach (var held in links.Where(link => recorded.TryGetValue(link.ResourceKind, out var id) && id == link.ResourceId))
        {
            held.LinkedAt = now;
            held.LinkedByMembershipId = workspace.MembershipId;
        }

        var newLinks = recorded
            .Where(result => !links.Any(link => link.ResourceKind == result.Key && link.ResourceId == result.Value))
            .Select(result => new WorkflowRunLink
            {
                Id = Guid.NewGuid(),
                WorkflowRunId = run.Id,
                ResourceKind = result.Key,
                ResourceId = result.Value,
                StepKey = stepKey,
                LinkedAt = now,
                LinkedByMembershipId = workspace.MembershipId,
            })
            .ToList();

        run.CompletedStepCount++;

        run.CurrentStepKey = NextCurrent(definition, steps, stepKey);
        run.UpdatedAt = now;

        if (await data.SaveAsync(run, expected, cancellationToken, newLinks) == WorkflowRunWrite.Stale)
        {
            if (await data.GetAsync(runId, cancellationToken) is not { } current)
            {
                return Fail(NotFound());
            }

            // Lost to a request that completed this same step — a double submit: it landed, and so did this.
            var settled = current.Steps.Single(candidate => candidate.StepKey == stepKey);

            return settled.Status == WorkflowStepRunStatus.Completed
                ? await GetAsync(runId, cancellationToken)
                : Fail(Stale(current.Run, settled));
        }

        return await GetAsync(runId, cancellationToken);
    }

    /// <remarks>
    /// <para>
    /// <strong>Back reopens the step before.</strong> It goes to the step immediately before the current one in the
    /// pinned version and makes it open again — a completed step's completion, or a skipped one's skip, is cleared
    /// and its count taken off — so it can be worked on and completed again through the ordinary completion, which
    /// re-checks its rule. Nothing goes forward this way and nothing later than the current step is touched.
    /// </para>
    /// <para>
    /// <strong>Nothing is lost.</strong> The step being left keeps its state and its saved draft, the reopened step
    /// keeps its draft, and no record the run holds is unlinked.
    /// </para>
    /// <para>
    /// <strong>Safe to repeat.</strong> <paramref name="stepKey"/> names the step being left, so a Back whose result
    /// already holds — the run is on the step before it, and that step is open — is answered as the run stands with
    /// nothing written. That cannot tell a repeat from a Back that was never needed, and does not have to: either
    /// way the run is already where the request would leave it.
    /// </para>
    /// </remarks>
    public async Task<OperationResult<WorkflowRunServiceModel>> BackAsync(
        Guid runId, string stepKey, WorkflowStepBackViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (await data.GetForUpdateAsync(runId, cancellationToken) is not { } stored)
        {
            return Fail(NotFound());
        }

        var (run, steps, _) = stored;

        if (!MayChange(run))
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.RunForbidden,
                "Only the person who started this run, the person it's for, or an Editor can change it.",
                new Dictionary<string, string[]>()));
        }

        var definition = catalogue.Find(run.DefinitionKey, run.DefinitionVersion)
            ?? throw new InvalidOperationException(
                $"Workflow run {run.Id} is pinned to {run.DefinitionKey}@{run.DefinitionVersion}, which the catalogue does not have.");

        var index = definition.Steps.ToList().FindIndex(candidate => candidate.Key.Value == stepKey);
        if (index < 0)
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.StepNotFound, "This run has no such step.", new Dictionary<string, string[]>()));
        }

        if (run.Status != WorkflowRunStatus.InProgress)
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.RunNotInProgress,
                "This run is finished, so nothing in it changes any more.",
                new Dictionary<string, string[]>()));
        }

        var previousKey = index > 0 ? definition.Steps[index - 1].Key.Value : null;

        if (run.CurrentStepKey != stepKey)
        {
            // The run is already where this Back would leave it — on the step before, and that step open.
            if (previousKey is not null
                && run.CurrentStepKey == previousKey
                && !Done(steps).Contains(previousKey))
            {
                return await GetAsync(runId, cancellationToken);
            }

            return Fail(new OperationError(
                WorkflowErrorCodes.StepNotCurrent,
                "This isn't the step the run is on.",
                new Dictionary<string, string[]>(),
                new Dictionary<string, object?> { ["currentStepKey"] = run.CurrentStepKey }));
        }

        if (previousKey is null)
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.AtFirstStep,
                "This is the first step, so there's nothing to go back to.",
                new Dictionary<string, string[]>()));
        }

        WorkflowRunConcurrencyToken.TryParse(model.ExpectedConcurrencyToken, out var expected);
        var left = steps.Single(candidate => candidate.StepKey == stepKey);
        if (!expected.AsSpan().SequenceEqual(run.RowVersion))
        {
            return Fail(Stale(run, left));
        }

        var now = clock.UtcNow;
        var reopened = steps.Single(candidate => candidate.StepKey == previousKey);
        switch (reopened.Status)
        {
            // Every step before the current one is done or passed over: start, completion and Back all keep it so.
            case WorkflowStepRunStatus.NotStarted or WorkflowStepRunStatus.InProgress:
                throw new InvalidOperationException(
                    $"Workflow run {run.Id} is on {stepKey} while the step before it, {previousKey}, is still open.");

            case WorkflowStepRunStatus.Completed:
                reopened.CompletedAt = null;
                reopened.CompletedByMembershipId = null;
                run.CompletedStepCount--;
                break;

            case WorkflowStepRunStatus.Skipped:
                reopened.SkippedAt = null;
                reopened.SkippedByMembershipId = null;
                reopened.SkipReason = null;
                run.SkippedStepCount--;
                break;
        }

        reopened.Status = WorkflowStepRunStatus.InProgress;
        reopened.StartedAt ??= now;
        run.CurrentStepKey = previousKey;

        // Where the creator last was is the step they have just left; "Continue" should take them to this one.
        run.LastRoute = null;
        run.UpdatedAt = now;

        if (await data.SaveAsync(run, expected, cancellationToken) == WorkflowRunWrite.Stale)
        {
            if (await data.GetAsync(runId, cancellationToken) is not { } current)
            {
                return Fail(NotFound());
            }

            // Lost to the same Back from somewhere else: it landed, and so did this — judged as a replay is.
            return current.Run.CurrentStepKey == previousKey && !Done(current.Steps).Contains(previousKey)
                ? await GetAsync(runId, cancellationToken)
                : Fail(Stale(current.Run, current.Steps.Single(candidate => candidate.StepKey == stepKey)));
        }

        return await GetAsync(runId, cancellationToken);
    }

    /// <summary>
    /// Where the run goes once <paramref name="stepKey"/> is done or passed over: the first later step not yet done or
    /// passed over; with none, the last step of its version, where it waits to be finished — the last, not this one,
    /// which after a Back may be a step in the middle. Completion and Skip both move by this, so they cannot drift.
    /// </summary>
    private static string NextCurrent(WorkflowDefinition definition, IEnumerable<WorkflowStepRun> steps, string stepKey)
    {
        var done = Done(steps);
        var index = definition.Steps.ToList().FindIndex(candidate => candidate.Key.Value == stepKey);

        return definition.Steps.Skip(index + 1).FirstOrDefault(candidate => !done.Contains(candidate.Key.Value))?.Key.Value
            ?? definition.Steps[^1].Key.Value;
    }

    /// <remarks>
    /// <para>
    /// <strong>Optional steps only, and a skip is never a result.</strong> A step its workflow requires is refused.
    /// Skipping records that the creator passed the step over — when, who, and in their words why — and nothing else:
    /// no link, no completion, and the step's saved draft stays a draft rather than becoming what the step made.
    /// </para>
    /// <para>
    /// <strong>One write</strong>, as completion's: the step, the run's skipped count, and the run's move on to the
    /// next open step, against the token the caller read. Back undoes it (13A.9).
    /// </para>
    /// <para>
    /// <strong>Safe to repeat.</strong> A step already skipped answers as the run stands with nothing written, whatever
    /// was sent; so does a duplicate that loses a race to the first.
    /// </para>
    /// </remarks>
    public async Task<OperationResult<WorkflowRunServiceModel>> SkipAsync(
        Guid runId, string stepKey, SkipWorkflowStepViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (await data.GetForUpdateAsync(runId, cancellationToken) is not { } stored)
        {
            return Fail(NotFound());
        }

        var (run, steps, _) = stored;

        if (!MayChange(run))
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.RunForbidden,
                "Only the person who started this run, the person it's for, or an Editor can change it.",
                new Dictionary<string, string[]>()));
        }

        var definition = catalogue.Find(run.DefinitionKey, run.DefinitionVersion)
            ?? throw new InvalidOperationException(
                $"Workflow run {run.Id} is pinned to {run.DefinitionKey}@{run.DefinitionVersion}, which the catalogue does not have.");

        if (definition.Steps.FirstOrDefault(candidate => candidate.Key.Value == stepKey) is not { } step)
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.StepNotFound, "This run has no such step.", new Dictionary<string, string[]>()));
        }

        var row = steps.Single(candidate => candidate.StepKey == stepKey);

        // Already passed over: the answer it would have had, with nothing written.
        if (row.Status == WorkflowStepRunStatus.Skipped)
        {
            return await GetAsync(runId, cancellationToken);
        }

        if (run.Status != WorkflowRunStatus.InProgress)
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.RunNotInProgress,
                "This run is finished, so nothing in it changes any more.",
                new Dictionary<string, string[]>()));
        }

        if (run.CurrentStepKey != stepKey)
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.StepNotCurrent,
                "This isn't the step the run is on.",
                new Dictionary<string, string[]>(),
                new Dictionary<string, object?> { ["currentStepKey"] = run.CurrentStepKey }));
        }

        if (row.Status == WorkflowStepRunStatus.Completed)
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.StepAlreadyDone,
                "This step is already done. Go back to it to change it.",
                new Dictionary<string, string[]>()));
        }

        if (step.Requirement != WorkflowStepRequirement.Optional)
        {
            return Fail(new OperationError(
                WorkflowErrorCodes.StepRequired,
                "This step can't be skipped; the journey needs it.",
                new Dictionary<string, string[]>()));
        }

        WorkflowRunConcurrencyToken.TryParse(model.ExpectedConcurrencyToken, out var expected);
        if (!expected.AsSpan().SequenceEqual(run.RowVersion))
        {
            return Fail(Stale(run, row));
        }

        var now = clock.UtcNow;
        row.Status = WorkflowStepRunStatus.Skipped;
        row.SkippedAt = now;
        row.SkippedByMembershipId = workspace.MembershipId;
        row.SkipReason = model.Reason?.Trim() is { Length: > 0 } reason ? reason : null;
        run.SkippedStepCount++;
        run.CurrentStepKey = NextCurrent(definition, steps, stepKey);
        run.UpdatedAt = now;

        if (await data.SaveAsync(run, expected, cancellationToken) == WorkflowRunWrite.Stale)
        {
            if (await data.GetAsync(runId, cancellationToken) is not { } current)
            {
                return Fail(NotFound());
            }

            // Lost to a request that skipped this same step — a double submit: it landed, and so did this.
            var settled = current.Steps.Single(candidate => candidate.StepKey == stepKey);

            return settled.Status == WorkflowStepRunStatus.Skipped
                ? await GetAsync(runId, cancellationToken)
                : Fail(Stale(current.Run, settled));
        }

        return await GetAsync(runId, cancellationToken);
    }

    private static OperationError PassedOver() => new(
        WorkflowErrorCodes.StepAlreadyDone,
        "This step was passed over. Go back to it to do it.",
        new Dictionary<string, string[]>());

    /// <summary>A new run ready to be written: its row, its step rows, its links, and its first action.</summary>
    private sealed record PreparedRun(
        WorkflowRun Run, List<WorkflowStepRun> Steps, List<WorkflowRunLink> Links, WorkflowNextActionServiceModel NextAction);

    /// <summary>
    /// Builds a new run of <paramref name="definition"/> as a start does — every step not started, the first done when
    /// it links exactly the record the run starts from — or refuses it when the caller cannot take its first action.
    /// Start and Start over both build through here, so a run starts the same way however it is started.
    /// </summary>
    private async Task<(PreparedRun? Prepared, OperationError? Refused)> PrepareAsync(
        WorkflowDefinition definition,
        string title,
        IReadOnlyDictionary<WorkflowResourceKind, Guid> links,
        Guid createdBy,
        Guid? assignedTo,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var membershipId = workspace.MembershipId;
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            DefinitionKey = definition.Key.Value,
            DefinitionVersion = definition.Version,
            Title = title,
            Status = WorkflowRunStatus.InProgress,
            StepCount = definition.Steps.Count,
            CreatedByMembershipId = createdBy,
            AssignedMembershipId = assignedTo,
            StartedAt = now,
            UpdatedAt = now,
        };

        var steps = definition.Steps
            .Select(step => new WorkflowStepRun
            {
                Id = Guid.NewGuid(),
                WorkflowRunId = run.Id,
                StepKey = step.Key.Value,
                Status = WorkflowStepRunStatus.NotStarted,
            })
            .ToList();

        // A run that started from exactly what its first step links has done that step — and only that one, and
        // only when another step remains, so a start never finishes a journey on its own.
        if (definition.Steps.Count > 1
            && definition.Steps[0].CompletionRule is WorkflowCompletionRule.ResourceLinked { Kinds: [var first] }
            && links.ContainsKey(first))
        {
            steps[0].Status = WorkflowStepRunStatus.Completed;
            steps[0].CompletedAt = now;
            steps[0].CompletedByMembershipId = membershipId;
            run.CompletedStepCount = 1;
        }

        var done = Done(steps);
        var next = definition.Steps.First(step => !done.Contains(step.Key.Value));
        run.CurrentStepKey = next.Key.Value;

        var nextAction = await NextActionAsync(definition, next, done, links, cancellationToken);
        if (RoleNeeded(nextAction) is { } needed)
        {
            return (null, new OperationError(
                WorkflowErrorCodes.RoleInsufficient,
                $"Someone with {needed} access needs to start this.",
                new Dictionary<string, string[]>(),
                new Dictionary<string, object?> { ["neededRole"] = needed.ToString() }));
        }

        var rows = links
            .Select(link => new WorkflowRunLink
            {
                Id = Guid.NewGuid(),
                WorkflowRunId = run.Id,
                ResourceKind = link.Key,
                ResourceId = link.Value,
                StepKey = null,
                LinkedAt = now,
                LinkedByMembershipId = membershipId,
            })
            .ToList();

        return (new PreparedRun(run, steps, rows, nextAction), null);
    }

    /// <remarks>
    /// <para>
    /// <strong>Not a reset: a new run, and the old one kept whole.</strong> The old run is stopped — abandoned, with a
    /// pointer to the run that replaced it — and nothing in it changes otherwise: its steps, their reasons and drafts,
    /// and every record it linked stay as they were, as its history. The records themselves belong to their modules
    /// and are never touched.
    /// </para>
    /// <para>
    /// <strong>The new run starts as a start does</strong>, on the newest version of the same workflow, through the same
    /// builder. It keeps the old run's owner and the person it is for, so starting someone's run over does not quietly
    /// make it the restarter's; its title, unless a new one is sent; and the record the old run started from, when the
    /// newest version still starts from that kind and the record is still there.
    /// </para>
    /// <para>
    /// <strong>One write, and never a third run.</strong> The old run's stop (against the caller's token) and the new
    /// run land together or not at all. A run already replaced answers with its replacement, whatever was sent.
    /// </para>
    /// </remarks>
    public async Task<OperationResult<WorkflowRunRestart>> RestartAsync(
        Guid runId, RestartWorkflowRunViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (await data.GetForUpdateAsync(runId, cancellationToken) is not { } stored)
        {
            return OperationResult<WorkflowRunRestart>.Failure(NotFound());
        }

        var (run, _, links) = stored;

        if (!MayChange(run))
        {
            return OperationResult<WorkflowRunRestart>.Failure(new OperationError(
                WorkflowErrorCodes.RunForbidden,
                "Only the person who started this run, the person it's for, or an Editor can change it.",
                new Dictionary<string, string[]>()));
        }

        // Already started over: the run that replaced it, with nothing written.
        if (run.ReplacedByRunId is { } replacement)
        {
            return await ReplacementAsync(replacement, cancellationToken);
        }

        if (run.Status != WorkflowRunStatus.InProgress)
        {
            return OperationResult<WorkflowRunRestart>.Failure(new OperationError(
                WorkflowErrorCodes.RunNotInProgress,
                "This run is finished, so it can't be started over. Start the journey again instead.",
                new Dictionary<string, string[]>()));
        }

        WorkflowRunConcurrencyToken.TryParse(model.ExpectedConcurrencyToken, out var expected);
        if (!expected.AsSpan().SequenceEqual(run.RowVersion))
        {
            return OperationResult<WorkflowRunRestart>.Failure(RestartConflict(run));
        }

        var definition = catalogue.Latest().FirstOrDefault(candidate => candidate.Key.Value == run.DefinitionKey)
            ?? throw new InvalidOperationException($"Workflow run {run.Id} is of {run.DefinitionKey}, which the catalogue no longer has.");

        if (!definition.IsAvailable)
        {
            return OperationResult<WorkflowRunRestart>.Failure(new OperationError(
                WorkflowErrorCodes.DefinitionUnavailable,
                definition.UnavailableReason!,
                new Dictionary<string, string[]>(),
                new Dictionary<string, object?> { ["reason"] = definition.UnavailableReason }));
        }

        // The record the old run started from, when the newest version still starts from it and it is still there.
        var carried = new Dictionary<WorkflowResourceKind, Guid>();
        if (links.Where(link => link.StepKey is null).OrderBy(link => link.LinkedAt).LastOrDefault() is { } origin
            && definition.StartsFrom.Contains(origin.ResourceKind)
            && CheckableKinds.Contains(origin.ResourceKind)
            && await facts.ExistsAsync(origin.ResourceKind, origin.ResourceId, cancellationToken))
        {
            carried[origin.ResourceKind] = origin.ResourceId;
        }

        var unmet = await UnmetAsync(definition.Prerequisites, definition, new HashSet<string>(), carried, cancellationToken);
        if (unmet.Count > 0)
        {
            return OperationResult<WorkflowRunRestart>.Failure(new OperationError(
                WorkflowErrorCodes.PrerequisitesUnmet,
                "This journey needs something else first.",
                new Dictionary<string, string[]>(),
                new Dictionary<string, object?> { ["waitingFor"] = unmet }));
        }

        var (prepared, refused) = await PrepareAsync(
            definition,
            model.Title?.Trim() is { Length: > 0 } title ? title : run.Title,
            carried,
            createdBy: run.CreatedByMembershipId,
            assignedTo: run.AssignedMembershipId,
            cancellationToken);
        if (refused is not null)
        {
            return OperationResult<WorkflowRunRestart>.Failure(refused);
        }

        var now = prepared!.Run.StartedAt;
        run.Status = WorkflowRunStatus.Abandoned;
        run.AbandonedAt = now;
        run.ReplacedByRunId = prepared.Run.Id;
        run.UpdatedAt = now;

        if (await data.RestartAsync(run, expected, prepared.Run, prepared.Steps, prepared.Links, cancellationToken) == WorkflowRunWrite.Stale)
        {
            // Lost to another Start over of the same run: answer with the run that one started. Lost to any other
            // change: the conflict, with the token to take another look from.
            return await data.GetAsync(runId, cancellationToken) switch
            {
                { Run.ReplacedByRunId: { } winner } => await ReplacementAsync(winner, cancellationToken),
                { } current => OperationResult<WorkflowRunRestart>.Failure(RestartConflict(current.Run)),
                null => OperationResult<WorkflowRunRestart>.Failure(NotFound()),
            };
        }

        return OperationResult<WorkflowRunRestart>.Success(new WorkflowRunRestart(
            WorkflowRunMapper.ToServiceModel(definition, prepared.Run, prepared.Steps, prepared.Links, prepared.NextAction),
            Started: true));
    }

    private static OperationError RestartConflict(WorkflowRun run) => new(
        WorkflowErrorCodes.RunConflict,
        "This run changed since you last looked. Take another look before starting over.",
        new Dictionary<string, string[]>(),
        new Dictionary<string, object?> { ["currentConcurrencyToken"] = WorkflowRunConcurrencyToken.From(run.RowVersion) });

    private async Task<OperationResult<WorkflowRunRestart>> ReplacementAsync(Guid replacement, CancellationToken cancellationToken) =>
        await GetAsync(replacement, cancellationToken) is { Succeeded: true, Value: { } current }
            ? OperationResult<WorkflowRunRestart>.Success(new WorkflowRunRestart(current, Started: false))
            : OperationResult<WorkflowRunRestart>.Failure(NotFound());

    /// <summary>The owner, the person it is for, or an Editor and above.</summary>
    private bool MayChange(WorkflowRun run) =>
        workspace.Role >= WorkspaceRole.Editor
        || run.CreatedByMembershipId == workspace.MembershipId
        || run.AssignedMembershipId == workspace.MembershipId;

    private static OperationError NotFound() => new(
        WorkflowErrorCodes.RunNotFound, "That run could not be found in this workspace.", new Dictionary<string, string[]>());

    /// <summary>A refusal that says what the run holds now, so the client can take it up from there.</summary>
    private static OperationError Stale(WorkflowRun run, WorkflowStepRun row) => new(
        WorkflowErrorCodes.RunConflict,
        "This run changed since you last looked. Take another look before saving.",
        new Dictionary<string, string[]>(),
        new Dictionary<string, object?>
        {
            ["currentConcurrencyToken"] = WorkflowRunConcurrencyToken.From(run.RowVersion),
            ["draftKind"] = row.DraftResourceKind?.ToString(),
            ["draftResourceId"] = row.DraftResourceId,
        });

    /// <summary>The step to do now, its actions for the caller, and what it is still waiting for.</summary>
    private async Task<WorkflowNextActionServiceModel> NextActionAsync(
        WorkflowDefinition definition,
        WorkflowStepDefinition step,
        IReadOnlySet<string> done,
        IReadOnlyDictionary<WorkflowResourceKind, Guid> links,
        CancellationToken cancellationToken) => new()
    {
        StepKey = step.Key.Value,
        Title = step.Title,
        Help = step.Help,
        Requirement = step.Requirement,
        Actions =
        [
            .. new[] { step.Capability.Primary }.Concat(step.Capability.Alternatives)
                .Select(key => WorkflowCapabilities.ByKey[key])
                .Select(capability => new WorkflowActionServiceModel
                {
                    CapabilityKey = capability.Key.Value,
                    Name = capability.Name,
                    Route = WorkflowRoutes.Resolve(capability.RouteTemplate, links),
                    Available = capability.IsAvailable,
                    MinimumRole = capability.MinimumRole,
                    YouCanDoIt = capability.IsAvailable && workspace.Role >= capability.MinimumRole,
                }),
        ],
        WaitingFor = await UnmetAsync(step.Prerequisites, definition, done, links, cancellationToken),
    };

    /// <summary>
    /// The least role that could take the next action, when something could and the caller cannot; otherwise null.
    /// </summary>
    private static WorkspaceRole? RoleNeeded(WorkflowNextActionServiceModel? next) =>
        next is not null && next.Actions.Any(action => action.Available) && !next.Actions.Any(action => action.YouCanDoIt)
            ? next.Actions.Where(action => action.Available).Min(action => action.MinimumRole)
            : null;

    /// <summary>Steps that no longer stand in the way of a later one: done, or passed over.</summary>
    private static HashSet<string> Done(IEnumerable<WorkflowStepRun> steps) =>
        steps.Where(step => step.Status is WorkflowStepRunStatus.Completed or WorkflowStepRunStatus.Skipped)
            .Select(step => step.StepKey)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>What of these prerequisites is not true yet, each said in plain words.</summary>
    private async Task<IReadOnlyList<string>> UnmetAsync(
        IReadOnlyList<WorkflowPrerequisite> prerequisites,
        WorkflowDefinition definition,
        IReadOnlySet<string> done,
        IReadOnlyDictionary<WorkflowResourceKind, Guid> links,
        CancellationToken cancellationToken)
    {
        var unmet = new List<string>();

        foreach (var prerequisite in prerequisites)
        {
            switch (prerequisite)
            {
                case WorkflowPrerequisite.StepCompleted step when !done.Contains(step.Step.Value):
                    var title = definition.Steps.First(candidate => candidate.Key == step.Step).Title;
                    unmet.Add($"Finish “{title}” first.");
                    break;

                case WorkflowPrerequisite.ResourceLinked linked when !links.ContainsKey(linked.Kind):
                    unmet.Add($"This needs {Noun(linked.Kind, article: true)} first.");
                    break;

                case WorkflowPrerequisite.WorkspaceFact { Fact: WorkflowWorkspaceFact.SchedulingZoneSet }
                    when !await facts.SchedulingZoneSetAsync(cancellationToken):
                    unmet.Add("Set your workspace's time zone in brand settings first.");
                    break;
            }
        }

        return unmet;
    }

    private static string Noun(WorkflowResourceKind kind, bool article)
    {
        var noun = kind switch
        {
            WorkflowResourceKind.Recipe => "recipe",
            WorkflowResourceKind.CreativeContext => "idea",
            WorkflowResourceKind.LongFormDraft => "piece of writing",
            WorkflowResourceKind.BrandStyleGuide => "brand guide",
            WorkflowResourceKind.MediaAsset => "picture",
            WorkflowResourceKind.ContentCard => "card",
            WorkflowResourceKind.PublishingRecord => "booking",
            WorkflowResourceKind.WorkTask => "task",
            _ => "record",
        };

        return !article ? noun : noun[0] is 'a' or 'e' or 'i' or 'o' or 'u' ? $"an {noun}" : $"a {noun}";
    }

    private static OperationResult<WorkflowRunServiceModel> Fail(OperationError error) =>
        OperationResult<WorkflowRunServiceModel>.Failure(error);
}
