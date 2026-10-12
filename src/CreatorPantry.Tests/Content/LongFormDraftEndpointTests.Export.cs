using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// LF.5.2's two routes through the gateway: what a reader receives, the download headers, the error contract,
/// and what another workspace can never receive. What the document says is <c>LongFormExporterTests</c>'.
/// </summary>
public sealed partial class LongFormDraftEndpointTests
{
    private const string AcceptedBody = "Some breads ask for an afternoon.";

    private static string ExportOf(SeededWorkspace workspace, Guid draftId, string format) =>
        $"{DraftsIn(workspace)}/{draftId}/exports/{format}";

    [GeneratedRegex(@"^attachment; filename=[a-z0-9]+(-[a-z0-9]+)*-r\d+\.(md|html)$")]
    private static partial Regex SafeExportDisposition();

    [Fact]
    public async Task An_accepted_draft_is_downloaded_as_markdown_and_as_html_with_the_headers_of_a_private_file()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var draft = await AcceptedDraftAsync(client, A);

        var markdown = await client.GetAsync(ExportOf(A, Id(draft), "markdown"), Ct);
        var html = await client.GetAsync(ExportOf(A, Id(draft), "html"), Ct);

        Assert.Equal(HttpStatusCode.OK, markdown.StatusCode);
        Assert.Equal(HttpStatusCode.OK, html.StatusCode);

