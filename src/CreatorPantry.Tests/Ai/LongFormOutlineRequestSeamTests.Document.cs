using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// LF.3.4 through the real seam: the draft read whole, with what was found about each generated section, and
/// its acceptance. The assembler's bytes are <c>LongFormDocumentAssemblerTests</c>'; this is what reaches it
/// and what an acceptance does.
/// </summary>
public sealed partial class LongFormOutlineRequestSeamTests
{
    // ---- Reading the document ----

    [Fact]
    public async Task The_document_is_the_written_sections_in_plan_order_with_what_was_found_about_each_generated_one()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var hook = draft.Latest!.Sections[0].Id;
        var tips = draft.Latest.Sections[1].Id;
        var closing = draft.Latest.Sections[2].Id;

        _sectionHandler.Behavior = (context, _) => Task.FromResult(SectionOf(context) == tips
            ? BodyProposal(
                context,
                draft.CreativeContextId,
                "- Stir gently.\n- Use 900g flour.",
                [
                    new AiOutputWarning
                    {
                        Kind = AiWarningKind.UnverifiedClaim,
                        Message = $"[{AiEditorialClaimScanner.UnsupportedNumber}] \"900g\" is a figure the recipe does not state.",
                        ChangeIndex = 0,
                    },
                    new AiOutputWarning
                    {
                        Kind = AiWarningKind.Assumption,
                        Message = AiPolicy.ModelWarningLabel + "I assumed a family audience.",
                        ChangeIndex = 0,
                    },
                ],
                reused: string.Join(LongFormSectionFields.ReusedPartsSeparator, "tips", "storageReheating"))
            : BodyProposal(context, draft.CreativeContextId, "Some breads ask for an afternoon."));

        await AskAsync(WorkspaceA, draft.Id, hook, 1, "write-1");
        await AskAsync(WorkspaceA, draft.Id, tips, 1, "write-2");
        await RunPendingAsync();

        var review = await ReviewAsync(WorkspaceA, draft.Id);
        var document = review.Document;

        Assert.Equal(
            "## The loaf for people who do not bake\n\nSome breads ask for an afternoon.\n\n"
                + "## Three things that help\n\n- Stir gently.\n- Use 900g flour.\n",
            document.Markdown);
        Assert.Equal(3, document.RevisionNumber);
        Assert.Equal(LongFormDraftStatus.Writing, document.Status);
        Assert.Equal([true, true, false], document.Sections.Select(section => section.IsWritten));
        Assert.Equal(["tips", "storageReheating"], document.Sections[1].ReusedEditorialParts);
        Assert.Empty(document.Sections[0].ReusedEditorialParts);
        Assert.Equal(ThreeSections[1].Intent, document.Sections[1].Intent);

        // What stands between this document and acceptance, by name and by section.
        var issue = Assert.Single(document.NeedsConfirmation);
        Assert.Equal(LongFormAcceptanceIssue.UnwrittenSections, issue.Issue);
        Assert.Equal([closing], issue.SectionIds);

        // The scanner's finding is the section's own, recorded when the body was written into the draft: the
        // plan is pinned to no recipe, so the figure is one nothing supports.
        var finding = Assert.Single(document.Sections[1].Findings);
        Assert.Equal(AiEditorialClaimScanner.UnsupportedNumber, finding.Code);
        Assert.Contains("900", finding.Message);
        Assert.Empty(document.Sections[0].Findings);

        // The model's own caution is still there beside it, marked as the model's. The copy of the figure
        // finding this proposal carries is not shown a second time.
        var caution = Assert.Single(review.Warnings);
        Assert.Equal(tips, caution.SectionId);
        Assert.Null(caution.Code);
        Assert.True(caution.FromModel);
        Assert.Equal("I assumed a family audience.", caution.Message);

