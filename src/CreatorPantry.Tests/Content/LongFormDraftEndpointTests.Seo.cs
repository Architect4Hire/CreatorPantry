using System.Net;
using System.Net.Http.Json;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Tests.Tenancy;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// LF.5.3's two routes through the gateway: an SEO package asked for about an accepted long-form draft. Status
/// codes, roles and isolation; what the package says is the handler's and the evaluation set's.
/// </summary>
public sealed partial class LongFormDraftEndpointTests
{
    private static string SeoRequestsOf(SeededWorkspace workspace, Guid draftId) =>
        $"{DraftsIn(workspace)}/{draftId}/seo-package-requests";

    [Fact]
    public async Task An_seo_package_is_queued_for_an_accepted_piece_and_its_request_can_be_followed()
    {
        Tasks.Enabled.Add(AiTaskCatalog.SeoPackage);
        using var client = await SignInAsync(A.OwnerEmail);
        var draft = await AcceptedDraftAsync(client, A);

        var asked = await client.PostAsJsonAsync(SeoRequestsOf(A, Id(draft)), new { revisionNumber = 2 }, "key-seo", Ct);

        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        var request = await BodyOf(asked);
        var requestId = request.GetProperty("aiProposalRequestId").GetGuid();

        Assert.Equal("SeoPackage", request.GetProperty("taskType").GetString());
        Assert.Equal("Requested", request.GetProperty("status").GetString());
        Assert.EndsWith($"/long-form-drafts/{Id(draft)}/seo-package-requests/{requestId}", asked.Headers.Location!.ToString(), StringComparison.Ordinal);

        // Asked again with the same key: the same request, and nothing bought twice.
        var again = await client.PostAsJsonAsync(SeoRequestsOf(A, Id(draft)), new { revisionNumber = 2 }, "key-seo", Ct);
        Assert.Equal(requestId, (await BodyOf(again)).GetProperty("aiProposalRequestId").GetGuid());

        var followed = await client.GetAsync($"{SeoRequestsOf(A, Id(draft))}/{requestId}", Ct);
        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);
        Assert.Equal(requestId, (await BodyOf(followed)).GetProperty("aiProposalRequestId").GetGuid());

        // It is not a recipe's request, and is not found as one.
        var asRecipe = await client.GetAsync(
            $"/api/v1/workspaces/{A.Slug}/recipes/{Guid.NewGuid()}/seo-package-requests/{requestId}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, asRecipe.StatusCode);
    }

    [Fact]
    public async Task A_piece_that_is_not_accepted_a_wrong_revision_and_alt_text_are_each_refused_by_name()
    {
        Tasks.Enabled.Add(AiTaskCatalog.SeoPackage);
        using var client = await SignInAsync(A.OwnerEmail);

        // In the other workspace only so the two drafts do not share a start key; the rule is the same.
        using var other = await SignInAsync(B.OwnerEmail);
        var unaccepted = await ApprovedDraftAsync(other, B);
        var early = await other.PostAsJsonAsync(SeoRequestsOf(B, Id(unaccepted)), new { revisionNumber = 1 }, "key-early", Ct);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal(AiSeoPackageRequestErrors.DraftNotAccepted, Code(await BodyOf(early)));

        var draft = await AcceptedDraftAsync(client, A);

        var wrong = await client.PostAsJsonAsync(SeoRequestsOf(A, Id(draft)), new { revisionNumber = 1 }, "key-wrong", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(AiSeoPackageRequestErrors.DraftRevisionInvalid, Code(await BodyOf(wrong)));

        // No image the package has been given a fact about, so no alt text to ask for.
        var alt = await client.PostAsJsonAsync(
            SeoRequestsOf(A, Id(draft)), new { revisionNumber = 2, sections = new[] { "seoTitle", "altText" } }, "key-alt", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, alt.StatusCode);
        Assert.Equal(AiSeoPackageRequestErrors.RequestInvalid, Code(await BodyOf(alt)));

        var unknown = await client.PostAsJsonAsync(SeoRequestsOf(A, Guid.NewGuid()), new { revisionNumber = 2 }, "key-unknown", Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(AiSeoPackageRequestErrors.DraftNotFound, Code(await BodyOf(unknown)));
    }

    [Fact]
    public async Task A_viewer_may_follow_an_seo_request_and_may_not_make_one()
    {
        Tasks.Enabled.Add(AiTaskCatalog.SeoPackage);
        using var owner = await SignInAsync(B.OwnerEmail);
        using var viewer = await SignInAsync(B.MemberEmail);
        var draft = await AcceptedDraftAsync(owner, B);

        Assert.Equal(WorkspaceRole.Viewer, B.MemberRole);

        var refused = await viewer.PostAsJsonAsync(SeoRequestsOf(B, Id(draft)), new { revisionNumber = 2 }, "key-viewer", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var asked = await BodyOf(await owner.PostAsJsonAsync(SeoRequestsOf(B, Id(draft)), new { revisionNumber = 2 }, "key-owner", Ct));
        var requestId = asked.GetProperty("aiProposalRequestId").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"{SeoRequestsOf(B, Id(draft))}/{requestId}", Ct)).StatusCode);
    }

    [Fact]
    public async Task One_workspace_can_neither_ask_for_nor_follow_an_seo_package_about_anothers_piece()
    {
        Tasks.Enabled.Add(AiTaskCatalog.SeoPackage);
        using var ours = await SignInAsync(A.OwnerEmail);
        using var theirs = await SignInAsync(B.OwnerEmail);
        var mine = await AcceptedDraftAsync(ours, A);
        var draft = await AcceptedDraftAsync(theirs, B);

        var asked = await BodyOf(await theirs.PostAsJsonAsync(SeoRequestsOf(B, Id(draft)), new { revisionNumber = 2 }, "key-b", Ct));
        var requestId = asked.GetProperty("aiProposalRequestId").GetGuid();

        // A, signed in to A, naming B's draft on A's own routes: the draft is not there, and nothing is queued.
        var crossed = await ours.PostAsJsonAsync(SeoRequestsOf(A, Id(draft)), new { revisionNumber = 2 }, "key-a", Ct);
        Assert.Equal(HttpStatusCode.NotFound, crossed.StatusCode);
        Assert.Equal(AiSeoPackageRequestErrors.DraftNotFound, Code(await BodyOf(crossed)));

        // B's request is not found under B's draft id on A's routes, nor under one of A's own drafts.
        foreach (var under in new[] { Id(draft), Id(mine) })
        {
            var followed = await ours.GetAsync($"{SeoRequestsOf(A, under)}/{requestId}", Ct);
            Assert.Equal(HttpStatusCode.NotFound, followed.StatusCode);
            Assert.Equal(AiSeoPackageRequestErrors.RequestNotFound, Code(await BodyOf(followed)));
        }

        // A on B's routes is not a member of B at all: the workspace itself is what is not found.
        Assert.Equal(HttpStatusCode.NotFound, (await ours.GetAsync($"{SeoRequestsOf(B, Id(draft))}/{requestId}", Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await ours.PostAsJsonAsync(SeoRequestsOf(B, Id(draft)), new { revisionNumber = 2 }, "key-a2", Ct)).StatusCode);

        // And B still follows its own.
        Assert.Equal(HttpStatusCode.OK, (await theirs.GetAsync($"{SeoRequestsOf(B, Id(draft))}/{requestId}", Ct)).StatusCode);
    }
}
