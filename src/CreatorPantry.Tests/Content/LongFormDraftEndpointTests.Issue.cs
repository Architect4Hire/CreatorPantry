using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// LF.4.1 through the gateway: a newsletter issue's items bound one to one to the references its revision
/// pinned, what an item reads as once the piece of work drops its reference, ordering items by reference, and
/// the creator's subject line and note.
/// </summary>
/// <remarks>
/// Every reference here is a real row on the draft's creative context, naming a real recipe, because whether an
/// item still resolves is read from those rows. A plan is landed the way the worker lands one.
/// </remarks>
public sealed partial class LongFormDraftEndpointTests
{
    // ---- the binding -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task An_issue_with_one_item_for_each_reference_is_valid_resolved_and_approvable(int recipes)
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var (draft, references) = await IssueAsync(client, A, recipes);

        Assert.True(draft.GetProperty("planCheck").GetProperty("isValid").GetBoolean());
        Assert.Empty(draft.GetProperty("unresolvedItems").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("framing").ValueKind);

        var items = Items(draft);
        Assert.Equal(references, items.Select(item => Source(item)!.Value));
        Assert.All(items, item => Assert.Equal("Resolved", item.GetProperty("sourceState").GetString()));

        // Nothing but an item has a source, or a state to report.
        Assert.All(
            Sections(draft).Where(section => section.GetProperty("role").GetString() != "Item"),
            section => Assert.Equal(JsonValueKind.Null, section.GetProperty("sourceState").ValueKind));

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(client, A, draft, revisionNumber: 1)).StatusCode);
    }

    [Fact]
    public async Task An_item_count_that_disagrees_with_the_references_is_saved_reported_and_not_approvable()
    {
        using var client = await SignInAsync(A.OwnerEmail);

        // Two references pinned, one item: the second recipe is in the issue and nothing covers it.
        var (draft, _) = await IssueAsync(client, A, recipes: 2, plan: references =>
        [
            new(LongFormSectionRole.Greeting, "Hello again", "Say hello.", null),
            new(LongFormSectionRole.Item, "The loaf", "Introduce the loaf.", references[0]),
        ]);

        var check = draft.GetProperty("planCheck");
        Assert.False(check.GetProperty("isValid").GetBoolean());

        var finding = Assert.Single(check.GetProperty("findings").EnumerateArray());
        Assert.Equal(LongFormFindingCodes.ItemsSourceUncovered, finding.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, finding.GetProperty("sectionId").ValueKind);

        var refused = await ApproveAsync(client, A, draft, revisionNumber: 1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var problem = await BodyOf(refused);
        Assert.Equal(ContentErrorCodes.LongFormPlanUnprocessable, Code(problem));
        Assert.True(problem.GetProperty("errors").TryGetProperty("sections", out _));
        Assert.Equal("Draft", (await ReadAsync(client, A, Id(draft))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Two_items_for_one_reference_and_an_item_for_none_are_each_named_on_their_own_section()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var (draft, _) = await IssueAsync(client, A, recipes: 2, plan: references =>
        [
            new(LongFormSectionRole.Item, "The loaf", "Introduce the loaf.", references[0]),
            new(LongFormSectionRole.Item, "The loaf again", "Introduce it twice.", references[0]),
            new(LongFormSectionRole.Item, "Something else", "Introduce nothing in particular.", null),
        ]);

        var sections = Sections(draft);
        var findings = draft.GetProperty("planCheck").GetProperty("findings").EnumerateArray()
            .Select(finding => (
                Code: finding.GetProperty("code").GetString(),
                Section: finding.GetProperty("sectionId").ValueKind is JsonValueKind.Null ? (Guid?)null : finding.GetProperty("sectionId").GetGuid()))
            .ToList();

        Assert.Equal(
            new (string? Code, Guid? Section)[]
            {
                (LongFormFindingCodes.ItemSourceRepeated, Id(sections[1])),
                (LongFormFindingCodes.ItemSourceMissing, Id(sections[2])),
                (LongFormFindingCodes.ItemsSourceUncovered, null),
            },
            findings);

        // An item that names nothing has nothing to resolve to, and is reported as such.
        Assert.Equal([Id(sections[2])], draft.GetProperty("unresolvedItems").EnumerateArray().Select(id => id.GetGuid()));
    }

    // ---- a reference the piece of work no longer names ---------------------------------------------------

    [Fact]
    public async Task An_item_whose_reference_is_removed_is_kept_marked_unresolved_and_reported_and_its_revision_is_untouched()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var (draft, references) = await IssueAsync(client, A, recipes: 2);
        var root = $"{DraftsIn(A)}/{Id(draft)}";
        var approved = await BodyOf(await ApproveAsync(client, A, draft, revisionNumber: 1));
        var lost = Items(approved)[1];

        // The creator writes the item, and only then is its recipe taken off the piece of work.
        var written = await client.PatchAsJsonAsync(
            $"{root}/sections/{Id(lost)}", new { expectedConcurrencyToken = Token(approved), body = "A cake that keeps." }, Ct);
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);

        var before = await RevisionsAsync(A);

        await InAsync(A, async services =>
        {
            var db = services.GetRequiredService<CreatorPantryDbContext>();
            db.CreativeContextReferences.Remove(await db.CreativeContextReferences.SingleAsync(reference => reference.Id == references[1], Ct));

            return await db.SaveChangesAsync(Ct);
        });

        var read = await ReadAsync(client, A, Id(draft));
        var items = Items(read);

        // Kept: still in the plan, still naming the reference, still written.
        Assert.Equal(references, items.Select(item => Source(item)!.Value));
        Assert.True(items[1].GetProperty("isWritten").GetBoolean());

        // Marked and reported, and none of it is a failure of the plan or a change of state.
        Assert.Equal(["Resolved", "Unresolved"], items.Select(item => item.GetProperty("sourceState").GetString()));
        Assert.Equal([Id(lost)], read.GetProperty("unresolvedItems").EnumerateArray().Select(id => id.GetGuid()));
        Assert.True(read.GetProperty("planCheck").GetProperty("isValid").GetBoolean());
        Assert.Equal("Writing", read.GetProperty("status").GetString());

        // The other two reads of the draft say the same.
        var state = (await BodyOf(await client.GetAsync($"{root}/sections", Ct))).GetProperty("draft").GetProperty("sections")
            .EnumerateArray().Single(section => Id(section) == Id(lost));
        Assert.Equal("Unresolved", state.GetProperty("sourceState").GetString());
        Assert.Equal(references[1], Source(state));
        Assert.Equal("A cake that keeps.", state.GetProperty("body").GetString());

        var document = (await BodyOf(await client.GetAsync($"{root}/document", Ct))).GetProperty("document");
        Assert.Equal([Id(lost)], document.GetProperty("unresolvedItems").EnumerateArray().Select(id => id.GetGuid()));
        Assert.Contains("A cake that keeps.", document.GetProperty("markdown").GetString());
        Assert.Empty(document.GetProperty("needsConfirmation").EnumerateArray()
            .Where(issue => issue.GetProperty("issue").GetString() != "UnwrittenSections"));

        // Nothing was written by the deletion or by reading: the revisions and their pins are byte for byte
        // what they were.
        Assert.Equal(before, await RevisionsAsync(A));

        // And a later revision carries the item and its pin forward rather than dropping what it cannot resolve.
        var edited = await client.PatchAsJsonAsync(
            $"{root}/sections/{Id(lost)}", new { expectedConcurrencyToken = Token(read), body = "A cake that keeps for days." }, Ct);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var after = await ReadAsync(client, A, Id(draft));
        Assert.Equal(references, Sources(after));
        Assert.Equal(2, await PinCountAsync(A, after.GetProperty("latest").GetProperty("revisionNumber").GetInt32()));
    }

    // ---- ordering ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Items_are_reordered_by_reference_each_moving_whole_and_every_other_section_staying_put()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var (draft, references) = await IssueAsync(client, A, recipes: 3);
        var root = $"{DraftsIn(A)}/{Id(draft)}";
        var approved = await BodyOf(await ApproveAsync(client, A, draft, revisionNumber: 1));
        var first = Items(approved)[0];

        var written = await BodyOf(await client.PatchAsJsonAsync(
            $"{root}/sections/{Id(first)}", new { expectedConcurrencyToken = Token(approved), body = "The loaf, torn open." }, Ct));

        Guid[] order = [references[2], references[0], references[1]];
        var response = await client.PutAsJsonAsync(
            $"{root}/items/order", new { expectedConcurrencyToken = Token(written), sourceReferenceIds = order }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());

        var reordered = await BodyOf(response);
        var sections = Sections(reordered);

        // The greeting and the closing are where they were; the three places between them hold the items in
        // the order asked for.
        Assert.Equal(["Greeting", "Item", "Item", "Item", "Closing"], sections.Select(section => section.GetProperty("role").GetString()));
        Assert.Equal(order, Sources(reordered));

        // The item is the same section it was — id, heading, text — in a different place.
        var moved = Items(reordered)[1];
        Assert.Equal(Id(first), Id(moved));
        Assert.Equal(first.GetProperty("heading").GetString(), moved.GetProperty("heading").GetString());
        Assert.True(moved.GetProperty("isWritten").GetBoolean());

        // A new revision, a piece still being written, a valid plan, and every pin carried.
        Assert.Equal(3, reordered.GetProperty("latest").GetProperty("revisionNumber").GetInt32());
        Assert.Equal("Writing", reordered.GetProperty("status").GetString());
        Assert.True(reordered.GetProperty("planCheck").GetProperty("isValid").GetBoolean());
        Assert.Equal(3, await PinCountAsync(A, revisionNumber: 3));

        await InAsync(A, async services =>
        {
            var audit = await services.GetRequiredService<CreatorPantryDbContext>().AuditLogs.AsNoTracking()
                .SingleAsync(entry => entry.Action == ContentAuditActions.LongFormItemsReordered, Ct);

            // A revision number, and no reference or heading.
            Assert.Contains("revision 3", audit.Summary);
            Assert.DoesNotContain(references[2].ToString("D"), audit.Summary);

            return 0;
        });

        // The order already in force is not an edit.
        var again = await client.PutAsJsonAsync(
            $"{root}/items/order", new { expectedConcurrencyToken = Token(reordered), sourceReferenceIds = order }, Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(3, (await BodyOf(again)).GetProperty("latest").GetProperty("revisionNumber").GetInt32());
    }

    [Fact]
    public async Task An_order_that_does_not_name_each_item_exactly_once_is_refused_and_writes_nothing()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var (draft, references) = await IssueAsync(client, A, recipes: 2);
        var root = $"{DraftsIn(A)}/{Id(draft)}";

        Guid[][] orders =
        [
            [references[0]],
            [references[0], references[1], Guid.NewGuid()],
            [references[0], Guid.NewGuid()],
            [references[0], references[0]],
        ];

        foreach (var order in orders)
        {
            var response = await client.PutAsJsonAsync(
                $"{root}/items/order", new { expectedConcurrencyToken = Token(draft), sourceReferenceIds = order }, Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var problem = await BodyOf(response);
            Assert.Equal(ContentErrorCodes.LongFormItemOrderInvalid, Code(problem));
            Assert.True(problem.GetProperty("errors").TryGetProperty("sourceReferenceIds", out _));
        }

        var stale = await client.PutAsJsonAsync(
            $"{root}/items/order", new { expectedConcurrencyToken = SomebodyElsesToken, sourceReferenceIds = new[] { references[1], references[0] } }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(ContentErrorCodes.LongFormDraftStale, Code(await BodyOf(stale)));

        Assert.Equal(1, (await ReadAsync(client, A, Id(draft))).GetProperty("latest").GetProperty("revisionNumber").GetInt32());
    }

    [Fact]
    public async Task A_plan_edit_may_move_an_item_and_may_not_point_it_at_another_recipe()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var (draft, references) = await IssueAsync(client, A, recipes: 2);
        var sections = Sections(draft);

        object Submitted(JsonElement section, Guid? source) => new
        {
            id = Id(section),
            role = section.GetProperty("role").GetString(),
            heading = section.GetProperty("heading").GetString(),
            intent = section.GetProperty("intent").GetString(),
            sourceReferenceId = source,
        };

        // The two items swap the recipes they cover: each would keep its heading under the other's recipe.
        var repointed = await client.PatchAsJsonAsync(
            $"{DraftsIn(A)}/{Id(draft)}/plan",
            new
            {
                expectedConcurrencyToken = Token(draft),
                sections = new[]
                {
                    Submitted(sections[0], null),
                    Submitted(sections[1], references[1]),
                    Submitted(sections[2], references[0]),
                    Submitted(sections[3], null),
                },
            },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, repointed.StatusCode);

        var errors = (await BodyOf(repointed)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("sections[1].sourceReferenceId", out _));
        Assert.True(errors.TryGetProperty("sections[2].sourceReferenceId", out _));

        // The same two items in the other order, each still covering its own recipe, is an ordinary edit.
        var moved = await client.PatchAsJsonAsync(
            $"{DraftsIn(A)}/{Id(draft)}/plan",
            new
            {
                expectedConcurrencyToken = Token(draft),
                sections = new[]
                {
                    Submitted(sections[0], null),
                    Submitted(sections[2], references[1]),
                    Submitted(sections[1], references[0]),
                    Submitted(sections[3], null),
                },
            },
            Ct);

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        var saved = await BodyOf(moved);
        Assert.Equal([references[1], references[0]], Sources(saved));
        Assert.Equal([Id(sections[2]), Id(sections[1])], Items(saved).Select(Id));
        Assert.True(saved.GetProperty("planCheck").GetProperty("isValid").GetBoolean());
    }

    [Fact]
    public async Task Removing_an_item_takes_its_recipe_out_of_the_issue_and_leaves_the_revision_before_as_it_was()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var (draft, references) = await IssueAsync(client, A, recipes: 2);
        var kept = Sections(draft).Where(section => Source(section) != references[1]);

        var response = await client.PatchAsJsonAsync(
            $"{DraftsIn(A)}/{Id(draft)}/plan",
            new
            {
                expectedConcurrencyToken = Token(draft),
                sections = kept.Select(section => new
                {
                    id = Id(section),
                    role = section.GetProperty("role").GetString(),
                    heading = section.GetProperty("heading").GetString(),
                    intent = section.GetProperty("intent").GetString(),
                    sourceReferenceId = Source(section),
                }),
            },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // One item for the one recipe the issue now covers: a valid plan, not one with a recipe left over.
        var saved = await BodyOf(response);
        Assert.Equal([references[0]], Sources(saved));
        Assert.True(saved.GetProperty("planCheck").GetProperty("isValid").GetBoolean());
        Assert.Equal(1, await PinCountAsync(A, revisionNumber: 2));
        Assert.Equal(2, await PinCountAsync(A, revisionNumber: 1));
    }

    // ---- the subject line and the note -------------------------------------------------------------------

    [Fact]
    public async Task The_subject_and_note_are_saved_as_a_revision_and_survive_every_other_kind_of_write()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var (draft, _) = await IssueAsync(client, A, recipes: 1);
        var root = $"{DraftsIn(A)}/{Id(draft)}";
        var approved = await BodyOf(await ApproveAsync(client, A, draft, revisionNumber: 1));

        const string subject = "Three loaves for a cold week";
        const string note = "It rained all week, so I baked.\n\nHere is what came of it.";

        var response = await client.PutAsJsonAsync(
            $"{root}/framing", new { expectedConcurrencyToken = Token(approved), subject, note }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());

        var framed = await BodyOf(response);
        Assert.Equal(subject, framed.GetProperty("framing").GetProperty("subject").GetString());
        Assert.Equal(note, framed.GetProperty("framing").GetProperty("note").GetString());
        Assert.Equal(2, framed.GetProperty("latest").GetProperty("revisionNumber").GetInt32());
        Assert.Equal("CreatorEdit", framed.GetProperty("latest").GetProperty("source").GetString());

        // Not a change to the plan, so the approval stands; and revision 1 is the revision it was.
        Assert.Equal("Outlined", framed.GetProperty("status").GetString());
        Assert.Equal(1, await PinCountAsync(A, revisionNumber: 2));

        await InAsync(A, async services =>
        {
            var db = services.GetRequiredService<CreatorPantryDbContext>();
            var first = await db.LongFormRevisions.AsNoTracking().SingleAsync(revision => revision.RevisionNumber == 1, Ct);
            Assert.DoesNotContain("framing", first.Content);

            // The creator's words are in the revision and nowhere in the audit trail.
            var audit = await db.AuditLogs.AsNoTracking().SingleAsync(entry => entry.Action == ContentAuditActions.LongFormFramingEdited, Ct);
            Assert.DoesNotContain("loaves", audit.Summary);

            return 0;
        });

        // The same framing again is not an edit.
        var again = await BodyOf(await client.PutAsJsonAsync(
            $"{root}/framing", new { expectedConcurrencyToken = Token(framed), subject, note }, Ct));
        Assert.Equal(2, again.GetProperty("latest").GetProperty("revisionNumber").GetInt32());

        // A section written afterwards, and the document read whole, both still carry it.
        var item = Items(framed)[0];
        var written = await client.PatchAsJsonAsync(
            $"{root}/sections/{Id(item)}", new { expectedConcurrencyToken = Token(framed), body = "The loaf." }, Ct);
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);

        var document = (await BodyOf(await client.GetAsync($"{root}/document", Ct))).GetProperty("document");
        Assert.Equal(subject, document.GetProperty("framing").GetProperty("subject").GetString());
        Assert.DoesNotContain(subject, document.GetProperty("markdown").GetString());

        // Blank clears: an issue with neither part has no framing.
        var cleared = await BodyOf(await client.PutAsJsonAsync(
            $"{root}/framing", new { expectedConcurrencyToken = document.GetProperty("concurrencyToken").GetString(), subject = " ", note = (string?)null }, Ct));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("framing").ValueKind);
    }

    [Fact]
    public async Task A_plan_edit_keeps_the_subject_and_note()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var (draft, references) = await IssueAsync(client, A, recipes: 1);
        var framed = await BodyOf(await client.PutAsJsonAsync(
            $"{DraftsIn(A)}/{Id(draft)}/framing", new { expectedConcurrencyToken = Token(draft), subject = "One loaf" }, Ct));

        var edited = await client.PatchAsJsonAsync(
            $"{DraftsIn(A)}/{Id(draft)}/plan",
            new
            {
                expectedConcurrencyToken = Token(framed),
                sections = new[] { new { role = "Item", heading = "The only loaf", intent = "Introduce it.", sourceReferenceId = references[0] } },
            },
            Ct);

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal("One loaf", (await BodyOf(edited)).GetProperty("framing").GetProperty("subject").GetString());
    }

    [Theory]
    [InlineData("subject", "Two\nlines")]
    [InlineData("subject", 151)]
    [InlineData("note", 601)]
    public async Task A_subject_or_note_outside_its_limits_is_refused_and_writes_nothing(string field, object value)
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var (draft, _) = await IssueAsync(client, A, recipes: 1);
        var text = value is int length ? new string('a', length) : (string)value;

        var response = await client.PutAsJsonAsync(
            $"{DraftsIn(A)}/{Id(draft)}/framing",
            new Dictionary<string, string?> { ["expectedConcurrencyToken"] = Token(draft), [field] = text },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await BodyOf(response);
        Assert.Equal(ContentErrorCodes.LongFormFramingInvalid, Code(problem));
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Equal(1, (await ReadAsync(client, A, Id(draft))).GetProperty("latest").GetProperty("revisionNumber").GetInt32());
    }

    [Fact]
    public async Task A_subject_and_a_note_at_their_limits_are_kept()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var (draft, _) = await IssueAsync(client, A, recipes: 1);

        var response = await client.PutAsJsonAsync(
            $"{DraftsIn(A)}/{Id(draft)}/framing",
            new
            {
                expectedConcurrencyToken = Token(draft),
                subject = new string('a', LongFormPolicy.SubjectMaxCharacters),
                note = new string('a', LongFormPolicy.NoteMaxCharacters),
            },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- who, and what kind of piece ---------------------------------------------------------------------

    [Fact]
    public async Task A_blog_article_has_no_items_to_order_and_no_framing()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var draft = await DraftWithPlanAsync(client, A);

        HttpResponseMessage[] attempts =
        [
            await client.PutAsJsonAsync($"{DraftsIn(A)}/{Id(draft)}/items/order", new { expectedConcurrencyToken = Token(draft), sourceReferenceIds = Array.Empty<Guid>() }, Ct),
            await client.PutAsJsonAsync($"{DraftsIn(A)}/{Id(draft)}/framing", new { expectedConcurrencyToken = Token(draft), subject = "A subject" }, Ct),
        ];

        foreach (var response in attempts)
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(ContentErrorCodes.LongFormNotAnIssue, Code(await BodyOf(response)));
        }

        var read = await ReadAsync(client, A, Id(draft));
        Assert.Equal(1, read.GetProperty("latest").GetProperty("revisionNumber").GetInt32());
        Assert.Empty(read.GetProperty("unresolvedItems").EnumerateArray());
    }

    [Fact]
    public async Task A_viewer_may_not_order_or_frame_and_another_workspace_cannot_reach_the_issue_at_all()
    {
        using var owner = await SignInAsync(B.OwnerEmail);
        using var viewer = await SignInAsync(B.MemberEmail);
        using var outsider = await SignInAsync(A.OwnerEmail);
        var (draft, references) = await IssueAsync(owner, B, recipes: 2);
        var order = new { expectedConcurrencyToken = Token(draft), sourceReferenceIds = new[] { references[1], references[0] } };
        var framing = new { expectedConcurrencyToken = Token(draft), subject = "Planted" };

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync($"{DraftsIn(B)}/{Id(draft)}/items/order", order, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync($"{DraftsIn(B)}/{Id(draft)}/framing", framing, Ct)).StatusCode);

        // A, on A's own routes, naming B's draft and B's references by their ids.
        foreach (var response in new[]
        {
            await outsider.PutAsJsonAsync($"{DraftsIn(A)}/{Id(draft)}/items/order", order, Ct),
            await outsider.PutAsJsonAsync($"{DraftsIn(A)}/{Id(draft)}/framing", framing, Ct),
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(ContentErrorCodes.LongFormDraftNotFound, Code(await BodyOf(response)));
        }

        // B's references do not make an order valid for an issue of A's own: they are not A's to name.
        var (mine, _) = await IssueAsync(outsider, A, recipes: 2);
        var borrowed = await outsider.PutAsJsonAsync(
            $"{DraftsIn(A)}/{Id(mine)}/items/order", new { expectedConcurrencyToken = Token(mine), sourceReferenceIds = references }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, borrowed.StatusCode);
        Assert.Equal(ContentErrorCodes.LongFormItemOrderInvalid, Code(await BodyOf(borrowed)));

        // B's issue is exactly as B left it, and A's own items resolve against A's own context only.
        var untouched = await ReadAsync(owner, B, Id(draft));
        Assert.Equal(1, untouched.GetProperty("latest").GetProperty("revisionNumber").GetInt32());
        Assert.Equal(references, Sources(untouched));
        Assert.Equal(JsonValueKind.Null, untouched.GetProperty("framing").ValueKind);
        Assert.Empty((await ReadAsync(outsider, A, Id(mine))).GetProperty("unresolvedItems").EnumerateArray());
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static List<JsonElement> Sections(JsonElement draft) =>
        [.. draft.GetProperty("latest").GetProperty("sections").EnumerateArray()];

    private static List<JsonElement> Items(JsonElement draft) =>
        [.. Sections(draft).Where(section => section.GetProperty("role").GetString() == "Item")];

    /// <summary>The reference each item covers, in plan order.</summary>
    private static Guid[] Sources(JsonElement draft) => [.. Items(draft).Select(item => Source(item)!.Value)];

    private static Guid? Source(JsonElement section) =>
        section.GetProperty("sourceReferenceId").ValueKind is JsonValueKind.Null ? null : section.GetProperty("sourceReferenceId").GetGuid();

    /// <summary>Every revision and pin in the workspace as stored, to show that nothing rewrote one.</summary>
    private Task<string> RevisionsAsync(SeededWorkspace workspace) => InAsync(workspace, async services =>
    {
        var db = services.GetRequiredService<CreatorPantryDbContext>();
        var revisions = await db.LongFormRevisions.AsNoTracking().OrderBy(revision => revision.RevisionNumber)
            .Select(revision => $"{revision.Id}|{revision.RevisionNumber}|{revision.Content}").ToListAsync(Ct);
        var pins = await db.LongFormRevisionRecipePins.AsNoTracking().OrderBy(pin => pin.Id)
            .Select(pin => $"{pin.LongFormRevisionId}|{pin.SortOrder}|{pin.CreativeContextReferenceId}|{pin.RecipeVersionId}").ToListAsync(Ct);

        return string.Join('\n', revisions.Concat(pins));
    });

    private Task<int> PinCountAsync(SeededWorkspace workspace, int revisionNumber) => InAsync(workspace, async services =>
    {
        var db = services.GetRequiredService<CreatorPantryDbContext>();
        var revision = await db.LongFormRevisions.AsNoTracking().SingleAsync(candidate => candidate.RevisionNumber == revisionNumber, Ct);

        return await db.LongFormRevisionRecipePins.CountAsync(pin => pin.LongFormRevisionId == revision.Id, Ct);
    });

    /// <summary>
    /// A newsletter issue whose creative context names <paramref name="recipes"/> recipes, with a plan landed
    /// the way the worker lands one and pinned to each of them.
    /// </summary>
    /// <param name="plan">
    /// The sections to land, given the references in order. Left out, the plan is a greeting, one item for each
    /// reference, and a closing.
    /// </param>
    private async Task<(JsonElement Draft, Guid[] References)> IssueAsync(
        GatewayClient client,
        SeededWorkspace workspace,
        int recipes,
        Func<Guid[], LongFormGeneratedSectionInput[]>? plan = null)
    {
        var started = await StartAsync(client, workspace, key: $"key-issue-{Guid.NewGuid():N}", kind: "NewsletterIssue");
        var draft = started.GetProperty("draft");
        var requestId = started.GetProperty("request").GetProperty("aiProposalRequestId").GetGuid();
        var contextId = draft.GetProperty("creativeContextId").GetGuid();

        var references = await InAsync(workspace, async services =>
        {
            var db = services.GetRequiredService<CreatorPantryDbContext>();
            var now = DateTimeOffset.UtcNow;
            var pins = new List<LongFormRecipePinInput>();

            for (var index = 0; index < recipes; index++)
            {
                var (recipeId, versionId) = await CreativeContextSeeds.RecipeAsync(db, now, Ct);
                var reference = new CreativeContextReference
                {
                    Id = Guid.NewGuid(),
                    CreativeContextId = contextId,
                    Kind = CreativeContextReferenceKind.Recipe,
                    SortOrder = index,
                    RecipeId = recipeId,
                    AddedAt = now,
                };

                db.CreativeContextReferences.Add(reference);
                pins.Add(new LongFormRecipePinInput(reference.Id, recipeId, versionId));
            }

            var proposal = new AiProposal
            {
                Id = Guid.NewGuid(),
                AiOperationId = requestId,
                OutputSchemaVersion = "content.long-form-outline.v1",
                PromptTemplateId = AiTaskCatalog.LongFormOutline,
                PromptTemplateVersion = "1.0.0",
                PromptTemplateBodyChecksum = "sha256:" + new string('b', 64),
                ProviderName = "test-provider",
                ModelName = "test-model",
                CreatedAt = now,
            };

            db.AiProposals.Add(proposal);
            await db.SaveChangesAsync(Ct);

            var ids = pins.Select(pin => pin.CreativeContextReferenceId).ToArray();

            var landed = await services.GetRequiredService<ILongFormDraftFacade>().RecordGeneratedPlanAsync(
                new LongFormGeneratedPlanInput(
                    Id(draft),
                    contextId,
                    LongFormKind.NewsletterIssue,
                    proposal.Id,
                    proposal.PromptTemplateId,
                    proposal.PromptTemplateVersion,
                    proposal.PromptTemplateBodyChecksum,
                    BrandStyleGuideVersionId: null,
                    CreativeContextVersion: null,
                    plan?.Invoke(ids) ??
                    [
                        new(LongFormSectionRole.Greeting, "Hello again", "Say hello.", null),
                        .. ids.Select((id, index) => new LongFormGeneratedSectionInput(
                            LongFormSectionRole.Item, $"Recipe {index + 1}", $"Introduce recipe {index + 1}.", id)),
                        new(LongFormSectionRole.Closing, "Until next week", "Sign the issue off.", null),
                    ],
                    pins),
                Ct);

            Assert.True(landed.Succeeded, landed.Error?.Code);

            return ids;
        });

        return (await ReadAsync(client, workspace, Id(draft)), references);
    }
}
