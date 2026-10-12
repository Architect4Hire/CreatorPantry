using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// LF.3.3 through the same real seam as the outline: the request business, the operation store, the worker and
/// the landing step over SQLite, with the model replaced by a scripted handler.
/// </summary>
/// <remarks>
/// What a section says is the scripted handler's; what happens to it — which section it lands in, what it
/// leaves alone, what a retry gets, what an empty allowance refuses — is the code under test. The real
/// handler's own refusals are <c>LongFormSectionAiTaskHandlerTests</c>'.
/// </remarks>
public sealed partial class LongFormOutlineRequestSeamTests
{
    private static readonly (string Role, string Heading, string Intent, int? Source)[] ThreeSections =
    [
        ("Hook", "The loaf for people who do not bake", "Open on how little this bread asks of the cook.", null),
        ("Tips", "Three things that help", "What goes wrong and how to avoid it.", null),
        ("Closing", "Bake it this weekend", "Send the reader off to try it.", null),
    ];

    // ---- One section ----

    [Fact]
    public async Task Asking_for_one_section_queues_one_request_closes_the_plan_and_writes_that_section_only()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var tips = draft.Latest!.Sections[1].Id;
        WriteSectionsAs(context => $"Body for {SectionOf(context):N}.");

        var asked = await AskAsync(WorkspaceA, draft.Id, tips, 1, "write-1");

        Assert.True(asked.Result.Succeeded, asked.Result.Error?.Code);
        Assert.False(asked.Replayed);
        Assert.Equal(tips, asked.Result.Value!.Request.SectionId);
        Assert.Equal(AiOperationStatus.Requested, asked.Result.Value.Request.Request.Status);

        // Asking is what closes the plan: nothing has been written yet, and the headings can no longer move.
        Assert.Equal(LongFormDraftStatus.Writing, asked.Result.Value.Draft.Status);
        Assert.All(asked.Result.Value.Draft.Sections, section => Assert.False(section.IsWritten));

        Assert.Equal(1, (await RunPendingAsync()).Proposed);

        var status = await SectionsAsync(WorkspaceA, draft.Id);
        Assert.Equal(2, status.Draft.RevisionNumber);
        Assert.Equal([false, true, false], status.Draft.Sections.Select(section => section.IsWritten));
        Assert.Equal($"Body for {tips:N}.", status.Draft.Sections[1].Body);
        Assert.Equal(ContentRevisionSource.AiGenerated, status.Draft.Sections[1].WrittenBy);
        Assert.Equal(ThreeSections.Select(section => section.Heading), status.Draft.Sections.Select(section => section.Heading));

        var request = Assert.Single(status.Requests);
        Assert.Equal(tips, request.SectionId);
        Assert.Equal(LongFormSectionRequestOutcome.Written, request.Outcome);

