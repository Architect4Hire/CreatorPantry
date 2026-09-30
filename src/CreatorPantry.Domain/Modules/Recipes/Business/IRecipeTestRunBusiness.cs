using CreatorPantry.Domain.Managers.Patching;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Business;

/// <summary>
/// Domain rules for the test kitchen: what a recorded test may say, which version it may say it about, and
/// what a refusal means. Calls the DataLayer only.
/// </summary>
public interface IRecipeTestRunBusiness
{
    /// <summary>
    /// Records one test of one exact version of a recipe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Nothing here writes to the recipe.</strong> Every "actual" figure the creator reports sits on
    /// the test run beside what the version claims, never over it. Reconciling the two is the creator's
    /// decision and happens through the edit seam, which writes a version of its own.
    /// </para>
    /// <para>
    /// The refusals, in the order they are decided: a recipe that is not visible is
    /// <c>recipes.recipe.not_found</c>, whether it is unknown or another workspace's; a version number the
    /// recipe does not have is <c>recipes.version.not_found</c>, naming the field the caller sent; an archived
    /// recipe is <c>recipes.archived.conflict</c>; a <c>testedAt</c> in the future is
    /// <c>recipes.testRun.invalid_request</c>.
    /// </para>
    /// </remarks>
    /// <param name="actualYieldUnitDimension">
    /// The dimension of <see cref="CanonicalCreateTestRun.ActualYieldUnitId"/>, already resolved and verified
    /// by the facade through the measurement module. Null when no unit was named.
    /// </param>
    /// <remarks>
    /// The dimension arrives as an argument rather than being looked up here for the reason
    /// <c>IRecipeBusiness.CreateAsync</c> gives about its own yield unit: reading another module's catalogue is
    /// facade-to-facade work, and Business calls the DataLayer only. It has to be stored beside the id because
    /// the composite foreign key pins the two together.
    /// </remarks>
    Task<OperationResult<CreatedRecipeTestRunServiceModel>> CreateAsync(
        Guid recipeId,
        CanonicalCreateTestRun request,
        MeasurementDimension? actualYieldUnitDimension,
        CancellationToken cancellationToken);

    /// <summary>
    /// Changes part of one recorded test and returns it as it now stands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The version that was cooked never moves.</strong> Nothing in the request can repoint a run at a
    /// different version, because that would not be an edit to a record of what happened — it would be a claim
    /// that something else happened.
    /// </para>
    /// <para>
    /// <strong>Nothing here writes to the recipe</strong>, exactly as on a create.
    /// </para>
    /// <para>
    /// The refusals: an unknown run, another workspace's, or one belonging to a different recipe is
    /// <c>recipes.testRun.not_found</c>; an archived recipe is <c>recipes.archived.conflict</c>; a stale token
    /// is <c>recipes.testRun.conflict</c>; dropping a resolved issue is
    /// <c>recipes.testIssue.removal.conflict</c>; an incoherent merged result is
    /// <c>recipes.testRun.invalid_request</c>.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeTestRunServiceModel>> UpdateAsync(
        Guid recipeId,
        Guid testRunId,
        CanonicalUpdateTestRun request,
        MeasurementDimension? actualYieldUnitDimension,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records what was done about one issue a test found.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Writes nothing but the resolution.</strong> The issue is read without tracking and the
    /// observation behind it is never read at all, so "resolving an issue does not rewrite the test
    /// observation" holds by construction rather than by care.
    /// </para>
    /// <para>
    /// The refusals: an unknown run or issue is <c>recipes.testRun.not_found</c> or
    /// <c>recipes.testIssue.not_found</c>; an issue already resolved is
    /// <c>recipes.testIssue.resolved.conflict</c>; a correction version this recipe does not have is
    /// <c>recipes.version.not_found</c>; one at or before the tested version, with no override reason, is
    /// <c>recipes.testIssue.invalid_request</c>.
    /// </para>
    /// </remarks>
    Task<OperationResult<ResolvedTestIssueServiceModel>> ResolveIssueAsync(
        Guid recipeId,
        Guid testRunId,
        Guid issueId,
        CanonicalResolveTestIssue request,
        CancellationToken cancellationToken);
}