        Assert.Equal([hook, tips], review.Requests.Select(request => request.SectionId));
        Assert.All(review.Requests, request => Assert.Equal(LongFormSectionRequestOutcome.Written, request.Outcome));
    }

    [Fact]
    public async Task Once_the_creator_rewrites_a_generated_section_its_findings_and_its_attribution_go_with_the_old_text()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var tips = draft.Latest!.Sections[1].Id;

        _sectionHandler.Behavior = (context, _) => Task.FromResult(BodyProposal(
            context,
            draft.CreativeContextId,
            "Use 900g flour.",
            [new AiOutputWarning { Kind = AiWarningKind.UnverifiedClaim, Message = "[editorial.unsupported_number] A figure.", ChangeIndex = 0 }],
            reused: "tips"));

        await AskAsync(WorkspaceA, draft.Id, tips, 1, "write-1");
        await RunPendingAsync();
        Assert.Single((await ReviewAsync(WorkspaceA, draft.Id)).Document.Sections[1].Findings);

        var state = (await SectionsAsync(WorkspaceA, draft.Id)).Draft;
        await EditAsync(WorkspaceA, draft.Id, tips, state.ConcurrencyToken, "My own tips, in my own words.");

        var review = await ReviewAsync(WorkspaceA, draft.Id);

        // The old text's findings went with it. The creator's own words were read too, and have none.
        Assert.Empty(review.Warnings);
        Assert.Empty(review.Document.Sections[1].Findings);
        Assert.Empty(review.Document.Sections[1].ReusedEditorialParts);
        Assert.Equal(ContentRevisionSource.CreatorEdit, review.Document.Sections[1].WrittenBy);
        Assert.Equal(LongFormSectionRequestOutcome.Superseded, Assert.Single(review.Requests).Outcome);
    }

    // ---- The scanner (LF.3.2) ----

    [Fact]
    public async Task A_creators_own_edit_is_scanned_against_the_pinned_recipe_saved_as_typed_and_never_refused()
    {
        var contextId = await SeedContextAsync(WorkspaceA);
        var bread = await SeedRecipeReferenceAsync(WorkspaceA, contextId, 0);
        var draft = await ApprovedDraftAsync(WorkspaceA, contextId, [bread], "key-1");
        var state = (await SectionsAsync(WorkspaceA, draft.Id)).Draft;
        var hook = state.Sections[0].Id;

        // 450g is the pinned recipe's own figure and is left alone; 900g is not.
        const string body = "Start with 450g plain flour, or use 900g for a crowd. It is perfectly safe for babies, and it never fails.";
        var edited = await EditAsync(WorkspaceA, draft.Id, hook, state.ConcurrencyToken, body);

        // Saved exactly as typed. A finding is a caution, not a refusal.
        Assert.Equal(body, edited.Sections[0].Body);
        Assert.Equal(ContentRevisionSource.CreatorEdit, edited.Sections[0].WrittenBy);

        var review = await ReviewAsync(WorkspaceA, draft.Id);
        var codes = review.Document.Sections[0].Findings.Select(finding => finding.Code).ToList();

        Assert.Contains(AiEditorialClaimScanner.UnsupportedNumber, codes);
        Assert.Contains(AiEditorialClaimScanner.UnsupportedSafetyClaim, codes);
        Assert.Contains(LongFormClaimScanner.UnsupportedGuarantee, codes);

        var figures = review.Document.Sections[0].Findings.Where(finding => finding.Code == AiEditorialClaimScanner.UnsupportedNumber).ToList();
        Assert.Contains(figures, finding => finding.Message.Contains("900", StringComparison.Ordinal));
        Assert.DoesNotContain(figures, finding => finding.Message.Contains("450", StringComparison.Ordinal));

        // Stored with the section in the revision, so the same revision always says the same thing.
        Assert.Equal(
            review.Document.Sections[0].Findings,
            (await ReviewAsync(WorkspaceA, draft.Id)).Document.Sections[0].Findings);

        // And it does not stand in the way of accepting the piece, beyond what was already wrong with it.
        Assert.Equal(
            [LongFormAcceptanceIssue.UnwrittenSections],
            review.Document.NeedsConfirmation.Select(issue => issue.Issue));

        // Put right, and the findings go.
        var fixedText = await EditAsync(WorkspaceA, draft.Id, hook, edited.ConcurrencyToken, "A loaf for a grey morning.");
        Assert.Equal("A loaf for a grey morning.", fixedText.Sections[0].Body);
        Assert.Empty((await ReviewAsync(WorkspaceA, draft.Id)).Document.Sections[0].Findings);
    }

    [Fact]
    public async Task What_the_scanner_finds_stays_with_its_section_through_a_neighbours_edit_and_a_settled_mark()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var state = (await SectionsAsync(WorkspaceA, draft.Id)).Draft;

        var first = await EditAsync(WorkspaceA, draft.Id, state.Sections[0].Id, state.ConcurrencyToken, "Ready in 20 minutes.");
        var second = await EditAsync(WorkspaceA, draft.Id, state.Sections[1].Id, first.ConcurrencyToken, "Stir gently.");
        await SettleAsync(WorkspaceA, draft.Id, state.Sections[0].Id, second.ConcurrencyToken, true);

        var sections = (await ReviewAsync(WorkspaceA, draft.Id)).Document.Sections;

        Assert.Equal(AiEditorialClaimScanner.UnsupportedNumber, Assert.Single(sections[0].Findings).Code);
        Assert.True(sections[0].Settled);
        Assert.Empty(sections[1].Findings);
    }

    [Fact]
    public async Task The_models_own_cautions_and_the_scanners_findings_are_both_shown_and_neither_hides_the_other()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var hook = draft.Latest!.Sections[0].Id;

        _sectionHandler.Behavior = (context, _) => Task.FromResult(BodyProposal(
            context,
            draft.CreativeContextId,
            "This loaf is completely nut-free and the most searched bread recipe this year.",
            [
                new AiOutputWarning { Kind = AiWarningKind.Assumption, Message = AiPolicy.ModelWarningLabel + "Nothing here needs checking.", ChangeIndex = 0 },
                new AiOutputWarning { Kind = AiWarningKind.Rationale, Message = $"[{LongFormFindings.DrawsOnAcceptedCopy}] Written from copy you already accepted: headnote.", ChangeIndex = 0 },
            ]));

        await AskAsync(WorkspaceA, draft.Id, hook, 1, "write-1");
        await RunPendingAsync();

        var review = await ReviewAsync(WorkspaceA, draft.Id);
        var codes = review.Document.Sections[0].Findings.Select(finding => finding.Code).ToList();

        // The model said nothing needed checking. The scanner looked anyway.
        Assert.Contains(AiEditorialClaimScanner.UnsupportedSafetyClaim, codes);
        Assert.Contains(AiSeoClaimScanner.UnsupportedMetric, codes);

        // And what the model and the handler said is still shown, beside it.
        Assert.Equal(2, review.Warnings.Count);
        Assert.Contains(review.Warnings, warning => warning.FromModel && warning.Message == "Nothing here needs checking.");
        Assert.Contains(review.Warnings, warning => warning.Code == LongFormFindings.DrawsOnAcceptedCopy);
    }

    // ---- Accepting ----

    [Fact]
    public async Task A_finished_document_is_accepted_once_and_accepting_it_again_changes_nothing()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        WriteSectionsAs(context => $"Body for {SectionOf(context):N}.");
        await AskRestAsync(WorkspaceA, draft.Id, 1, "rest-1");
        await RunPendingAsync();

        var document = (await ReviewAsync(WorkspaceA, draft.Id)).Document;
        Assert.Empty(document.NeedsConfirmation);

        var accepted = await AcceptAsync(WorkspaceA, draft.Id, document.ConcurrencyToken, document.RevisionNumber!.Value);

        Assert.True(accepted.Succeeded, accepted.Error?.Code);
        Assert.Equal(LongFormDraftStatus.Accepted, accepted.Value!.Status);
        Assert.Equal(document.RevisionNumber, accepted.Value.AcceptedRevisionNumber);
        Assert.Equal(Now, accepted.Value.AcceptedAt);

        // The article of record is exactly what was read.
        Assert.Equal(document.Markdown, accepted.Value.Markdown);

        // The retry after a lost response still holds the old token. It is the same acceptance.
        var again = await AcceptAsync(WorkspaceA, draft.Id, document.ConcurrencyToken, document.RevisionNumber.Value);

        Assert.True(again.Succeeded, again.Error?.Code);
        Assert.Equal(accepted.Value.AcceptedRevisionNumber, again.Value!.AcceptedRevisionNumber);

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        // No revision was written to accept, and one acceptance was recorded, saying nothing of the piece.
        Assert.Equal(document.RevisionNumber, await db.LongFormRevisions.CountAsync(Ct));
        var row = await db.LongFormDrafts.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(
            await db.LongFormRevisions.Where(revision => revision.RevisionNumber == document.RevisionNumber).Select(revision => revision.Id).SingleAsync(Ct),
            row.AcceptedRevisionId);

        var entry = Assert.Single(await db.AuditLogs.AsNoTracking()
            .Where(candidate => candidate.Action == ContentAuditActions.LongFormDocumentAccepted)
            .ToListAsync(Ct));
        Assert.DoesNotContain("Body for", entry.Summary);
        Assert.DoesNotContain("loaf", entry.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unfinished_document_is_accepted_only_when_what_is_wrong_with_it_is_confirmed_by_name()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var state = (await SectionsAsync(WorkspaceA, draft.Id)).Draft;
        var longBody = new string('a', state.SectionMaxCharacters + 1);
        var edited = await EditAsync(WorkspaceA, draft.Id, state.Sections[0].Id, state.ConcurrencyToken, longBody);

        var document = (await ReviewAsync(WorkspaceA, draft.Id)).Document;
        Assert.Equal(
            [LongFormAcceptanceIssue.UnwrittenSections, LongFormAcceptanceIssue.OverCeiling],
            document.NeedsConfirmation.Select(issue => issue.Issue));

        // Nothing confirmed, and one of the two confirmed: neither accepts, and both say what is wrong.
        foreach (var confirmed in new LongFormAcceptanceIssue[][] { [], [LongFormAcceptanceIssue.OverCeiling] })
        {
            var refused = await AcceptAsync(WorkspaceA, draft.Id, edited.ConcurrencyToken, 2, confirmed);

            Assert.Equal(ContentErrorCodes.LongFormAcceptanceConfirmationRequired, refused.Error!.Code);
            Assert.Contains("confirmed", refused.Error.FieldErrors.Keys);
            Assert.NotNull(refused.Error.Extensions!["issues"]);
        }

        Assert.Equal(LongFormDraftStatus.Writing, (await ReviewAsync(WorkspaceA, draft.Id)).Document.Status);

        // No audit entry for an acceptance that did not happen.
        Assert.Equal(0, await CountAsync(WorkspaceA, db => db.AuditLogs.CountAsync(entry => entry.Action == ContentAuditActions.LongFormDocumentAccepted, Ct)));

        var accepted = await AcceptAsync(
            WorkspaceA, draft.Id, edited.ConcurrencyToken, 2,
            [LongFormAcceptanceIssue.UnwrittenSections, LongFormAcceptanceIssue.OverCeiling]);

        Assert.True(accepted.Succeeded, accepted.Error?.Code);
        Assert.Equal(LongFormDraftStatus.Accepted, accepted.Value!.Status);

        // Accepted as it is: nothing was trimmed to make it fit and nothing was written to fill it out.
        Assert.Contains(longBody, accepted.Value.Markdown, StringComparison.Ordinal);
        Assert.Equal([true, false, false], accepted.Value.Sections.Select(section => section.IsWritten));
    }

    [Fact]
    public async Task Confirming_something_the_document_does_not_have_wrong_with_it_accepts_nothing()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        WriteSectionsAs(_ => "A body.");
        await AskRestAsync(WorkspaceA, draft.Id, 1, "rest-1");
        await RunPendingAsync();

        var document = (await ReviewAsync(WorkspaceA, draft.Id)).Document;
        Assert.Empty(document.NeedsConfirmation);

        // Nothing is wrong with it. A request that says it accepts an unwritten section was not looking at this.
        var blanket = await AcceptAsync(
            WorkspaceA, draft.Id, document.ConcurrencyToken, document.RevisionNumber!.Value,
            [LongFormAcceptanceIssue.UnwrittenSections, LongFormAcceptanceIssue.OverCeiling]);

        Assert.Equal(ContentErrorCodes.LongFormAcceptanceConfirmationRequired, blanket.Error!.Code);
        Assert.Equal(LongFormDraftStatus.Writing, (await ReviewAsync(WorkspaceA, draft.Id)).Document.Status);

        // And one that confirms more than is wrong, when something is.
        var state = (await SectionsAsync(WorkspaceA, draft.Id)).Draft;
        var blanked = await EditAsync(WorkspaceA, draft.Id, state.Sections[0].Id, state.ConcurrencyToken, string.Empty);
        var tooMuch = await AcceptAsync(
            WorkspaceA, draft.Id, blanked.ConcurrencyToken, blanked.RevisionNumber!.Value,
            [LongFormAcceptanceIssue.UnwrittenSections, LongFormAcceptanceIssue.OverCeiling]);

        Assert.Equal(ContentErrorCodes.LongFormAcceptanceConfirmationRequired, tooMuch.Error!.Code);
        Assert.True((await AcceptAsync(
            WorkspaceA, draft.Id, blanked.ConcurrencyToken, blanked.RevisionNumber.Value,
            [LongFormAcceptanceIssue.UnwrittenSections])).Succeeded);
    }

    [Fact]
    public async Task A_document_with_nothing_written_cannot_be_accepted_whatever_is_confirmed()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var document = (await ReviewAsync(WorkspaceA, draft.Id)).Document;

        Assert.Equal(string.Empty, document.Markdown);

        var refused = await AcceptAsync(
            WorkspaceA, draft.Id, document.ConcurrencyToken, 1,
            [LongFormAcceptanceIssue.UnwrittenSections, LongFormAcceptanceIssue.OverCeiling]);

        Assert.Equal(ContentErrorCodes.LongFormAcceptanceNothingWritten, refused.Error!.Code);
        Assert.Equal(LongFormDraftStatus.Outlined, (await ReviewAsync(WorkspaceA, draft.Id)).Document.Status);
    }

    [Fact]
    public async Task An_acceptance_names_the_revision_it_read_and_a_draft_that_is_not_being_written_takes_none()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var state = (await SectionsAsync(WorkspaceA, draft.Id)).Draft;
        var edited = await EditAsync(WorkspaceA, draft.Id, state.Sections[0].Id, state.ConcurrencyToken, "An opening.");
        LongFormAcceptanceIssue[] unfinished = [LongFormAcceptanceIssue.UnwrittenSections];

        // Composed against revision 1; the document is at 2.
        Assert.Equal(
            ContentErrorCodes.LongFormDraftStale,
            (await AcceptAsync(WorkspaceA, draft.Id, edited.ConcurrencyToken, 1, unfinished)).Error!.Code);
        Assert.Equal(
            ContentErrorCodes.LongFormAcceptanceInvalid,
            (await AcceptAsync(WorkspaceA, draft.Id, edited.ConcurrencyToken, 0, unfinished)).Error!.Code);
        Assert.Equal(
            ContentErrorCodes.LongFormAcceptanceInvalid,
            (await AcceptAsync(WorkspaceA, draft.Id, "not-a-token", 2, unfinished)).Error!.Code);

        await SetStatusAsync(WorkspaceA, LongFormDraftStatus.Draft);
        Assert.Equal(
            ContentErrorCodes.LongFormPlanNotApproved,
            (await AcceptAsync(WorkspaceA, draft.Id, edited.ConcurrencyToken, 2, unfinished)).Error!.Code);

        await SetStatusAsync(WorkspaceA, LongFormDraftStatus.Assembled);
        Assert.Equal(
            ContentErrorCodes.LongFormDraftNotWritable,
            (await AcceptAsync(WorkspaceA, draft.Id, edited.ConcurrencyToken, 2, unfinished)).Error!.Code);
    }

    [Fact]
    public async Task An_accepted_document_takes_no_more_writing_editing_or_settling()
    {
        var draft = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        WriteSectionsAs(_ => "A body.");
        await AskRestAsync(WorkspaceA, draft.Id, 1, "rest-1");
        await RunPendingAsync();

        var document = (await ReviewAsync(WorkspaceA, draft.Id)).Document;
        var accepted = (await AcceptAsync(WorkspaceA, draft.Id, document.ConcurrencyToken, document.RevisionNumber!.Value)).Value!;
        var section = accepted.Sections[0].Id;

        Assert.Equal(
            ContentErrorCodes.LongFormDraftAccepted,
            (await AskAsync(WorkspaceA, draft.Id, section, accepted.RevisionNumber!.Value, "write-9")).Result.Error!.Code);
        Assert.Equal(
            ContentErrorCodes.LongFormDraftAccepted,
            (await TryEditAsync(WorkspaceA, draft.Id, section, accepted.ConcurrencyToken, "Changed my mind.")).Error!.Code);
        Assert.Equal(
            ContentErrorCodes.LongFormDraftAccepted,
            (await TrySettleAsync(WorkspaceA, draft.Id, section, accepted.ConcurrencyToken, true)).Error!.Code);

        // And it still reads as the document that was accepted.
        var after = (await ReviewAsync(WorkspaceA, draft.Id)).Document;
        Assert.Equal(accepted.Markdown, after.Markdown);
        Assert.Equal(accepted.AcceptedRevisionNumber, after.AcceptedRevisionNumber);
    }

    // ---- Isolation ----

    [Fact]
    public async Task One_workspace_can_neither_read_nor_accept_anothers_document_and_sees_none_of_its_findings()
    {
        var a = await ApprovedThreeSectionDraftAsync(WorkspaceA, "key-1");
        var b = await ApprovedThreeSectionDraftAsync(WorkspaceB, "key-1");

        _sectionHandler.Behavior = (context, _) => Task.FromResult(BodyProposal(
            context,
            Guid.NewGuid(),
            context.WorkspaceId == WorkspaceA ? "A's secret opening." : "B's own opening.",
            context.WorkspaceId == WorkspaceA
                ? [new AiOutputWarning { Kind = AiWarningKind.SafetyCaution, Message = "[editorial.unsupported_safety_claim] A's finding.", ChangeIndex = 0 }]
                : null));

        await AskAsync(WorkspaceA, a.Id, a.Latest!.Sections[0].Id, 1, "write-1");
        await AskAsync(WorkspaceB, b.Id, b.Latest!.Sections[0].Id, 1, "write-1");
        await RunPendingAsync();

        var ofA = await ReviewAsync(WorkspaceA, a.Id);
        var ofB = await ReviewAsync(WorkspaceB, b.Id);

        Assert.Contains("A's secret opening.", ofA.Document.Markdown);
        Assert.Contains("B's own opening.", ofB.Document.Markdown);
        Assert.DoesNotContain("A's", ofB.Document.Markdown);
        Assert.Empty(ofB.Warnings);
        Assert.All(ofB.Document.Sections, section => Assert.Empty(section.Findings));

        // B, by A's id: the draft is not there, to read or to accept. The same answer an unknown id gets.
        var read = await TryReviewAsync(WorkspaceB, a.Id);
        Assert.Equal(ContentErrorCodes.LongFormDraftNotFound, read.Error!.Code);
        Assert.Equal((await TryReviewAsync(WorkspaceB, Guid.NewGuid())).Error!.Message, read.Error.Message);

        var accept = await AcceptAsync(
            WorkspaceB, a.Id, ofA.Document.ConcurrencyToken, ofA.Document.RevisionNumber!.Value,
            [LongFormAcceptanceIssue.UnwrittenSections]);
        Assert.Equal(ContentErrorCodes.LongFormDraftNotFound, accept.Error!.Code);

        // A's draft is as A left it, and A can accept it; B's is untouched by that.
        Assert.Equal(LongFormDraftStatus.Writing, (await ReviewAsync(WorkspaceA, a.Id)).Document.Status);
        Assert.True((await AcceptAsync(
            WorkspaceA, a.Id, ofA.Document.ConcurrencyToken, ofA.Document.RevisionNumber.Value,
            [LongFormAcceptanceIssue.UnwrittenSections])).Succeeded);
        Assert.Equal(LongFormDraftStatus.Writing, (await ReviewAsync(WorkspaceB, b.Id)).Document.Status);
    }

    // ---- Helpers ----

    private async Task<OperationResult<LongFormDocumentReviewServiceModel>> TryReviewAsync(Guid workspaceId, Guid draftId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiLongFormSectionRequestFacade>().GetDocumentAsync(draftId, Ct);
    }

    private async Task<LongFormDocumentReviewServiceModel> ReviewAsync(Guid workspaceId, Guid draftId)
    {
        var result = await TryReviewAsync(workspaceId, draftId);

        Assert.True(result.Succeeded, result.Error?.Code);

        return result.Value!;
    }

    private async Task<OperationResult<LongFormAssembledDocumentServiceModel>> AcceptAsync(
        Guid workspaceId, Guid draftId, string token, int revisionNumber, IReadOnlyList<LongFormAcceptanceIssue>? confirmed = null)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<ILongFormDocumentFacade>().AcceptAsync(
            workspaceId == WorkspaceA ? AccountA : "user-b",
            draftId,
            new AcceptLongFormDocumentViewModel
            {
                ExpectedConcurrencyToken = token,
                RevisionNumber = revisionNumber,
                Confirmed = confirmed,
            },
            Ct);
    }
}