        // The plan is closed.
        Assert.Equal(
            ContentErrorCodes.LongFormPlanLocked,
            (await TryDraftsAsync(WorkspaceA, facade => facade.PatchPlanAsync(
                AccountA,
                draft.Id,
                new PatchLongFormPlanViewModel
                {
                    ExpectedConcurrencyToken = status.Draft.ConcurrencyToken,
                    Sections = [new LongFormPlanSectionInputViewModel { Role = LongFormSectionRole.Hook, Heading = "x", Intent = "y" }],
                },
                Ct))).Error!.Code);
    }

    [Fact]
    public async Task Writing_a_second_section_leaves_the_first_exactly_as_it_was()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var hook = draft.Latest!.Sections[0].Id;
        var tips = draft.Latest.Sections[1].Id;
        WriteSectionsAs(context => $"Body for {SectionOf(context):N}.\n\nSecond paragraph, with trailing space. ");

        await AskAsync(WorkspaceA, draft.Id, hook, 1, "write-1");
        await RunPendingAsync();
        var first = (await SectionsAsync(WorkspaceA, draft.Id)).Draft.Sections[0];

        // Named against revision 1, which is no longer the latest. The Tips section has not changed since,
        // so it is written: another section landing is not a reason to refuse this one.
        var asked = await AskAsync(WorkspaceA, draft.Id, tips, 1, "write-2");
        Assert.True(asked.Result.Succeeded, asked.Result.Error?.Code);
        await RunPendingAsync();

        var after = (await SectionsAsync(WorkspaceA, draft.Id)).Draft;
        Assert.Equal(3, after.RevisionNumber);
        Assert.Equal(first, after.Sections[0]);
        Assert.True(after.Sections[1].IsWritten);
        Assert.False(after.Sections[2].IsWritten);
    }

    [Fact]
    public async Task A_retry_with_the_same_key_gets_the_same_request_even_after_it_has_written_the_section()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var hook = draft.Latest!.Sections[0].Id;
        WriteSectionsAs(_ => "The opening.");

        var first = await AskAsync(WorkspaceA, draft.Id, hook, 1, "write-1");
        var again = await AskAsync(WorkspaceA, draft.Id, hook, 1, "write-1");

        Assert.True(again.Replayed);
        Assert.Equal(first.Result.Value!.Request.Request.AiProposalRequestId, again.Result.Value!.Request.Request.AiProposalRequestId);

        await RunPendingAsync();

        // By now the section is written, so a new request against revision 1 would be stale. This is not new.
        var late = await AskAsync(WorkspaceA, draft.Id, hook, 1, "write-1");

        Assert.True(late.Result.Succeeded, late.Result.Error?.Code);
        Assert.True(late.Replayed);
        Assert.Equal(first.Result.Value.Request.Request.AiProposalRequestId, late.Result.Value!.Request.Request.AiProposalRequestId);
        Assert.Equal(1, await CountAsync(WorkspaceA, db => db.AiOperations.CountAsync(operation => operation.TaskType == AiTaskType.LongFormSection, Ct)));
        Assert.Equal(2, await CountAsync(WorkspaceA, db => db.LongFormRevisions.CountAsync(Ct)));
    }

    [Theory]
    [InlineData("section")]
    [InlineData("revision")]
    [InlineData("instruction")]
    public async Task The_same_key_for_a_different_request_is_refused_and_queues_nothing(string changed)
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var hook = draft.Latest!.Sections[0].Id;
        var tips = draft.Latest.Sections[1].Id;
        await AskAsync(WorkspaceA, draft.Id, hook, 1, "write-1");

        var other = changed switch
        {
            "section" => await AskAsync(WorkspaceA, draft.Id, tips, 1, "write-1"),
            "revision" => await AskAsync(WorkspaceA, draft.Id, hook, 2, "write-1"),
            _ => await AskAsync(WorkspaceA, draft.Id, hook, 1, "write-1", "Make it warmer."),
        };

        Assert.Equal(IdempotencyPolicy.KeyReusedCode, other.Result.Error!.Code);
        Assert.Equal(1, await CountAsync(WorkspaceA, db => db.AiOperations.CountAsync(operation => operation.TaskType == AiTaskType.LongFormSection, Ct)));
    }

    [Fact]
    public async Task A_request_with_no_key_or_no_revision_is_refused_and_a_switched_off_task_queues_nothing()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var hook = draft.Latest!.Sections[0].Id;

        Assert.Equal(IdempotencyPolicy.KeyRequiredCode, (await AskAsync(WorkspaceA, draft.Id, hook, 1, key: null)).Result.Error!.Code);
        Assert.Equal(AiLongFormSectionRequestErrors.RequestInvalid, (await AskAsync(WorkspaceA, draft.Id, hook, 0, "write-1")).Result.Error!.Code);
        Assert.Equal(
            AiLongFormSectionRequestErrors.RequestInvalid,
            (await AskAsync(WorkspaceA, draft.Id, hook, 1, "write-1", new string('x', AiPolicy.LongFormSectionInstructionMaxLength + 1))).Result.Error!.Code);

        _tasks.Enabled.Remove(AiTaskCatalog.LongFormSection);

        Assert.Equal(AiLongFormSectionRequestErrors.TaskNotEnabled, (await AskAsync(WorkspaceA, draft.Id, hook, 1, "write-1")).Result.Error!.Code);
        Assert.Equal(AiLongFormSectionRequestErrors.TaskNotEnabled, (await AskRestAsync(WorkspaceA, draft.Id, 1, "rest-1")).Result.Error!.Code);
        Assert.Equal(0, await CountAsync(WorkspaceA, db => db.AiOperations.CountAsync(operation => operation.TaskType == AiTaskType.LongFormSection, Ct)));

        // And a refused request did not close the plan.
        Assert.Equal(LongFormDraftStatus.Outlined, (await SectionsAsync(WorkspaceA, draft.Id)).Draft.Status);
    }

    // ---- Writing it again ----

    [Fact]
    public async Task Writing_one_section_again_replaces_its_text_and_no_other()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        WriteSectionsAs(context => $"First version of {SectionOf(context):N}.");
        await AskRestAsync(WorkspaceA, draft.Id, 1, "rest-1");
        await RunPendingAsync();

        var written = (await SectionsAsync(WorkspaceA, draft.Id)).Draft;
        var tips = written.Sections[1].Id;
        string? instructionSeen = null;

        _sectionHandler.Behavior = (context, _) =>
        {
            instructionSeen = LongFormSectionInputs.ReadInstruction(context.Inputs);

            return Task.FromResult(BodyProposal(context, written.CreativeContextId, "A warmer version."));
        };

        var again = await AskAsync(WorkspaceA, draft.Id, tips, written.RevisionNumber!.Value, "write-again", "Make it warmer.");
        Assert.True(again.Result.Succeeded, again.Result.Error?.Code);
        await RunPendingAsync();

        var after = (await SectionsAsync(WorkspaceA, draft.Id)).Draft;
        Assert.Equal("Make it warmer.", instructionSeen);
        Assert.Equal("A warmer version.", after.Sections[1].Body);
        Assert.Equal(written.Sections[0], after.Sections[0]);
        Assert.Equal(written.Sections[2], after.Sections[2]);
        Assert.Equal(written.RevisionNumber + 1, after.RevisionNumber);
    }

    [Fact]
    public async Task A_request_composed_against_a_section_that_has_since_changed_is_refused_before_it_is_queued()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var hook = draft.Latest!.Sections[0].Id;
        var state = (await SectionsAsync(WorkspaceA, draft.Id)).Draft;

        await EditAsync(WorkspaceA, draft.Id, hook, state.ConcurrencyToken, "My own opening.");

        // Composed against revision 1, when the section was empty. It is not empty now.
        var stale = await AskAsync(WorkspaceA, draft.Id, hook, 1, "write-1");

        Assert.Equal(ContentErrorCodes.LongFormRevisionStale, stale.Result.Error!.Code);
        Assert.Equal(0, await CountAsync(WorkspaceA, db => db.AiOperations.CountAsync(operation => operation.TaskType == AiTaskType.LongFormSection, Ct)));

        // Against the revision that holds the edit, it is a rewrite of the creator's text and is accepted.
        Assert.True((await AskAsync(WorkspaceA, draft.Id, hook, 2, "write-2")).Result.Succeeded);
    }

    [Fact]
    public async Task The_creators_own_edit_made_while_a_section_is_being_written_wins_and_the_request_is_reported_as_superseded()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var hook = draft.Latest!.Sections[0].Id;
        WriteSectionsAs(_ => "What the writer wrote.");

        var asked = await AskAsync(WorkspaceA, draft.Id, hook, 1, "write-1");
        await EditAsync(WorkspaceA, draft.Id, hook, asked.Result.Value!.Draft.ConcurrencyToken, "What I wrote myself.");

        Assert.Equal(1, (await RunPendingAsync()).Proposed);

        var status = await SectionsAsync(WorkspaceA, draft.Id);
        Assert.Equal("What I wrote myself.", status.Draft.Sections[0].Body);
        Assert.Equal(ContentRevisionSource.CreatorEdit, status.Draft.Sections[0].WrittenBy);
        Assert.Equal(LongFormSectionRequestOutcome.Superseded, Assert.Single(status.Requests).Outcome);

        // The proposal is kept, the draft has the creator's revision and no other, and the sweep does not
        // change its mind.
        Assert.Equal(2, await CountAsync(WorkspaceA, db => db.AiProposals.CountAsync(Ct)));
        await RunMaintenanceAsync();
        Assert.Equal(2, await CountAsync(WorkspaceA, db => db.LongFormRevisions.CountAsync(Ct)));
    }

    [Fact]
    public async Task Landing_the_same_section_twice_writes_one_revision()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        WriteSectionsAs(_ => "The opening.");
        await AskAsync(WorkspaceA, draft.Id, draft.Latest!.Sections[0].Id, 1, "write-1");
        await RunPendingAsync();

        await RunMaintenanceAsync();
        await RunMaintenanceAsync();

        Assert.Equal(2, await CountAsync(WorkspaceA, db => db.LongFormRevisions.CountAsync(Ct)));
        Assert.Equal("The opening.", (await SectionsAsync(WorkspaceA, draft.Id)).Draft.Sections[0].Body);
    }

    // ---- Write the rest ----

    [Fact]
    public async Task Write_the_rest_queues_one_request_for_each_unwritten_section_in_plan_order()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var state = (await SectionsAsync(WorkspaceA, draft.Id)).Draft;
        await EditAsync(WorkspaceA, draft.Id, state.Sections[1].Id, state.ConcurrencyToken, "My own tips.");

        var order = new List<Guid>();
        WriteSectionsAs(context =>
        {
            order.Add(SectionOf(context));

            return $"Body for {SectionOf(context):N}.";
        });

        var batch = await AskRestAsync(WorkspaceA, draft.Id, 2, "rest-1");

        Assert.True(batch.Result.Succeeded, batch.Result.Error?.Code);
        Assert.False(batch.Replayed);

        // The section the creator wrote is not asked for. The other two are, in plan order.
        Assert.Equal([state.Sections[0].Id, state.Sections[2].Id], batch.Result.Value!.Requests.Select(request => request.SectionId));
        Assert.Equal(2, batch.Result.Value.Requests.Select(request => request.Request.AiProposalRequestId).Distinct().Count());

        Assert.Equal(2, (await RunPendingAsync()).Proposed);
        Assert.Equal([state.Sections[0].Id, state.Sections[2].Id], order);

        var after = await SectionsAsync(WorkspaceA, draft.Id);
        Assert.All(after.Draft.Sections, section => Assert.True(section.IsWritten));
        Assert.Equal("My own tips.", after.Draft.Sections[1].Body);
        Assert.Equal(2, after.Requests.Count);
        Assert.All(after.Requests, request => Assert.Equal(LongFormSectionRequestOutcome.Written, request.Outcome));

        // Still being written as far as the draft is concerned: assembling it is a later step.
        Assert.Equal(LongFormDraftStatus.Writing, after.Draft.Status);
    }

    [Fact]
    public async Task Replaying_write_the_rest_starts_nothing_new_before_or_after_the_sections_are_written()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        WriteSectionsAs(_ => "A body.");

        var first = await AskRestAsync(WorkspaceA, draft.Id, 1, "rest-1");
        var again = await AskRestAsync(WorkspaceA, draft.Id, 1, "rest-1");

        Assert.True(again.Replayed);
        Assert.Equal(
            first.Result.Value!.Requests.Select(request => request.Request.AiProposalRequestId),
            again.Result.Value!.Requests.Select(request => request.Request.AiProposalRequestId));

        // A different key while those are still queued: every section is already being written.
        var other = await AskRestAsync(WorkspaceA, draft.Id, 1, "rest-2");
        Assert.True(other.Result.Succeeded, other.Result.Error?.Code);
        Assert.Empty(other.Result.Value!.Requests);

        await RunPendingAsync();

        // By now every section is written. The retry still gets the first answer: the same three requests.
        var late = await AskRestAsync(WorkspaceA, draft.Id, 1, "rest-1");
        Assert.True(late.Result.Succeeded, late.Result.Error?.Code);
        Assert.True(late.Replayed);
        Assert.Equal(
            first.Result.Value.Requests.Select(request => request.Request.AiProposalRequestId),
            late.Result.Value!.Requests.Select(request => request.Request.AiProposalRequestId));

        // And it gets it even once the draft has stopped being written, when a new request would be refused.
        await SetStatusAsync(WorkspaceA, LongFormDraftStatus.Accepted);
        var afterwards = await AskRestAsync(WorkspaceA, draft.Id, 1, "rest-1");
        Assert.True(afterwards.Result.Succeeded, afterwards.Result.Error?.Code);
        Assert.True(afterwards.Replayed);
        Assert.Equal(ContentErrorCodes.LongFormDraftAccepted, (await AskRestAsync(WorkspaceA, draft.Id, 1, "rest-9")).Result.Error!.Code);

        Assert.Equal(3, await CountAsync(WorkspaceA, db => db.AiOperations.CountAsync(operation => operation.TaskType == AiTaskType.LongFormSection, Ct)));
    }

    [Fact]
    public async Task The_same_batch_key_for_a_different_revision_is_refused_and_queues_nothing_more()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        await AskRestAsync(WorkspaceA, draft.Id, 1, "rest-1");

        var other = await AskRestAsync(WorkspaceA, draft.Id, 2, "rest-1");

        Assert.Equal(IdempotencyPolicy.KeyReusedCode, other.Result.Error!.Code);
        Assert.Equal(3, await CountAsync(WorkspaceA, db => db.AiOperations.CountAsync(operation => operation.TaskType == AiTaskType.LongFormSection, Ct)));
    }

    [Fact]
    public async Task Beginning_to_write_is_recorded_once_and_says_nothing_of_any_section()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        await AskAsync(WorkspaceA, draft.Id, draft.Latest!.Sections[0].Id, 1, "write-1");
        await AskAsync(WorkspaceA, draft.Id, draft.Latest.Sections[1].Id, 1, "write-2");

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var entry = Assert.Single(await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AuditLogs
            .AsNoTracking()
            .Where(candidate => candidate.Action == ContentAuditActions.LongFormWritingBegan)
            .ToListAsync(Ct));

        Assert.Equal(draft.Id.ToString("D"), entry.ResourceId);
        Assert.DoesNotContain("loaf", entry.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Write_the_rest_is_refused_whole_when_the_allowance_covers_only_some_and_says_how_many()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");

        // One and a half runs' worth: enough for one section, not for three.
        await LeaveAllowanceForAsync(runs: 1.5m);
        var peek = await PeekAsync();
        var coverable = (int)Math.Floor(peek.Remaining / peek.Required);
        Assert.Equal(1, coverable);

        _sectionHandler.Behavior = (_, _) => throw new InvalidOperationException("The provider was reached.");

        var batch = await AskRestAsync(WorkspaceA, draft.Id, 1, "rest-1");

        var error = batch.Result.Error!;
        Assert.Equal(AiProposalErrors.QuotaExhausted, error.Code);
        Assert.Equal(3, error.Extensions![AiLongFormSectionRequestErrors.SectionsRequestedExtension]);
        Assert.Equal(coverable, error.Extensions[AiLongFormSectionRequestErrors.SectionsCoverableExtension]);
        Assert.Equal(peek.Required * 3, error.Extensions["required"]);

        // Nothing half-spent: no request queued, nothing claimed, and the plan is still open.
        Assert.Equal(0, await CountAsync(WorkspaceA, db => db.AiOperations.CountAsync(operation => operation.TaskType == AiTaskType.LongFormSection, Ct)));
        Assert.Equal(0, (await RunPendingAsync()).Claimed);
        Assert.Equal(LongFormDraftStatus.Outlined, (await SectionsAsync(WorkspaceA, draft.Id)).Draft.Status);

        // One section at a time is still within it.
        Assert.True((await AskAsync(WorkspaceA, draft.Id, draft.Latest!.Sections[0].Id, 1, "write-1")).Result.Succeeded);
    }

    [Theory]
    [InlineData(false, AiProposalErrors.QuotaExhausted)]
    [InlineData(true, AiProposalErrors.QuotaSuspended)]
    public async Task A_spent_or_suspended_allowance_refuses_a_section_and_the_rest_and_queues_nothing(
        bool suspended, string expected)
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");

        if (suspended)
        {
            await GiveQuotaAsync(allowance: 1000m, suspended: true);
        }
        else
        {
            await LeaveAllowanceForAsync(runs: 0m);
        }

        Assert.Equal(expected, (await AskAsync(WorkspaceA, draft.Id, draft.Latest!.Sections[0].Id, 1, "write-1")).Result.Error!.Code);
        Assert.Equal(expected, (await AskRestAsync(WorkspaceA, draft.Id, 1, "rest-1")).Result.Error!.Code);
        Assert.Equal(0, await CountAsync(WorkspaceA, db => db.AiOperations.CountAsync(operation => operation.TaskType == AiTaskType.LongFormSection, Ct)));
    }

    [Fact]
    public async Task A_run_cancelled_part_way_through_marks_that_section_cancelled_and_leaves_the_written_ones_alone()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var calls = 0;

        _sectionHandler.Behavior = (context, _) => Task.FromResult(++calls == 2
            ? AiTaskHandlerOutcome.ForFailure(AiFailureCategory.Cancelled, "The run was cancelled.", [])
            : BodyProposal(context, draft.CreativeContextId, $"Body {calls}."));

        await AskRestAsync(WorkspaceA, draft.Id, 1, "rest-1");
        var summary = await RunPendingAsync();

        Assert.Equal(2, summary.Proposed);
        Assert.Equal(1, summary.Failed);

        var status = await SectionsAsync(WorkspaceA, draft.Id);
        Assert.Equal(["Body 1.", null, "Body 3."], status.Draft.Sections.Select(section => section.Body));
        Assert.Equal(
            [LongFormSectionRequestOutcome.Written, LongFormSectionRequestOutcome.Failed, LongFormSectionRequestOutcome.Written],
            status.Requests.Select(request => request.Outcome));
        Assert.Equal(AiFailureCategory.Cancelled, status.Requests[1].FailureCategory);

        // The cancelled section can be asked for again, and only it is.
        WriteSectionsAs(_ => "Body 2, at last.");
        var again = await AskRestAsync(WorkspaceA, draft.Id, status.Draft.RevisionNumber!.Value, "rest-2");
        Assert.Equal(status.Draft.Sections[1].Id, Assert.Single(again.Result.Value!.Requests).SectionId);
        await RunPendingAsync();

        var after = (await SectionsAsync(WorkspaceA, draft.Id)).Draft;
        Assert.Equal(["Body 1.", "Body 2, at last.", "Body 3."], after.Sections.Select(section => section.Body));
    }

    // ---- The draft's status ----

    [Theory]
    [InlineData(LongFormDraftStatus.Draft, ContentErrorCodes.LongFormPlanNotApproved)]
    [InlineData(LongFormDraftStatus.Assembled, ContentErrorCodes.LongFormDraftNotWritable)]
    [InlineData(LongFormDraftStatus.Accepted, ContentErrorCodes.LongFormDraftAccepted)]
    public async Task A_draft_that_is_neither_approved_nor_being_written_takes_no_section_request_edit_or_settlement(
        LongFormDraftStatus status, string expected)
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var hook = draft.Latest!.Sections[0].Id;
        await SetStatusAsync(WorkspaceA, status);
        var token = (await SectionsAsync(WorkspaceA, draft.Id)).Draft.ConcurrencyToken;

        Assert.Equal(expected, (await AskAsync(WorkspaceA, draft.Id, hook, 1, "write-1")).Result.Error!.Code);
        Assert.Equal(expected, (await AskRestAsync(WorkspaceA, draft.Id, 1, "rest-1")).Result.Error!.Code);
        Assert.Equal(expected, (await TryEditAsync(WorkspaceA, draft.Id, hook, token, "Mine.")).Error!.Code);
        Assert.Equal(expected, (await TrySettleAsync(WorkspaceA, draft.Id, hook, token, true)).Error!.Code);

        Assert.Equal(0, await CountAsync(WorkspaceA, db => db.AiOperations.CountAsync(operation => operation.TaskType == AiTaskType.LongFormSection, Ct)));
        Assert.Equal(1, await CountAsync(WorkspaceA, db => db.LongFormRevisions.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_body_that_finishes_after_the_draft_has_stopped_being_written_is_not_put_in_it()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        WriteSectionsAs(_ => "Too late.");
        await AskAsync(WorkspaceA, draft.Id, draft.Latest!.Sections[0].Id, 1, "write-1");
        await SetStatusAsync(WorkspaceA, LongFormDraftStatus.Accepted);

        await RunPendingAsync();

        Assert.Equal(1, await CountAsync(WorkspaceA, db => db.LongFormRevisions.CountAsync(Ct)));
    }

    // ---- The creator's own edit ----

    [Fact]
    public async Task A_creators_edit_is_a_new_revision_that_changes_one_section_and_closes_the_plan()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var state = (await SectionsAsync(WorkspaceA, draft.Id)).Draft;
        var tips = state.Sections[1].Id;

        var edited = await EditAsync(WorkspaceA, draft.Id, tips, state.ConcurrencyToken, "- Stir gently.\n- Keep the oven hot.");

        Assert.Equal(2, edited.RevisionNumber);
        Assert.Equal(LongFormDraftStatus.Writing, edited.Status);
        Assert.Equal("- Stir gently.\n- Keep the oven hot.", edited.Sections[1].Body);
        Assert.Equal(ContentRevisionSource.CreatorEdit, edited.Sections[1].WrittenBy);
        Assert.Null(edited.Sections[1].AiProposalId);
        Assert.Equal([false, true, false], edited.Sections.Select(section => section.IsWritten));

        // Saving it as it already reads is not an edit.
        var same = await EditAsync(WorkspaceA, draft.Id, tips, edited.ConcurrencyToken, "- Stir gently.\n- Keep the oven hot.");
        Assert.Equal(2, same.RevisionNumber);
        Assert.Equal(edited.ConcurrencyToken, same.ConcurrencyToken);

        // A token that is not the draft's is refused, and the text is as it was. That the token moves on every
        // write is LongFormDraftTests' to show on SQL Server: this fixture's SQLite store never moves one.
        Assert.Equal(
            ContentErrorCodes.LongFormDraftStale,
            (await TryEditAsync(WorkspaceA, draft.Id, tips, "AAAAAAAAAAE=", "Someone else's.")).Error!.Code);
        Assert.Equal(
            ContentErrorCodes.LongFormSectionNotFound,
            (await TryEditAsync(WorkspaceA, draft.Id, Guid.NewGuid(), edited.ConcurrencyToken, "Nowhere.")).Error!.Code);

        // One audit entry for the one edit, and it carries no word of the section.
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var audit = await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AuditLogs
            .AsNoTracking()
            .Where(entry => entry.Action == ContentAuditActions.LongFormSectionEdited)
            .ToListAsync(Ct);
        Assert.DoesNotContain("Stir", Assert.Single(audit).Summary);
    }

    [Fact]
    public async Task A_blank_edit_makes_the_section_unwritten_again_and_write_the_rest_picks_it_up()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var state = (await SectionsAsync(WorkspaceA, draft.Id)).Draft;
        var hook = state.Sections[0].Id;

        var written = await EditAsync(WorkspaceA, draft.Id, hook, state.ConcurrencyToken, "An opening.");
        var blanked = await EditAsync(WorkspaceA, draft.Id, hook, written.ConcurrencyToken, "   \n ");

        Assert.False(blanked.Sections[0].IsWritten);
        Assert.Null(blanked.Sections[0].Body);
        Assert.Null(blanked.Sections[0].WrittenBy);

        var batch = await AskRestAsync(WorkspaceA, draft.Id, blanked.RevisionNumber!.Value, "rest-1");
        Assert.Contains(hook, batch.Result.Value!.Requests.Select(request => request.SectionId));
    }

    [Fact]
    public async Task Text_over_the_ceiling_is_saved_whole_and_reported_and_text_past_the_row_is_refused()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var state = (await SectionsAsync(WorkspaceA, draft.Id)).Draft;
        var hook = state.Sections[0].Id;
        var longBody = new string('a', state.SectionMaxCharacters + 1);

        var saved = await EditAsync(WorkspaceA, draft.Id, hook, state.ConcurrencyToken, longBody);

        Assert.Equal(longBody, saved.Sections[0].Body);
        Assert.True(saved.Sections[0].OverCeiling);
        Assert.Equal(state.SectionMaxCharacters + 1, saved.Sections[0].CharacterCount);

        var refused = await TryEditAsync(
            WorkspaceA, draft.Id, hook, saved.ConcurrencyToken, new string('a', LongFormDraftPolicy.SectionBodyMaxLength + 1));

        Assert.Equal(ContentErrorCodes.LongFormSectionInvalid, refused.Error!.Code);
        Assert.Contains("body", refused.Error.FieldErrors.Keys);
    }

    // ---- Settled ----

    [Fact]
    public async Task Settling_is_reversible_changes_no_text_and_protects_the_section_from_being_written_again()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var state = (await SectionsAsync(WorkspaceA, draft.Id)).Draft;
        var hook = state.Sections[0].Id;

        // Nothing to be settled about a section with no text.
        Assert.Equal(
            ContentErrorCodes.LongFormSectionNotWritten,
            (await TrySettleAsync(WorkspaceA, draft.Id, hook, state.ConcurrencyToken, true)).Error!.Code);

        var written = await EditAsync(WorkspaceA, draft.Id, hook, state.ConcurrencyToken, "An opening I am happy with.");
        var settled = await SettleAsync(WorkspaceA, draft.Id, hook, written.ConcurrencyToken, true);

        Assert.True(settled.Sections[0].Settled);
        Assert.Equal(written.Sections[0].Body, settled.Sections[0].Body);
        Assert.Equal(written.Sections[0].WrittenBy, settled.Sections[0].WrittenBy);
        Assert.Equal(written.RevisionNumber + 1, settled.RevisionNumber);

        // Settled is not accepted: the draft is still being written.
        Assert.Equal(LongFormDraftStatus.Writing, settled.Status);

        // Settling twice is one settling.
        Assert.Equal(settled.RevisionNumber, (await SettleAsync(WorkspaceA, draft.Id, hook, settled.ConcurrencyToken, true)).RevisionNumber);

        // It will not be written over, one at a time or with the rest.
        Assert.Equal(
            ContentErrorCodes.LongFormSectionSettled,
            (await AskAsync(WorkspaceA, draft.Id, hook, settled.RevisionNumber!.Value, "write-1")).Result.Error!.Code);
        var batch = await AskRestAsync(WorkspaceA, draft.Id, settled.RevisionNumber.Value, "rest-1");
        Assert.DoesNotContain(hook, batch.Result.Value!.Requests.Select(request => request.SectionId));

        var current = (await SectionsAsync(WorkspaceA, draft.Id)).Draft;
        var cleared = await SettleAsync(WorkspaceA, draft.Id, hook, current.ConcurrencyToken, false);

        Assert.False(cleared.Sections[0].Settled);
        Assert.Equal(written.Sections[0].Body, cleared.Sections[0].Body);
        Assert.True((await AskAsync(WorkspaceA, draft.Id, hook, cleared.RevisionNumber!.Value, "write-2")).Result.Succeeded);
    }

    [Fact]
    public async Task Editing_a_settled_section_clears_the_mark_and_a_body_that_arrives_for_one_is_not_put_in_it()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var state = (await SectionsAsync(WorkspaceA, draft.Id)).Draft;
        var hook = state.Sections[0].Id;
        WriteSectionsAs(_ => "What the writer wrote.");

        var written = await EditAsync(WorkspaceA, draft.Id, hook, state.ConcurrencyToken, "Mine.");

        // Asked for while unsettled, settled before it ran.
        await AskAsync(WorkspaceA, draft.Id, hook, written.RevisionNumber!.Value, "write-1");
        var current = (await SectionsAsync(WorkspaceA, draft.Id)).Draft;
        await SettleAsync(WorkspaceA, draft.Id, hook, current.ConcurrencyToken, true);
        await RunPendingAsync();

        var after = (await SectionsAsync(WorkspaceA, draft.Id)).Draft;
        Assert.Equal("Mine.", after.Sections[0].Body);
        Assert.True(after.Sections[0].Settled);

        var edited = await EditAsync(WorkspaceA, draft.Id, hook, after.ConcurrencyToken, "Mine, changed.");
        Assert.False(edited.Sections[0].Settled);
    }

    // ---- Isolation ----

    [Fact]
    public async Task One_workspace_can_neither_ask_for_read_edit_nor_settle_a_section_of_anothers_draft()
    {
        var a = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var b = await ApprovedThreeSectionDraftAsync(WorkspaceB, "key-1");
        var sectionOfA = a.Latest!.Sections[0].Id;
        var stateOfA = (await SectionsAsync(WorkspaceA, a.Id)).Draft;
        await EditAsync(WorkspaceA, a.Id, sectionOfA, stateOfA.ConcurrencyToken, "A's own words.");
        var tokenOfA = (await SectionsAsync(WorkspaceA, a.Id)).Draft.ConcurrencyToken;

        // B, by A's ids, on every route: the draft is not there. The same answer an unknown id gets.
        Assert.Equal(ContentErrorCodes.LongFormDraftNotFound, (await AskAsync(WorkspaceB, a.Id, sectionOfA, 2, "write-1")).Result.Error!.Code);
        Assert.Equal(ContentErrorCodes.LongFormDraftNotFound, (await AskRestAsync(WorkspaceB, a.Id, 2, "rest-1")).Result.Error!.Code);
        Assert.Equal(ContentErrorCodes.LongFormDraftNotFound, (await TryEditAsync(WorkspaceB, a.Id, sectionOfA, tokenOfA, "B's words.")).Error!.Code);
        Assert.Equal(ContentErrorCodes.LongFormDraftNotFound, (await TrySettleAsync(WorkspaceB, a.Id, sectionOfA, tokenOfA, true)).Error!.Code);
        Assert.Equal(ContentErrorCodes.LongFormDraftNotFound, (await TrySectionsAsync(WorkspaceB, a.Id)).Error!.Code);
        Assert.Equal(
            (await TrySectionsAsync(WorkspaceB, Guid.NewGuid())).Error!.Message,
            (await TrySectionsAsync(WorkspaceB, a.Id)).Error!.Message);

        // B's own draft cannot be made to answer for a section of A's.
        var stateOfB = (await SectionsAsync(WorkspaceB, b.Id)).Draft;
        Assert.Equal(ContentErrorCodes.LongFormSectionNotFound, (await AskAsync(WorkspaceB, b.Id, sectionOfA, 1, "write-2")).Result.Error!.Code);
        Assert.Equal(ContentErrorCodes.LongFormSectionNotFound, (await TryEditAsync(WorkspaceB, b.Id, sectionOfA, stateOfB.ConcurrencyToken, "B's words.")).Error!.Code);

        // Nothing was queued for B, and A's text is A's.
        Assert.Equal(0, await CountAsync(WorkspaceB, db => db.AiOperations.CountAsync(operation => operation.TaskType == AiTaskType.LongFormSection, Ct)));
        Assert.Equal("A's own words.", (await SectionsAsync(WorkspaceA, a.Id)).Draft.Sections[0].Body);
        Assert.All((await SectionsAsync(WorkspaceB, b.Id)).Draft.Sections, section => Assert.False(section.IsWritten));
    }

    [Fact]
    public async Task A_key_used_in_one_workspace_tells_another_nothing_and_anothers_batch_keeps_no_section_busy_here()
    {
        var a = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var b = await ApprovedThreeSectionDraftAsync(WorkspaceB, "key-1");
        WriteSectionsAs(_ => "A body.");

        // A queues every section of its own draft under one key.
        var batchA = await AskRestAsync(WorkspaceA, a.Id, 1, "shared-key");
        Assert.Equal(3, batchA.Result.Value!.Requests.Count);

        // B, with A's key against A's draft: not there. The key is never reached, so it answers nothing.
        Assert.Equal(ContentErrorCodes.LongFormDraftNotFound, (await AskRestAsync(WorkspaceB, a.Id, 1, "shared-key")).Result.Error!.Code);
        Assert.Equal(
            ContentErrorCodes.LongFormDraftNotFound,
            (await AskAsync(WorkspaceB, a.Id, a.Latest!.Sections[0].Id, 1, AiLongFormSectionRequestPolicy.BatchKey("shared-key", a.Latest.Sections[0].Id))).Result.Error!.Code);

        // B, with the same key against its own draft: a new batch of its own, replaying nothing of A's, with
        // none of its sections held back by A's being busy.
        var batchB = await AskRestAsync(WorkspaceB, b.Id, 1, "shared-key");
        Assert.True(batchB.Result.Succeeded, batchB.Result.Error?.Code);
        Assert.False(batchB.Replayed);
        Assert.Equal(3, batchB.Result.Value!.Requests.Count);
        Assert.Empty(batchA.Result.Value.Requests.Select(request => request.Request.AiProposalRequestId)
            .Intersect(batchB.Result.Value.Requests.Select(request => request.Request.AiProposalRequestId)));

        // The sweep alone, with work waiting in both: each body lands in its own draft.
        await RunPendingAsync();
        await RunMaintenanceAsync();

        Assert.All((await SectionsAsync(WorkspaceA, a.Id)).Draft.Sections, section => Assert.True(section.IsWritten));
        Assert.All((await SectionsAsync(WorkspaceB, b.Id)).Draft.Sections, section => Assert.True(section.IsWritten));
        Assert.Equal(4, await CountAsync(WorkspaceA, db => db.LongFormRevisions.CountAsync(Ct)));
        Assert.Equal(4, await CountAsync(WorkspaceB, db => db.LongFormRevisions.CountAsync(Ct)));
    }

    [Fact]
    public async Task Each_workspaces_requests_write_its_own_draft_and_are_listed_only_there()
    {
        var a = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var b = await ApprovedThreeSectionDraftAsync(WorkspaceB, "key-1");
        WriteSectionsAs(context => context.WorkspaceId == WorkspaceA ? "A's body." : "B's body.");

        // The same key in two workspaces is two requests.
        var askedA = await AskAsync(WorkspaceA, a.Id, a.Latest!.Sections[0].Id, 1, "write-1");
        var askedB = await AskAsync(WorkspaceB, b.Id, b.Latest!.Sections[0].Id, 1, "write-1");
        Assert.False(askedB.Replayed);
        Assert.NotEqual(askedA.Result.Value!.Request.Request.AiProposalRequestId, askedB.Result.Value!.Request.Request.AiProposalRequestId);

        Assert.Equal(2, (await RunPendingAsync()).Proposed);

        var ofA = await SectionsAsync(WorkspaceA, a.Id);
        var ofB = await SectionsAsync(WorkspaceB, b.Id);
        Assert.Equal("A's body.", ofA.Draft.Sections[0].Body);
        Assert.Equal("B's body.", ofB.Draft.Sections[0].Body);
        Assert.Equal(askedA.Result.Value.Request.Request.AiProposalRequestId, Assert.Single(ofA.Requests).RequestId);
        Assert.Equal(askedB.Result.Value.Request.Request.AiProposalRequestId, Assert.Single(ofB.Requests).RequestId);
        Assert.Equal(2, await CountAsync(WorkspaceB, db => db.LongFormRevisions.CountAsync(Ct)));
    }

    // ---- Helpers ----

    private async Task<LongFormDraftServiceModel> ApprovedThreeSectionDraftAsync(Guid workspaceId, string key)
    {
        var contextId = await SeedContextAsync(workspaceId);
        var started = (await StartAsync(workspaceId, contextId, LongFormKind.BlogArticle, key)).Result.Value!;
        _handler.Behavior = (context, _) => Task.FromResult(PlanProposal(context, contextId, [], ThreeSections));
        await RunPendingAsync();

        var landed = await DraftAsync(workspaceId, started.Draft.Id);

        return await DraftsAsync(workspaceId, facade => facade.ApprovePlanAsync(
            workspaceId == WorkspaceA ? AccountA : "user-b",
            landed.Id,
            new ApproveLongFormPlanViewModel { ExpectedConcurrencyToken = landed.ConcurrencyToken, RevisionNumber = 1 },
            Ct));
    }

    /// <summary>Scripts the writer: every section request gets a body made from its own execution context.</summary>
    private void WriteSectionsAs(Func<AiTaskExecutionContext, string> body) =>
        _sectionHandler.Behavior = (context, _) => Task.FromResult(BodyProposal(context, Guid.NewGuid(), body(context)));

    private static Guid SectionOf(AiTaskExecutionContext context) => LongFormSectionInputs.ReadSectionId(context.Inputs)!.Value;

    /// <summary>
    /// A section body as the capability stores it: the rows <see cref="LongFormSectionFields"/> defines, through
    /// the shared assembler, against the section and revision the request named.
    /// </summary>
    private static AiTaskHandlerOutcome BodyProposal(
        AiTaskExecutionContext context,
        Guid contextId,
        string body,
        IReadOnlyList<AiOutputWarning>? warnings = null,
        string? reused = null)
    {
        var sectionId = SectionOf(context);
        var changes = new List<AiResolvedChange>();

        void Set(string field, string value) => changes.Add(new AiResolvedChange(
            AiChangeKind.Set, AiChangeTargetKind.LongFormSectionBody, sectionId, field,
            BeforeValue: null, value, ProposedPosition: null, changes.Count));

        var parts = LongFormSectionFields.Split(body);

        for (var index = 0; index < parts.Count; index++)
        {
            Set(LongFormSectionFields.BodyPart(index), parts[index]);
        }

        Set(LongFormSectionFields.BasedOnRevision, LongFormSectionInputs.ReadRevisionNumber(context.Inputs)!.Value.ToString());

        if (reused is not null)
        {
            Set(LongFormSectionFields.ReusedEditorialParts, reused);
        }

        var assembly = AiProposalAssembler.Assemble(
            context.WorkspaceId,
            context.OperationId,
            pinnedVersionId: null,
            currentVersionId: null,
            new AiOutputDocument { SchemaVersion = "content.long-form-section.v1", Warnings = warnings ?? [] },
            changes,
            new AiProposalProvenance(
                "content.long-form-section.v1",
                AiTaskCatalog.LongFormSection,
                "1.0.0",
                "sha256:" + new string('b', 64),
                "test-provider",
                "test-model",
                null),
            Now,
            null,
            null);

        Assert.True(assembly.Succeeded, assembly.Failure?.Message);

        return AiTaskHandlerOutcome.ForProposal(assembly.Proposal!, [Attempt()]);
    }

    private async Task<IdempotentOutcome<LongFormSectionRequestedServiceModel>> AskAsync(
        Guid workspaceId, Guid draftId, Guid sectionId, int revisionNumber, string? key, string? instruction = null)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiLongFormSectionRequestFacade>().RequestAsync(
            draftId,
            sectionId,
            new RequestLongFormSectionViewModel { RevisionNumber = revisionNumber, Instruction = instruction },
            key,
            Ct);
    }

    private async Task<IdempotentOutcome<LongFormSectionBatchServiceModel>> AskRestAsync(
        Guid workspaceId, Guid draftId, int revisionNumber, string? key)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiLongFormSectionRequestFacade>().RequestRestAsync(
            draftId, new RequestLongFormSectionBatchViewModel { RevisionNumber = revisionNumber }, key, Ct);
    }

    private async Task<OperationResult<LongFormSectionsStatusServiceModel>> TrySectionsAsync(Guid workspaceId, Guid draftId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiLongFormSectionRequestFacade>().GetSectionsAsync(draftId, Ct);
    }

    private async Task<LongFormSectionsStatusServiceModel> SectionsAsync(Guid workspaceId, Guid draftId)
    {
        var result = await TrySectionsAsync(workspaceId, draftId);

        Assert.True(result.Succeeded, result.Error?.Code);

        return result.Value!;
    }

    private async Task<OperationResult<LongFormSectionsServiceModel>> TryEditAsync(
        Guid workspaceId, Guid draftId, Guid sectionId, string token, string? body)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<ILongFormSectionFacade>().EditSectionAsync(
            workspaceId == WorkspaceA ? AccountA : "user-b",
            draftId,
            sectionId,
            new EditLongFormSectionViewModel { ExpectedConcurrencyToken = token, Body = body },
            Ct);
    }

    private async Task<LongFormSectionsServiceModel> EditAsync(
        Guid workspaceId, Guid draftId, Guid sectionId, string token, string? body)
    {
        var result = await TryEditAsync(workspaceId, draftId, sectionId, token, body);

        Assert.True(result.Succeeded, result.Error?.Code);

        return result.Value!;
    }

    private async Task<OperationResult<LongFormSectionsServiceModel>> TrySettleAsync(
        Guid workspaceId, Guid draftId, Guid sectionId, string token, bool settled)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<ILongFormSectionFacade>().SettleSectionAsync(
            workspaceId == WorkspaceA ? AccountA : "user-b",
            draftId,
            sectionId,
            new SettleLongFormSectionViewModel { ExpectedConcurrencyToken = token, Settled = settled },
            Ct);
    }

    private async Task<LongFormSectionsServiceModel> SettleAsync(
        Guid workspaceId, Guid draftId, Guid sectionId, string token, bool settled)
    {
        var result = await TrySettleAsync(workspaceId, draftId, sectionId, token, settled);

        Assert.True(result.Succeeded, result.Error?.Code);

        return result.Value!;
    }

    private async Task<OperationResult<LongFormDraftServiceModel>> TryDraftsAsync(
        Guid workspaceId, Func<ILongFormDraftFacade, Task<OperationResult<LongFormDraftServiceModel>>> ask)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await ask(scope.ServiceProvider.GetRequiredService<ILongFormDraftFacade>());
    }

    private async Task SetStatusAsync(Guid workspaceId, LongFormDraftStatus status)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var row = await db.LongFormDrafts.SingleAsync(Ct);

        row.Status = status;

        if (status is LongFormDraftStatus.Accepted or LongFormDraftStatus.NeedsReview)
        {
            row.AcceptedRevisionId = (await db.LongFormRevisions.AsNoTracking().OrderBy(revision => revision.RevisionNumber).FirstAsync(Ct)).Id;
            row.AcceptedAt = Now;
        }

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// Leaves the account's open allowance period with room for this many section runs and no more.
    /// </summary>
    /// <remarks>
    /// The period was opened when the plan was made, so its allowance is set on the period itself: a quota row
    /// added now would not reach a period that already exists.
    /// </remarks>
    private async Task LeaveAllowanceForAsync(decimal runs)
    {
        var perRun = (await PeekAsync()).Required;
        Assert.True(perRun > 0m);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var period = await db.AccountAiQuotaPeriods.SingleAsync(candidate => candidate.AccountId == AccountA, Ct);

        period.CarriedOver = 0m;
        period.Allowance = period.Consumed + period.Reserved + (perRun * runs);

        await db.SaveChangesAsync(Ct);
    }

    private async Task<CreatorPantry.Domain.Modules.AiUsage.Managers.AiQuotaStateServiceModel> PeekAsync()
    {
        using var scope = _provider.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>()
            .PeekAsync(AccountA, AiTaskType.LongFormSection, isMeterable: true, Ct);
    }
}
