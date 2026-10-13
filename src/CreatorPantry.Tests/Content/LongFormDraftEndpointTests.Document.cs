using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// LF.3.4's two routes through the gateway: status codes, shapes, roles and isolation. What an acceptance does
/// to a draft, and what the assembler writes, are the seam's and the assembler's own tests.
/// </summary>
public sealed partial class LongFormDraftEndpointTests
{
    [Fact]
    public async Task The_document_reads_whole_and_is_accepted_once_what_is_wrong_with_it_is_confirmed()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var draft = await ApprovedDraftAsync(client, A);
        var root = $"{DraftsIn(A)}/{Id(draft)}";
        var first = Id(draft.GetProperty("latest").GetProperty("sections")[0]);

        // One section written by the creator; the other two are not.
        var token = Token((await BodyOf(await client.GetAsync($"{root}/sections", Ct))).GetProperty("draft"));
        await client.PatchAsJsonAsync($"{root}/sections/{first}", new { expectedConcurrencyToken = token, body = "Some breads ask for an afternoon.\r\n\r\n" }, Ct);

        var read = await client.GetAsync($"{root}/document", Ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Contains("no-store", read.Headers.CacheControl!.ToString());

        var review = await BodyOf(read);
        var document = review.GetProperty("document");

        // The creator's heading, one blank line, their text with one kind of line ending and one final newline.
        Assert.Equal(
            "## The loaf for people who do not bake\n\nSome breads ask for an afternoon.\n",
            document.GetProperty("markdown").GetString());
        Assert.Equal(2, document.GetProperty("revisionNumber").GetInt32());
        Assert.Equal("Writing", document.GetProperty("status").GetString());
        Assert.Equal("NotAssessed", document.GetProperty("wordTarget").GetString());
        Assert.Equal(3, document.GetProperty("sections").GetArrayLength());
        Assert.Equal("CreatorEdit", document.GetProperty("sections")[0].GetProperty("writtenBy").GetString());
        Assert.Equal(JsonValueKind.Null, document.GetProperty("sections")[1].GetProperty("body").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.GetProperty("acceptedRevisionNumber").ValueKind);
        Assert.Empty(review.GetProperty("warnings").EnumerateArray());
        Assert.Empty(review.GetProperty("requests").EnumerateArray());

        var issue = Assert.Single(document.GetProperty("needsConfirmation").EnumerateArray());
        Assert.Equal("UnwrittenSections", issue.GetProperty("issue").GetString());
        Assert.Equal(2, issue.GetProperty("sectionIds").GetArrayLength());

        // Unconfirmed: nothing is accepted, and the refusal says what is wrong and about which sections.
        var acceptance = new { expectedConcurrencyToken = document.GetProperty("concurrencyToken").GetString(), revisionNumber = 2 };
        var refused = await client.PostAsJsonAsync($"{root}/acceptance", acceptance, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        Assert.Contains("no-store", refused.Headers.CacheControl!.ToString());

        var problem = await BodyOf(refused);
        Assert.Equal(ContentErrorCodes.LongFormAcceptanceConfirmationRequired, Code(problem));
        Assert.True(problem.GetProperty("errors").TryGetProperty("confirmed", out _));
        var named = Assert.Single(problem.GetProperty("issues").EnumerateArray());
        Assert.Equal(["issue", "sectionIds"], named.EnumerateObject().Select(property => property.Name));
        Assert.Equal("UnwrittenSections", named.GetProperty("issue").GetString());
        Assert.Equal(
            document.GetProperty("sections").EnumerateArray().Skip(1).Select(Id),
            named.GetProperty("sectionIds").EnumerateArray().Select(id => id.GetGuid()));

        // Composed against a revision the draft has moved on from: a conflict, and nothing accepted.
        var stale = await client.PostAsJsonAsync(
            $"{root}/acceptance",
            new { acceptance.expectedConcurrencyToken, revisionNumber = 1, confirmed = new[] { "UnwrittenSections" } },
            Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(ContentErrorCodes.LongFormDraftStale, Code(await BodyOf(stale)));

        // Confirmed by name: accepted, and the document that comes back is the one that was read.
        var confirmed = new { acceptance.expectedConcurrencyToken, acceptance.revisionNumber, confirmed = new[] { "UnwrittenSections" } };
        var accepted = await client.PostAsJsonAsync($"{root}/acceptance", confirmed, Ct);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Contains("no-store", accepted.Headers.CacheControl!.ToString());

        var article = await BodyOf(accepted);
        Assert.Equal("Accepted", article.GetProperty("status").GetString());
        Assert.Equal(2, article.GetProperty("acceptedRevisionNumber").GetInt32());
        Assert.Equal(document.GetProperty("markdown").GetString(), article.GetProperty("markdown").GetString());
        Assert.NotEqual(JsonValueKind.Null, article.GetProperty("acceptedAt").ValueKind);

        // Again: the same acceptance, and still one accepted revision and no new one.
        var again = await client.PostAsJsonAsync($"{root}/acceptance", confirmed, Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(2, await InAsync(A, services => services.GetRequiredService<CreatorPantryDbContext>().LongFormRevisions.CountAsync(Ct)));

        // And nothing more is written to it.
        var late = await client.PatchAsJsonAsync(
            $"{root}/sections/{first}", new { expectedConcurrencyToken = Token(article), body = "Changed my mind." }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal(ContentErrorCodes.LongFormDraftAccepted, Code(await BodyOf(late)));
    }

    [Theory]
    [InlineData("{\"revisionNumber\":1}", "expectedConcurrencyToken")]
    [InlineData("{\"expectedConcurrencyToken\":\"AAAAAAAAB9E=\",\"revisionNumber\":0}", "revisionNumber")]
    public async Task An_acceptance_that_names_no_token_or_no_revision_is_a_400_naming_the_field(string json, string field)
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var draft = await ApprovedDraftAsync(client, A);

        var response = await client.PostAsJsonAsync(
            $"{DraftsIn(A)}/{Id(draft)}/acceptance", JsonSerializer.Deserialize<JsonElement>(json), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await BodyOf(response);
        Assert.Equal(ContentErrorCodes.LongFormAcceptanceInvalid, Code(problem));
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task A_piece_with_nothing_written_is_a_422_and_an_unapproved_one_a_409()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var unapproved = await DraftWithPlanAsync(client, A);

        var early = await client.PostAsJsonAsync(
            $"{DraftsIn(A)}/{Id(unapproved)}/acceptance",
            new { expectedConcurrencyToken = Token(unapproved), revisionNumber = 1, confirmed = new[] { "UnwrittenSections" } },
            Ct);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal(ContentErrorCodes.LongFormPlanNotApproved, Code(await BodyOf(early)));

        var approved = await BodyOf(await ApproveAsync(client, A, unapproved, revisionNumber: 1));
        var empty = await client.PostAsJsonAsync(
            $"{DraftsIn(A)}/{Id(approved)}/acceptance",
            new { expectedConcurrencyToken = Token(approved), revisionNumber = 1, confirmed = new[] { "UnwrittenSections" } },
            Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Equal(ContentErrorCodes.LongFormAcceptanceNothingWritten, Code(await BodyOf(empty)));
    }

    [Fact]
    public async Task A_viewer_may_read_the_document_and_may_not_accept_it()
    {
        using var owner = await SignInAsync(B.OwnerEmail);
        using var viewer = await SignInAsync(B.MemberEmail);
        var draft = await ApprovedDraftAsync(owner, B);
        var root = $"{DraftsIn(B)}/{Id(draft)}";

        Assert.Equal(WorkspaceRole.Viewer, B.MemberRole);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"{root}/document", Ct)).StatusCode);

        var attempt = await viewer.PostAsJsonAsync(
            $"{root}/acceptance",
            new { expectedConcurrencyToken = Token(draft), revisionNumber = 1, confirmed = new[] { "UnwrittenSections" } },
            Ct);

        Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
        Assert.Equal("Outlined", (await BodyOf(await owner.GetAsync($"{root}/document", Ct))).GetProperty("document").GetProperty("status").GetString());
    }

    [Fact]
    public async Task One_workspace_can_neither_read_nor_accept_anothers_document_by_its_id()
    {
        using var ours = await SignInAsync(A.OwnerEmail);
        using var theirs = await SignInAsync(B.OwnerEmail);
        await ApprovedDraftAsync(ours, A);
        var draft = await ApprovedDraftAsync(theirs, B);
        var root = $"{DraftsIn(A)}/{Id(draft)}";

        // A, signed in to A, naming B's draft on A's own routes.
        foreach (var response in new[]
        {
            await ours.GetAsync($"{root}/document", Ct),
            await ours.PostAsJsonAsync(
                $"{root}/acceptance",
                new { expectedConcurrencyToken = Token(draft), revisionNumber = 1, confirmed = new[] { "UnwrittenSections" } },
                Ct),
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(ContentErrorCodes.LongFormDraftNotFound, Code(await BodyOf(response)));
        }

        // A on B's routes is not a member of B at all: the workspace itself is what is not found.
        Assert.Equal(HttpStatusCode.NotFound, (await ours.GetAsync($"{DraftsIn(B)}/{Id(draft)}/document", Ct)).StatusCode);

        // B's draft is exactly as B left it.
        var untouched = await BodyOf(await theirs.GetAsync($"{DraftsIn(B)}/{Id(draft)}/document", Ct));
        Assert.Equal("Outlined", untouched.GetProperty("document").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, untouched.GetProperty("document").GetProperty("acceptedRevisionNumber").ValueKind);
    }
}
