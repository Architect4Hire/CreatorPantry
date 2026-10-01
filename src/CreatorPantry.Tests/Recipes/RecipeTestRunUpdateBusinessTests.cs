using CreatorPantry.Domain.Managers.Patching;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// What Business decides on an edit to a recorded test and on resolving one of its issues, against a recording
/// DataLayer. No database: under test is the merge, the reconciliation, the refusal ordering, and what is
/// deliberately left untouched.
/// </summary>
public sealed class RecipeTestRunUpdateBusinessTests
{
    private static readonly Guid ActorMembershipId = Guid.NewGuid();
    private static readonly Guid EditorMembershipId = Guid.NewGuid();
    private static readonly Guid RecipeId = Guid.NewGuid();
    private static readonly Guid TestedVersionId = Guid.NewGuid();
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] CurrentRowVersion = [1, 2, 3, 4, 5, 6, 7, 8];

    private static readonly string CurrentToken = RecipeConcurrencyToken.From(CurrentRowVersion);

    private readonly FakeTestRunDataLayer _dataLayer = new();
    private readonly IRecipeTestRunBusiness _business;

    public RecipeTestRunUpdateBusinessTests() =>
        _business = new ServiceCollection()
            .AddSingleton<IRecipeTestRunDataLayer>(_dataLayer)
            .AddSingleton<IWorkspaceContext>(new EditingWorkspaceContext(ActorMembershipId, WorkspaceId))
            .AddSingleton<IClock>(new FixedClock(Now))
            .AddSingleton<IRecipeTestRunBusiness, RecipeTestRunBusiness>()
            .BuildServiceProvider()
            .GetRequiredService<IRecipeTestRunBusiness>();

    /// <summary>A stored run with one note and one issue raised from it, as a read would return it.</summary>
    private static RecipeTestRun StoredRun(bool resolveTheIssue = false)
    {
        var run = new RecipeTestRun
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            RecipeId = RecipeId,
            RecipeVersionId = TestedVersionId,
            TestedAt = Now.AddDays(-2),
            TestedByMembershipId = EditorMembershipId,
            Outcome = TestRunOutcome.SucceededWithIssues,
            Rating = 3,
            SummaryNotes = "Went well enough.",
            CreatedByMembershipId = EditorMembershipId,
            UpdatedByMembershipId = EditorMembershipId,
            CreatedAt = Now.AddDays(-2),
            UpdatedAt = Now.AddDays(-2),
            RowVersion = CurrentRowVersion,
        };

        var observation = new TestObservation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            RecipeTestRunId = run.Id,
            Kind = TestObservationKind.Texture,
            Text = "The crumb was close.",
            SortOrder = 0,
        };
        run.Observations.Add(observation);

        var issue = new TestIssue
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            RecipeId = RecipeId,
            RecipeTestRunId = run.Id,
            Severity = TestIssueSeverity.Major,
            Title = "Crumb too dense",
            TestObservationId = observation.Id,
            SortOrder = 0,
        };

        if (resolveTheIssue)
        {
            issue.Resolution = new TestIssueResolution
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                RecipeId = RecipeId,
                TestIssueId = issue.Id,
                Kind = TestIssueResolutionKind.Fixed,
                ResolvedByMembershipId = EditorMembershipId,
                ResolvedAt = Now.AddDays(-1),
            };
        }

        run.Issues.Add(issue);

        return run;
    }

    private static CanonicalUpdateTestRun Edit(string? token = null) =>
        new() { ExpectedConcurrencyToken = token ?? CurrentToken };

    private Task<OperationResult<RecipeTestRunServiceModel>> UpdateAsync(
        CanonicalUpdateTestRun request,
        MeasurementDimension? yieldUnitDimension = null) =>
        _business.UpdateAsync(
            RecipeId, _dataLayer.Loaded!.Id, request, yieldUnitDimension, TestContext.Current.CancellationToken);

    // ---- Read ----

    /// <summary>
    /// The read's two refusals are a different pair from the edit's, and the order is the disclosure rule: a
    /// caller who may not see the recipe learns only that, and never whether the test id is one of its tests.
    /// </summary>
    [Fact]
    public async Task An_invisible_recipe_is_refused_as_a_missing_recipe_before_the_test_is_looked_for()
    {
        _dataLayer.Loaded = StoredRun();
        var testRunId = _dataLayer.Loaded.Id;
        _dataLayer.Status = null;

        var result = await _business.GetAsync(RecipeId, testRunId, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, result.Error!.Code);
    }

    /// <summary>Visible recipe, no such test: safe to name, because listing them would show the same thing.</summary>
    [Fact]
    public async Task A_missing_test_of_a_visible_recipe_is_refused_as_a_missing_test()
    {
        _dataLayer.Loaded = null;

        var result = await _business.GetAsync(RecipeId, Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.TestRunNotFound, result.Error!.Code);
    }

    /// <summary>
    /// Archiving withdraws a recipe from content changes, not from its own history. The edit refuses on this
    /// status; the read must not.
    /// </summary>
    [Fact]
    public async Task An_archived_recipe_still_reads_its_own_tests()
    {
        _dataLayer.Loaded = StoredRun();
        _dataLayer.Status = RecipeStatus.Archived;

        var result = await _business.GetAsync(
            RecipeId, _dataLayer.Loaded.Id, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(_dataLayer.Loaded.Id, result.Value!.Id);
    }

    /// <summary>
    /// A read is not somebody standing by the write-up. The edit stamps both of these deliberately; this must
    /// leave them exactly as the last person to write it left them.
    /// </summary>
    [Fact]
    public async Task Reading_a_test_stamps_nothing_on_it()
    {
        var stored = StoredRun();
        _dataLayer.Loaded = stored;
        var updatedAt = stored.UpdatedAt;
        var updatedBy = stored.UpdatedByMembershipId;

        var result = await _business.GetAsync(RecipeId, stored.Id, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(updatedAt, stored.UpdatedAt);
        Assert.Equal(updatedBy, stored.UpdatedByMembershipId);
        Assert.NotEqual(ActorMembershipId, stored.UpdatedByMembershipId);
    }

    // ---- Update: refusals ----

    [Fact]
    public async Task An_invisible_recipe_hides_its_tests()
    {
        _dataLayer.Loaded = StoredRun();
        _dataLayer.Status = null;

        var result = await UpdateAsync(Edit());

        Assert.Equal(RecipeErrorCodes.TestRunNotFound, result.Error!.Code);
        Assert.Null(_dataLayer.Updated);
    }

    [Fact]
    public async Task An_unknown_test_is_not_found()
    {
        var run = StoredRun();
        _dataLayer.Loaded = run;

        // The recipe is visible; the run is not there.
        _dataLayer.Loaded = null;

        var result = await _business.UpdateAsync(
            RecipeId, run.Id, Edit(), null, TestContext.Current.CancellationToken);

        Assert.Equal(RecipeErrorCodes.TestRunNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task An_archived_recipe_refuses_an_edit_to_its_tests()
    {
        _dataLayer.Loaded = StoredRun();
        _dataLayer.Status = RecipeStatus.Archived;

        var result = await UpdateAsync(Edit());

        Assert.Equal(RecipeErrorCodes.RecipeArchivedConflict, result.Error!.Code);
        Assert.Null(_dataLayer.Updated);
    }

    [Fact]
    public async Task A_stale_token_is_a_conflict_and_nothing_is_merged()
    {
        _dataLayer.Loaded = StoredRun();

        var result = await UpdateAsync(
            Edit(RecipeConcurrencyToken.From([8, 7, 6, 5, 4, 3, 2, 1])) with
            {
                SummaryNotes = PatchField<string?>.Submitted("Rewritten."),
            });

        Assert.Equal(RecipeErrorCodes.TestRunConflict, result.Error!.Code);

        // Refused before the merge, so the loaded graph is untouched — which is what lets the caller re-read
        // and compose again against a state nobody half-changed.
        Assert.Equal("Went well enough.", _dataLayer.Loaded!.SummaryNotes);
        Assert.Null(_dataLayer.Updated);
    }

    [Fact]
    public async Task A_run_that_moves_on_during_the_save_is_a_conflict()
    {
        _dataLayer.Loaded = StoredRun();
        _dataLayer.UpdateConflicts = true;

        Assert.Equal(
            RecipeErrorCodes.TestRunConflict,
            (await UpdateAsync(Edit() with { Rating = PatchField<int?>.Submitted(5) })).Error!.Code);
    }

    // ---- Update: the merge ----

    [Fact]
    public async Task Fields_the_body_does_not_mention_are_left_alone()
    {
        _dataLayer.Loaded = StoredRun();

        var result = await UpdateAsync(Edit() with { Rating = PatchField<int?>.Submitted(5) });
        var run = _dataLayer.Updated!;

        Assert.True(result.Succeeded);
        Assert.Equal(5, run.Rating);

        // Everything else exactly as it was. This is the failure a hand-rolled patch makes: a client that omits
        // a field it does not know about has the server blank it.
        Assert.Equal("Went well enough.", run.SummaryNotes);
        Assert.Equal(TestRunOutcome.SucceededWithIssues, run.Outcome);
        Assert.Equal(Now.AddDays(-2), run.TestedAt);
    }

    [Fact]
    public async Task A_field_sent_as_null_is_cleared()
    {
        _dataLayer.Loaded = StoredRun();

        await UpdateAsync(Edit() with { Rating = PatchField<int?>.Submitted(null) });

        Assert.Null(_dataLayer.Updated!.Rating);
    }

    [Fact]
    public async Task The_version_that_was_cooked_cannot_be_changed()
    {
        _dataLayer.Loaded = StoredRun();

        await UpdateAsync(Edit() with { SummaryNotes = PatchField<string?>.Submitted("Second look.") });

        // There is no field for it on the request, so this asserts the absence holds through the merge rather
        // than that a rule refused something. Repointing a test would be a claim that a different thing
        // happened.
        Assert.Equal(TestedVersionId, _dataLayer.Updated!.RecipeVersionId);
    }

    [Fact]
    public async Task An_edit_stamps_the_actor_and_the_clock()
    {
        _dataLayer.Loaded = StoredRun();

        await UpdateAsync(Edit() with { Rating = PatchField<int?>.Submitted(2) });
        var run = _dataLayer.Updated!;

        Assert.Equal(ActorMembershipId, run.UpdatedByMembershipId);
        Assert.Equal(Now, run.UpdatedAt);

        // Authorship does not move: whoever recorded the test still recorded it, and whoever cooked it still
        // cooked it.
        Assert.Equal(EditorMembershipId, run.CreatedByMembershipId);
        Assert.Equal(EditorMembershipId, run.TestedByMembershipId);
        Assert.Equal(Now.AddDays(-2), run.CreatedAt);
    }

    [Fact]
    public async Task Clearing_a_yield_amount_while_a_unit_remains_is_refused()
    {
        var run = StoredRun();
        run.ActualYieldQuantity = 10m;
        run.ActualYieldUnitId = Guid.NewGuid();
        run.ActualYieldUnitDimension = MeasurementDimension.Count;
        _dataLayer.Loaded = run;

        // Coherent field by field and incoherent as a result — which is exactly why the rule reads the merged
        // run rather than the submitted body.
        var result = await UpdateAsync(
            Edit() with { ActualYieldQuantity = PatchField<decimal?>.Submitted(null) });

        Assert.Equal(RecipeErrorCodes.TestRunInvalidRequest, result.Error!.Code);
        Assert.Contains("actualYieldQuantity", result.Error.FieldErrors.Keys);
        Assert.Null(_dataLayer.Updated);
    }

    [Fact]
    public async Task Clearing_the_unit_and_the_amount_together_is_accepted()
    {
        var run = StoredRun();
        run.ActualYieldQuantity = 10m;
        run.ActualYieldUnitId = Guid.NewGuid();
        run.ActualYieldUnitDimension = MeasurementDimension.Count;
        _dataLayer.Loaded = run;

        var result = await UpdateAsync(Edit() with
        {
            ActualYieldQuantity = PatchField<decimal?>.Submitted(null),
            ActualYieldUnitId = PatchField<Guid?>.Submitted(null),
        });

        Assert.True(result.Succeeded);

        // The pair moves together, which is what CK_RecipeTestRuns_ActualYieldUnit_Dimension requires.
        Assert.Null(_dataLayer.Updated!.ActualYieldUnitId);
        Assert.Null(_dataLayer.Updated.ActualYieldUnitDimension);
    }

    // ---- Update: reconciliation ----

    [Fact]
    public async Task A_submitted_observation_list_updates_adds_and_removes()
    {
        var run = StoredRun();
        var existingId = run.Observations.Single().Id;
        _dataLayer.Loaded = run;

        var result = await UpdateAsync(Edit() with
        {
            // The existing note, reworded, plus a new one. Issues resubmitted unchanged so the resolved-issue
            // rule has nothing to complain about.
            Observations = PatchField<IReadOnlyList<CanonicalTestObservationEdit>>.Submitted(
            [
                new CanonicalTestObservationEdit(existingId, TestObservationKind.Texture, "The crumb was dense."),
                new CanonicalTestObservationEdit(null, TestObservationKind.Appearance, "Pale on top."),
            ]),
        });

        Assert.True(result.Succeeded);
        var observations = _dataLayer.Updated!.Observations.OrderBy(o => o.SortOrder).ToList();

        Assert.Equal(2, observations.Count);

        // Updated in place — the id survives, which is what keeps an issue's link to it intact.
        Assert.Equal(existingId, observations[0].Id);
        Assert.Equal("The crumb was dense.", observations[0].Text);
        Assert.NotEqual(existingId, observations[1].Id);
        Assert.Equal(WorkspaceId, observations[1].WorkspaceId);
    }

    [Fact]
    public async Task An_omitted_observation_is_removed()
    {
        var run = StoredRun();
        _dataLayer.Loaded = run;

        await UpdateAsync(Edit() with
        {
            Observations = PatchField<IReadOnlyList<CanonicalTestObservationEdit>>.Submitted([]),
            // The issue has to come too, or it would keep pointing at a note that no longer exists.
            Issues = PatchField<IReadOnlyList<CanonicalTestIssueEdit>>.Submitted(
            [
                new CanonicalTestIssueEdit(
                    run.Issues.Single().Id, TestIssueSeverity.Major, "Crumb too dense", null, null),
            ]),
        });

        Assert.Empty(_dataLayer.Updated!.Observations);
        Assert.Null(_dataLayer.Updated.Issues.Single().TestObservationId);
    }

    [Fact]
    public async Task An_unrecognised_observation_id_is_treated_as_a_new_note()
    {
        var run = StoredRun();
        _dataLayer.Loaded = run;

        // Not refused: deciding whether the id is unknown or names another workspace's row is the disclosure
        // tenancy.md forbids, and the outcome is identical either way.
        await UpdateAsync(Edit() with
        {
            Observations = PatchField<IReadOnlyList<CanonicalTestObservationEdit>>.Submitted(
                [new CanonicalTestObservationEdit(Guid.NewGuid(), TestObservationKind.Aroma, "Smelled right.")]),
            Issues = PatchField<IReadOnlyList<CanonicalTestIssueEdit>>.Submitted(
            [
                new CanonicalTestIssueEdit(
                    run.Issues.Single().Id, TestIssueSeverity.Major, "Crumb too dense", null, null),
            ]),
        });

        var observation = Assert.Single(_dataLayer.Updated!.Observations);
        Assert.Equal("Smelled right.", observation.Text);
    }

    [Fact]
    public async Task An_issue_can_be_repointed_at_a_note_the_same_edit_adds()
    {
        var run = StoredRun();
        var issueId = run.Issues.Single().Id;
        _dataLayer.Loaded = run;

        await UpdateAsync(Edit() with
        {
            Observations = PatchField<IReadOnlyList<CanonicalTestObservationEdit>>.Submitted(
                [new CanonicalTestObservationEdit(null, TestObservationKind.Timing, "Took 40 minutes.")]),
            Issues = PatchField<IReadOnlyList<CanonicalTestIssueEdit>>.Submitted(
                [new CanonicalTestIssueEdit(issueId, TestIssueSeverity.Minor, "Slow bake", null, 0)]),
        });

        var run2 = _dataLayer.Updated!;
        var note = Assert.Single(run2.Observations);
        var issue = Assert.Single(run2.Issues);

        // Resolved after the notes were reconciled, so the position names the note this edit just created.
        Assert.Equal(note.Id, issue.TestObservationId);
        Assert.Equal(TestIssueSeverity.Minor, issue.Severity);
    }

    [Fact]
    public async Task An_issue_index_without_the_observations_is_refused()
    {
        _dataLayer.Loaded = StoredRun();

        var result = await UpdateAsync(Edit() with
        {
            Issues = PatchField<IReadOnlyList<CanonicalTestIssueEdit>>.Submitted(
                [new CanonicalTestIssueEdit(null, TestIssueSeverity.Minor, "New issue", null, 0)]),
        });

        // A position into a list the caller did not send cannot be resolved, and guessing that it means the
        // stored order would file the issue against whatever happened to be there.
        Assert.Equal(RecipeErrorCodes.TestRunInvalidRequest, result.Error!.Code);
        Assert.Contains("issues", result.Error.FieldErrors.Keys);
    }

    [Fact]
    public async Task Leaving_the_observations_alone_leaves_an_issues_link_alone()
    {
        var run = StoredRun();
        var noteId = run.Observations.Single().Id;
        var issueId = run.Issues.Single().Id;
        _dataLayer.Loaded = run;

        // Issues submitted, observations not. The link must survive: the request was in no position to say
        // anything about it, so silently clearing it would lose a fact nobody asked to change.
        await UpdateAsync(Edit() with
        {
            Issues = PatchField<IReadOnlyList<CanonicalTestIssueEdit>>.Submitted(
                [new CanonicalTestIssueEdit(issueId, TestIssueSeverity.Blocking, "Crumb far too dense", null, null)]),
        });

        var issue = Assert.Single(_dataLayer.Updated!.Issues);
        Assert.Equal(noteId, issue.TestObservationId);
        Assert.Equal(TestIssueSeverity.Blocking, issue.Severity);
    }

    [Fact]
    public async Task A_resolved_issue_cannot_be_dropped_by_omitting_it()
    {
        var run = StoredRun(resolveTheIssue: true);
        _dataLayer.Loaded = run;

        var result = await UpdateAsync(Edit() with
        {
            Issues = PatchField<IReadOnlyList<CanonicalTestIssueEdit>>.Submitted([]),
        });

        Assert.Equal(RecipeErrorCodes.TestIssueResolvedRemovalConflict, result.Error!.Code);

        // Refused before anything is detached, so the run is left exactly as it was read — and the resolution,
        // which the database would have refused to delete anyway, is still attached to its issue.
        Assert.Single(_dataLayer.Loaded!.Issues);
        Assert.NotNull(_dataLayer.Loaded.Issues.Single().Resolution);
        Assert.Null(_dataLayer.Updated);
    }

    [Fact]
    public async Task A_resolved_issue_can_still_be_reworded()
    {
        var run = StoredRun(resolveTheIssue: true);
        var issueId = run.Issues.Single().Id;
        _dataLayer.Loaded = run;

        // Resolving does not freeze the issue: severity is the tester's judgement and stays theirs to revise.
        var result = await UpdateAsync(Edit() with
        {
            Issues = PatchField<IReadOnlyList<CanonicalTestIssueEdit>>.Submitted(
                [new CanonicalTestIssueEdit(issueId, TestIssueSeverity.Minor, "Crumb a little dense", null, null)]),
        });

        Assert.True(result.Succeeded);
        Assert.Equal(TestIssueSeverity.Minor, _dataLayer.Updated!.Issues.Single().Severity);
    }

    [Fact]
    public async Task An_unresolved_issue_can_be_dropped()
    {
        var run = StoredRun();
        _dataLayer.Loaded = run;

        var result = await UpdateAsync(Edit() with
        {
            Issues = PatchField<IReadOnlyList<CanonicalTestIssueEdit>>.Submitted([]),
        });

        Assert.True(result.Succeeded);
        Assert.Empty(_dataLayer.Updated!.Issues);
    }

    [Fact]
    public async Task The_response_carries_the_run_with_its_notes_and_issues()
    {
        _dataLayer.Loaded = StoredRun(resolveTheIssue: true);

        var result = await UpdateAsync(Edit() with { Rating = PatchField<int?>.Submitted(4) });
        var model = result.Value!;

        Assert.Equal(4, model.Rating);
        Assert.Equal(CurrentToken, model.ConcurrencyToken);
        Assert.Single(model.Observations);

        // A resolved issue publishes its resolution, and an unresolved one would publish null — that absence is
        // the state, with no separate flag to disagree with it.
        Assert.NotNull(Assert.Single(model.Issues).Resolution);
    }

    // ---- Resolution ----

    private Task<OperationResult<ResolvedTestIssueServiceModel>> ResolveAsync(
        CanonicalResolveTestIssue request,
        Guid? issueId = null) =>
        _business.ResolveIssueAsync(
            RecipeId,
            Guid.NewGuid(),
            issueId ?? Guid.NewGuid(),
            request,
            TestContext.Current.CancellationToken);

    private static CanonicalResolveTestIssue Resolution(
        TestIssueResolutionKind kind = TestIssueResolutionKind.Fixed,
        int? versionNumber = null,
        string? overrideReason = null) =>
        new()
        {
            Kind = kind,
            Notes = "Raised the hydration.",
            ResolutionVersionNumber = versionNumber,
            PredatingVersionOverrideReason = overrideReason,
        };

    [Fact]
    public async Task An_unknown_issue_is_not_found()
    {
        _dataLayer.IssueTarget = null;

        var result = await ResolveAsync(Resolution());

        Assert.Equal(RecipeErrorCodes.TestIssueNotFound, result.Error!.Code);
        Assert.Null(_dataLayer.Resolved);
    }

    [Fact]
    public async Task An_already_resolved_issue_is_a_conflict()
    {
        _dataLayer.IssueTarget = new TestIssueResolutionTarget(
            Guid.NewGuid(), RecipeId, TestedVersionId, TestedVersionNumber: 3, AlreadyResolved: true);

        var result = await ResolveAsync(Resolution());

        Assert.Equal(RecipeErrorCodes.TestIssueResolvedConflict, result.Error!.Code);
        Assert.Null(_dataLayer.Resolved);
    }

    [Fact]
    public async Task An_issue_resolved_between_the_read_and_the_write_is_the_same_conflict()
    {
        _dataLayer.IssueTarget = Target();
        _dataLayer.ResolutionRaceLost = true;

        // The unique index is the authority; the read above only makes the common case readable.
        Assert.Equal(
            RecipeErrorCodes.TestIssueResolvedConflict,
            (await ResolveAsync(Resolution(TestIssueResolutionKind.WontFix))).Error!.Code);
    }

    [Fact]
    public async Task A_resolution_records_its_actor_and_the_clock()
    {
        var target = Target();
        _dataLayer.IssueTarget = target;

        var result = await ResolveAsync(Resolution(TestIssueResolutionKind.NotReproduced));
        var resolution = _dataLayer.Resolved!;

        Assert.True(result.Succeeded);
        Assert.Equal(TestIssueResolutionKind.NotReproduced, resolution.Kind);
        Assert.Equal(ActorMembershipId, resolution.ResolvedByMembershipId);
        Assert.Equal(Now, resolution.ResolvedAt);

        // The recipe comes from the issue, not from the route: it is the value the composite foreign key uses
        // to pin a correction version to the right recipe.
        Assert.Equal(target.RecipeId, resolution.RecipeId);
        Assert.Equal(target.IssueId, resolution.TestIssueId);
        Assert.Null(resolution.ResolutionRecipeVersionId);
    }

    [Fact]
    public async Task A_correction_version_the_recipe_does_not_have_is_not_found()
    {
        _dataLayer.IssueTarget = Target();
        _dataLayer.VersionIdForNumber = null;

        var result = await ResolveAsync(Resolution(versionNumber: 9));

        Assert.Equal(RecipeErrorCodes.VersionNotFound, result.Error!.Code);
        Assert.Contains("resolutionVersionNumber", result.Error.FieldErrors.Keys);
        Assert.Null(_dataLayer.Resolved);
    }

    [Fact]
    public async Task A_later_version_is_accepted_as_the_correction()
    {
        var correctionId = Guid.NewGuid();
        _dataLayer.IssueTarget = Target();
        _dataLayer.VersionIdForNumber = correctionId;

        var result = await ResolveAsync(Resolution(versionNumber: 4));

        Assert.True(result.Succeeded);
        Assert.Equal(correctionId, _dataLayer.Resolved!.ResolutionRecipeVersionId);

        // No override happened, so nothing records one.
        Assert.Null(_dataLayer.Resolved.PredatingVersionOverrideReason);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(2)]
    public async Task A_version_at_or_before_the_tested_one_is_refused_without_a_reason(int versionNumber)
    {
        _dataLayer.IssueTarget = Target();
        _dataLayer.VersionIdForNumber = Guid.NewGuid();

        // Version 3 was tested. Neither 3 nor 2 can contain a fix for something that test found.
        var result = await ResolveAsync(Resolution(versionNumber: versionNumber));

        Assert.Equal(RecipeErrorCodes.TestIssueInvalidRequest, result.Error!.Code);
        Assert.Contains("resolutionVersionNumber", result.Error.FieldErrors.Keys);
        Assert.Null(_dataLayer.Resolved);
    }

    [Fact]
    public async Task A_version_at_or_before_the_tested_one_is_accepted_with_a_reason()
    {
        _dataLayer.IssueTarget = Target();
        _dataLayer.VersionIdForNumber = Guid.NewGuid();

        var result = await ResolveAsync(Resolution(
            versionNumber: 2, overrideReason: "Version 2 had it right; the regression came in 3."));

        Assert.True(result.Succeeded);

        // Recorded, because this is the case where somebody overrode the check and the next reader needs to know
        // it was a decision rather than a mis-click.
        Assert.Equal(
            "Version 2 had it right; the regression came in 3.",
            _dataLayer.Resolved!.PredatingVersionOverrideReason);
    }

    [Fact]
    public async Task An_override_reason_sent_for_a_later_version_is_not_recorded()
    {
        _dataLayer.IssueTarget = Target();
        _dataLayer.VersionIdForNumber = Guid.NewGuid();

        await ResolveAsync(Resolution(versionNumber: 5, overrideReason: "Not needed."));

        // Nothing was overridden, so a stored reason would imply an override that never happened.
        Assert.Null(_dataLayer.Resolved!.PredatingVersionOverrideReason);
    }

    [Fact]
    public async Task An_archived_recipe_refuses_a_resolution()
    {
        _dataLayer.IssueTarget = Target();
        _dataLayer.Status = RecipeStatus.Archived;

        Assert.Equal(
            RecipeErrorCodes.RecipeArchivedConflict,
            (await ResolveAsync(Resolution())).Error!.Code);
    }

    private static TestIssueResolutionTarget Target() =>
        new(Guid.NewGuid(), RecipeId, TestedVersionId, TestedVersionNumber: 3, AlreadyResolved: false);

    private sealed class EditingWorkspaceContext(Guid membershipId, Guid workspaceId) : IWorkspaceContext
    {
        public bool IsResolved => true;

        public Guid WorkspaceId { get; } = workspaceId;

        public string WorkspaceSlug => "workspace-a";

        public Guid MembershipId { get; } = membershipId;

        public WorkspaceRole Role => WorkspaceRole.Owner;

        public string AccountId => "account-a";
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    /// <summary>
    /// A DataLayer that records what Business handed it and answers whatever the test set up.
    /// </summary>
    /// <remarks>
    /// Separate from <c>RecipeTestRunBusinessTests</c>' fake rather than shared: that one exists to prove what a
    /// create builds, and giving it the levers an edit needs would make both files harder to read than the
    /// duplication costs.
    /// </remarks>
    private sealed class FakeTestRunDataLayer : IRecipeTestRunDataLayer
    {
        /// <summary>Named <c>Status</c> rather than <c>RecipeStatus</c>, which would shadow the enum itself.</summary>
        public RecipeStatus? Status { get; set; } = RecipeStatus.Draft;

        public RecipeTestRun? Loaded { get; set; }

        public RecipeTestRun? Updated { get; private set; }

        public TestIssueResolutionTarget? IssueTarget { get; set; }

        public TestIssueResolution? Resolved { get; private set; }

        public Guid? VersionIdForNumber { get; set; }

        public bool UpdateConflicts { get; set; }

        public bool ResolutionRaceLost { get; set; }

        public Task<TestRunTarget?> FindTargetAsync(
            Guid recipeId, int versionNumber, CancellationToken cancellationToken) =>
            Task.FromResult<TestRunTarget?>(null);

        public Task<RecipeStatus?> FindRecipeStatusAsync(Guid recipeId, CancellationToken cancellationToken) =>
            Task.FromResult(Status);

        public Task<CreatedRecipeTestRun> CreateAsync(RecipeTestRun run, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake serves the edit and resolution paths only.");

        public Task<RecipeTestRun?> GetForUpdateAsync(
            Guid recipeId, Guid testRunId, CancellationToken cancellationToken) =>
            Task.FromResult(Loaded);

        // The read path loads the same graph as the edit path, untracked; a fake has no tracker, so the two
        // answer from one field.
        public Task<RecipeTestRun?> GetForReadAsync(
            Guid recipeId, Guid testRunId, CancellationToken cancellationToken) =>
            Task.FromResult(Loaded);

        public Task<TestRunUpdateOutcome> UpdateAsync(RecipeTestRun run, CancellationToken cancellationToken)
        {
            if (UpdateConflicts)
            {
                return Task.FromResult(TestRunUpdateOutcome.Conflict());
            }

            Updated = run;

            return Task.FromResult(TestRunUpdateOutcome.Applied());
        }

        public Task<TestIssueResolutionTarget?> FindIssueForResolutionAsync(
            Guid recipeId, Guid testRunId, Guid issueId, CancellationToken cancellationToken) =>
            Task.FromResult(IssueTarget);

        public Task<Guid?> FindVersionIdAsync(
            Guid recipeId, int versionNumber, CancellationToken cancellationToken) =>
            Task.FromResult(VersionIdForNumber);

        /// <summary>
        /// Never called from this class, which is about the update and resolution seams. The read path has its own
        /// coverage in <see cref="RecipeTestRunBusinessTests"/>; returning null here would be a 404 if anything
        /// reached it, which is the honest answer for a fake that holds no history.
        /// </summary>
        public Task<TestRunHistoryPage?> ListAsync(
            TestRunHistoryCriteria criteria, CancellationToken cancellationToken) =>
            Task.FromResult<TestRunHistoryPage?>(null);

        public Task<TestIssueResolutionOutcome> ResolveAsync(
            TestIssueResolution resolution, CancellationToken cancellationToken)
        {
            if (ResolutionRaceLost)
            {
                return Task.FromResult(TestIssueResolutionOutcome.AlreadyResolved());
            }

            Resolved = resolution;

            return Task.FromResult(TestIssueResolutionOutcome.Applied(resolution));
        }
    }
}
