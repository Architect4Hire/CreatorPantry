using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// AF.6.1 against a real SQL Server: the post package's write seam, its immutable history, per-channel
/// decisions, staleness after a recipe change, and two-workspace isolation.
/// </summary>
/// <remarks>
/// SQL Server rather than SQLite because the migration is part of what is under test — three tables that all
/// reach one workspace, a cycle of restricted keys between a slot and its revisions, and a row version that has
/// to move. The fixture is shared, so every assertion is scoped to the context the test created rather than to
/// table counts.
/// </remarks>
public sealed class SocialPackageTests(SqlServerRecipeFixture fixture) : IClassFixture<SqlServerRecipeFixture>
{
    private const string User = "user-1";

    private const string Caption = "Olive oil cake, still warm. Recipe on the blog.";

    private static readonly Guid WsA = SqlServerRecipeFixture.WorkspaceA;
    private static readonly Guid WsB = SqlServerRecipeFixture.WorkspaceB;

    private static readonly RecipeVersionFacts EditFacts =
        new(RecipeVersionSource.CreatorEdit, RecipeVersionReadiness.Draft, "Edited.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Writing ----

    [Fact]
    public async Task A_generated_post_opens_one_slot_per_channel_and_awaits_a_decision()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var contextId = await SeedContextAsync(WsA, recipe);

        await GenerateAsync(WsA, contextId, "instagram", recipe);
        var package = await GenerateAsync(WsA, contextId, "pinterest", recipe);

        Assert.Equal(contextId, package.CreativeContextId);
        Assert.Equal(new[] { "instagram", "pinterest" }, package.Channels.Select(channel => channel.ChannelKey).Order());

        var instagram = Channel(package, "instagram");
        Assert.Equal(ContentProposalStatus.Proposed, instagram.Status);
        Assert.Null(instagram.Accepted);
        Assert.Null(instagram.IsCurrent);
        Assert.Equal(1, instagram.Latest.RevisionNumber);
        Assert.Equal(ContentRevisionSource.AiGenerated, instagram.Latest.Source);
        Assert.Equal(Caption, instagram.Latest.Body);

        // The exact sources it was written from.
        Assert.Equal(recipe.RecipeId, instagram.Latest.RecipeId);
        Assert.Equal(recipe.VersionId, instagram.Latest.RecipeVersionId);
        Assert.NotNull(instagram.Latest.AiProposalId);
        Assert.Equal("content.channel-posts", instagram.Latest.PromptTemplateId);
    }

    [Fact]
    public async Task An_over_limit_body_is_stored_as_written_and_flagged()
    {
        var contextId = await SeedContextAsync(WsA, recipe: null);
        var body = new string('x', 300);

        var package = await EditAsync(
            WsA, contextId, "x", body, expected: null, new SocialLimitResult(300, 280, SocialLimitStatus.Over, "1.0.0"));

        var latest = Channel(package, "x").Latest;
        Assert.Equal(body, latest.Body);
        Assert.Equal(SocialLimitStatus.Over, latest.LimitStatus);
        Assert.Equal(300, latest.CharacterCount);
        Assert.Equal(280, latest.CharacterLimit);
        Assert.Equal("1.0.0", latest.ChannelProfileVersion);
    }

    [Fact]
    public async Task A_limit_result_that_disagrees_with_itself_is_refused_before_anything_is_written()
    {
        var contextId = await SeedContextAsync(WsA, recipe: null);

        await using var scope = fixture.ScopeFor(WsA);
        var result = await Facade(scope).RecordEditAsync(
            new SocialEditedRevisionInput(
                contextId, "x", "Short.", new SocialLimitResult(6, 280, SocialLimitStatus.Over, "1.0.0"), null),
            Ct);

        Assert.Equal(ContentErrorCodes.SocialInvalid, result.Error!.Code);
        Assert.True(result.Error.FieldErrors.ContainsKey("limit"));
        Assert.Equal(0, await SqlServerRecipeFixture.Db(scope).SocialPackages.CountAsync(p => p.CreativeContextId == contextId, Ct));
    }

    // ---- Per-channel decisions ----

    [Fact]
    public async Task Each_channel_is_accepted_or_rejected_on_its_own()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var contextId = await SeedContextAsync(WsA, recipe);
        await GenerateAsync(WsA, contextId, "instagram", recipe);
        var drafted = await GenerateAsync(WsA, contextId, "pinterest", recipe);

        var instagramRevision = Channel(drafted, "instagram").Latest.Id;
        var pinterestRevision = Channel(drafted, "pinterest").Latest.Id;

        await DecideAsync(WsA, facade => facade.AcceptAsync(User, contextId, "instagram", instagramRevision, Ct));
        var decided = await DecideAsync(WsA, facade => facade.RejectAsync(User, contextId, "pinterest", pinterestRevision, Ct));

        Assert.Equal(ContentProposalStatus.Accepted, Channel(decided, "instagram").Status);
        Assert.Equal(instagramRevision, Channel(decided, "instagram").Accepted!.Id);
        Assert.True(Channel(decided, "instagram").IsCurrent);
        Assert.Equal(ContentProposalStatus.Rejected, Channel(decided, "pinterest").Status);
        Assert.Null(Channel(decided, "pinterest").Accepted);

        // Regenerating one channel leaves its neighbour exactly as it was.
        var regenerated = await GenerateAsync(WsA, contextId, "pinterest", recipe, "A second go at the pin.");

