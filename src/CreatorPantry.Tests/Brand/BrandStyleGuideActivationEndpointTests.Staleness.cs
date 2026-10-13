using System.Net;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Outbox.Events;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Tests.Content;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// LF.6.1 from the brand side, in a whole host: activating a style guide version commits the event that
/// announces it, and the brand module's own handler — reading the active version when the message is
/// delivered — marks accepted long-form articles written under another.
/// </summary>
/// <remarks>
/// What the content module does with an active version it is handed is
/// <c>LongFormStalenessPropagationTests</c>'. These are the two things only a host with the brand module can
/// show: the activation and its event are one commit, and the path from one to the other is real.
/// </remarks>
public sealed partial class BrandStyleGuideActivationEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_activation_commits_one_event_naming_the_version_it_activated()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await ApprovedGuideAsync(client, _fixture.WorkspaceA);

        await Activated(client, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true });

        var message = Assert.Single(await ActivationEventsAsync(guide.VersionId));
        var change = BrandStyleGuideActivatedEvent.TryParse(message.PayloadJson)!;

        Assert.Equal(guide.VersionId, message.CorrelationId);
        Assert.Equal(_fixture.WorkspaceA.Id, change.WorkspaceId);
        Assert.Equal(guide.VersionId, change.BrandStyleGuideVersionId);
        Assert.NotEqual(Guid.Empty, change.InitiatedByMembershipId);

        // Identifiers and nothing else: not the guide's name, and not a word of it.
        Assert.DoesNotContain("House voice", message.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Warm, direct", message.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repointing_the_default_commits_an_event_too_and_an_activation_that_loses_a_race_commits_none()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var first = await ApprovedGuideAsync(client, _fixture.WorkspaceA);
        var second = await ApprovedGuideAsync(client, _fixture.WorkspaceA, "Second voice");
        var third = await ApprovedGuideAsync(client, _fixture.WorkspaceA, "Third voice");

        // The first activation inserts the workspace's default; the second repoints it. Each announces itself.
        await Activated(client, _fixture.WorkspaceA, first.Id, 1, new { confirmed = true });
        await Activated(
            client, _fixture.WorkspaceA, second.Id, 1, new { confirmed = true, expectedActiveVersionId = first.VersionId });

        Assert.Single(await ActivationEventsAsync(first.VersionId));
        Assert.Single(await ActivationEventsAsync(second.VersionId));
        Assert.Equal(second.VersionId, (await DefaultAsync(_fixture.WorkspaceA))!.BrandStyleGuideVersionId);

        // A writer that read "no default" and tries to insert one now loses on the primary key. Its staged
        // event went with its cleared tracker: nothing announces an activation that did not happen.
        var lost = await WithDataLayerAsync(_fixture.WorkspaceA, (layer, _) => layer.ActivateAsync(
            current: null,
            third.VersionId,
            "Mine instead",
            Guid.NewGuid(),
            Moment.AddDays(1),
            AuditOf(third.Id),
            Ct));

        Assert.Equal(BrandStyleGuideActivationWrite.Conflict, lost);
        Assert.Empty(await ActivationEventsAsync(third.VersionId));
        Assert.Equal(second.VersionId, (await DefaultAsync(_fixture.WorkspaceA))!.BrandStyleGuideVersionId);
    }

    [Fact]
    public async Task An_activation_that_is_refused_commits_no_event()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        // Created and never approved: there is nothing to activate.
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "Unapproved voice",
            questionnaire = new { voice = "Warm, direct, never fussy." },
        });

        var refused = await ActivateAsync(client, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true });

        Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
        Assert.Null(await DefaultAsync(_fixture.WorkspaceA));
        Assert.Empty(await ActivationEventsAsync(guide.VersionId));
    }

    [Fact]
    public async Task Activating_a_version_marks_accepted_articles_written_under_another_and_only_this_workspaces()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await ApprovedGuideAsync(client, _fixture.WorkspaceA);

        // Written with no guide in force, in each workspace, and one written under the version about to be
        // activated.
        var underNone = await SeedAcceptedArticleAsync(_fixture.WorkspaceA, voice: null);
        var underThis = await SeedAcceptedArticleAsync(_fixture.WorkspaceA, voice: guide.VersionId);
        var elsewhere = await SeedAcceptedArticleAsync(_fixture.WorkspaceB, voice: null);

        await Activated(client, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true });

        // Committed, and waiting: the article still reads Accepted until the event is delivered.
        Assert.Single(await ActivationEventsAsync(guide.VersionId));

        await DispatchOutboxAsync();

        var marked = await ArticleAsync(_fixture.WorkspaceA, underNone);
        Assert.Equal(LongFormDraftStatus.NeedsReview, marked.Status);
        Assert.Equal(ContentStaleReasons.VoiceChanged, marked.StaleReasons);
        Assert.NotNull(marked.StaleSince);

        // Written under the active version: current. Another workspace's: not this activation's business.
        Assert.Equal(LongFormDraftStatus.Accepted, (await ArticleAsync(_fixture.WorkspaceA, underThis)).Status);
        Assert.Equal(LongFormDraftStatus.Accepted, (await ArticleAsync(_fixture.WorkspaceB, elsewhere)).Status);

        // Delivered again, straight to the handler: it reads the active version afresh and finds nothing to do.
        var message = Assert.Single(await ActivationEventsAsync(guide.VersionId));
        await DeliverAsync(message.Type, message.PayloadJson, message.CorrelationId);

        var again = await ArticleAsync(_fixture.WorkspaceA, underNone);
        Assert.Equal(marked.StaleSince, again.StaleSince);
        Assert.Equal(Convert.ToHexString(marked.RowVersion), Convert.ToHexString(again.RowVersion));
        Assert.Equal(LongFormDraftStatus.Accepted, (await ArticleAsync(_fixture.WorkspaceA, underThis)).Status);
    }

    [Fact]
    public async Task An_event_naming_a_version_that_is_not_the_active_one_is_decided_against_the_active_one()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await ApprovedGuideAsync(client, _fixture.WorkspaceA);
        await Activated(client, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true });
        await DispatchOutboxAsync();

        var current = await SeedAcceptedArticleAsync(_fixture.WorkspaceA, voice: guide.VersionId);

        // Workspace B has never activated a guide, and has an article written with none: current, there.
        var elsewhere = await SeedAcceptedArticleAsync(_fixture.WorkspaceB, voice: null);

        // A forged or long-delayed event naming some other version, and one claiming the other workspace for
        // A's version. The handler does not take either's word for what is active: it resolves the workspace
        // the payload claims and reads that workspace's own active version.
        foreach (var payload in new[]
        {
            new BrandStyleGuideActivatedEvent(_fixture.WorkspaceA.Id, Guid.NewGuid(), Guid.NewGuid()).Serialize(),
            new BrandStyleGuideActivatedEvent(_fixture.WorkspaceB.Id, guide.VersionId, Guid.NewGuid()).Serialize(),
        })
        {
            await DeliverAsync(BrandStyleGuideActivatedEvent.MessageType, payload, Guid.NewGuid());
        }

        Assert.Equal(LongFormDraftStatus.Accepted, (await ArticleAsync(_fixture.WorkspaceA, current)).Status);

        // B was re-examined against B's own state — no active guide — and A's version made no difference to it.
        var other = await ArticleAsync(_fixture.WorkspaceB, elsewhere);
        Assert.Equal(LongFormDraftStatus.Accepted, other.Status);
        Assert.Equal(ContentStaleReasons.None, other.StaleReasons);
    }

    [Fact]
    public async Task A_malformed_activation_payload_fails_the_delivery_so_it_is_retried()
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredKeyedService<IOutboxMessageHandler>(BrandStyleGuideActivatedEvent.MessageType);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new OutboxMessageEnvelope(Guid.NewGuid(), BrandStyleGuideActivatedEvent.MessageType, "{ not json", Guid.NewGuid(), 1), Ct));
    }

    // ---- Helpers ----

    private async Task<List<OutboxMessage>> ActivationEventsAsync(Guid versionId)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var id = versionId.ToString();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().OutboxMessages
            .AsNoTracking()
            .Where(message => message.Type == BrandStyleGuideActivatedEvent.MessageType && message.PayloadJson.Contains(id))
            .ToListAsync(Ct);
    }

    /// <summary>One dispatch pass, as the worker's polling loop would run it.</summary>
    private async Task DispatchOutboxAsync()
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>().DispatchDueAsync(Ct);
    }

    private async Task DeliverAsync(string type, string payloadJson, Guid correlationId)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredKeyedService<IOutboxMessageHandler>(type);

        await handler.HandleAsync(new OutboxMessageEnvelope(Guid.NewGuid(), type, payloadJson, correlationId, 2), Ct);
    }

    private Task<LongFormDraft> ArticleAsync(SeededWorkspace workspace, Guid draftId) =>
        InScopeAsync(workspace, db => db.LongFormDrafts.AsNoTracking().SingleAsync(draft => draft.Id == draftId, Ct));

    /// <summary>An accepted long-form article with no recipe, written under the given style guide version.</summary>
    private Task<Guid> SeedAcceptedArticleAsync(SeededWorkspace workspace, Guid? voice) =>
        InScopeAsync(workspace, async db =>
        {
            var context = CreativeContextSeeds.NewContext(Moment);
            db.CreativeContexts.Add(context);
            await db.SaveChangesAsync(Ct);

            var draft = new LongFormDraft
            {
                Id = Guid.NewGuid(),
                CreativeContextId = context.Id,
                Kind = LongFormKind.BlogArticle,
                Status = LongFormDraftStatus.Draft,
                CreatedByMembershipId = Guid.NewGuid(),
                CreatedAt = Moment,
                UpdatedAt = Moment,
            };

            var revision = new LongFormRevision
            {
                Id = Guid.NewGuid(),
                LongFormDraftId = draft.Id,
                RevisionNumber = 1,
                Source = ContentRevisionSource.CreatorEdit,
                BrandStyleGuideVersionId = voice,
                SchemaVersion = LongFormDocument.CurrentSchemaVersion,
                Content = new LongFormDocument(
                [
                    new(Guid.NewGuid(), LongFormSectionRole.Hook, "Start here", "Open.", "Accepted words.", false, null,
                        new(ContentRevisionSource.CreatorEdit, null, null, null, null, null, 1)),
                ]).Serialize(),
                CreatedByMembershipId = Guid.NewGuid(),
                CreatedAt = Moment,
            };

            db.AddRange(draft, revision);
            await db.SaveChangesAsync(Ct);

            draft.AcceptedRevisionId = revision.Id;
            draft.AcceptedAt = Moment;
            draft.Status = LongFormDraftStatus.Accepted;
            await db.SaveChangesAsync(Ct);

            return draft.Id;
        });
}
