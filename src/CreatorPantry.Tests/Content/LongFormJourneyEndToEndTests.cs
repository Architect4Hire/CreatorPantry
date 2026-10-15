using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Modules.Ai;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The long-form journey walked end to end over HTTP (LF.6.2): a recipe, its editorial package, a piece of
/// work opened from that recipe, a long-form draft planned, approved, written, rewritten, accepted, given an
/// SEO package and exported — and then marked for review when the recipe moves. A neighbouring workspace tries
/// every step against the same ids and reaches none of them. A second walk is a newsletter issue about two
/// recipes; a third is a piece with no recipe behind it at all.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> Every step below has thorough tests of its own, and those land a plan or
/// write a section by hand so they can stay on one seam. Nothing before this ran the real outline handler into
/// the real approval into the real section handler into acceptance, export and staleness through their routes,
/// so an id or a revision number one phase hands the next in the wrong shape would have passed all of them.
/// </para>
/// <para>
/// <strong>What is real and what is not.</strong> Real: the Gateway, the API, the database, every facade, the
/// editorial, outline, section and SEO task handlers with their validators, the worker's own pass and the
/// outbox's delivery to the staleness consumer. Stubbed: the chat model alone, scripted with one known-good
/// answer per task as <see cref="CreatorJourneyEndToEndTests"/> scripts it.
/// </para>
/// <para>
/// <strong>What has no route, and is run by hand.</strong> The AI queue driver and the outbox dispatcher belong
/// to the Worker host; the API host has neither, by design. Each is resolved from the host and run where the
/// journey needs it, and the tasks are switched on through the host's own options object.
/// </para>
/// <para>
/// <strong>What cannot be driven at all.</strong> An editorial package is requested and written here and is
/// never accepted: no route accepts one, and nothing in the domain does either. The journey therefore asserts
/// what follows from that — the long-form writer is handed no accepted editorial copy — rather than seeding a
/// row no creator could have made.
/// </para>
/// </remarks>
public sealed class LongFormJourneyEndToEndTests : IAsyncLifetime
{
    private const string OurDish = "Cardamom Plum Galette";

    private const string OurLine = "6 ripe plums, stoned and quartered";

    /// <summary>What the neighbour is making for themselves, in words that appear nowhere in our journey.</summary>
    private const string TheirDish = "Smoked Barley Broth";

    private const string TheirLine = "2 handfuls of pearl barley";

    private readonly ScriptedChatClient _chat = new();

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IChatClient>();
            services.AddSingleton<IChatClient>(_chat);