        Assert.Equal("text/markdown; charset=utf-8", markdown.Content.Headers.ContentType!.ToString());
        Assert.Equal("text/html; charset=utf-8", html.Content.Headers.ContentType!.ToString());
        Assert.Equal("attachment; filename=soda-bread-autumn-r2.md", markdown.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal("attachment; filename=soda-bread-autumn-r2.html", html.Content.Headers.ContentDisposition!.ToString());

        foreach (var response in new[] { markdown, html })
        {
            // Private, and asked for again before it is reused: never kept by anything between here and the reader.
            Assert.Equal("no-cache, private", response.Headers.CacheControl!.ToString());
            Assert.Contains("Cookie", response.Headers.Vary);
            Assert.Equal(["nosniff"], response.Headers.GetValues("X-Content-Type-Options"));
            Assert.NotNull(response.Headers.ETag);

            // Two of the plan's three sections are unwritten, and the acceptance confirmed as much.
            Assert.Equal(
                [LongFormExportCodes.SectionsUnwritten],
                response.Headers.GetValues("Cp-Export-Warnings"));
        }

        // The page is a creator's content served from the app's own origin. The edge's policy is what makes a
        // browser that renders it anyway run, load and submit nothing — so it must be on this response too.
        var policy = Assert.Single(html.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("form-action 'none'", policy, StringComparison.Ordinal);

        Assert.Equal(
            "# Soda bread, autumn\n\n## The loaf for people who do not bake\n\n" + AcceptedBody + "\n\n---\n\n"
            + "Exported from CreatorPantry · Blog article · Revision 2\n",
            await markdown.Content.ReadAsStringAsync(Ct));

        var page = await html.Content.ReadAsStringAsync(Ct);
        Assert.StartsWith("<!DOCTYPE html>\n", page, StringComparison.Ordinal);
        Assert.Contains(
            "<h1>Soda bread, autumn</h1>\n<section>\n<h2>The loaf for people who do not bake</h2>\n<p>" + AcceptedBody + "</p>\n</section>\n",
            page,
            StringComparison.Ordinal);

        // No id of anything, and no word of where anything is kept.
        foreach (var body in new[] { await markdown.Content.ReadAsStringAsync(Ct), page })
        {
            Assert.DoesNotContain(Id(draft).ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(A.Id.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(A.Slug, body, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("markdown")]
    [InlineData("html")]
    public async Task An_export_that_has_not_changed_answers_304_to_its_own_validator(string format)
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var draft = await AcceptedDraftAsync(client, A);

        var first = await client.GetAsync(ExportOf(A, Id(draft), format), Ct);
        var etag = first.Headers.ETag!.ToString();

        foreach (var validator in new[] { etag, "W/" + etag, "*" })
        {
            var again = await client.GetAsync(
                ExportOf(A, Id(draft), format), new Dictionary<string, string> { ["If-None-Match"] = validator }, Ct);

            Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
            Assert.Equal(etag, again.Headers.ETag!.ToString());
            Assert.Empty(await again.Content.ReadAsStringAsync(Ct));
        }

        var stale = await client.GetAsync(
            ExportOf(A, Id(draft), format),
            new Dictionary<string, string> { ["If-None-Match"] = "\"0123456789abcdef0123456789abcdef\"" },
            Ct);
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
    }

    [Theory]
    [InlineData("markdown")]
    [InlineData("html")]
    public async Task A_draft_that_has_not_been_accepted_is_a_409_that_nothing_may_keep(string format)
    {
        using var client = await SignInAsync(A.OwnerEmail);

        // Planned, approved and partly written — everything but accepted.
        var draft = await ApprovedDraftAsync(client, A);
        await WriteFirstSectionAsync(client, A, draft);

        var response = await client.GetAsync(ExportOf(A, Id(draft), format), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.Null(response.Headers.ETag);

        var problem = await BodyOf(response);
        Assert.Equal(ContentErrorCodes.LongFormExportNotAccepted, Code(problem));

        // Not a word of the piece in the refusal.
        Assert.DoesNotContain(AcceptedBody, problem.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Crème brûlée & \"friends\" — for two", "creme-brulee-friends-for-two")]
    [InlineData("../../etc/passwd", "etc-passwd")]
    [InlineData("a\\b/c:d*e?f\"g<h>i|j", "a-b-c-d-e-f-g-h-i-j")]
    [InlineData("Line one\r\nContent-Type: text/plain", "line-one-content-type-text-plain")]
    [InlineData("aux", "article-aux")]
    [InlineData("🍞🍞🍞", "article")]
    [InlineData(null, "article")]
    public async Task The_file_is_named_from_the_creators_title_and_from_nothing_else(string? title, string slug)
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var draft = await AcceptedDraftAsync(client, A);

        await InAsync(A, async services =>
        {
            var db = services.GetRequiredService<CreatorPantryDbContext>();
            var contextId = draft.GetProperty("creativeContextId").GetGuid();
            var context = await db.CreativeContexts.SingleAsync(candidate => candidate.Id == contextId, Ct);
            context.WorkingTitle = title;

            return await db.SaveChangesAsync(Ct);
        });

        foreach (var (format, extension) in new[] { ("markdown", "md"), ("html", "html") })
        {
            var response = await client.GetAsync(ExportOf(A, Id(draft), format), Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Read raw: the parsed header would hide a quote, an encoded form or a second parameter.
            var disposition = Assert.Single(response.Content.Headers.GetValues("Content-Disposition"));

            Assert.Equal($"attachment; filename={slug}-r2.{extension}", disposition);
            Assert.Matches(SafeExportDisposition(), disposition);
        }
    }

    [Fact]
    public async Task A_viewer_may_download_and_a_caller_with_no_session_may_not()
    {
        using var owner = await SignInAsync(B.OwnerEmail);
        using var viewer = await SignInAsync(B.MemberEmail);
        var draft = await AcceptedDraftAsync(owner, B);

        Assert.Equal(WorkspaceRole.Viewer, B.MemberRole);

        using var anonymous = _fixture.Gateway.CreateClient();

        foreach (var format in new[] { "markdown", "html" })
        {
            Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(ExportOf(B, Id(draft), format), Ct)).StatusCode);

            var refused = await anonymous.GetAsync(ExportOf(B, Id(draft), format), Ct);
            Assert.True(refused.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect or HttpStatusCode.Found);
            Assert.DoesNotContain(AcceptedBody, await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task One_workspace_cannot_download_anothers_accepted_draft_by_its_id_in_either_format()
    {
        using var ours = await SignInAsync(A.OwnerEmail);
        using var theirs = await SignInAsync(B.OwnerEmail);
        var mine = await AcceptedDraftAsync(ours, A, "Workspace A's accepted words.");
        var draft = await AcceptedDraftAsync(theirs, B, "Workspace B's accepted words.");

        foreach (var format in new[] { "markdown", "html" })
        {
            // A, signed in to A, naming B's draft on A's own routes: the draft is not there.
            var crossed = await ours.GetAsync(ExportOf(A, Id(draft), format), Ct);
            Assert.Equal(HttpStatusCode.NotFound, crossed.StatusCode);
            Assert.Equal(ContentErrorCodes.LongFormDraftNotFound, Code(await BodyOf(crossed)));
            Assert.Contains("no-store", crossed.Headers.CacheControl!.ToString());
            Assert.Null(crossed.Content.Headers.ContentDisposition);

            // A on B's routes is not a member of B at all: the workspace itself is what is not found.
            var foreign = await ours.GetAsync(ExportOf(B, Id(draft), format), Ct);
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
            Assert.DoesNotContain("Workspace B", await foreign.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

            // Even holding B's validator, A learns nothing: a 404, never a 304 that would confirm the bytes.
            var etag = (await theirs.GetAsync(ExportOf(B, Id(draft), format), Ct)).Headers.ETag!.ToString();
            var probed = await ours.GetAsync(
                ExportOf(A, Id(draft), format), new Dictionary<string, string> { ["If-None-Match"] = etag }, Ct);
            Assert.Equal(HttpStatusCode.NotFound, probed.StatusCode);

            // And each workspace's own download holds its own words and none of the other's.
            var a = await (await ours.GetAsync(ExportOf(A, Id(mine), format), Ct)).Content.ReadAsStringAsync(Ct);
            var b = await (await theirs.GetAsync(ExportOf(B, Id(draft), format), Ct)).Content.ReadAsStringAsync(Ct);

            Assert.Contains("Workspace A", a, StringComparison.Ordinal);
            Assert.DoesNotContain("Workspace B", a, StringComparison.Ordinal);
            Assert.Contains("Workspace B", b, StringComparison.Ordinal);
            Assert.DoesNotContain("Workspace A", b, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_malformed_draft_id_and_a_format_there_is_none_of_are_404s()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var draft = await AcceptedDraftAsync(client, A);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{DraftsIn(A)}/not-a-guid/exports/markdown", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(ExportOf(A, Id(draft), "pdf"), Ct)).StatusCode);
    }

    /// <summary>The first section of an approved draft, written by its creator over HTTP.</summary>
    private static async Task<JsonElement> WriteFirstSectionAsync(
        GatewayClient client, SeededWorkspace workspace, JsonElement draft, string body = AcceptedBody)
    {
        var root = $"{DraftsIn(workspace)}/{Id(draft)}";
        var first = Id(draft.GetProperty("latest").GetProperty("sections")[0]);
        var token = Token((await BodyOf(await client.GetAsync($"{root}/sections", Ct))).GetProperty("draft"));

        var written = await client.PatchAsJsonAsync(
            $"{root}/sections/{first}", new { expectedConcurrencyToken = token, body }, Ct);
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);

        return (await BodyOf(await client.GetAsync($"{root}/document", Ct))).GetProperty("document");
    }

    /// <summary>
    /// A draft accepted over HTTP as a creator accepts one: one section written, the other two confirmed
    /// unwritten. Its accepted revision is the second.
    /// </summary>
    private async Task<JsonElement> AcceptedDraftAsync(GatewayClient client, SeededWorkspace workspace, string body = AcceptedBody)
    {
        var draft = await ApprovedDraftAsync(client, workspace);
        var document = await WriteFirstSectionAsync(client, workspace, draft, body);

        var accepted = await client.PostAsJsonAsync(
            $"{DraftsIn(workspace)}/{Id(draft)}/acceptance",
            new
            {
                expectedConcurrencyToken = document.GetProperty("concurrencyToken").GetString(),
                revisionNumber = document.GetProperty("revisionNumber").GetInt32(),
                confirmed = new[] { "UnwrittenSections" },
            },
            Ct);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var article = await BodyOf(accepted);
        Assert.Equal("Accepted", article.GetProperty("status").GetString());

        // The draft as the draft route reads it, so its id and creative context are where the helpers look.
        return await ReadAsync(client, workspace, Id(draft));
    }
}