        Assert.Equal(ContentProposalStatus.Proposed, Channel(regenerated, "pinterest").Status);
        Assert.Equal(2, Channel(regenerated, "pinterest").Latest.RevisionNumber);
        Assert.Equal(ContentProposalStatus.Accepted, Channel(regenerated, "instagram").Status);
        Assert.Equal(instagramRevision, Channel(regenerated, "instagram").Accepted!.Id);
        Assert.Equal(Channel(decided, "instagram").UpdatedAt, Channel(regenerated, "instagram").UpdatedAt);
    }

    [Fact]
    public async Task Accepting_twice_changes_nothing_and_is_audited_once()
    {
        var contextId = await SeedContextAsync(WsA, recipe: null);
        var drafted = await EditAsync(WsA, contextId, "threads", Caption, expected: null);
        var revisionId = Channel(drafted, "threads").Latest.Id;

        var first = await DecideAsync(WsA, facade => facade.AcceptAsync(User, contextId, "threads", revisionId, Ct));
        var second = await DecideAsync(WsA, facade => facade.AcceptAsync(User, contextId, "threads", revisionId, Ct));

        Assert.Equal(Channel(first, "threads").UpdatedAt, Channel(second, "threads").UpdatedAt);
        Assert.Equal(revisionId, Channel(second, "threads").Accepted!.Id);

        await using var scope = fixture.ScopeFor(WsA);
        var db = SqlServerRecipeFixture.Db(scope);
        var channelId = await ChannelIdAsync(db, contextId, "threads");
        var resourceId = channelId.ToString("D");

        Assert.Equal(1, await db.SocialRevisions.CountAsync(r => r.SocialPackageChannelId == channelId, Ct));

        var audit = await db.AuditLogs.SingleAsync(
            entry => entry.ResourceId == resourceId && entry.Action == ContentAuditActions.SocialChannelAccepted, Ct);
        Assert.Equal(User, audit.ActorUserId);
        Assert.Equal(nameof(ContentProposalStatus.Accepted), audit.AfterReference);

        // An audit summary names the channel and a revision number, never a word of the post.
        Assert.DoesNotContain("cake", audit.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Only_the_newest_revision_can_be_decided_on()
    {
        var contextId = await SeedContextAsync(WsA, recipe: null);
        var first = await EditAsync(WsA, contextId, "facebook", Caption, expected: null);
        var firstRevision = Channel(first, "facebook").Latest.Id;
        await EditAsync(WsA, contextId, "facebook", "A rewrite.", firstRevision);

        await using var scope = fixture.ScopeFor(WsA);
        var accept = await Facade(scope).AcceptAsync(User, contextId, "facebook", firstRevision, Ct);
        var reject = await Facade(scope).RejectAsync(User, contextId, "facebook", firstRevision, Ct);

        Assert.Equal(ContentErrorCodes.SocialStale, accept.Error!.Code);
        Assert.Equal(ContentErrorCodes.SocialStale, reject.Error!.Code);
        Assert.Equal(ContentProposalStatus.Proposed, await StatusAsync(WsA, contextId, "facebook"));
    }

    // ---- Immutable history ----

    [Fact]
    public async Task Edits_and_regenerations_append_revisions_and_never_take_away_the_accepted_words()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var contextId = await SeedContextAsync(WsA, recipe);
        var generated = await GenerateAsync(WsA, contextId, "instagram", recipe);
        var acceptedId = Channel(generated, "instagram").Latest.Id;
        await DecideAsync(WsA, facade => facade.AcceptAsync(User, contextId, "instagram", acceptedId, Ct));

        var edited = await EditAsync(WsA, contextId, "instagram", "My own words for it.", acceptedId);
        var editId = Channel(edited, "instagram").Latest.Id;

        Assert.Equal(ContentProposalStatus.Proposed, Channel(edited, "instagram").Status);
        Assert.Equal(2, Channel(edited, "instagram").Latest.RevisionNumber);
        Assert.Equal(acceptedId, Channel(edited, "instagram").Latest.ParentRevisionId);
        Assert.Equal(ContentRevisionSource.CreatorEdit, Channel(edited, "instagram").Latest.Source);
        Assert.Null(Channel(edited, "instagram").Latest.AiProposalId);
        Assert.Equal(Caption, Channel(edited, "instagram").Accepted!.Body);

        // Nothing may treat a channel with newer words awaiting a decision as current.
        Assert.False(Channel(edited, "instagram").IsCurrent);

        var rejected = await DecideAsync(WsA, facade => facade.RejectAsync(User, contextId, "instagram", editId, Ct));
        Assert.Equal(ContentProposalStatus.Rejected, Channel(rejected, "instagram").Status);
        Assert.Equal(acceptedId, Channel(rejected, "instagram").Accepted!.Id);

        var regenerated = await GenerateAsync(WsA, contextId, "instagram", recipe, "A third version.");
        Assert.Equal(3, Channel(regenerated, "instagram").Latest.RevisionNumber);
        Assert.Equal(acceptedId, Channel(regenerated, "instagram").Accepted!.Id);

        await using var scope = fixture.ScopeFor(WsA);
        var db = SqlServerRecipeFixture.Db(scope);
        var channelId = await ChannelIdAsync(db, contextId, "instagram");
        var bodies = await db.SocialRevisions
            .Where(r => r.SocialPackageChannelId == channelId)
            .OrderBy(r => r.RevisionNumber)
            .Select(r => r.Body)
            .ToListAsync(Ct);

        Assert.Equal(new[] { Caption, "My own words for it.", "A third version." }, bodies);
    }

    [Fact]
    public async Task A_revision_can_be_neither_rewritten_repinned_nor_deleted()
    {
        var contextId = await SeedContextAsync(WsA, recipe: null);
        var drafted = await EditAsync(WsA, contextId, "tiktok", Caption, expected: null);
        var revisionId = Channel(drafted, "tiktok").Latest.Id;
        await DecideAsync(WsA, facade => facade.AcceptAsync(User, contextId, "tiktok", revisionId, Ct));

        await using (var scope = fixture.ScopeFor(WsA))
        {
            var db = SqlServerRecipeFixture.Db(scope);
            (await db.SocialRevisions.SingleAsync(r => r.Id == revisionId, Ct)).Body = "Rewritten.";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        }

        await using (var scope = fixture.ScopeFor(WsA))
        {
            var db = SqlServerRecipeFixture.Db(scope);
            (await db.SocialRevisions.SingleAsync(r => r.Id == revisionId, Ct)).ContextPackageChecksum = "sha256:other";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        }

        await using (var scope = fixture.ScopeFor(WsA))
        {
            var db = SqlServerRecipeFixture.Db(scope);
            db.SocialRevisions.Remove(await db.SocialRevisions.SingleAsync(r => r.Id == revisionId, Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        }

        await using var check = fixture.ScopeFor(WsA);
        Assert.Equal(Caption, (await SqlServerRecipeFixture.Db(check).SocialRevisions.SingleAsync(r => r.Id == revisionId, Ct)).Body);
    }

    [Fact]
    public async Task An_edit_composed_against_an_older_revision_is_refused_and_leaves_nothing_behind()
    {
        var contextId = await SeedContextAsync(WsA, recipe: null);
        var first = await EditAsync(WsA, contextId, "blog", Caption, expected: null);
        var firstRevision = Channel(first, "blog").Latest.Id;
        await EditAsync(WsA, contextId, "blog", "The second draft.", firstRevision);

        await using var scope = fixture.ScopeFor(WsA);
        var facade = Facade(scope);

        var stale = await facade.RecordEditAsync(
            new SocialEditedRevisionInput(contextId, "blog", "Composed against the first.", null, firstRevision), Ct);

        // And a first-words edit on a channel nobody has written for yet, refused for the same reason, must
        // not leave a slot staged on the tracked package for this scope's next save to carry out.
        var strayChannel = await facade.RecordEditAsync(
            new SocialEditedRevisionInput(contextId, "newsletter", "Never saved.", null, Guid.NewGuid()), Ct);
        await SqlServerRecipeFixture.Db(scope).SaveChangesAsync(Ct);

        Assert.Equal(ContentErrorCodes.SocialStale, stale.Error!.Code);
        Assert.Equal(ContentErrorCodes.SocialStale, strayChannel.Error!.Code);

        var package = (await facade.GetAsync(contextId, Ct)).Value!;
        Assert.Equal("blog", Assert.Single(package.Channels).ChannelKey);
        Assert.Equal("The second draft.", Channel(package, "blog").Latest.Body);
    }

    // ---- Staleness ----

    [Fact]
    public async Task A_recipe_change_marks_the_accepted_channel_NeedsReview_and_leaves_its_words_alone()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var contextId = await SeedContextAsync(WsA, recipe);
        await GenerateAsync(WsA, contextId, "instagram", recipe);
        var drafted = await GenerateAsync(WsA, contextId, "pinterest", recipe);
        var acceptedId = Channel(drafted, "instagram").Latest.Id;
        await DecideAsync(WsA, facade => facade.AcceptAsync(User, contextId, "instagram", acceptedId, Ct));

        await EditRecipeAsync(WsA, recipe.RecipeId);

        // The window between the recipe commit and delivery: the flag is not set, and the channel is still
        // never reported current.
        var during = await GetAsync(WsA, contextId);
        Assert.Equal(ContentProposalStatus.Accepted, Channel(during, "instagram").Status);
        Assert.False(Channel(during, "instagram").IsCurrent);

        await DispatchAsync();

        var after = await GetAsync(WsA, contextId);
        var instagram = Channel(after, "instagram");
        Assert.Equal(ContentProposalStatus.NeedsReview, instagram.Status);
        Assert.Equal(ContentStaleReasons.RecipeChanged, instagram.StaleReasons);
        Assert.NotNull(instagram.StaleSince);
        Assert.False(instagram.IsCurrent);

        // The accepted words and their pin are exactly what was accepted, and nothing was regenerated.
        Assert.Equal(acceptedId, instagram.Accepted!.Id);
        Assert.Equal(Caption, instagram.Accepted.Body);
        Assert.Equal(recipe.VersionId, instagram.Accepted.RecipeVersionId);
        Assert.Equal(1, instagram.Latest.RevisionNumber);

        // An unaccepted channel has no claim of currency to withdraw.
        Assert.Equal(ContentProposalStatus.Proposed, Channel(after, "pinterest").Status);

        // A system move: audited with no person behind it.
        await using var scope = fixture.ScopeFor(WsA);
        var db = SqlServerRecipeFixture.Db(scope);
        var resourceId = (await ChannelIdAsync(db, contextId, "instagram")).ToString("D");
        var audit = await db.AuditLogs.SingleAsync(
            entry => entry.ResourceId == resourceId && entry.Action == ContentAuditActions.SocialChannelMarkedStale, Ct);
        Assert.Null(audit.ActorUserId);
    }

    [Fact]
    public async Task A_replayed_recipe_change_marks_a_channel_once()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var contextId = await SeedContextAsync(WsA, recipe);
        var drafted = await GenerateAsync(WsA, contextId, "instagram", recipe);
        await DecideAsync(
            WsA, facade => facade.AcceptAsync(User, contextId, "instagram", Channel(drafted, "instagram").Latest.Id, Ct));
        await EditRecipeAsync(WsA, recipe.RecipeId);

        await using var scope = fixture.ScopeFor(WsA);
        var staleness = scope.ServiceProvider.GetRequiredService<IContentStalenessBusiness>();

        Assert.Equal(1, await staleness.ApplyRecipeChangeAsync(recipe.RecipeId, Ct));
        Assert.Equal(0, await staleness.ApplyRecipeChangeAsync(recipe.RecipeId, Ct));
    }

    [Fact]
    public async Task A_post_with_no_recipe_pin_is_never_made_stale_by_a_recipe()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var contextId = await SeedContextAsync(WsA, recipe: null);
        var drafted = await EditAsync(WsA, contextId, "threads", Caption, expected: null);
        await DecideAsync(
            WsA, facade => facade.AcceptAsync(User, contextId, "threads", Channel(drafted, "threads").Latest.Id, Ct));

        await EditRecipeAsync(WsA, recipe.RecipeId);
        await DispatchAsync();

        var after = Channel(await GetAsync(WsA, contextId), "threads");
        Assert.Null(after.Accepted!.RecipeVersionId);
        Assert.Equal(ContentProposalStatus.Accepted, after.Status);
        Assert.True(after.IsCurrent);
    }

    [Fact]
    public async Task Words_written_against_an_older_recipe_version_cannot_be_accepted_until_they_are_edited()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var contextId = await SeedContextAsync(WsA, recipe);
        var drafted = await GenerateAsync(WsA, contextId, "instagram", recipe);
        var generatedId = Channel(drafted, "instagram").Latest.Id;
        var second = await EditRecipeAsync(WsA, recipe.RecipeId);

        await using (var scope = fixture.ScopeFor(WsA))
        {
            var refused = await Facade(scope).AcceptAsync(User, contextId, "instagram", generatedId, Ct);
            Assert.Equal(ContentErrorCodes.SocialSourceStale, refused.Error!.Code);
        }

        // The creator's edit is written against the recipe as it stands now, so it is pinned there.
        var edited = await EditAsync(WsA, contextId, "instagram", "Updated for the new method.", generatedId);
        var editId = Channel(edited, "instagram").Latest.Id;
        Assert.Equal(second.Id, Channel(edited, "instagram").Latest.RecipeVersionId);

        var accepted = await DecideAsync(WsA, facade => facade.AcceptAsync(User, contextId, "instagram", editId, Ct));
        Assert.Equal(ContentProposalStatus.Accepted, Channel(accepted, "instagram").Status);
        Assert.True(Channel(accepted, "instagram").IsCurrent);
    }

    [Fact]
    public async Task Reaffirming_stale_words_writes_a_new_revision_pinned_to_the_latest_version()
    {
        var recipe = await CreateRecipeAsync(WsA);
        var contextId = await SeedContextAsync(WsA, recipe);
        var drafted = await GenerateAsync(WsA, contextId, "instagram", recipe);
        var acceptedId = Channel(drafted, "instagram").Latest.Id;
        await DecideAsync(WsA, facade => facade.AcceptAsync(User, contextId, "instagram", acceptedId, Ct));
        var second = await EditRecipeAsync(WsA, recipe.RecipeId);
        await DispatchAsync();

        // A stale channel is not re-accepted by the ordinary decision: that would hide that anyone looked.
        await using (var scope = fixture.ScopeFor(WsA))
        {
            var refused = await Facade(scope).AcceptAsync(User, contextId, "instagram", acceptedId, Ct);
            Assert.Equal(ContentErrorCodes.SocialDecisionConflict, refused.Error!.Code);
        }

        var reaffirmed = Channel(
            await DecideAsync(WsA, facade => facade.ReaffirmAsync(User, contextId, "instagram", Ct)), "instagram");

        Assert.Equal(ContentProposalStatus.Accepted, reaffirmed.Status);
        Assert.True(reaffirmed.IsCurrent);
        Assert.Null(reaffirmed.StaleSince);
        Assert.Equal(ContentStaleReasons.None, reaffirmed.StaleReasons);
        Assert.Equal(2, reaffirmed.Accepted!.RevisionNumber);
        Assert.Equal(ContentRevisionSource.Reaffirmed, reaffirmed.Accepted.Source);
        Assert.Equal(acceptedId, reaffirmed.Accepted.ParentRevisionId);
        Assert.Equal(Caption, reaffirmed.Accepted.Body);
        Assert.Equal(second.Id, reaffirmed.Accepted.RecipeVersionId);

        // History still says what was accepted against what.
        await using var check = fixture.ScopeFor(WsA);
        var original = await SqlServerRecipeFixture.Db(check).SocialRevisions.SingleAsync(r => r.Id == acceptedId, Ct);
        Assert.Equal(recipe.VersionId, original.RecipeVersionId);

        // And a late replay of the change does not flip what the creator has just reaffirmed.
        Assert.Equal(
            0, await check.ServiceProvider.GetRequiredService<IContentStalenessBusiness>().ApplyRecipeChangeAsync(recipe.RecipeId, Ct));
    }

    // ---- Channel keys ----

    [Fact]
    public async Task A_key_that_is_not_a_channel_is_refused_and_nothing_is_created()
    {
        var contextId = await SeedContextAsync(WsA, recipe: null);

        await using var scope = fixture.ScopeFor(WsA);
        var result = await Facade(scope).RecordEditAsync(
            new SocialEditedRevisionInput(contextId, "myspace", Caption, null, null), Ct);

        Assert.Equal(ContentErrorCodes.SocialChannelUnprocessable, result.Error!.Code);
        Assert.True(result.Error.FieldErrors.ContainsKey("channelKey"));
        Assert.Equal(0, await SqlServerRecipeFixture.Db(scope).SocialPackages.CountAsync(p => p.CreativeContextId == contextId, Ct));
    }

    [Fact]
    public async Task A_retired_key_already_stored_keeps_working_and_cannot_be_newly_chosen()
    {
        var contextId = await SeedContextAsync(WsA, recipe: null);
        var drafted = await EditAsync(WsA, contextId, "tiktok", Caption, expected: null);
        var firstRevision = Channel(drafted, "tiktok").Latest.Id;

        // The catalogue as it reads after tiktok and x are retired.
        var retired = new ContentChannelCatalog(
            [new("instagram", "Instagram"), new("tiktok", "TikTok", IsActive: false), new("x", "X", IsActive: false)]);

        await using var scope = fixture.ScopeFor(WsA);
        var business = Business(scope, catalogue: retired);

        var edited = await business.RecordEditAsync(
            new SocialEditedRevisionInput(contextId, "tiktok", "Still editable.", null, firstRevision), Ct);
        Assert.True(edited.Succeeded);

        var editId = Channel(edited.Value!, "tiktok").Latest.Id;
        var accepted = await business.AcceptAsync(User, contextId, "tiktok", editId, Ct);
        Assert.Equal(ContentProposalStatus.Accepted, Channel(accepted.Value!, "tiktok").Status);

        var newlyChosen = await business.RecordEditAsync(new SocialEditedRevisionInput(contextId, "x", Caption, null, null), Ct);
        Assert.Equal(ContentErrorCodes.SocialChannelUnprocessable, newlyChosen.Error!.Code);
    }

    // ---- Roles ----

    [Fact]
    public async Task A_contributor_drafts_but_does_not_decide_and_a_viewer_does_neither()
    {
        var contextId = await SeedContextAsync(WsA, recipe: null);

        await using var scope = fixture.ScopeFor(WsA);
        var contributor = Business(scope, role: WorkspaceRole.Contributor);
        var viewer = Business(scope, role: WorkspaceRole.Viewer);

        var drafted = await contributor.RecordEditAsync(new SocialEditedRevisionInput(contextId, "facebook", Caption, null, null), Ct);
        Assert.True(drafted.Succeeded);
        var revisionId = Channel(drafted.Value!, "facebook").Latest.Id;

        Assert.Equal(
            ContentErrorCodes.SocialForbidden,
            (await contributor.AcceptAsync(User, contextId, "facebook", revisionId, Ct)).Error!.Code);
        Assert.Equal(
            ContentErrorCodes.SocialForbidden,
            (await contributor.RejectAsync(User, contextId, "facebook", revisionId, Ct)).Error!.Code);
        Assert.Equal(
            ContentErrorCodes.SocialForbidden,
            (await viewer.RecordEditAsync(new SocialEditedRevisionInput(contextId, "facebook", "No.", null, revisionId), Ct)).Error!.Code);

        Assert.Equal(ContentProposalStatus.Proposed, await StatusAsync(WsA, contextId, "facebook"));
    }

    // ---- Isolation ----

    [Fact]
    public async Task One_workspace_can_neither_read_write_nor_decide_the_others_posts()
    {
        var contextA = await SeedContextAsync(WsA, recipe: null);
        var draftedA = await EditAsync(WsA, contextA, "instagram", "A's caption.", expected: null);
        var revisionA = Channel(draftedA, "instagram").Latest.Id;

        // The same channel for a context of B's own, so "not found" below is isolation and not emptiness.
        var contextB = await SeedContextAsync(WsB, recipe: null);
        await EditAsync(WsB, contextB, "instagram", "B's caption.", expected: null);

        await using var scope = fixture.ScopeFor(WsB);
        var facade = Facade(scope);

        Assert.Equal(ContentErrorCodes.SocialNotFound, (await facade.GetAsync(contextA, Ct)).Error!.Code);
        Assert.Equal(
            ContentErrorCodes.SocialNotFound,
            (await facade.RecordEditAsync(new SocialEditedRevisionInput(contextA, "instagram", "Mine now.", null, revisionA), Ct)).Error!.Code);
        Assert.Equal(
            ContentErrorCodes.SocialNotFound,
            (await facade.AcceptAsync(User, contextA, "instagram", revisionA, Ct)).Error!.Code);
        Assert.Equal(
            ContentErrorCodes.SocialNotFound,
            (await facade.RejectAsync(User, contextA, "instagram", revisionA, Ct)).Error!.Code);
        Assert.Equal(
            ContentErrorCodes.SocialNotFound,
            (await facade.ReaffirmAsync(User, contextA, "instagram", Ct)).Error!.Code);

        // Nor through the sets themselves: A's rows are not in what B's scope reads over.
        var db = SqlServerRecipeFixture.Db(scope);
        Assert.Null(await db.SocialRevisions.FirstOrDefaultAsync(r => r.Id == revisionA, Ct));
        Assert.DoesNotContain("A's caption.", await db.SocialRevisions.Select(r => r.Body).ToListAsync(Ct));
        Assert.Equal(0, await db.SocialPackages.CountAsync(p => p.CreativeContextId == contextA, Ct));

        // And A's post is exactly as A left it.
        var untouched = Channel(await GetAsync(WsA, contextA), "instagram");
        Assert.Equal(ContentProposalStatus.Proposed, untouched.Status);
        Assert.Equal("A's caption.", untouched.Latest.Body);
    }

    [Fact]
    public async Task A_post_cannot_be_pinned_to_another_workspaces_sources()
    {
        var recipeA = await CreateRecipeAsync(WsA);
        var proposalA = await SeedAiProposalAsync(WsA);
        var recipeB = await CreateRecipeAsync(WsB);
        var contextB = await SeedContextAsync(WsB, recipeB);

        await using var scope = fixture.ScopeFor(WsB);
        var facade = Facade(scope);

        // A's recipe is not one B's context names, so the pin is refused before it reaches a key.
        var foreignRecipe = await facade.RecordGeneratedAsync(
            Generated(contextB, "instagram", await SeedAiProposalAsync(WsB), recipeA.RecipeId, recipeA.VersionId), Ct);

        // A's AI proposal is refused by the workspace-paired key, with the same answer.
        var foreignProposal = await facade.RecordGeneratedAsync(
            Generated(contextB, "instagram", proposalA, recipeB.RecipeId, recipeB.VersionId), Ct);

        // The two brand pins are paired the same way: an id this workspace does not hold names nothing here,
        // whether or not some other workspace holds it.
        var unknownBrandRevision = await facade.RecordGeneratedAsync(
            Generated(contextB, "instagram", await SeedAiProposalAsync(WsB), recipeB.RecipeId, recipeB.VersionId)
                with { BrandProfileRevisionId = Guid.NewGuid() },
            Ct);
        var unknownStyleGuide = await facade.RecordGeneratedAsync(
            Generated(contextB, "instagram", await SeedAiProposalAsync(WsB), recipeB.RecipeId, recipeB.VersionId)
                with { BrandStyleGuideVersionId = Guid.NewGuid() },
            Ct);

        Assert.Equal(ContentErrorCodes.SocialSourceUnprocessable, foreignRecipe.Error!.Code);
        Assert.Equal(ContentErrorCodes.SocialSourceUnprocessable, foreignProposal.Error!.Code);
        Assert.Equal(ContentErrorCodes.SocialSourceUnprocessable, unknownBrandRevision.Error!.Code);
        Assert.Equal(ContentErrorCodes.SocialSourceUnprocessable, unknownStyleGuide.Error!.Code);
        Assert.Equal(foreignRecipe.Error.Message, foreignProposal.Error.Message);
        Assert.Equal(0, await SqlServerRecipeFixture.Db(scope).SocialPackages.CountAsync(p => p.CreativeContextId == contextB, Ct));
    }

    [Fact]
    public async Task A_recipe_change_in_one_workspace_never_touches_the_other_workspaces_posts()
    {
        var recipeA = await CreateRecipeAsync(WsA);
        var recipeB = await CreateRecipeAsync(WsB);
        var contextA = await SeedContextAsync(WsA, recipeA);
        var contextB = await SeedContextAsync(WsB, recipeB);

        foreach (var (workspaceId, contextId, recipe) in new[] { (WsA, contextA, recipeA), (WsB, contextB, recipeB) })
        {
            var drafted = await GenerateAsync(workspaceId, contextId, "instagram", recipe);
            await DecideAsync(
                workspaceId, facade => facade.AcceptAsync(User, contextId, "instagram", Channel(drafted, "instagram").Latest.Id, Ct));
        }

        await EditRecipeAsync(WsA, recipeA.RecipeId);
        await DispatchAsync();

        Assert.Equal(ContentProposalStatus.NeedsReview, await StatusAsync(WsA, contextA, "instagram"));
        Assert.Equal(ContentProposalStatus.Accepted, await StatusAsync(WsB, contextB, "instagram"));
        Assert.True(Channel(await GetAsync(WsB, contextB), "instagram").IsCurrent);

        // B asking about A's recipe finds nothing to mark: the sweep runs inside B's filter. The pins query is
        // asked directly as well, because the sweep stops at "no such recipe" before it would reach it.
        await using var scope = fixture.ScopeFor(WsB);
        var pins = scope.ServiceProvider.GetRequiredService<ISocialPackageRepository>();
        Assert.Empty(await pins.FindAcceptedPinsAsync(recipeA.RecipeId, Ct));
        Assert.Single(await pins.FindAcceptedPinsAsync(recipeB.RecipeId, Ct));
        Assert.Equal(
            0, await scope.ServiceProvider.GetRequiredService<IContentStalenessBusiness>().ApplyRecipeChangeAsync(recipeA.RecipeId, Ct));
    }

    // ---- What only the engine answers ----

    [Fact]
    public async Task A_context_has_one_package_and_a_package_one_slot_per_channel()
    {
        var contextId = await SeedContextAsync(WsA, recipe: null);
        await EditAsync(WsA, contextId, "instagram", Caption, expected: null);

        await using (var scope = fixture.ScopeFor(WsA))
        {
            var db = SqlServerRecipeFixture.Db(scope);
            db.SocialPackages.Add(new SocialPackage
            {
                Id = Guid.NewGuid(),
                CreativeContextId = contextId,
                CreatedByMembershipId = Guid.NewGuid(),
                CreatedAt = SqlServerRecipeFixture.Now,
                UpdatedAt = SqlServerRecipeFixture.Now,
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }

        await using (var scope = fixture.ScopeFor(WsA))
        {
            var db = SqlServerRecipeFixture.Db(scope);
            var packageId = (await db.SocialPackages.SingleAsync(p => p.CreativeContextId == contextId, Ct)).Id;
            db.SocialPackageChannels.Add(new SocialPackageChannel
            {
                Id = Guid.NewGuid(),
                SocialPackageId = packageId,
                ChannelKey = "instagram",
                Status = ContentProposalStatus.Proposed,
                CreatedAt = SqlServerRecipeFixture.Now,
                UpdatedAt = SqlServerRecipeFixture.Now,
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }
    }

    [Fact]
    public async Task A_decision_composed_against_a_slot_that_has_since_moved_is_refused()
    {
        var contextId = await SeedContextAsync(WsA, recipe: null);
        var drafted = await EditAsync(WsA, contextId, "pinterest", Caption, expected: null);
        var revisionId = Channel(drafted, "pinterest").Latest.Id;

        await using var first = fixture.ScopeFor(WsA);
        await using var second = fixture.ScopeFor(WsA);
        var firstLayer = first.ServiceProvider.GetRequiredService<ISocialPackageDataLayer>();
        var secondLayer = second.ServiceProvider.GetRequiredService<ISocialPackageDataLayer>();

        var readByFirst = (await firstLayer.FindForUpdateAsync(contextId, Ct))!;
        var readBySecond = (await secondLayer.FindForUpdateAsync(contextId, Ct))!;

        foreach (var package in new[] { readByFirst, readBySecond })
        {
            var channel = package.Channels.Single();
            channel.Status = ContentProposalStatus.Accepted;
            channel.AcceptedRevisionId = revisionId;
            channel.UpdatedAt = SqlServerRecipeFixture.Now.AddMinutes(1);
        }

        Assert.Equal(SocialPackageWrite.Saved, await firstLayer.SaveAsync(readByFirst, isNew: false, null, null, Ct));
        Assert.Equal(SocialPackageWrite.Stale, await secondLayer.SaveAsync(readBySecond, isNew: false, null, null, Ct));
    }

    [Fact]
    public async Task Deleting_a_workspace_still_succeeds_with_an_accepted_post_in_it()
    {
        // A slot and its revisions point at each other with restricted keys, and reach the workspace by two
        // different routes. If SQL Server checked those NO ACTION constraints mid-cascade rather than at the
        // end of the statement, a workspace holding one accepted post could never be erased.
        var workspaceId = Guid.NewGuid();

        await using (var seed = fixture.ScopeFor(WsA))
        {
            var db = SqlServerRecipeFixture.Db(seed);
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = "Doomed",
                Slug = $"doomed-{workspaceId:N}",
                CreatedAt = SqlServerRecipeFixture.Now,
            });
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = fixture.ScopeFor(workspaceId))
        {
            var db = SqlServerRecipeFixture.Db(scope);
            var context = CreativeContextSeeds.NewContext(SqlServerRecipeFixture.Now);
            var package = new SocialPackage
            {
                Id = Guid.NewGuid(),
                CreativeContextId = context.Id,
                CreatedByMembershipId = Guid.NewGuid(),
                CreatedAt = SqlServerRecipeFixture.Now,
                UpdatedAt = SqlServerRecipeFixture.Now,
            };
            var channel = new SocialPackageChannel
            {
                Id = Guid.NewGuid(),
                SocialPackageId = package.Id,
                ChannelKey = "instagram",
                Status = ContentProposalStatus.Proposed,
                CreatedAt = SqlServerRecipeFixture.Now,
                UpdatedAt = SqlServerRecipeFixture.Now,
            };
            var revision = new SocialRevision
            {
                Id = Guid.NewGuid(),
                SocialPackageChannelId = channel.Id,
                RevisionNumber = 1,
                Source = ContentRevisionSource.CreatorEdit,
                Body = Caption,
                CreatedByMembershipId = Guid.NewGuid(),
                CreatedAt = SqlServerRecipeFixture.Now,
            };
            package.Channels.Add(channel);

            db.CreativeContexts.Add(context);
            await db.SaveChangesAsync(Ct);
            db.SocialPackages.Add(package);
            db.SocialRevisions.Add(revision);
            await db.SaveChangesAsync(Ct);

            channel.Status = ContentProposalStatus.Accepted;
            channel.AcceptedRevisionId = revision.Id;
            await db.SaveChangesAsync(Ct);
        }

        await using var erase = fixture.ScopeFor(WsA);
        var unfiltered = SqlServerRecipeFixture.Db(erase);
        unfiltered.Workspaces.Remove(await unfiltered.Workspaces.SingleAsync(item => item.Id == workspaceId, Ct));
        await unfiltered.SaveChangesAsync(Ct);

        // Counted with raw SQL rather than IgnoreQueryFilters: the erasure is what is under test.
        foreach (var table in new[] { "SocialPackages", "SocialPackageChannels", "SocialRevisions" })
        {
            var remaining = await unfiltered.Database
                .SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM {table} WHERE WorkspaceId = {{0}}", workspaceId)
                .SingleAsync(Ct);

            Assert.Equal(0, remaining);
        }
    }

    // ---- Helpers ----

    private static ISocialPackageFacade Facade(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<ISocialPackageFacade>();

    /// <summary>The business built by hand, for the two things the fixture's container fixes: role and catalogue.</summary>
    private static SocialPackageBusiness Business(
        AsyncServiceScope scope, WorkspaceRole? role = null, IContentChannelCatalog? catalogue = null)
    {
        var services = scope.ServiceProvider;
        var workspace = services.GetRequiredService<IWorkspaceContext>();

        return new SocialPackageBusiness(
            services.GetRequiredService<ISocialPackageDataLayer>(),
            catalogue ?? services.GetRequiredService<IContentChannelCatalog>(),
            services.GetRequiredService<IContentChannelProfileCatalog>(),
            role is { } asRole ? new RoleOverride(workspace, asRole) : workspace,
            services.GetRequiredService<IClock>());
    }

    private static SocialChannelServiceModel Channel(SocialPackageServiceModel package, string key) =>
        package.Channels.Single(channel => channel.ChannelKey == key);

    private static SocialGeneratedRevisionInput Generated(
        Guid contextId, string channelKey, Guid aiProposalId, Guid? recipeId, Guid? recipeVersionId, string body = Caption) => new(
        contextId,
        channelKey,
        body,
        Limit: null,
        aiProposalId,
        "content.channel-posts",
        "1.0.0",
        "sha256:" + new string('b', 64),
        recipeId,
        recipeVersionId,
        BrandProfileRevisionId: null,
        BrandStyleGuideVersionId: null,
        CreativeContextVersion: "AAAAAAAAB9E=",
        ContextPackageChecksum: "sha256:" + new string('c', 64));

    private async Task<SocialPackageServiceModel> GenerateAsync(
        Guid workspaceId, Guid contextId, string channelKey, CreatedRecipe recipe, string body = Caption)
    {
        var proposalId = await SeedAiProposalAsync(workspaceId);

        await using var scope = fixture.ScopeFor(workspaceId);
        var result = await Facade(scope).RecordGeneratedAsync(
            Generated(contextId, channelKey, proposalId, recipe.RecipeId, recipe.VersionId, body), Ct);

        return Unwrap(result);
    }

    private async Task<SocialPackageServiceModel> EditAsync(
        Guid workspaceId, Guid contextId, string channelKey, string body, Guid? expected, SocialLimitResult? limit = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId);

        return Unwrap(await Facade(scope).RecordEditAsync(
            new SocialEditedRevisionInput(contextId, channelKey, body, limit, expected), Ct));
    }

    private async Task<SocialPackageServiceModel> DecideAsync(
        Guid workspaceId, Func<ISocialPackageFacade, Task<OperationResult<SocialPackageServiceModel>>> decide)
    {
        await using var scope = fixture.ScopeFor(workspaceId);

        return Unwrap(await decide(Facade(scope)));
    }

    private async Task<SocialPackageServiceModel> GetAsync(Guid workspaceId, Guid contextId)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        var result = await Facade(scope).GetAsync(contextId, Ct);

        Assert.True(result.Succeeded, result.Error?.Code);

        return result.Value!;
    }

    private static SocialPackageServiceModel Unwrap(OperationResult<SocialPackageServiceModel> result)
    {
        Assert.True(result.Succeeded, result.Error?.Code);

        return result.Value!;
    }

    private async Task<ContentProposalStatus> StatusAsync(Guid workspaceId, Guid contextId, string channelKey) =>
        Channel(await GetAsync(workspaceId, contextId), channelKey).Status;

    private static async Task<Guid> ChannelIdAsync(CreatorPantryDbContext db, Guid contextId, string channelKey) =>
        await (
            from package in db.SocialPackages
            where package.CreativeContextId == contextId
            join channel in db.SocialPackageChannels on package.Id equals channel.SocialPackageId
            where channel.ChannelKey == channelKey
            select channel.Id)
            .SingleAsync(Ct);

    /// <summary>A creative context, naming the recipe when there is one.</summary>
    private async Task<Guid> SeedContextAsync(Guid workspaceId, CreatedRecipe? recipe)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        var db = SqlServerRecipeFixture.Db(scope);
        var context = CreativeContextSeeds.NewContext(SqlServerRecipeFixture.Now);
        db.CreativeContexts.Add(context);
        await db.SaveChangesAsync(Ct);

        if (recipe is not null)
        {
            var reference = CreativeContextSeeds.Reference(
                context, CreativeContextReferenceKind.Recipe, 0, SqlServerRecipeFixture.Now);
            reference.RecipeId = recipe.RecipeId;
            db.CreativeContextReferences.Add(reference);
            await db.SaveChangesAsync(Ct);
        }

        return context.Id;
    }

    private async Task<Guid> SeedAiProposalAsync(Guid workspaceId)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        var db = SqlServerRecipeFixture.Db(scope);
        var operationId = await CreativeContextSeeds.ConceptRequestAsync(db, SqlServerRecipeFixture.Now, Ct);
        var proposal = new AiProposal
        {
            Id = Guid.NewGuid(),
            AiOperationId = operationId,
            OutputSchemaVersion = "content.channel-posts.v1",
            PromptTemplateId = "content.channel-posts",
            PromptTemplateVersion = "1.0.0",
            PromptTemplateBodyChecksum = "sha256:" + new string('b', 64),
            ProviderName = "test-provider",
            ModelName = "test-model",
            CreatedAt = SqlServerRecipeFixture.Now,
        };
        db.AiProposals.Add(proposal);
        await db.SaveChangesAsync(Ct);

        return proposal.Id;
    }

    private async Task<CreatedRecipe> CreateRecipeAsync(Guid workspaceId)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        var tagId = workspaceId == WsA ? SqlServerRecipeFixture.TagIdA : SqlServerRecipeFixture.TagIdB;

        return await scope.ServiceProvider.GetRequiredService<IRecipeDataLayer>().CreateAsync(
            SqlServerRecipeFixture.NewRecipe(
                "Olive oil cake", tagId, SqlServerRecipeFixture.MediaAssetIdFor(workspaceId)),
            new RecipeVersionFacts(RecipeVersionSource.CreatorEdit, RecipeVersionReadiness.Draft, "Created."),
            [],
            Ct);
    }

    private async Task<RecipeVersion> EditRecipeAsync(Guid workspaceId, Guid recipeId)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        var dataLayer = scope.ServiceProvider.GetRequiredService<IRecipeDataLayer>();
        var loaded = (await dataLayer.GetForUpdateAsync(recipeId, Ct))!;
        loaded.Recipe.Recipe.Title = $"Edited {Guid.NewGuid():N}";

        var outcome = await dataLayer.UpdateAsync(loaded, EditFacts, null, null, Ct);
        Assert.False(outcome.Conflicted);

        return outcome.Version!;
    }

    /// <summary>One dispatch pass, as the worker's polling loop would run it.</summary>
    private async Task DispatchAsync()
    {
        await using var scope = fixture.ScopeFor(WsA);
        await scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>().DispatchDueAsync(Ct);
    }

    /// <summary>The resolved workspace with a different role, for the moves the fixture's Owner cannot be refused.</summary>
    private sealed class RoleOverride(IWorkspaceContext inner, WorkspaceRole role) : IWorkspaceContext
    {
        public bool IsResolved => inner.IsResolved;

        public Guid WorkspaceId => inner.WorkspaceId;

        public string WorkspaceSlug => inner.WorkspaceSlug;

        public Guid MembershipId => inner.MembershipId;

        public string AccountId => inner.AccountId;

        public WorkspaceRole Role => role;
    }
}