            // The queue driver the Worker host adds and the API host deliberately does not.
            services.AddAiOperationWorker();
        });

        var tasks = _fixture.Api.Factory.Services.GetRequiredService<AiTaskOptions>();
        foreach (var task in (string[])
        [
            AiTaskCatalog.EditorialPackage,
            AiTaskCatalog.LongFormOutline,
            AiTaskCatalog.LongFormSection,
            AiTaskCatalog.SeoPackage,
        ])
        {
            tasks.Enabled.Add(task);
        }
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SeededWorkspace A => _fixture.WorkspaceA;

    private SeededWorkspace B => _fixture.WorkspaceB;

    private static string In(SeededWorkspace workspace, string path) => $"/api/v1/workspaces/{workspace.Slug}/{path}";

    // ---- 1. A blog article from one recipe --------------------------------------------------------------

    [Fact]
    public async Task A_recipe_becomes_an_accepted_article_that_is_exported_and_then_marked_for_review_and_the_neighbour_reaches_none_of_it()
    {
        using var ours = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        using var theirs = await _fixture.SignInAsync(B.OwnerEmail, cancellationToken: Ct);

        // 1. A recipe, with the facts an approval asks for.
        var recipeId = await RecipeAsync(ours, A, OurDish, OurLine);

        await AssertNotFoundAsync(theirs.GetAsync(In(B, $"recipes/{recipeId}"), Ct), "the recipe");

        // 2. Approved, one step at a time. The neighbour cannot move it, before or after.
        var token = await RecipeTokenAsync(ours, A, recipeId);
        await AssertNotFoundAsync(
            theirs.PostAsJsonAsync(
                In(B, $"recipes/{recipeId}/readiness-transitions"),
                new { targetStatus = "InDevelopment", expectedConcurrencyToken = token },
                Key(),
                Ct),
            "moving the recipe");

        await ApproveRecipeAsync(ours, A, recipeId);

        // 3. Its editorial package: asked for, written by the worker, and readable. Accepting it is the step
        // this journey cannot take — there is no route for it — so it stays a proposal.
        var versionId = await RecipeVersionIdAsync(ours, A, recipeId);
        var editorial = new { sourceVersionId = versionId, sections = new[] { "headnote", "introduction", "cta" } };

        await AssertNotFoundAsync(
            theirs.PostAsJsonAsync(In(B, $"recipes/{recipeId}/editorial-package-requests"), editorial, Key(), Ct),
            "asking for an editorial package");

        var editorialRequestId = await RequestAsync(ours, A, $"recipes/{recipeId}/editorial-package-requests", editorial);
        Assert.Equal(0, _chat.Calls);
        await RunAiPassAsync(operations: 1);

        var package = await BodyOf(await ours.GetAsync(In(A, $"recipes/{recipeId}/editorial-package-requests/{editorialRequestId}"), Ct));
        Assert.Equal("Proposed", package.GetProperty("status").GetString());

        await AssertNotFoundAsync(
            theirs.GetAsync(In(B, $"recipes/{recipeId}/editorial-package-requests/{editorialRequestId}"), Ct),
            "the editorial package request");

        // 4. A piece of work from that recipe.
        var contextId = await ContextAsync(ours, A, "Plum galette, late summer", recipeId);

        var borrowed = await theirs.PostAsJsonAsync(
            In(B, "creative-contexts"), new { workingTitle = "Borrowed", from = new { kind = "Recipe", recipeId } }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, borrowed.StatusCode);
        Assert.Equal(ContentErrorCodes.CreativeContextReferenceUnprocessable, await CodeOf(borrowed));

        await AssertNotFoundAsync(theirs.GetAsync(In(B, $"creative-contexts/{contextId}"), Ct), "the piece of work");

        // 5. Start the article. The neighbour naming our piece of work starts nothing and queues nothing.
        var stolen = await theirs.PostAsJsonAsync(
            In(B, "long-form-drafts"), new { creativeContextId = contextId, kind = "BlogArticle" }, Key(), Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stolen.StatusCode);
        Assert.Equal(ContentErrorCodes.LongFormContextUnprocessable, await CodeOf(stolen));
        Assert.Empty(await ItemsAsync(theirs, B, "long-form-drafts"));

        var (draftId, outlineRequestId) = await StartAsync(ours, A, contextId, "BlogArticle");
        var root = $"long-form-drafts/{draftId}";

        // The neighbour has an article of their own in the queue for the same pass: one worker serves both,
        // and each plan is written from its own workspace's material and lands on its own draft.
        var theirRecipeId = await RecipeAsync(theirs, B, TheirDish, TheirLine);
        var theirContextId = await ContextAsync(theirs, B, "Barley broth, midwinter", theirRecipeId);
        var (theirDraftId, theirOutlineRequestId) = await StartAsync(theirs, B, theirContextId, "BlogArticle");
        var theirRoot = $"long-form-drafts/{theirDraftId}";

        // 6. The worker's pass plans both.
        _chat.Outline = Outline(
            ("Hook", "A galette for people who fear pastry", "Open on how forgiving this one is.", null),
            ("WhyItWorks", "Why the fruit does not weep", "Explain what keeps the base crisp.", null),
            ("Closing", "Make it this weekend", "Send the reader off to try it.", null));
        await RunAiPassAsync(operations: 2);

        var planned = await PlannedAsync(ours, A, draftId, outlineRequestId);
        Assert.Equal("Draft", planned.GetProperty("status").GetString());
        Assert.Equal(1, planned.GetProperty("latest").GetProperty("revisionNumber").GetInt32());
        Assert.Equal("AiGenerated", planned.GetProperty("latest").GetProperty("source").GetString());

        var theirPlan = await PlannedAsync(theirs, B, theirDraftId, theirOutlineRequestId);

        await AssertNotFoundAsync(theirs.GetAsync(In(B, root), Ct), "the draft");
        await AssertNotFoundAsync(theirs.GetAsync(In(B, $"{root}/outline-requests/{outlineRequestId}"), Ct), "the outline request");
        await AssertNotFoundAsync(
            theirs.GetAsync(In(B, $"{theirRoot}/outline-requests/{outlineRequestId}"), Ct), "the outline request, under their own draft");
        await AssertNotFoundAsync(
            theirs.PostAsJsonAsync(In(B, $"{root}/outline-requests"), new { }, Key(), Ct), "asking for another outline");
        await AssertNotFoundAsync(ours.GetAsync(In(A, theirRoot), Ct), "their draft, to us");

        // 7. The creator's own plan: one section renamed, one added.
        var sections = Sections(planned.GetProperty("latest"));
        var plan = new
        {
            expectedConcurrencyToken = Token(planned),
            sections = new object[]
            {
                new { id = Id(sections[0]), role = "Hook", heading = "The galette that forgives you", intent = sections[0].GetProperty("intent").GetString() },
                new { id = Id(sections[1]), role = "WhyItWorks", heading = sections[1].GetProperty("heading").GetString(), intent = sections[1].GetProperty("intent").GetString() },
                new { role = "Tips", heading = "Three things worth knowing", intent = "Pass on what a first attempt teaches." },
                new { id = Id(sections[2]), role = "Closing", heading = sections[2].GetProperty("heading").GetString(), intent = sections[2].GetProperty("intent").GetString() },
            },
        };

        await AssertNotFoundAsync(theirs.PatchAsJsonAsync(In(B, $"{root}/plan"), plan, Ct), "editing the plan");

        var edited = await Ok(ours.PatchAsJsonAsync(In(A, $"{root}/plan"), plan, Ct));
        Assert.Equal(2, edited.GetProperty("latest").GetProperty("revisionNumber").GetInt32());
        Assert.Equal("CreatorEdit", edited.GetProperty("latest").GetProperty("source").GetString());
        Assert.Equal(4, Sections(edited.GetProperty("latest")).Count);

        // 8. Approved by its creator, and by nobody else.
        var approval = new { expectedConcurrencyToken = Token(edited), revisionNumber = 2 };
        await AssertNotFoundAsync(theirs.PostAsJsonAsync(In(B, $"{root}/plan/approval"), approval, Ct), "approving the plan");
        Assert.Equal("Draft", (await Ok(ours.GetAsync(In(A, root), Ct))).GetProperty("status").GetString());

        var approved = await Ok(ours.PostAsJsonAsync(In(A, $"{root}/plan/approval"), approval, Ct));
        Assert.Equal("Outlined", approved.GetProperty("status").GetString());

        // 9. Every section written, one request to a section, by the worker.
        var batch = new { revisionNumber = approved.GetProperty("latest").GetProperty("revisionNumber").GetInt32() };
        await AssertNotFoundAsync(
            theirs.PostAsJsonAsync(In(B, $"{root}/section-writing-batches"), batch, Key(), Ct), "asking for the sections");
        await AssertNotFoundAsync(theirs.GetAsync(In(B, $"{root}/sections"), Ct), "the sections");

        var callsBeforeWriting = _chat.Calls;
        var asked = await ours.PostAsJsonAsync(In(A, $"{root}/section-writing-batches"), batch, Key(), Ct);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        Assert.Equal(4, (await BodyOf(asked)).GetProperty("requests").GetArrayLength());
        Assert.Equal(callsBeforeWriting, _chat.Calls);

        await RunAiPassAsync(operations: 4);

        var written = await Ok(ours.GetAsync(In(A, $"{root}/sections"), Ct));
        var bodies = Sections(written.GetProperty("draft"));
        Assert.Equal("Writing", written.GetProperty("draft").GetProperty("status").GetString());
        Assert.All(bodies, section => Assert.True(section.GetProperty("isWritten").GetBoolean()));
        Assert.All(written.GetProperty("requests").EnumerateArray(), request =>
            Assert.Equal("Written", request.GetProperty("outcome").GetString()));

        // Four sections, four different answers: no section was given another's text.
        Assert.Equal(4, bodies.Select(section => section.GetProperty("body").GetString()).Distinct().Count());

        // 10. One section written again. Its text is replaced and no other section's is.
        var again = Id(bodies[1]);
        var before = bodies.Select(section => section.GetProperty("body").GetString()).ToList();
        var rewrite = new { revisionNumber = written.GetProperty("draft").GetProperty("revisionNumber").GetInt32(), instruction = "Shorter." };

        await AssertNotFoundAsync(
            theirs.PostAsJsonAsync(In(B, $"{root}/sections/{again}/writing-requests"), rewrite, Key(), Ct), "rewriting a section");

        // Our section named under the neighbour's own draft is not a section of that draft.
        var theirApproved = await Ok(theirs.PostAsJsonAsync(
            In(B, $"{theirRoot}/plan/approval"), new { expectedConcurrencyToken = Token(theirPlan), revisionNumber = 1 }, Ct));
        await AssertNotFoundAsync(
            theirs.PatchAsJsonAsync(
                In(B, $"{theirRoot}/sections/{again}"), new { expectedConcurrencyToken = Token(theirApproved), body = "Planted." }, Ct),
            "our section, under their own draft");

        var requested = await ours.PostAsJsonAsync(In(A, $"{root}/sections/{again}/writing-requests"), rewrite, Key(), Ct);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        await RunAiPassAsync(operations: 1);

        var rewritten = await Ok(ours.GetAsync(In(A, $"{root}/sections"), Ct));
        var after = Sections(rewritten.GetProperty("draft")).Select(section => section.GetProperty("body").GetString()).ToList();
        Assert.NotEqual(before[1], after[1]);
        Assert.Equal((before[0], before[2], before[3]), (after[0], after[2], after[3]));

        // 11. One section in the creator's own words.
        const string OwnWords = "Use the plums you would eat out of hand, and nothing softer.";
        var tips = Id(bodies[2]);
        var mine = new { expectedConcurrencyToken = Token(rewritten.GetProperty("draft")), body = OwnWords };

        await AssertNotFoundAsync(theirs.PatchAsJsonAsync(In(B, $"{root}/sections/{tips}"), mine, Ct), "editing a section");
        await AssertNotFoundAsync(
            theirs.PostAsJsonAsync(
                In(B, $"{root}/sections/{tips}/settlement"), new { mine.expectedConcurrencyToken, settled = true }, Ct),
            "settling a section");

        var own = await Ok(ours.PatchAsJsonAsync(In(A, $"{root}/sections/{tips}"), mine, Ct));
        Assert.Equal(OwnWords, own.GetProperty("sections")[2].GetProperty("body").GetString());
        Assert.Equal("CreatorEdit", own.GetProperty("sections")[2].GetProperty("writtenBy").GetString());

        // The whole piece is written with no style guide in force: a brand without one is not stopped (13A.18).
        Assert.Equal(HttpStatusCode.NoContent, (await ours.GetAsync(In(A, "brand-style-guides/active"), Ct)).StatusCode);

        // 12. Read whole, and accepted. The writer was handed no accepted editorial copy, because none exists.
        var review = await Ok(ours.GetAsync(In(A, $"{root}/document"), Ct));
        var document = review.GetProperty("document");
        Assert.Contains("## The galette that forgives you", document.GetProperty("markdown").GetString(), StringComparison.Ordinal);
        Assert.Contains(OwnWords, document.GetProperty("markdown").GetString(), StringComparison.Ordinal);
        Assert.All(document.GetProperty("sections").EnumerateArray(), section =>
            Assert.Empty(section.GetProperty("reusedEditorialParts").EnumerateArray()));
        Assert.All(_chat.PromptsFor(ScriptedChatClient.SectionTask), prompt =>
            Assert.DoesNotContain(ScriptedChatClient.Headnote, prompt, StringComparison.Ordinal));

        var acceptance = Acceptance(document);
        await AssertNotFoundAsync(theirs.GetAsync(In(B, $"{root}/document"), Ct), "the document");
        await AssertNotFoundAsync(theirs.PostAsJsonAsync(In(B, $"{root}/acceptance"), acceptance, Ct), "accepting the document");

        var article = await Ok(ours.PostAsJsonAsync(In(A, $"{root}/acceptance"), acceptance, Ct));
        var acceptedRevision = article.GetProperty("acceptedRevisionNumber").GetInt32();
        Assert.Equal("Accepted", article.GetProperty("status").GetString());
        Assert.Equal(document.GetProperty("revisionNumber").GetInt32(), acceptedRevision);
        Assert.Equal(document.GetProperty("markdown").GetString(), article.GetProperty("markdown").GetString());

        // 13. An SEO package about the accepted article.
        var seo = new { revisionNumber = acceptedRevision, sections = new[] { "seoTitle", "metaDescription", "keyPhrases", "slug" } };
        await AssertNotFoundAsync(
            theirs.PostAsJsonAsync(In(B, $"{root}/seo-package-requests"), seo, Key(), Ct), "asking for an SEO package");

        var seoRequestId = await RequestAsync(ours, A, $"{root}/seo-package-requests", seo);
        await RunAiPassAsync(operations: 1);

        var seoPackage = await Ok(ours.GetAsync(In(A, $"{root}/seo-package-requests/{seoRequestId}"), Ct));
        Assert.Equal("Proposed", seoPackage.GetProperty("status").GetString());
        Assert.Contains(ScriptedChatClient.SeoTitle, seoPackage.GetProperty("proposal").GetRawText(), StringComparison.Ordinal);

        // Advisory: the article is the revision it was.
        Assert.Equal(acceptedRevision, (await Ok(ours.GetAsync(In(A, root), Ct))).GetProperty("latest").GetProperty("revisionNumber").GetInt32());

        await AssertNotFoundAsync(theirs.GetAsync(In(B, $"{root}/seo-package-requests/{seoRequestId}"), Ct), "the SEO request");
        await AssertNotFoundAsync(
            theirs.GetAsync(In(B, $"{theirRoot}/seo-package-requests/{seoRequestId}"), Ct), "the SEO request, under their own draft");

        // 14. Exported, as Markdown and as HTML.
        var markdown = await ours.GetAsync(In(A, $"{root}/exports/markdown"), Ct);
        var html = await ours.GetAsync(In(A, $"{root}/exports/html"), Ct);
        Assert.Equal(HttpStatusCode.OK, markdown.StatusCode);
        Assert.Equal(HttpStatusCode.OK, html.StatusCode);
        Assert.False(markdown.Headers.Contains("Cp-Export-Warnings"));

        var file = await markdown.Content.ReadAsStringAsync(Ct);
        Assert.StartsWith("# Plum galette, late summer\n", file, StringComparison.Ordinal);
        Assert.Contains(OwnWords, file, StringComparison.Ordinal);
        Assert.Contains(after[1]!, file, StringComparison.Ordinal);
        Assert.Contains(OurDish, file, StringComparison.Ordinal);

        var page = await html.Content.ReadAsStringAsync(Ct);
        Assert.Contains("<h2>The galette that forgives you</h2>", page, StringComparison.Ordinal);
        Assert.Contains(OwnWords, page, StringComparison.Ordinal);

        await AssertNotFoundAsync(theirs.GetAsync(In(B, $"{root}/exports/markdown"), Ct), "the Markdown export");
        await AssertNotFoundAsync(theirs.GetAsync(In(B, $"{root}/exports/html"), Ct), "the HTML export");

        // The neighbour accepts an article of their own, so the delivery below has two accepted pieces to
        // choose between.
        var theirSection = Id(Sections(theirApproved.GetProperty("latest"))[0]);
        var theirWritten = await Ok(theirs.PatchAsJsonAsync(
            In(B, $"{theirRoot}/sections/{theirSection}"),
            new { expectedConcurrencyToken = Token(theirApproved), body = "Barley wants patience and a low flame." },
            Ct));
        Assert.Equal(2, theirWritten.GetProperty("revisionNumber").GetInt32());

        var theirDocument = (await Ok(theirs.GetAsync(In(B, $"{theirRoot}/document"), Ct))).GetProperty("document");
        var theirArticle = await Ok(theirs.PostAsJsonAsync(In(B, $"{theirRoot}/acceptance"), Acceptance(theirDocument), Ct));
        Assert.Equal("Accepted", theirArticle.GetProperty("status").GetString());

        // 15. The recipe is edited. The neighbour cannot edit it; ours writes a version, and the outbox tells
        // the article it is behind.
        token = await RecipeTokenAsync(ours, A, recipeId);
        await AssertNotFoundAsync(
            theirs.PatchAsJsonAsync(In(B, $"recipes/{recipeId}"), new { expectedConcurrencyToken = token, title = "Theirs now" }, Ct),
            "editing the recipe");
        await DrainOutboxAsync();
        Assert.Equal("Accepted", (await Ok(ours.GetAsync(In(A, root), Ct))).GetProperty("status").GetString());

        var changed = await ours.PatchAsJsonAsync(
            In(A, $"recipes/{recipeId}"), new { expectedConcurrencyToken = token, title = OurDish + ", revisited" }, Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        await DrainOutboxAsync();

        var stale = (await Ok(ours.GetAsync(In(A, $"{root}/document"), Ct))).GetProperty("document");
        Assert.Equal("NeedsReview", stale.GetProperty("status").GetString());
        Assert.Equal(["RecipeChanged"], stale.GetProperty("staleReasons").EnumerateArray().Select(reason => reason.GetString()));
        Assert.NotEqual(JsonValueKind.Null, stale.GetProperty("staleSince").ValueKind);

        // Marked, and not rewritten: the same revision and the same words.
        Assert.Equal(acceptedRevision, stale.GetProperty("acceptedRevisionNumber").GetInt32());
        Assert.Equal(article.GetProperty("markdown").GetString(), stale.GetProperty("markdown").GetString());

        // Still its creator's accepted article, exported as accepted and saying its source has moved.
        var late = await ours.GetAsync(In(A, $"{root}/exports/markdown"), Ct);
        Assert.Equal(HttpStatusCode.OK, late.StatusCode);
        Assert.Equal([LongFormExportCodes.SourcesMoved], late.Headers.GetValues("Cp-Export-Warnings"));
        Assert.Equal(file, await late.Content.ReadAsStringAsync(Ct));

        // And nothing new is written about a piece that is waiting for review.
        var refused = await ours.PostAsJsonAsync(In(A, $"{root}/seo-package-requests"), seo, Key(), Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(AiSeoPackageRequestErrors.DraftNotAccepted, await CodeOf(refused));

        // The same delivery left the neighbour's article exactly as they accepted it.
        var theirs2 = (await Ok(theirs.GetAsync(In(B, $"{theirRoot}/document"), Ct))).GetProperty("document");
        Assert.Equal("Accepted", theirs2.GetProperty("status").GetString());
        Assert.Empty(theirs2.GetProperty("staleReasons").EnumerateArray());

        // Each plan was written from its own workspace's recipe and from nothing of the other's.
        var outlines = _chat.PromptsFor(ScriptedChatClient.OutlineTask);
        var ourOutline = Assert.Single(outlines, prompt => prompt.Contains(OurDish, StringComparison.Ordinal));
        var theirOutline = Assert.Single(outlines, prompt => prompt.Contains(TheirDish, StringComparison.Ordinal));
        Assert.Contains(OurLine, ourOutline, StringComparison.Ordinal);
        Assert.DoesNotContain(TheirLine, ourOutline, StringComparison.Ordinal);
        Assert.DoesNotContain(OurLine, theirOutline, StringComparison.Ordinal);
        Assert.All(_chat.PromptsFor(ScriptedChatClient.SectionTask), prompt =>
        {
            Assert.Contains(OurDish, prompt, StringComparison.Ordinal);
            Assert.DoesNotContain(TheirDish, prompt, StringComparison.Ordinal);
        });

        // Our own slug is no way in for the neighbour's session, and each workspace lists only its own.
        foreach (var path in (string[])
        [
            $"recipes/{recipeId}", $"creative-contexts/{contextId}", root, $"{root}/sections", $"{root}/document",
            $"{root}/exports/markdown", $"{root}/exports/html", $"{root}/seo-package-requests/{seoRequestId}",
            $"{root}/outline-requests/{outlineRequestId}", "long-form-drafts",
        ])
        {
            Assert.Equal(HttpStatusCode.NotFound, (await theirs.GetAsync(In(A, path), Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await ours.GetAsync(In(A, path), Ct)).StatusCode);
        }

        Assert.Equal([draftId], (await ItemsAsync(ours, A, "long-form-drafts")).Select(Id));
        Assert.Equal([theirDraftId], (await ItemsAsync(theirs, B, "long-form-drafts")).Select(Id));
        Assert.Equal([recipeId], (await ItemsAsync(ours, A, "recipes")).Select(Id));
        Assert.Equal([theirRecipeId], (await ItemsAsync(theirs, B, "recipes")).Select(Id));

        // None of the neighbour's attempts against our ids reached a model: our editorial package, plan, four
        // sections, one rewrite and SEO package, and the one plan the neighbour asked for about their own dish.
        Assert.Equal(9, _chat.Calls);
    }

    // ---- 2. A newsletter issue about two recipes --------------------------------------------------------

    [Fact]
    public async Task A_newsletter_issue_covers_two_recipes_is_exported_naming_both_and_is_marked_for_review_when_either_moves()
    {
        const string Second = "Brown Butter Hazelnut Loaf";

        using var ours = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        using var theirs = await _fixture.SignInAsync(B.OwnerEmail, cancellationToken: Ct);

        // Two recipes on one piece of work: opened from the first, the second added to it.
        var first = await RecipeAsync(ours, A, OurDish, OurLine);
        var second = await RecipeAsync(ours, A, Second, "A fistful of toasted hazelnuts");
        var contextId = await ContextAsync(ours, A, "September letter", first);
        var context = await Ok(ours.GetAsync(In(A, $"creative-contexts/{contextId}"), Ct));
        var adding = new { expectedConcurrencyToken = Token(context), reference = new { kind = "Recipe", recipeId = second } };

        // The neighbour can add neither recipe to a piece of work of their own, nor anything to ours.
        var theirContext = await Ok(theirs.PostAsJsonAsync(In(B, "creative-contexts"), new { workingTitle = "Their letter" }, Ct), HttpStatusCode.Created);
        var crossed = await theirs.PostAsJsonAsync(
            In(B, $"creative-contexts/{Id(theirContext)}/references"),
            new { expectedConcurrencyToken = Token(theirContext), adding.reference },
            Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, crossed.StatusCode);
        Assert.Equal(ContentErrorCodes.CreativeContextReferenceUnprocessable, await CodeOf(crossed));
        await AssertNotFoundAsync(
            theirs.PostAsJsonAsync(In(B, $"creative-contexts/{contextId}/references"), adding, Ct), "adding to the piece of work");

        context = await Ok(ours.PostAsJsonAsync(In(A, $"creative-contexts/{contextId}/references"), adding, Ct), HttpStatusCode.Created);
        var references = context.GetProperty("references").EnumerateArray()
            .Where(reference => reference.GetProperty("kind").GetString() == "Recipe")
            .ToDictionary(reference => reference.GetProperty("recipeId").GetGuid(), Id);
        Assert.Equal(2, references.Count);

        // Planned: one item for each recipe, each naming its own.
        var (draftId, outlineRequestId) = await StartAsync(ours, A, contextId, "NewsletterIssue");
        var root = $"long-form-drafts/{draftId}";

        _chat.Outline = Outline(
            ("Greeting", "Hello again", "Say hello.", null),
            ("Item", "A galette for the last plums", "Introduce the galette.", 1),
            ("Item", "A loaf that keeps", "Introduce the loaf.", 2),
            ("Signoff", "Until next month", "Sign off.", null));
        await RunAiPassAsync(operations: 1);

        var planned = await PlannedAsync(ours, A, draftId, outlineRequestId);
        Assert.Equal([references[first], references[second]], Sources(planned));
        Assert.True(planned.GetProperty("planCheck").GetProperty("isValid").GetBoolean());

        // Framed in the creator's own words, and the items put the other way round.
        var framing = new { expectedConcurrencyToken = Token(planned), subject = "Plums, and a loaf for after", note = "Two bakes for the turn of the season." };
        await AssertNotFoundAsync(theirs.PutAsJsonAsync(In(B, $"{root}/framing"), framing, Ct), "framing the issue");
        var framed = await Ok(ours.PutAsJsonAsync(In(A, $"{root}/framing"), framing, Ct));

        var order = new { expectedConcurrencyToken = Token(framed), sourceReferenceIds = new[] { references[second], references[first] } };
        await AssertNotFoundAsync(theirs.PutAsJsonAsync(In(B, $"{root}/items/order"), order, Ct), "ordering the items");
        var ordered = await Ok(ours.PutAsJsonAsync(In(A, $"{root}/items/order"), order, Ct));
        Assert.Equal([references[second], references[first]], Sources(ordered));

        var revision = ordered.GetProperty("latest").GetProperty("revisionNumber").GetInt32();
        var approved = await Ok(ours.PostAsJsonAsync(
            In(A, $"{root}/plan/approval"), new { expectedConcurrencyToken = Token(ordered), revisionNumber = revision }, Ct));
        Assert.Equal("Outlined", approved.GetProperty("status").GetString());

        // Written, accepted and exported.
        await AssertNotFoundAsync(
            theirs.PostAsJsonAsync(In(B, $"{root}/section-writing-batches"), new { revisionNumber = revision }, Key(), Ct),
            "asking for the sections");
        var asked = await ours.PostAsJsonAsync(In(A, $"{root}/section-writing-batches"), new { revisionNumber = revision }, Key(), Ct);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        await RunAiPassAsync(operations: 4);

        // Each item was written about its own recipe: the request for one names that recipe as its subject.
        var itemPrompts = _chat.PromptsFor(ScriptedChatClient.SectionTask);
        Assert.Equal(4, itemPrompts.Count);
        Assert.All(itemPrompts, prompt => Assert.DoesNotContain(TheirDish, prompt, StringComparison.Ordinal));

        var document = (await Ok(ours.GetAsync(In(A, $"{root}/document"), Ct))).GetProperty("document");
        var acceptance = Acceptance(document);
        await AssertNotFoundAsync(theirs.PostAsJsonAsync(In(B, $"{root}/acceptance"), acceptance, Ct), "accepting the issue");

        var issue = await Ok(ours.PostAsJsonAsync(In(A, $"{root}/acceptance"), acceptance, Ct));
        Assert.Equal("Accepted", issue.GetProperty("status").GetString());

        await AssertNotFoundAsync(theirs.GetAsync(In(B, $"{root}/exports/markdown"), Ct), "the Markdown export");

        var exported = await ours.GetAsync(In(A, $"{root}/exports/markdown"), Ct);
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        var file = await exported.Content.ReadAsStringAsync(Ct);

        // The creator's note, both items in the order they chose, and both recipes named as its sources.
        Assert.Contains("Two bakes for the turn of the season.", file, StringComparison.Ordinal);
        Assert.True(
            file.IndexOf("## A loaf that keeps", StringComparison.Ordinal) < file.IndexOf("## A galette for the last plums", StringComparison.Ordinal),
            file);
        Assert.Contains(OurDish, file, StringComparison.Ordinal);
        Assert.Contains(Second, file, StringComparison.Ordinal);

        // Only the second recipe moves. The issue is one piece, and is marked once.
        var changed = await ours.PatchAsJsonAsync(
            In(A, $"recipes/{second}"),
            new { expectedConcurrencyToken = await RecipeTokenAsync(ours, A, second), title = Second + ", revisited" },
            Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        await DrainOutboxAsync();

        var stale = (await Ok(ours.GetAsync(In(A, $"{root}/document"), Ct))).GetProperty("document");
        Assert.Equal("NeedsReview", stale.GetProperty("status").GetString());
        Assert.Equal(["RecipeChanged"], stale.GetProperty("staleReasons").EnumerateArray().Select(reason => reason.GetString()));
        Assert.Equal(issue.GetProperty("markdown").GetString(), stale.GetProperty("markdown").GetString());

        // The first recipe moving too adds nothing more to say.
        changed = await ours.PatchAsJsonAsync(
            In(A, $"recipes/{first}"),
            new { expectedConcurrencyToken = await RecipeTokenAsync(ours, A, first), title = OurDish + ", revisited" },
            Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        await DrainOutboxAsync();

        var still = (await Ok(ours.GetAsync(In(A, $"{root}/document"), Ct))).GetProperty("document");
        Assert.Equal(stale.GetProperty("staleSince").GetString(), still.GetProperty("staleSince").GetString());
        Assert.Single(still.GetProperty("staleReasons").EnumerateArray());

        // Nothing the neighbour sent started a draft of their own, and ours is listed to us alone.
        Assert.Empty(await ItemsAsync(theirs, B, "long-form-drafts"));
        Assert.Equal([draftId], (await ItemsAsync(ours, A, "long-form-drafts")).Select(Id));
        Assert.Equal(5, _chat.Calls);
    }

    // ---- 3. A piece with no recipe behind it ------------------------------------------------------------

    [Fact]
    public async Task A_draft_with_no_recipe_reference_carries_no_recipe_facts_and_no_recipe_can_make_it_stale()
    {
        using var ours = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        using var theirs = await _fixture.SignInAsync(B.OwnerEmail, cancellationToken: Ct);

        // The workspace has a recipe. The piece of work does not name it.
        var recipeId = await RecipeAsync(ours, A, OurDish, OurLine);
        var opened = await Ok(
            ours.PostAsJsonAsync(In(A, "creative-contexts"), new { workingTitle = "Why I bake on Sundays", channelKeys = new[] { "blog" } }, Ct),
            HttpStatusCode.Created);
        Assert.Empty(opened.GetProperty("references").EnumerateArray());

        var (draftId, outlineRequestId) = await StartAsync(ours, A, Id(opened), "BlogArticle");
        var root = $"long-form-drafts/{draftId}";

        _chat.Outline = Outline(
            ("Hook", "The quiet hour", "Open on the house before anyone is up.", null),
            ("Closing", "Keep one morning", "Send the reader off to find theirs.", null));
        await RunAiPassAsync(operations: 1);

        var planned = await PlannedAsync(ours, A, draftId, outlineRequestId);
        Assert.All(Sections(planned.GetProperty("latest")), section =>
            Assert.Equal(JsonValueKind.Null, section.GetProperty("sourceReferenceId").ValueKind));

        var approved = await Ok(ours.PostAsJsonAsync(
            In(A, $"{root}/plan/approval"), new { expectedConcurrencyToken = Token(planned), revisionNumber = 1 }, Ct));

        await AssertNotFoundAsync(theirs.GetAsync(In(B, root), Ct), "the draft");
        await AssertNotFoundAsync(
            theirs.PostAsJsonAsync(In(B, $"{root}/section-writing-batches"), new { revisionNumber = 1 }, Key(), Ct),
            "asking for the sections");

        var asked = await ours.PostAsJsonAsync(In(A, $"{root}/section-writing-batches"), new { revisionNumber = 1 }, Key(), Ct);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        Assert.Equal("Outlined", approved.GetProperty("status").GetString());
        await RunAiPassAsync(operations: 2);

        var document = (await Ok(ours.GetAsync(In(A, $"{root}/document"), Ct))).GetProperty("document");
        var acceptance = Acceptance(document);
        await AssertNotFoundAsync(theirs.PostAsJsonAsync(In(B, $"{root}/acceptance"), acceptance, Ct), "accepting the document");

        var article = await Ok(ours.PostAsJsonAsync(In(A, $"{root}/acceptance"), acceptance, Ct));
        Assert.Equal("Accepted", article.GetProperty("status").GetString());

        // Nothing the model was sent for this piece — its plan or either section — held a word of the recipe
        // the workspace owns: not its title, and not a line of it.
        Assert.Equal(3, _chat.Calls);
        Assert.All(_chat.Prompts, prompt =>
        {
            Assert.DoesNotContain(OurDish, prompt, StringComparison.Ordinal);
            Assert.DoesNotContain(OurLine, prompt, StringComparison.Ordinal);
            Assert.DoesNotContain(recipeId.ToString(), prompt, StringComparison.OrdinalIgnoreCase);
        });

        // Nor does the file that leaves: it attributes no recipe, because it was written from none.
        await AssertNotFoundAsync(theirs.GetAsync(In(B, $"{root}/exports/markdown"), Ct), "the Markdown export");

        foreach (var format in (string[])["markdown", "html"])
        {
            var exported = await ours.GetAsync(In(A, $"{root}/exports/{format}"), Ct);
            Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
            Assert.False(exported.Headers.Contains("Cp-Export-Warnings"));

            var file = await exported.Content.ReadAsStringAsync(Ct);
            Assert.Contains("Why I bake on Sundays", file, StringComparison.Ordinal);
            Assert.DoesNotContain(OurDish, file, StringComparison.Ordinal);
            Assert.DoesNotContain("plums", file, StringComparison.OrdinalIgnoreCase);
        }

        // And a recipe moving is nothing to it: it has no source to be behind.
        var changed = await ours.PatchAsJsonAsync(
            In(A, $"recipes/{recipeId}"),
            new { expectedConcurrencyToken = await RecipeTokenAsync(ours, A, recipeId), title = OurDish + ", revisited" },
            Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        await DrainOutboxAsync();

        var after = (await Ok(ours.GetAsync(In(A, $"{root}/document"), Ct))).GetProperty("document");
        Assert.Equal("Accepted", after.GetProperty("status").GetString());
        Assert.Empty(after.GetProperty("staleReasons").EnumerateArray());
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static string Key() => Guid.NewGuid().ToString("N");

    private static Guid Id(JsonElement element) => element.GetProperty("id").GetGuid();

    private static string Token(JsonElement element) => element.GetProperty("concurrencyToken").GetString()!;

    private static List<JsonElement> Sections(JsonElement revision) => [.. revision.GetProperty("sections").EnumerateArray()];

    /// <summary>The reference each item of the latest plan covers, in plan order.</summary>
    private static List<Guid> Sources(JsonElement draft) =>
    [
        .. Sections(draft.GetProperty("latest"))
            .Where(section => section.GetProperty("role").GetString() == "Item")
            .Select(section => section.GetProperty("sourceReferenceId").GetGuid()),
    ];

    /// <summary>An acceptance of the document as it was read, confirming exactly what it says needs confirming.</summary>
    private static object Acceptance(JsonElement document) => new
    {
        expectedConcurrencyToken = Token(document),
        revisionNumber = document.GetProperty("revisionNumber").GetInt32(),
        confirmed = document.GetProperty("needsConfirmation").EnumerateArray()
            .Select(issue => issue.GetProperty("issue").GetString())
            .ToArray(),
    };

    private static string Outline(params (string Role, string Heading, string Intent, int? Source)[] sections) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = "content.long-form-outline.v1",
            sections = sections.Select(section => new
            {
                role = section.Role,
                heading = section.Heading,
                intent = section.Intent,
                source = section.Source,
            }),
        });

    /// <summary>A recipe with ingredients, a method, a yield and a time, written through its own routes.</summary>
    private static async Task<Guid> RecipeAsync(GatewayClient client, SeededWorkspace workspace, string title, string ingredient)
    {
        var created = await Ok(client.PostAsJsonAsync(In(workspace, "recipes"), new { title }, Ct), HttpStatusCode.Created);
        var recipeId = created.GetProperty("recipeId").GetGuid();

        var edited = await client.PatchAsJsonAsync(
            In(workspace, $"recipes/{recipeId}"),
            new
            {
                expectedConcurrencyToken = await RecipeTokenAsync(client, workspace, recipeId),
                yieldText = "serves eight",
                prepTimeMinutes = 30,
                cookTimeMinutes = 45,
                ingredientGroups = new[] { new { ingredients = new[] { new { displayText = ingredient } } } },
                instructions = new[] { new { steps = new[] { new { text = "Bake until deep gold at the edges." } } } },
            },
            Ct);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        return recipeId;
    }

    /// <summary>Cooked once and recorded, then walked to Approved one transition at a time.</summary>
    private static async Task ApproveRecipeAsync(GatewayClient client, SeededWorkspace workspace, Guid recipeId)
    {
        var recipe = await Ok(client.GetAsync(In(workspace, $"recipes/{recipeId}"), Ct));
        var recorded = await client.PostAsJsonAsync(
            In(workspace, $"recipes/{recipeId}/test-runs"),
            new
            {
                sourceVersionNumber = recipe.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32(),
                testedAt = "2026-09-20T18:00:00Z",
                outcome = "Succeeded",
            },
            Ct);
        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);

        foreach (var target in (string[])["InDevelopment", "Testing", "ReadyForReview", "Approved"])
        {
            var moved = await client.PostAsJsonAsync(
                In(workspace, $"recipes/{recipeId}/readiness-transitions"),
                new { targetStatus = target, expectedConcurrencyToken = await RecipeTokenAsync(client, workspace, recipeId) },
                Key(),
                Ct);

            Assert.True(
                moved.StatusCode == HttpStatusCode.OK,
                $"Moving the recipe to {target} answered {(int)moved.StatusCode}: {await moved.Content.ReadAsStringAsync(Ct)}");
        }
    }

    private static async Task<string> RecipeTokenAsync(GatewayClient client, SeededWorkspace workspace, Guid recipeId) =>
        Token(await Ok(client.GetAsync(In(workspace, $"recipes/{recipeId}"), Ct)));

    private static async Task<Guid> RecipeVersionIdAsync(GatewayClient client, SeededWorkspace workspace, Guid recipeId) =>
        Id((await Ok(client.GetAsync(In(workspace, $"recipes/{recipeId}"), Ct))).GetProperty("currentVersion"));

    /// <summary>A piece of work opened from one recipe.</summary>
    private static async Task<Guid> ContextAsync(GatewayClient client, SeededWorkspace workspace, string workingTitle, Guid recipeId)
    {
        var opened = await Ok(
            client.PostAsJsonAsync(
                In(workspace, "creative-contexts"),
                new { workingTitle, channelKeys = new[] { "blog" }, from = new { kind = "Recipe", recipeId } },
                Ct),
            HttpStatusCode.Created);

        Assert.Equal(recipeId, Assert.Single(opened.GetProperty("references").EnumerateArray()).GetProperty("recipeId").GetGuid());

        return Id(opened);
    }

    /// <summary>Starts a draft and returns it with the outline request to follow. Queued, not planned.</summary>
    private static async Task<(Guid DraftId, Guid RequestId)> StartAsync(
        GatewayClient client, SeededWorkspace workspace, Guid contextId, string kind)
    {
        var started = await Ok(
            client.PostAsJsonAsync(In(workspace, "long-form-drafts"), new { creativeContextId = contextId, kind }, Key(), Ct),
            HttpStatusCode.Accepted);

        Assert.Equal(JsonValueKind.Null, started.GetProperty("draft").GetProperty("latest").ValueKind);

        return (Id(started.GetProperty("draft")), started.GetProperty("request").GetProperty("aiProposalRequestId").GetGuid());
    }

    /// <summary>The draft once its outline request has finished, read through the request the creator polls.</summary>
    private static async Task<JsonElement> PlannedAsync(GatewayClient client, SeededWorkspace workspace, Guid draftId, Guid requestId)
    {
        var polled = await Ok(client.GetAsync(In(workspace, $"long-form-drafts/{draftId}/outline-requests/{requestId}"), Ct));
        Assert.Equal("Proposed", polled.GetProperty("request").GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, polled.GetProperty("draft").GetProperty("latest").ValueKind);

        return polled.GetProperty("draft");
    }

    /// <summary>Queues one AI request and returns the id to poll.</summary>
    private static async Task<Guid> RequestAsync(GatewayClient client, SeededWorkspace workspace, string route, object body) =>
        (await Ok(client.PostAsJsonAsync(In(workspace, route), body, Key(), Ct), HttpStatusCode.Accepted))
            .GetProperty("aiProposalRequestId").GetGuid();

    private static async Task<List<JsonElement>> ItemsAsync(GatewayClient client, SeededWorkspace workspace, string path) =>
        [.. (await Ok(client.GetAsync(In(workspace, path), Ct))).GetProperty("items").EnumerateArray()];

    /// <summary>The body of a response that must have succeeded, with what it said instead when it did not.</summary>
    private static async Task<JsonElement> Ok(Task<HttpResponseMessage> request, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await request;

        Assert.True(
            response.StatusCode == expected,
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath} answered "
                + $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");

        return await BodyOf(response);
    }

    private static async Task AssertNotFoundAsync(Task<HttpResponseMessage> attempt, string what)
    {
        var response = await attempt;

        Assert.True(
            response.StatusCode == HttpStatusCode.NotFound,
            $"{what} answered {(int)response.StatusCode} to another workspace");

        // A coded refusal, so this is the route declining the id and not an address nothing answers at.
        var code = await CodeOf(response);
        Assert.True(code?.EndsWith("not_found", StringComparison.Ordinal), $"{what} was refused as '{code}'");
    }

    /// <summary>
    /// The AI queue, run until it is empty. Everything queued for it must be claimed and must land, and
    /// nothing else may be.
    /// </summary>
    private async Task RunAiPassAsync(int operations)
    {
        var (claimed, proposed, failed) = (0, 0, 0);

        while (true)
        {
            await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
            var summary = await scope.ServiceProvider.GetRequiredService<IAiOperationWorker>().RunPendingAsync(Ct);

            if (summary.Claimed == 0)
            {
                break;
            }

            (claimed, proposed, failed) = (claimed + summary.Claimed, proposed + summary.Proposed, failed + summary.Failed);
        }

        Assert.Equal((operations, operations, 0), (claimed, proposed, failed));
    }

    /// <summary>Delivers every due outbox message, which is what tells an accepted piece its source has moved.</summary>
    private async Task DrainOutboxAsync()
    {
        while (true)
        {
            await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
            var summary = await scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>().DispatchDueAsync(Ct);

            if (summary.Claimed == 0)
            {
                return;
            }
        }
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await BodyOf(response)).GetProperty("code").GetString();

    /// <summary>
    /// One model for the whole journey: it reads which task is asking from the task instructions and gives
    /// that task a known-good answer. The plan is whatever the journey set last; each section gets a body of
    /// its own, so a rewrite is visibly a different text. Anything else asking is a step this journey has not
    /// scripted, and fails loudly rather than being answered with a guess.
    /// </summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        public const string EditorialTask = "Write an editorial package for the recipe in the SOURCE segment";

        public const string OutlineTask = "You are proposing a plan";

        public const string SectionTask = "The creator has approved a plan for the piece";

        public const string SeoTask = "Write an SEO package for the subject in the SOURCE segment";

        public const string SeoTitle = "A Forgiving Plum Galette for Late Summer";

        /// <summary>The proposed headnote: words no creator accepted, so words no later step may be handed.</summary>
        public const string Headnote = "A galette that asks very little of the cook.";

        private const string Editorial = $$"""
            {
              "schemaVersion": "content.editorial-package.v1",
              "sections": {
                "headnote": { "text": "{{Headnote}}" },
                "introduction": { "text": "Start with fruit you would happily eat as it is." },
                "cta": { "text": "Save it for the weekend." }
              }
            }
            """;

        private const string Seo = $$"""
            {
              "schemaVersion": "content.seo-package.v1",
              "sections": {
                "seoTitle": { "text": "{{SeoTitle}}" },
                "metaDescription": { "text": "A free-form plum galette with a crisp base and soft fruit, written for cooks who would rather not fuss with pastry." },
                "keyPhrases": [ { "phrase": "plum galette" }, { "phrase": "easy fruit galette" }, { "phrase": "late summer baking" } ]
              }
            }
            """;

        /// <summary>Plain prose that states no quantity, time or temperature, so nothing in it is a recipe claim.</summary>
        private static readonly string[] Bodies =
        [
            "Some bakes ask for a quiet afternoon and a steady hand. This one asks for neither.",
            "The base stays crisp because the fruit is given somewhere to send its juice.",
            "Keep the pastry cold, fold it loosely, and leave the edges as uneven as they fall.",
            "Make it once for yourself before you make it for anyone else.",
            "The short version: cold pastry, ripe fruit, and a hot oven waiting.",
            "What matters most is the fruit, and the fruit is already doing the work.",
            "A rough edge is not a mistake here. It is the whole look of the thing.",
            "Bring it to the table warm, and let people help themselves.",
        ];

        private readonly List<string> _prompts = [];

        private int _sections;

        public int Calls { get; private set; }

        /// <summary>The plan the next outline request is answered with.</summary>
        public string? Outline { get; set; }

        /// <summary>Everything every request sent, instructions and source material together.</summary>
        public IReadOnlyList<string> Prompts => _prompts;

        public IReadOnlyList<string> PromptsFor(string task) =>
            [.. _prompts.Where(prompt => prompt.Contains(task, StringComparison.Ordinal))];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            _prompts.Add(string.Concat(messages.Select(message => message.Text)));

            // The task instructions travel in the system message; creator and source text never do, so text
            // an earlier step produced cannot be mistaken for the task that is asking now.
            var instructions = string.Concat(messages.Where(message => message.Role == ChatRole.System).Select(message => message.Text));

            var answer =
                instructions.Contains(EditorialTask, StringComparison.Ordinal) ? Editorial
                : instructions.Contains(SeoTask, StringComparison.Ordinal) ? Seo
                : instructions.Contains(OutlineTask, StringComparison.Ordinal) ? Outline
                : instructions.Contains(SectionTask, StringComparison.Ordinal) ? Section()
                : null;

            return Task.FromResult(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                answer ?? throw new InvalidOperationException("A task this journey has not scripted asked the model for an answer.")))
            {
                ModelId = "test-model",
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The gateway does not stream.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private string Section() => JsonSerializer.Serialize(new
        {
            schemaVersion = "content.long-form-section.v1",
            body = Bodies[_sections++ % Bodies.Length],
            warnings = Array.Empty<object>(),
        });
    }
}