internal sealed class RecipeTestRunBusiness(
    IRecipeTestRunDataLayer dataLayer,
    IWorkspaceContext workspace,
    IClock clock) : IRecipeTestRunBusiness
{
    private const string CannotRecord = "That test could not be recorded as described.";

    private const string CannotUpdate = "That test could not be changed as described.";

    private const string CannotResolve = "That issue could not be resolved as described.";

    private const string SourceVersionNumberField = nameof(CreateRecipeTestRunViewModel.SourceVersionNumber);

    private const string TestedAtField = nameof(CreateRecipeTestRunViewModel.TestedAt);

    private const string ResolutionVersionNumberField = nameof(ResolveTestIssueViewModel.ResolutionVersionNumber);

    public async Task<OperationResult<CreatedRecipeTestRunServiceModel>> CreateAsync(
        Guid recipeId,
        CanonicalCreateTestRun request,
        MeasurementDimension? actualYieldUnitDimension,
        CancellationToken cancellationToken)
    {
        var target = await dataLayer.FindTargetAsync(recipeId, request.SourceVersionNumber, cancellationToken);
        if (target is null)
        {
            return NotFound();
        }

        // Before the archive gate, matching RestoreVersionAsync and for the reason it gives: a version this
        // recipe does not have is a mistake in the request, and no amount of bringing the recipe back will
        // make it right. Naming the field is what tells the two 404s apart for a client.
        if (target.VersionId is not { } versionId)
        {
            return OperationResult<CreatedRecipeTestRunServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.VersionNotFound,
                CannotRecord,
                [(SourceVersionNumberField, $"This recipe has no version {request.SourceVersionNumber}.")]));
        }

        // RecipePolicy.AcceptsContentChanges names the test-run seam explicitly as a caller that must ask, so
        // it asks. A test writes no recipe field, which is an argument for letting this through — but the rule
        // is written down in one place and disagreeing with it quietly is worse than the inconsistency. The
        // creator's remedy is to bring the recipe back first, which the error code says.
        if (!RecipePolicy.AcceptsContentChanges(target.Status))
        {
            return ArchivedConflict();
        }

        // One read of the clock for the whole operation, so the run and every row beneath it agree on when
        // this was recorded.
        var now = clock.UtcNow;

        // A test cooked in the future is a mistyped year, and it would sit at the top of this recipe's history
        // permanently. Tolerance rather than an exact comparison because a client's clock is not the server's.
        // There is deliberately no floor: a creator recording last year's bake is entering their own history.
        if (request.TestedAt > now + TestRunPolicy.FutureTolerance)
        {
            return OperationResult<CreatedRecipeTestRunServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.TestRunInvalidRequest,
                CannotRecord,
                [(TestedAtField, "A test cannot have been run in the future.")]));
        }

        var run = BuildRun(recipeId, versionId, request, actualYieldUnitDimension, now);
        var created = await dataLayer.CreateAsync(run, cancellationToken);

        return OperationResult<CreatedRecipeTestRunServiceModel>.Success(new CreatedRecipeTestRunServiceModel(
            created.TestRunId,
            recipeId,
            versionId,
            request.SourceVersionNumber,
            run.Outcome,
            run.TestedAt,
            run.CreatedAt,

            // Read after the save, which is what populated it: the database generates a rowversion, so asking
            // before the write would publish eight zero bytes and every first edit would be a conflict.
            RecipeConcurrencyToken.From(run.RowVersion),
            created.ObservationIds,
            created.IssueIds));
    }

    /// <summary>
    /// Assembles the whole graph: the run, its notes, and its issues with their notes already attached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>WorkspaceId</c> is left unset on every entity. The ownership interceptor stamps it, and Business
    /// assigning it is the defect tenancy.md names — a workspace that came from anywhere but the resolved
    /// context is a workspace a request could have chosen.
    /// </para>
    /// <para>
    /// The yield unit's dimension is stored beside its id because the composite foreign key
    /// <c>(ActualYieldUnitId, ActualYieldUnitDimension) -> MeasurementUnits (Id, Dimension)</c> pins the two
    /// together — which is what makes <c>CK_RecipeTestRuns_ActualYieldUnit_Dimension</c> able to rule out a
    /// yield measured in degrees. It arrives already resolved; nothing here reads the catalogue.
    /// </para>
    /// </remarks>
    private RecipeTestRun BuildRun(
        Guid recipeId,
        Guid versionId,
        CanonicalCreateTestRun request,
        MeasurementDimension? actualYieldUnitDimension,
        DateTimeOffset now)
    {
        var run = new RecipeTestRun
        {
            Id = Guid.NewGuid(),
            RecipeId = recipeId,
            RecipeVersionId = versionId,
            TestedAt = request.TestedAt,

            // The authenticated caller, from the resolved context and never from the request. The entity keeps
            // the tester and the author apart for the day a creator may write up somebody else's bake; until
            // the contract carries that, they are the same person.
            TestedByMembershipId = workspace.MembershipId,
            Outcome = request.Outcome,
            Rating = request.Rating,
            EnvironmentNotes = request.EnvironmentNotes,
            EquipmentNotes = request.EquipmentNotes,
            SummaryNotes = request.SummaryNotes,
            ActualYieldText = request.ActualYieldText,
            ActualYieldQuantity = request.ActualYieldQuantity,
            ActualYieldUnitId = request.ActualYieldUnitId,
            ActualYieldUnitDimension = actualYieldUnitDimension,
            ActualPrepTimeMinutes = request.ActualPrepTimeMinutes,
            ActualCookTimeMinutes = request.ActualCookTimeMinutes,
            ActualRestTimeMinutes = request.ActualRestTimeMinutes,

            // Stored exactly as sent. Nothing sums the other three into it — a kitchen that overlaps prep with
            // cooking produces a total below their sum, and deriving it would replace a measurement with
            // arithmetic.
            ActualTotalTimeMinutes = request.ActualTotalTimeMinutes,
            CreatedByMembershipId = workspace.MembershipId,
            UpdatedByMembershipId = workspace.MembershipId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Kept so an issue can find the note it came from. The key is the position the observation arrived at,
        // which is what TestIssueInputViewModel.ObservationIndex names.
        var observationsByIndex = new Dictionary<int, TestObservation>();

        var order = 0;
        foreach (var submitted in request.Observations)
        {
            var observation = new TestObservation
            {
                Id = Guid.NewGuid(),
                RecipeTestRunId = run.Id,
                Kind = submitted.Kind,
                Text = submitted.Text,
                SortOrder = order++,
            };

            observationsByIndex[submitted.SubmittedIndex] = observation;
            run.Observations.Add(observation);
        }

        order = 0;
        foreach (var submitted in request.Issues)
        {
            run.Issues.Add(new TestIssue
            {
                Id = Guid.NewGuid(),
                RecipeId = recipeId,
                RecipeTestRunId = run.Id,
                Severity = submitted.Severity,
                Title = submitted.Title,
                Description = submitted.Description,

                // Resolved from the position the request used to the id this write is about to give that note.
                // An index the validator has already bounded, so a miss here would be a bug rather than a bad
                // request — and it resolves to null rather than throwing, because an issue without its note is
                // still the issue the creator reported.
                TestObservationId = submitted.ObservationIndex is { } index
                    && observationsByIndex.TryGetValue(index, out var observation)
                        ? observation.Id
                        : null,
                SortOrder = order++,
            });
        }

        return run;
    }

    public async Task<OperationResult<RecipeTestRunServiceModel>> UpdateAsync(
        Guid recipeId,
        Guid testRunId,
        CanonicalUpdateTestRun request,
        MeasurementDimension? actualYieldUnitDimension,
        CancellationToken cancellationToken)
    {
        // The recipe first, because an archived one refuses this edit and because a caller who may not see the
        // recipe must not learn which of its tests exist.
        if (await dataLayer.FindRecipeStatusAsync(recipeId, cancellationToken) is not { } status)
        {
            return RunNotFound();
        }

        if (!RecipePolicy.AcceptsContentChanges(status))
        {
            return Refuse<RecipeTestRunServiceModel>(
                RecipeErrorCodes.RecipeArchivedConflict,
                "This recipe is archived. Bring it back from the archive before changing its tests.");
        }

        if (await dataLayer.GetForUpdateAsync(recipeId, testRunId, cancellationToken) is not { } run)
        {
            return RunNotFound();
        }

        // Checked before anything is merged, so a refused edit is answered from the state it was composed
        // against rather than from a half-applied one. The database repeats this when the save runs; this one
        // is not redundant, because it is the only one that can produce a readable answer and it catches the
        // far commoner case — a token that went stale minutes ago, not microseconds.
        if (!RecipeConcurrencyToken.Matches(request.ExpectedConcurrencyToken, run.RowVersion))
        {
            return Conflict();
        }

        // Every judgement about the run as it would end up, not as it was submitted: a patch names only part of
        // it, so a rule reading a submitted value alone would refuse coherent edits and accept incoherent ones.
        if (Invalid(request, run) is { } invalid)
        {
            return OperationResult<RecipeTestRunServiceModel>.Failure(invalid);
        }

        if (Merge(run, request, actualYieldUnitDimension) is { } refusal)
        {
            return OperationResult<RecipeTestRunServiceModel>.Failure(refusal);
        }

        // Stamped whether or not anything actually differs. An edit that changes nothing is still somebody
        // saying they looked at this test and stood by it, and the alternative — comparing every field to
        // decide whether to stamp — buys nothing here: unlike a recipe edit, this writes no version, so a
        // no-op save costs one row and misleads nobody.
        run.UpdatedByMembershipId = workspace.MembershipId;
        run.UpdatedAt = clock.UtcNow;

        if (!(await dataLayer.UpdateAsync(run, cancellationToken)).Succeeded)
        {
            return Conflict();
        }

        return OperationResult<RecipeTestRunServiceModel>.Success(RecipeTestRunMapper.ToServiceModel(run));
    }

    public async Task<OperationResult<ResolvedTestIssueServiceModel>> ResolveIssueAsync(
        Guid recipeId,
        Guid testRunId,
        Guid issueId,
        CanonicalResolveTestIssue request,
        CancellationToken cancellationToken)
    {
        if (await dataLayer.FindRecipeStatusAsync(recipeId, cancellationToken) is not { } status)
        {
            return Refuse<ResolvedTestIssueServiceModel>(
                RecipeErrorCodes.TestRunNotFound, "That test could not be found.");
        }

        if (!RecipePolicy.AcceptsContentChanges(status))
        {
            return Refuse<ResolvedTestIssueServiceModel>(
                RecipeErrorCodes.RecipeArchivedConflict,
                "This recipe is archived. Bring it back from the archive before resolving its issues.");
        }

        if (await dataLayer.FindIssueForResolutionAsync(recipeId, testRunId, issueId, cancellationToken)
            is not { } issue)
        {
            // One code for both "no such run" and "no such issue of it", because the read cannot tell them
            // apart without a second query whose only purpose would be to disclose which. The route already
            // names both ids, so a client has everything it needs to check either.
            return Refuse<ResolvedTestIssueServiceModel>(
                RecipeErrorCodes.TestIssueNotFound, "That issue is not one of this test's.");
        }

        // Asked before the write so the answer is readable. The unique index is still the authority — see
        // RecipeTestRunDataLayer.ResolveAsync for the race this cannot close.
        if (issue.AlreadyResolved)
        {
            return AlreadyResolved();
        }

        Guid? correctionVersionId = null;
        if (request.ResolutionVersionNumber is { } versionNumber)
        {
            correctionVersionId = await dataLayer.FindVersionIdAsync(recipeId, versionNumber, cancellationToken);
            if (correctionVersionId is null)
            {
                return OperationResult<ResolvedTestIssueServiceModel>.Failure(OperationError.Validation(
                    RecipeErrorCodes.VersionNotFound,
                    CannotResolve,
                    [(ResolutionVersionNumberField, $"This recipe has no version {versionNumber}.")]));
            }

            // A version written at or before the one that was tested cannot contain a fix for something that
            // test found. Compared by number rather than by timestamp because numbers run upward within a
            // recipe, which makes this two integers instead of two clocks.
            //
            // Refused rather than merely flagged, unless the caller says why it should stand: a creator may be
            // recording that an older version had it right all along and the regression came later, and that is
            // a real thing to record — but it has to be said rather than assumed.
            if (versionNumber <= issue.TestedVersionNumber
                && request.PredatingVersionOverrideReason is null)
            {
                return OperationResult<ResolvedTestIssueServiceModel>.Failure(OperationError.Validation(
                    RecipeErrorCodes.TestIssueInvalidRequest,
                    CannotResolve,
                    [(
                        ResolutionVersionNumberField,
                        $"Version {versionNumber} is not later than version {issue.TestedVersionNumber}, "
                            + "which is the one that was tested. Say why it is the correction anyway.")]));
            }
        }

        var resolution = new TestIssueResolution
        {
            Id = Guid.NewGuid(),

            // From the issue the lookup returned, never from the route: this is the value the composite
            // foreign key uses to pin the correction version to the right recipe, and taking it from the issue
            // is what makes that pinning mean something.
            RecipeId = issue.RecipeId,
            TestIssueId = issue.IssueId,
            Kind = request.Kind,
            Notes = request.Notes,
            ResolvedByMembershipId = workspace.MembershipId,
            ResolvedAt = clock.UtcNow,
            ResolutionRecipeVersionId = correctionVersionId,

            // Recorded only when it was actually needed. A reason sent for a version that is already later
            // than the tested one would otherwise sit in the record implying an override that never happened.
            PredatingVersionOverrideReason =
                correctionVersionId is not null
                    && request.ResolutionVersionNumber <= issue.TestedVersionNumber
                        ? request.PredatingVersionOverrideReason
                        : null,
        };

        var outcome = await dataLayer.ResolveAsync(resolution, cancellationToken);
        if (!outcome.Succeeded)
        {
            return AlreadyResolved();
        }

        return OperationResult<ResolvedTestIssueServiceModel>.Success(new ResolvedTestIssueServiceModel(
            testRunId, issue.IssueId, RecipeTestRunMapper.ToServiceModel(outcome.Resolution!)));
    }

    /// <summary>
    /// The invariants an edit must satisfy, judged against the run it would produce rather than against what
    /// the body happened to mention.
    /// </summary>
    private static OperationError? Invalid(CanonicalUpdateTestRun request, RecipeTestRun run)
    {
        var quantity = request.ActualYieldQuantity.Or(run.ActualYieldQuantity);
        var unitId = request.ActualYieldUnitId.Or(run.ActualYieldUnitId);

        // Mirrors CK_RecipeTestRuns_ActualYieldUnit_RequiresQuantity, and reachable only through a merge: a
        // request that clears the quantity while leaving an existing unit alone is coherent field by field and
        // incoherent as a result.
        if (unitId is not null && quantity is null)
        {
            return OperationError.Validation(
                RecipeErrorCodes.TestRunInvalidRequest,
                CannotUpdate,
                [(
                    nameof(UpdateRecipeTestRunViewModel.ActualYieldQuantity),
                    "A yield unit needs an amount to measure. Clear the unit as well, or keep an amount.")]);
        }

        // An index into a list the request did not submit cannot be resolved — the positions it would name are
        // the stored notes' order, which is not what a submitted list means anywhere else on this route.
        if (request.Issues.IsSubmitted
            && !request.Observations.IsSubmitted
            && request.Issues.Value.Any(issue => issue.ObservationIndex is not null))
        {
            return OperationError.Validation(
                RecipeErrorCodes.TestRunInvalidRequest,
                CannotUpdate,
                [(
                    nameof(UpdateRecipeTestRunViewModel.Issues),
                    "An issue can only point at an observation by position when the observations are sent too.")]);
        }

        return null;
    }

    /// <summary>
    /// Applies the edit to the loaded run, field by field and then list by list.
    /// </summary>
    /// <returns>The refusal a reconciliation produced, or null when the merge succeeded.</returns>
    /// <remarks>
    /// Written with <see cref="PatchField{T}.Or"/> throughout, so "a field the body did not mention is left
    /// alone" is the shape of the code rather than a rule somebody has to keep applying.
    /// </remarks>
    private static OperationError? Merge(
        RecipeTestRun run,
        CanonicalUpdateTestRun request,
        MeasurementDimension? actualYieldUnitDimension)
    {
        run.TestedAt = request.TestedAt.Or(run.TestedAt) ?? run.TestedAt;
        run.Outcome = request.Outcome.Or(run.Outcome) ?? run.Outcome;
        run.Rating = request.Rating.Or(run.Rating);
        run.EnvironmentNotes = request.EnvironmentNotes.Or(run.EnvironmentNotes);
        run.EquipmentNotes = request.EquipmentNotes.Or(run.EquipmentNotes);
        run.SummaryNotes = request.SummaryNotes.Or(run.SummaryNotes);
        run.ActualYieldText = request.ActualYieldText.Or(run.ActualYieldText);
        run.ActualYieldQuantity = request.ActualYieldQuantity.Or(run.ActualYieldQuantity);
        run.ActualPrepTimeMinutes = request.ActualPrepTimeMinutes.Or(run.ActualPrepTimeMinutes);
        run.ActualCookTimeMinutes = request.ActualCookTimeMinutes.Or(run.ActualCookTimeMinutes);
        run.ActualRestTimeMinutes = request.ActualRestTimeMinutes.Or(run.ActualRestTimeMinutes);
        run.ActualTotalTimeMinutes = request.ActualTotalTimeMinutes.Or(run.ActualTotalTimeMinutes);

        // The unit and its dimension move together or not at all. The dimension arrives already resolved for a
        // submitted unit and is null when the unit was cleared, which is exactly what
        // CK_RecipeTestRuns_ActualYieldUnit_Dimension requires of the pair.
        if (request.ActualYieldUnitId.IsSubmitted)
        {
            run.ActualYieldUnitId = request.ActualYieldUnitId.Value;
            run.ActualYieldUnitDimension = actualYieldUnitDimension;
        }

        // Reconciled eagerly rather than compared first, exactly as a recipe edit's instructions are: there is
        // no single "current value" to test a list against, only a graph to reconcile with.
        if (request.Observations.IsSubmitted)
        {
            ReconcileObservations(run, request.Observations.Value);
        }

        if (request.Issues.IsSubmitted)
        {
            return ReconcileIssues(run, request.Issues.Value, request.Observations);
        }

        return null;
    }

    /// <summary>
    /// Brings the run's notes into line with a submitted list: entries naming a note it owns are updated in
    /// place, entries naming none are added, and anything omitted is removed.
    /// </summary>
    /// <remarks>
    /// An id the submission names that does not belong to this run is not distinguished from no id at all: it
    /// is silently treated as a new note. Refusing it would first have to decide whether the id is unknown or
    /// names a row in another workspace, and answering that is the disclosure tenancy.md forbids. Treating both
    /// alike costs nothing — the result is identical either way.
    /// </remarks>
    private static void ReconcileObservations(
        RecipeTestRun run,
        IReadOnlyList<CanonicalTestObservationEdit> submitted)
    {
        var existing = run.Observations.ToDictionary(observation => observation.Id);
        var kept = new HashSet<Guid>();

        for (var index = 0; index < submitted.Count; index++)
        {
            var source = submitted[index];

            if (source.Id is { } id && existing.TryGetValue(id, out var found))
            {
                kept.Add(id);
                found.Kind = source.Kind;
                found.Text = source.Text;
                found.SortOrder = index;
                continue;
            }

            run.Observations.Add(new TestObservation
            {
                Id = Guid.NewGuid(),

                // Set here rather than left to the ownership interceptor, unlike on a create: this row is
                // added to a graph whose workspace is already known and loaded, and the composite foreign key
                // needs the two to agree at insert time.
                WorkspaceId = run.WorkspaceId,
                RecipeTestRunId = run.Id,
                Kind = source.Kind,
                Text = source.Text,
                SortOrder = index,
            });
        }

        foreach (var orphan in run.Observations.Where(observation => !kept.Contains(observation.Id)
            && existing.ContainsKey(observation.Id)).ToList())
        {
            run.Observations.Remove(orphan);
        }
    }

    /// <inheritdoc cref="ReconcileObservations"/>
    /// <returns>The refusal a removed-but-resolved issue produced, or null.</returns>
    private static OperationError? ReconcileIssues(
        RecipeTestRun run,
        IReadOnlyList<CanonicalTestIssueEdit> submitted,
        PatchField<IReadOnlyList<CanonicalTestObservationEdit>> observations)
    {
        var existing = run.Issues.ToDictionary(issue => issue.Id);
        var kept = new HashSet<Guid>();

        // Resolved after the observations were reconciled, so a position names the note the edit actually left
        // in place — including one this same request just added.
        var byPosition = observations.IsSubmitted
            ? run.Observations.OrderBy(observation => observation.SortOrder).ToList()
            : [];

        for (var index = 0; index < submitted.Count; index++)
        {
            var source = submitted[index];
            var observationId = source.ObservationIndex is { } position && position < byPosition.Count
                ? byPosition[position].Id
                : (Guid?)null;

            if (source.Id is { } id && existing.TryGetValue(id, out var found))
            {
                kept.Add(id);
                found.Severity = source.Severity;
                found.Title = source.Title;
                found.Description = source.Description;
                found.SortOrder = index;

                // Only when the request was in a position to say. An edit that left the observations alone
                // cannot repoint an issue, and must not silently unpoint one either.
                if (observations.IsSubmitted)
                {
                    found.TestObservationId = observationId;
                }

                continue;
            }

            run.Issues.Add(new TestIssue
            {
                Id = Guid.NewGuid(),
                WorkspaceId = run.WorkspaceId,
                RecipeId = run.RecipeId,
                RecipeTestRunId = run.Id,
                Severity = source.Severity,
                Title = source.Title,
                Description = source.Description,
                TestObservationId = observationId,
                SortOrder = index,
            });
        }

        var orphans = run.Issues
            .Where(issue => !kept.Contains(issue.Id) && existing.ContainsKey(issue.Id))
            .ToList();

        // Refused before anything is detached, so the run is left exactly as it was read. A resolution records
        // a decision somebody took about this issue, and dropping the issue would erase the decision with it —
        // the database says the same thing less helpfully, by refusing to delete an immutable row.
        if (orphans.FirstOrDefault(issue => issue.Resolution is not null) is { } resolved)
        {
            return new OperationError(
                RecipeErrorCodes.TestIssueResolvedRemovalConflict,
                $"\"{resolved.Title}\" has been resolved, so it cannot be removed from this test. "
                    + "Keep it in the list.",
                new Dictionary<string, string[]>());
        }

        foreach (var orphan in orphans)
        {
            run.Issues.Remove(orphan);
        }

        return null;
    }

    private static OperationResult<RecipeTestRunServiceModel> RunNotFound() =>
        Refuse<RecipeTestRunServiceModel>(RecipeErrorCodes.TestRunNotFound, "That test could not be found.");

    private static OperationResult<RecipeTestRunServiceModel> Conflict() =>
        Refuse<RecipeTestRunServiceModel>(
            RecipeErrorCodes.TestRunConflict,
            "This test has changed since you opened it. Reload it and make your edit again.");

    private static OperationResult<ResolvedTestIssueServiceModel> AlreadyResolved() =>
        Refuse<ResolvedTestIssueServiceModel>(
            RecipeErrorCodes.TestIssueResolvedConflict, "That issue has already been resolved.");

    private static OperationResult<T> Refuse<T>(string code, string message) =>
        OperationResult<T>.Failure(new OperationError(code, message, new Dictionary<string, string[]>()));

    private static OperationResult<CreatedRecipeTestRunServiceModel> NotFound() =>
        OperationResult<CreatedRecipeTestRunServiceModel>.Failure(new OperationError(
            RecipeErrorCodes.RecipeNotFound,
            "That recipe could not be found.",
            new Dictionary<string, string[]>()));

    private static OperationResult<CreatedRecipeTestRunServiceModel> ArchivedConflict() =>
        OperationResult<CreatedRecipeTestRunServiceModel>.Failure(new OperationError(
            RecipeErrorCodes.RecipeArchivedConflict,
            "This recipe is archived. Bring it back from the archive before recording a test against it.",
            new Dictionary<string, string[]>()));
}
