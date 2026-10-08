using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.Brand;
using CreatorPantry.Domain.Modules.Media;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// 11A.18's acceptance step over a real SQLite-backed data layer and the <em>real</em> brand module: full and
/// partial acceptance, rejection, the creator's own rewrites, the stale-guide refusal, replay, atomic rollback,
/// and two-workspace isolation.
/// </summary>
/// <remarks>
/// The brand module is wired for real rather than stubbed, because the thing worth proving is that accepted
/// guidance goes through the same write path a version the creator typed would and commits in one transaction
/// with the decision. A stub would prove the calls were made and nothing about whether they can be made
/// together.
/// </remarks>
public sealed class AiBrandGuideAcceptanceTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private const string UserId = "user-1";
    private const string Checksum = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>The rule the seeded guide already holds, so accepting the same one again is a no-op.</summary>
    private const string ExistingRule = "Say it plainly.";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public AiBrandGuideAcceptanceTests()
    {
        _connection.Open();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // A real key: the options validator refuses anything shorter, and a facade that could not be
                // constructed would fail every test here for a reason none of them is about.
                ["Idempotency:FingerprintKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddOutbox()
            .AddLogging()
            .AddDistributedMemoryCache()
            .AddApplicationCache()
            .AddApplicationTime()
            .AddSingleton<IClock>(new StoppedClock())
            .AddSingleton(new AiTaskOptions())
            .AddBrandModule()

            // The brand profile facade asks the Media module whether a submitted logo is in this workspace's
            // library (12.10k), so that lookup has to be resolvable wherever the brand module is composed.
            .AddMediaModule()
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<IAiBrandGuideAcceptanceBusiness, AiBrandGuideAcceptanceBusiness>()
            .AddAiUsageModule()
            .AddIdempotency(configuration)
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Database.EnsureCreated();

        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "workspace-a", CreatedAt = Now },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "workspace-b", CreatedAt = Now });

        db.SaveChanges();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ---- accepting everything --------------------------------------------------------------------------

    [Fact]
    public async Task Accepting_every_item_writes_one_new_draft_version()
    {
        var seeded = await SeedAsync(WorkspaceA);

        var result = await AcceptAllAsync(WorkspaceA, seeded);

        Assert.True(result.Succeeded, result.Error?.Message);
        var accepted = result.Value!;

        Assert.Equal(AiOperationStatus.Accepted, accepted.Status);
        Assert.Equal(seeded.GuideId, accepted.GuideId);
        Assert.False(accepted.Replayed);

        var written = accepted.Written;
        Assert.NotNull(written);
        Assert.Equal(2, written.GuideVersionNumber);
        Assert.Equal(1, written.ParentVersionNumber);

        Assert.Equal(1, await CountVersionsAsync(seeded.GuideId) - 1);
    }

    [Fact]
    public async Task The_new_version_holds_the_accepted_guidance_and_keeps_what_the_proposal_did_not_touch()
    {
        var seeded = await SeedAsync(WorkspaceA);

        await AcceptAllAsync(WorkspaceA, seeded);

        var version = await ReadWorkingVersionAsync(WorkspaceA, seeded.GuideId);

        // Replaced: the proposal spoke to Voice, and the creator took it.
        Assert.Equal(
            "A friend who happens to cook.",
            version.Sections.Single(section => section.SectionKey is BrandStyleGuideSectionKey.Voice).Body);

        // Added: keys the guide had nothing under.
        Assert.Equal(
            "Warm, but never fussy.",
            version.Sections.Single(section => section.SectionKey is BrandStyleGuideSectionKey.Tone).Body);

        var variant = version.Sections.Single(
            section => section.SectionKey is BrandStyleGuideSectionKey.ChannelVariant);
        Assert.Equal("instagram", variant.ChannelKey);

        // Untouched: the creator's own Audience section was not in the proposal and travels through.
        Assert.Equal(
            "Home cooks in a hurry.",
            version.Sections.Single(section => section.SectionKey is BrandStyleGuideSectionKey.Audience).Body);
    }

    /// <summary>
    /// The rule the guide already held is not duplicated, and is reported rather than quietly dropped.
    /// </summary>
    [Fact]
    public async Task A_rule_the_guide_already_holds_is_reported_rather_than_added_twice()
    {
        var seeded = await SeedAsync(WorkspaceA);

        var accepted = (await AcceptAllAsync(WorkspaceA, seeded)).Value!;

        Assert.Equal(1, accepted.Written!.RulesAdded);
        Assert.Equal(1, accepted.Written.RulesAlreadyPresent);

        var version = await ReadWorkingVersionAsync(WorkspaceA, seeded.GuideId);

        Assert.Equal(2, version.Rules.Count);
        Assert.Single(version.Rules, rule => rule.Text == ExistingRule);
        Assert.Single(version.Rules, rule => rule.Kind is BrandStyleGuideRuleKind.Dont);
    }

    /// <summary>A conflict is a finding about the creator's own material, and a guide has nowhere to put one.</summary>
    [Fact]
    public async Task An_accepted_conflict_produces_no_guidance_and_is_counted_as_dropped()
    {
        var seeded = await SeedAsync(WorkspaceA);

        var accepted = (await AcceptAllAsync(WorkspaceA, seeded)).Value!;

        Assert.Equal(1, accepted.DroppedItemCount);

        var version = await ReadWorkingVersionAsync(WorkspaceA, seeded.GuideId);

        Assert.DoesNotContain(version.Sections, section => section.Body.Contains("disagree", StringComparison.Ordinal));
        Assert.DoesNotContain(version.Rules, rule => rule.Text.Contains("disagree", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_new_version_cites_the_source_versions_the_accepted_guidance_rests_on()
    {
        var seeded = await SeedAsync(WorkspaceA);

        var accepted = (await AcceptAllAsync(WorkspaceA, seeded)).Value!;

        // One document, cited once, however many accepted items pointed at its passages.
        Assert.Equal(1, accepted.Written!.CitedSourceCount);
        Assert.Equal(0, accepted.Written.StaleSourceCount);

        var version = await ReadWorkingVersionAsync(WorkspaceA, seeded.GuideId);

        Assert.Equal(
            [new BrandStyleGuideSourceServiceModel(seeded.DocumentId, 1)],
            version.SourceDocuments);
    }

    /// <summary>Acceptance writes a draft. It does not approve the version and does not activate it.</summary>
    [Fact]
    public async Task Acceptance_approves_nothing_and_activates_nothing()
    {
        var seeded = await SeedAsync(WorkspaceA);

        await AcceptAllAsync(WorkspaceA, seeded);

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        Assert.Empty(await db.BrandStyleGuideApprovals.AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.BrandStyleGuideDefaults.AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken));

        var read = await ReadGuideAsync(WorkspaceA, seeded.GuideId);
        Assert.Null(read.WorkingVersion.Approval);
        Assert.Null(read.ActiveVersion);
    }

    [Fact]
    public async Task Every_change_of_an_accepted_proposal_is_recorded_as_taken()
    {
        var seeded = await SeedAsync(WorkspaceA);

        await AcceptAllAsync(WorkspaceA, seeded);

        var changes = await LoadChangesAsync(WorkspaceA, seeded.ProposalId);

        // Including the metadata rows: an item's dimension is accepted with the item, or the record would say a
        // section was taken and its own dimension declined.
        Assert.All(changes, change => Assert.Equal(AiChangeDisposition.Accepted, change.Disposition));
        Assert.All(changes, change => Assert.NotNull(change.DecidedAt));
        Assert.All(changes, change => Assert.NotNull(change.DecidedByMembershipId));
    }

    // ---- partial acceptance ----------------------------------------------------------------------------

    [Fact]
    public async Task Accepting_one_item_writes_only_that_one_and_leaves_the_creators_own_words_alone()
    {
        var seeded = await SeedAsync(WorkspaceA);

        var result = await AcceptAsync(WorkspaceA, new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = [seeded.ToneItemId],
        }, seeded.OperationId);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(AiOperationStatus.PartiallyAccepted, result.Value!.Status);
        Assert.Equal(1, result.Value.AcceptedItemCount);
        Assert.Equal(5, result.Value.RejectedItemCount);
        Assert.Equal(1, result.Value.Written!.SectionsAdded);
        Assert.Equal(0, result.Value.Written.SectionsReplaced);

        var version = await ReadWorkingVersionAsync(WorkspaceA, seeded.GuideId);

        Assert.Equal(
            "Warm, but never fussy.",
            version.Sections.Single(section => section.SectionKey is BrandStyleGuideSectionKey.Tone).Body);

        // The proposal offered a Voice section and the creator did not take it, so their own words stand.
        Assert.Equal(
            "Plain and unhurried.",
            version.Sections.Single(section => section.SectionKey is BrandStyleGuideSectionKey.Voice).Body);
    }

    [Fact]
    public async Task The_items_a_partial_acceptance_declined_are_recorded_as_declined()
    {
        var seeded = await SeedAsync(WorkspaceA);

        await AcceptAsync(WorkspaceA, new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = [seeded.ToneItemId],
        }, seeded.OperationId);

        var changes = await LoadChangesAsync(WorkspaceA, seeded.ProposalId);

        Assert.Equal(
            AiChangeDisposition.Accepted,
            changes.Single(change => change.Id == seeded.ToneItemId).Disposition);
        Assert.Equal(
            AiChangeDisposition.Rejected,
            changes.Single(change => change.Id == seeded.VoiceItemId).Disposition);

        // Nothing is left pending: a terminal operation with a pending change would mean nobody ever decided.
        Assert.DoesNotContain(changes, change => change.Disposition is AiChangeDisposition.Pending);
    }

    /// <summary>
    /// Accepting guidance that says what the guide already says writes no version — the no-op rule a creator's
    /// own edit follows — and still records the decision, because the creator did make one.
    /// </summary>
    [Fact]
    public async Task Accepting_only_guidance_the_guide_already_holds_writes_no_version()
    {
        var seeded = await SeedAsync(WorkspaceA);

        var result = await AcceptAsync(WorkspaceA, new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = [seeded.ExistingRuleItemId],
        }, seeded.OperationId);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Null(result.Value!.Written);
        Assert.Equal(AiOperationStatus.PartiallyAccepted, result.Value.Status);
        Assert.Equal(1, await CountVersionsAsync(seeded.GuideId));

        var operation = await LoadOperationAsync(WorkspaceA, seeded.OperationId);
        Assert.Equal(AiOperationStatus.PartiallyAccepted, operation.Status);
        Assert.NotNull(operation.CompletedAt);
    }

    /// <summary>Accepting findings alone produces no guidance, so there is nothing to write.</summary>
    [Fact]
    public async Task Accepting_only_a_conflict_writes_no_version_and_counts_it_dropped()
    {
        var seeded = await SeedAsync(WorkspaceA);

        var result = await AcceptAsync(WorkspaceA, new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = [seeded.ConflictItemId],
        }, seeded.OperationId);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Null(result.Value!.Written);
        Assert.Equal(1, result.Value.DroppedItemCount);
        Assert.Equal(1, await CountVersionsAsync(seeded.GuideId));
    }

    // ---- rejection -------------------------------------------------------------------------------------

    [Fact]
    public async Task Rejecting_writes_no_version_and_records_every_item_declined()
    {
        var seeded = await SeedAsync(WorkspaceA);

        var result = await AcceptAsync(
            WorkspaceA,
            new AcceptBrandGuideProposalViewModel { Decision = AiDispositionDecision.Reject },
            seeded.OperationId);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(AiOperationStatus.Rejected, result.Value!.Status);
        Assert.Null(result.Value.Written);
        Assert.Equal(0, result.Value.AcceptedItemCount);
        Assert.Equal(6, result.Value.RejectedItemCount);

        Assert.Equal(1, await CountVersionsAsync(seeded.GuideId));

        var changes = await LoadChangesAsync(WorkspaceA, seeded.ProposalId);
        Assert.All(changes, change => Assert.Equal(AiChangeDisposition.Rejected, change.Disposition));
    }

    // ---- the creator's own rewrites --------------------------------------------------------------------

    [Fact]
    public async Task A_rewritten_section_goes_into_the_guide_instead_of_the_models_words()
    {
        var seeded = await SeedAsync(WorkspaceA);

        var result = await AcceptAsync(WorkspaceA, new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = [seeded.ToneItemId],
            Edits = [new AiBrandGuideEditViewModel { ChangeId = seeded.ToneItemId, Value = "Dry, and a bit wry." }],
        }, seeded.OperationId);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(1, result.Value!.RewrittenItemCount);

        var version = await ReadWorkingVersionAsync(WorkspaceA, seeded.GuideId);

        Assert.Equal(
            "Dry, and a bit wry.",
            version.Sections.Single(section => section.SectionKey is BrandStyleGuideSectionKey.Tone).Body);
    }

    [Fact]
    public async Task A_rewrite_of_guidance_the_creator_is_not_accepting_is_refused()
    {
        var seeded = await SeedAsync(WorkspaceA);

        var result = await AcceptAsync(WorkspaceA, new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = [seeded.ToneItemId],
            Edits = [new AiBrandGuideEditViewModel { ChangeId = seeded.VoiceItemId, Value = "Mine." }],
        }, seeded.OperationId);

        Assert.False(result.Succeeded);
        Assert.Equal(AiBrandGuideAcceptanceErrors.SelectionInvalid, result.Error!.Code);
        Assert.Equal(1, await CountVersionsAsync(seeded.GuideId));
    }

    /// <summary>
    /// A finding becomes no guidance, so a rewrite of one would be taken and discarded. Refused instead.
    /// </summary>
    [Fact]
    public async Task A_rewrite_of_a_finding_is_refused_rather_than_silently_dropped()
    {
        var seeded = await SeedAsync(WorkspaceA);

        var result = await AcceptAsync(WorkspaceA, new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = [seeded.ConflictItemId],
            Edits = [new AiBrandGuideEditViewModel { ChangeId = seeded.ConflictItemId, Value = "Mine." }],
        }, seeded.OperationId);

        Assert.False(result.Succeeded);
        Assert.Equal(AiBrandGuideAcceptanceErrors.SelectionInvalid, result.Error!.Code);
    }

    // ---- selection ------------------------------------------------------------------------------------

    /// <summary>What makes accept-all a confirmation rather than a flag.</summary>
    [Fact]
    public async Task Accepting_everything_without_naming_every_item_is_refused()
    {
        var seeded = await SeedAsync(WorkspaceA);

        var result = await AcceptAsync(WorkspaceA, new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptAll,
            AcceptedChangeIds = [seeded.ToneItemId],
        }, seeded.OperationId);

        Assert.False(result.Succeeded);
        Assert.Equal(AiBrandGuideAcceptanceErrors.SelectionInvalid, result.Error!.Code);
        Assert.Equal(1, await CountVersionsAsync(seeded.GuideId));
    }

    /// <summary>
    /// A metadata row is the shape of the item a creator is looking at, not a thing to accept on its own.
    /// </summary>
    [Fact]
    public async Task Naming_a_metadata_row_on_its_own_is_refused()
    {
        var seeded = await SeedAsync(WorkspaceA);

        var result = await AcceptAsync(WorkspaceA, new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = [seeded.ToneDimensionRowId],
        }, seeded.OperationId);

        Assert.False(result.Succeeded);
        Assert.Equal(AiBrandGuideAcceptanceErrors.SelectionInvalid, result.Error!.Code);
        Assert.Contains("carries its text", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_item_from_another_proposal_cannot_be_accepted()
    {
        var seeded = await SeedAsync(WorkspaceA);
        var other = await SeedAsync(WorkspaceA);

        var result = await AcceptAsync(WorkspaceA, new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = [other.ToneItemId],
        }, seeded.OperationId);

        Assert.False(result.Succeeded);
        Assert.Equal(AiBrandGuideAcceptanceErrors.SelectionInvalid, result.Error!.Code);
    }

    // ---- staleness -------------------------------------------------------------------------------------

    /// <summary>
    /// The restriction's own words: a stale source guide version cannot be silently rebased. The guidance was
    /// explained by answers the creator has since rewritten, so neither laying it over the newer version nor
    /// branching from the older one is honest.
    /// </summary>
    [Fact]
    public async Task Guidance_cannot_be_accepted_onto_a_guide_that_has_been_edited_since()
    {
        var seeded = await SeedAsync(WorkspaceA);

        await AddVersionAsync(WorkspaceA, seeded.GuideId, versionNumber: 2);

        var result = await AcceptAllAsync(WorkspaceA, seeded);

        Assert.False(result.Succeeded);
        Assert.Equal(BrandErrorCodes.GuideWorkingVersionConflict, result.Error!.Code);
        Assert.Equal(2, await CountVersionsAsync(seeded.GuideId));
    }

    /// <summary>Nothing at all is written when the guide has moved on — not even the decision.</summary>
    [Fact]
    public async Task A_refused_acceptance_leaves_the_proposal_undecided()
    {
        var seeded = await SeedAsync(WorkspaceA);
        await AddVersionAsync(WorkspaceA, seeded.GuideId, versionNumber: 2);

        await AcceptAllAsync(WorkspaceA, seeded);

        var operation = await LoadOperationAsync(WorkspaceA, seeded.OperationId);
        var changes = await LoadChangesAsync(WorkspaceA, seeded.ProposalId);

        Assert.Equal(AiOperationStatus.Proposed, operation.Status);
        Assert.Null(operation.CompletedAt);
        Assert.All(changes, change => Assert.Equal(AiChangeDisposition.Pending, change.Disposition));
        Assert.Empty(await LoadFeedbackAsync(WorkspaceA, seeded.ProposalId));
    }

    /// <summary>
    /// A replaced source document is <em>not</em> a refusal. The citation stays pinned to the version the
    /// proposal read — never re-pointed at the newer text — and the staleness is reported so the creator knows
    /// the version cannot be activated as it stands.
    /// </summary>
    [Fact]
    public async Task A_cited_document_replaced_since_is_cited_at_the_pinned_version_and_reported_stale()
    {
        var seeded = await SeedAsync(WorkspaceA);

        await ReplaceDocumentAsync(WorkspaceA, seeded.DocumentId);

        var result = await AcceptAllAsync(WorkspaceA, seeded);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(1, result.Value!.Written!.StaleSourceCount);

        var version = await ReadWorkingVersionAsync(WorkspaceA, seeded.GuideId);

        Assert.Equal(
            [new BrandStyleGuideSourceServiceModel(seeded.DocumentId, 1)],
            version.SourceDocuments);
    }

    // ---- replay ----------------------------------------------------------------------------------------

    /// <summary>The restriction's own words: a replay creates no duplicate version.</summary>
    [Fact]
    public async Task Accepting_the_same_guidance_twice_writes_one_version()
    {
        var seeded = await SeedAsync(WorkspaceA);

        var first = await AcceptAllAsync(WorkspaceA, seeded);
        var second = await AcceptAllAsync(WorkspaceA, seeded);

        Assert.True(second.Succeeded, second.Error?.Message);
        Assert.False(first.Value!.Replayed);
        Assert.True(second.Value!.Replayed);

        // The replay names no version, deliberately: the caller has the guide's id and reads it.
        Assert.Null(second.Value.Written);
        Assert.Equal(2, await CountVersionsAsync(seeded.GuideId));
    }

    /// <summary>
    /// A retry asking for a <em>different</em> decision is a conflict, not a replay. Serving the earlier outcome
    /// would tell a creator their selection had been applied when a different one had.
    /// </summary>
    [Fact]
    public async Task A_second_different_decision_is_refused_rather_than_answered_with_the_first()
    {
        var seeded = await SeedAsync(WorkspaceA);
        await AcceptAllAsync(WorkspaceA, seeded);

        var second = await AcceptAsync(
            WorkspaceA,
            new AcceptBrandGuideProposalViewModel { Decision = AiDispositionDecision.Reject },
            seeded.OperationId);

        Assert.False(second.Succeeded);
        Assert.Equal(AiBrandGuideAcceptanceErrors.ProposalDecided, second.Error!.Code);
        Assert.Equal(2, await CountVersionsAsync(seeded.GuideId));
    }

    /// <summary>
    /// A replay that carried rewrites cannot be proven identical to the one recorded: nothing stores what the
    /// creator's words were. Refused rather than reporting success over discarded text.
    /// </summary>
    [Fact]
    public async Task A_replay_that_carries_a_rewrite_is_refused()
    {
        var seeded = await SeedAsync(WorkspaceA);

        var model = new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = [seeded.ToneItemId],
            Edits = [new AiBrandGuideEditViewModel { ChangeId = seeded.ToneItemId, Value = "Dry, and a bit wry." }],
        };

        Assert.True((await AcceptAsync(WorkspaceA, model, seeded.OperationId)).Succeeded);

        var second = await AcceptAsync(WorkspaceA, model, seeded.OperationId);

        Assert.False(second.Succeeded);
        Assert.Equal(AiBrandGuideAcceptanceErrors.ProposalDecided, second.Error!.Code);
        Assert.Equal(2, await CountVersionsAsync(seeded.GuideId));
    }

    // ---- rollback --------------------------------------------------------------------------------------

    /// <summary>
    /// The brand module's refusal rolls the whole acceptance back: no version, no dispositions, no feedback, no
    /// status change. An archived guide is the cheapest way to reach a refusal from inside the transaction.
    /// </summary>
    [Fact]
    public async Task A_refusal_inside_the_transaction_leaves_nothing_behind()
    {
        var seeded = await SeedAsync(WorkspaceA);
        await ArchiveGuideAsync(WorkspaceA, seeded.GuideId);

        var result = await AcceptAsync(WorkspaceA, new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptAll,
            AcceptedChangeIds = seeded.ItemIds,
            WasHelpful = true,
            Comment = "Useful.",
        }, seeded.OperationId);

        Assert.False(result.Succeeded);
        Assert.Equal(BrandErrorCodes.GuideArchivedConflict, result.Error!.Code);

        Assert.Equal(1, await CountVersionsAsync(seeded.GuideId));

        var operation = await LoadOperationAsync(WorkspaceA, seeded.OperationId);
        Assert.Equal(AiOperationStatus.Proposed, operation.Status);

        var changes = await LoadChangesAsync(WorkspaceA, seeded.ProposalId);
        Assert.All(changes, change => Assert.Equal(AiChangeDisposition.Pending, change.Disposition));

        // The feedback row is inside the same transaction, so it goes with everything else.
        Assert.Empty(await LoadFeedbackAsync(WorkspaceA, seeded.ProposalId));
    }

    // ---- the guide's own limits ------------------------------------------------------------------------

    /// <summary>
    /// Thirty proposed rules are legitimate and fifty stored ones are legitimate; only the sum is not. Refused
    /// rather than truncated — a cap enforced by dropping the tail would discard guidance the creator ticked and
    /// report success.
    /// </summary>
    [Fact]
    public async Task Guidance_that_would_take_the_guide_past_its_rule_cap_is_refused()
    {
        var seeded = await SeedAsync(WorkspaceA, fillRulesToCap: true);

        var result = await AcceptAsync(WorkspaceA, new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = [seeded.NewRuleItemId],
        }, seeded.OperationId);

        Assert.False(result.Succeeded);
        Assert.Equal(BrandErrorCodes.GuideVersionLimitExceeded, result.Error!.Code);
        Assert.Equal(1, await CountVersionsAsync(seeded.GuideId));

        // Nothing at all, as for every other refusal from inside the transaction.
        var operation = await LoadOperationAsync(WorkspaceA, seeded.OperationId);
        Assert.Equal(AiOperationStatus.Proposed, operation.Status);
    }

    /// <summary>
    /// Unreachable through any request this server accepts, because the request seam writes both fields. This is
    /// the answer for a row written before they existed, or by a future writer that forgets one.
    /// </summary>
    [Fact]
    public async Task A_proposal_whose_stored_inputs_name_no_guide_cannot_be_accepted()
    {
        var seeded = await SeedAsync(WorkspaceA);
        await StripInputsAsync(WorkspaceA, seeded.OperationId);

        var result = await AcceptAllAsync(WorkspaceA, seeded);

        Assert.False(result.Succeeded);
        Assert.Equal(AiBrandGuideAcceptanceErrors.ProposalUnreadable, result.Error!.Code);
        Assert.Equal(1, await CountVersionsAsync(seeded.GuideId));
    }

    // ---- authorization ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_contributor_may_not_accept_guidance_into_a_guide()
    {
        var seeded = await SeedAsync(WorkspaceA);

        var result = await AcceptAllAsync(WorkspaceA, seeded, WorkspaceRole.Contributor);

        Assert.False(result.Succeeded);
        Assert.Equal(AiBrandGuideAcceptanceErrors.AcceptanceForbidden, result.Error!.Code);
        Assert.Equal(1, await CountVersionsAsync(seeded.GuideId));
    }

    // ---- two workspaces --------------------------------------------------------------------------------

    [Fact]
    public async Task Another_workspace_cannot_see_the_proposal_to_decide_it()
    {
        var seeded = await SeedAsync(WorkspaceA);

        var result = await AcceptAllAsync(WorkspaceB, seeded, WorkspaceRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Equal(AiBrandGuideProposalRequestErrors.RequestNotFound, result.Error!.Code);
        Assert.Equal(1, await CountVersionsAsync(seeded.GuideId));
    }

    /// <summary>
    /// The cross-guide targeting defence, exercised rather than assumed: workspace B's own proposal, decided by
    /// a member of B, with workspace A's guide written into the stored inputs the acceptance reads its target
    /// from. That is the only place a guide id comes from, so it is the only place this attack can be mounted.
    /// </summary>
    /// <remarks>
    /// Reaches the guide through the brand module's filtered read, so A's guide is absent rather than refused —
    /// which is why the answer is <c>brand.guide.not_found</c> and says nothing about A having one.
    /// </remarks>
    [Fact]
    public async Task A_proposal_retargeted_at_another_workspaces_guide_writes_to_neither()
    {
        var mine = await SeedAsync(WorkspaceA);
        var theirs = await SeedAsync(WorkspaceB);

        await RetargetGuideAsync(WorkspaceB, theirs.OperationId, mine.GuideId);

        var result = await AcceptAllAsync(WorkspaceB, theirs, WorkspaceRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Equal(BrandErrorCodes.GuideNotFound, result.Error!.Code);

        // Neither guide moved, and B's own proposal is still undecided.
        Assert.Equal(1, await CountVersionsAsync(mine.GuideId));
        Assert.Equal(1, await CountVersionsAsync(theirs.GuideId));

        var operation = await LoadOperationAsync(WorkspaceB, theirs.OperationId);
        Assert.Equal(AiOperationStatus.Proposed, operation.Status);
    }

    /// <summary>
    /// A citation naming another workspace's passage resolves to nothing, so it cites nothing. The version is
    /// still written — provenance for a passage nobody here can read is the footnote the acceptance declines to
    /// fail over — and A's document is never named by B's guide.
    /// </summary>
    [Fact]
    public async Task A_citation_naming_another_workspaces_passage_cites_nothing()
    {
        var mine = await SeedAsync(WorkspaceA);
        var theirs = await SeedAsync(WorkspaceB);

        await RecitePassageAsync(WorkspaceB, theirs.ToneItemId, await FirstPassageAsync(WorkspaceA, mine.DocumentId));

        var result = await AcceptAsync(WorkspaceB, new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = [theirs.ToneItemId],
        }, theirs.OperationId, WorkspaceRole.Owner);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(0, result.Value!.Written!.CitedSourceCount);

        var version = await ReadWorkingVersionAsync(WorkspaceB, theirs.GuideId);

        Assert.Empty(version.SourceDocuments);
        Assert.DoesNotContain(version.SourceDocuments, source => source.DocumentId == mine.DocumentId);
    }

    [Fact]
    public async Task The_version_an_acceptance_writes_belongs_to_the_workspace_that_wrote_it()
    {
        var seeded = await SeedAsync(WorkspaceA);
        var accepted = (await AcceptAllAsync(WorkspaceA, seeded)).Value!;

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var version = await db.BrandStyleGuideVersions.IgnoreQueryFilters()
            .SingleAsync(row => row.Id == accepted.Written!.GuideVersionId, TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceA, version.WorkspaceId);

        using var otherScope = _provider.CreateScope();
        Resolve(otherScope, WorkspaceB, WorkspaceRole.Owner);

        var read = await otherScope.ServiceProvider.GetRequiredService<IBrandStyleGuideFacade>()
            .GetAsync(seeded.GuideId, TestContext.Current.CancellationToken);

        Assert.False(read.Succeeded);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private Task<OperationResult<AiBrandGuideAcceptanceServiceModel>> AcceptAllAsync(
        Guid workspaceId, SeededProposal seeded, WorkspaceRole role = WorkspaceRole.Editor) =>
        AcceptAsync(
            workspaceId,
            new AcceptBrandGuideProposalViewModel
            {
                Decision = AiDispositionDecision.AcceptAll,
                AcceptedChangeIds = seeded.ItemIds,
            },
            seeded.OperationId,
            role);

    private async Task<OperationResult<AiBrandGuideAcceptanceServiceModel>> AcceptAsync(
        Guid workspaceId,
        AcceptBrandGuideProposalViewModel model,
        Guid operationId,
        WorkspaceRole role = WorkspaceRole.Editor)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId, role);

        return await scope.ServiceProvider.GetRequiredService<IAiBrandGuideAcceptanceBusiness>()
            .AcceptAsync(UserId, operationId, model, TestContext.Current.CancellationToken);
    }

    private async Task<BrandStyleGuideDetailServiceModel> ReadGuideAsync(Guid workspaceId, Guid guideId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        var read = await scope.ServiceProvider.GetRequiredService<IBrandStyleGuideFacade>()
            .GetAsync(guideId, TestContext.Current.CancellationToken);

        Assert.True(read.Succeeded, read.Error?.Message);
        return read.Value!;
    }

    private async Task<BrandStyleGuideVersionDetailServiceModel> ReadWorkingVersionAsync(
        Guid workspaceId, Guid guideId) =>
        (await ReadGuideAsync(workspaceId, guideId)).WorkingVersion;

    /// <summary>Counted across every workspace, so "no version was written" means exactly that.</summary>
    private async Task<int> CountVersionsAsync(Guid guideId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.BrandStyleGuideVersions.IgnoreQueryFilters()
            .CountAsync(version => version.BrandStyleGuideId == guideId, TestContext.Current.CancellationToken);
    }

    private async Task<AiOperation> LoadOperationAsync(Guid workspaceId, Guid operationId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.AiOperations.AsNoTracking()
            .SingleAsync(operation => operation.Id == operationId, TestContext.Current.CancellationToken);
    }

    private async Task<List<AiStructuredChange>> LoadChangesAsync(Guid workspaceId, Guid proposalId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.AiStructuredChanges.AsNoTracking()
            .Where(change => change.AiProposalId == proposalId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<AiProposalFeedback>> LoadFeedbackAsync(Guid workspaceId, Guid proposalId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.AiProposalFeedback.AsNoTracking()
            .Where(row => row.AiProposalId == proposalId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Adds a further version to a guide, which is what makes an earlier proposal stale.</summary>
    private async Task AddVersionAsync(Guid workspaceId, Guid guideId, int versionNumber)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.BrandStyleGuideVersions.Add(new BrandStyleGuideVersion
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            BrandStyleGuideId = guideId,
            VersionNumber = versionNumber,
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
            Sections =
            [
                new BrandStyleGuideSection
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    SectionKey = BrandStyleGuideSectionKey.Voice,
                    Body = "Edited after the proposal was asked for.",
                },
            ],
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task ArchiveGuideAsync(Guid workspaceId, Guid guideId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var guide = await db.BrandStyleGuides.SingleAsync(
            row => row.Id == guideId, TestContext.Current.CancellationToken);

        guide.Status = BrandStyleGuideStatus.Archived;
        guide.ArchivedAt = Now;

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Bumps a document to version 2, which makes the proposal's citation of version 1 stale.</summary>
    private async Task ReplaceDocumentAsync(Guid workspaceId, Guid documentId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var document = await db.BrandSourceDocuments.SingleAsync(
            row => row.Id == documentId, TestContext.Current.CancellationToken);

        document.CurrentVersionNumber = 2;

        db.BrandSourceDocumentVersions.Add(new BrandSourceDocumentVersion
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            BrandSourceDocumentId = documentId,
            VersionNumber = 2,
            MediaType = "application/pdf",
            SizeBytes = 2048,
            ContentChecksum = Checksum,
            OriginalFileName = "house-style-v2.pdf",
            ObjectKey = $"brand-sources/{documentId:N}/2",
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Rewrites the guide an operation's stored inputs name, which is the only place acceptance takes its target
    /// from — and so the only place a guide from another workspace could be aimed at.
    /// </summary>
    private async Task RetargetGuideAsync(Guid workspaceId, Guid operationId, Guid guideId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var operation = await db.AiOperations.SingleAsync(
            row => row.Id == operationId, TestContext.Current.CancellationToken);

        var inputs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            operation.TaskInputsJson!)!;

        inputs[AiBrandGuideProposalInputs.GuideId] = guideId.ToString("D");
        operation.TaskInputsJson = System.Text.Json.JsonSerializer.Serialize(inputs);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Points one item's citations at <paramref name="passageId"/> and nothing else.</summary>
    private async Task RecitePassageAsync(Guid workspaceId, Guid itemId, Guid passageId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var item = await db.AiStructuredChanges.AsNoTracking()
            .SingleAsync(row => row.Id == itemId, TestContext.Current.CancellationToken);

        var citations = await db.AiStructuredChanges.SingleAsync(
            row => row.TargetId == item.TargetId && row.FieldName == AiBrandGuideFields.Citations,
            TestContext.Current.CancellationToken);

        citations.AfterValue = passageId.ToString("N");

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>One real passage id of a document, so a foreign citation names a row that genuinely exists.</summary>
    private async Task<Guid> FirstPassageAsync(Guid workspaceId, Guid documentId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.BrandSourceChunks.AsNoTracking()
            .Where(chunk => db.BrandSourceChunkSets.AsNoTracking()
                .Any(set => set.Id == chunk.BrandSourceChunkSetId && set.BrandSourceDocumentId == documentId))
            .OrderBy(chunk => chunk.Ordinal)
            .Select(chunk => chunk.Id)
            .FirstAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Clears the operation's stored inputs, which is the one way its guide becomes unreadable.</summary>
    private async Task StripInputsAsync(Guid workspaceId, Guid operationId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var operation = await db.AiOperations.SingleAsync(
            row => row.Id == operationId, TestContext.Current.CancellationToken);

        operation.TaskInputsJson = null;

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private sealed record SeededProposal(
        Guid OperationId,
        Guid ProposalId,
        Guid GuideId,
        Guid DocumentId,
        Guid VoiceItemId,
        Guid ToneItemId,
        Guid ToneDimensionRowId,
        Guid ExistingRuleItemId,
        Guid NewRuleItemId,
        Guid ConflictItemId,
        IReadOnlyList<Guid> ItemIds);

    /// <summary>
    /// A guide with one version, a source document with indexed passages, and a completed brand-guide proposal
    /// over both — in the shape <c>BrandGuideProposalAiTaskHandler.Translate</c> produces: one <c>Add</c> row per
    /// item carrying its text, with its item kind, dimension, channel, evidence and citations as <c>Set</c> rows
    /// addressed to the same target.
    /// </summary>
    /// <param name="fillRulesToCap">
    /// Pads the guide's own rules up to <see cref="BrandPolicy.MaxStyleGuideRules"/>, so accepting one more
    /// would take it past the ceiling.
    /// </param>
    private async Task<SeededProposal> SeedAsync(Guid workspaceId, bool fillRulesToCap = false)
    {
        var member = Guid.NewGuid();
        var guideId = Guid.NewGuid();
        var guideVersionId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var documentVersionId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();
        var firstPassageId = Guid.NewGuid();
        var secondPassageId = Guid.NewGuid();

        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.BrandStyleGuides.Add(new BrandStyleGuide
        {
            Id = guideId,
            WorkspaceId = workspaceId,
            DisplayName = "House voice",
            Status = BrandStyleGuideStatus.Active,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = member,
            UpdatedByMembershipId = member,
        });

        db.BrandStyleGuideVersions.Add(new BrandStyleGuideVersion
        {
            Id = guideVersionId,
            WorkspaceId = workspaceId,
            BrandStyleGuideId = guideId,
            VersionNumber = 1,
            CreatedByMembershipId = member,
            CreatedAt = Now,
            Sections =
            [
                // Spoken to by the proposal, so accepting replaces it.
                new BrandStyleGuideSection
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    SectionKey = BrandStyleGuideSectionKey.Voice,
                    Body = "Plain and unhurried.",
                },

                // Not spoken to by the proposal, so it must survive an acceptance untouched.
                new BrandStyleGuideSection
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    SectionKey = BrandStyleGuideSectionKey.Audience,
                    Body = "Home cooks in a hurry.",
                },
            ],
            Rules =
            [
                new BrandStyleGuideRule
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    Kind = BrandStyleGuideRuleKind.Do,
                    Text = ExistingRule,
                    SortOrder = 0,
                },

                .. Enumerable
                    .Range(1, fillRulesToCap ? BrandPolicy.MaxStyleGuideRules - 1 : 0)
                    .Select(index => new BrandStyleGuideRule
                    {
                        Id = Guid.NewGuid(),
                        WorkspaceId = workspaceId,
                        Kind = BrandStyleGuideRuleKind.Do,
                        Text = $"Rule {index}.",
                        SortOrder = index,
                    }),
            ],
        });

        var document = new BrandSourceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            Title = "House style",
            DocumentType = BrandSourceDocumentType.StyleGuide,
            Purpose = BrandSourcePurpose.Voice,
            CurrentVersionNumber = 1,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = member,
            UpdatedByMembershipId = member,
        };

        var documentVersion = new BrandSourceDocumentVersion
        {
            Id = documentVersionId,
            WorkspaceId = workspaceId,
            BrandSourceDocumentId = documentId,
            VersionNumber = 1,
            MediaType = "application/pdf",
            SizeBytes = 1024,
            ContentChecksum = Checksum,
            OriginalFileName = "house-style.pdf",
            ObjectKey = $"brand-sources/{documentId:N}/1",
            CreatedByMembershipId = member,
            CreatedAt = Now,
        };

        var extraction = new BrandSourceExtraction
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            BrandSourceDocumentVersionId = documentVersionId,
            Ordinal = 1,
            Status = BrandSourceExtractionStatus.Succeeded,
            Origin = BrandSourceExtractionOrigin.Extracted,
            ExtractedTextObjectKey = $"brand-sources/text/{documentVersionId:N}/1",
            ContentChecksum = Checksum,
            CreatedAt = Now,
        };

        var chunkSet = new BrandSourceChunkSet
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            BrandSourceDocumentId = documentId,
            BrandSourceDocumentVersionId = documentVersionId,
            BrandSourceExtractionId = extraction.Id,
            SourceStatus = BrandSourceExtractionStatus.Succeeded,
            Status = BrandSourceChunkSetStatus.Current,
            ChunkerId = "text/paragraph-1600c-200o@1",
            EmbeddingModel = "text-embedding-3-small",
            EmbeddingDimension = BrandPolicy.EmbeddingDimension,
            ChunkCount = 2,
            CreatedAt = Now,
            EmbeddedAt = Now,
        };

        db.BrandSourceDocuments.Add(document);
        db.BrandSourceDocumentVersions.Add(documentVersion);
        db.BrandSourceExtractions.Add(extraction);
        db.BrandSourceChunkSets.Add(chunkSet);

        foreach (var (passageId, ordinal) in new[] { (firstPassageId, 1), (secondPassageId, 2) })
        {
            db.BrandSourceChunks.Add(new BrandSourceChunk
            {
                Id = passageId,
                WorkspaceId = workspaceId,
                BrandSourceChunkSetId = chunkSet.Id,
                Ordinal = ordinal,
                StartByteOffset = (ordinal - 1) * 1400,
                ByteLength = 1600,
                ContentChecksum = Checksum,
                Text = $"We write like a friend who happens to cook. Passage {ordinal}.",
                Embedding = Unit(ordinal),
            });
        }

        var order = 0;
        var changes = new List<AiStructuredChange>();

        AiStructuredChange Row(
            AiChangeKind kind, Guid targetId, string? field, string? value, int? position = null)
        {
            var change = new AiStructuredChange
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                AiProposalId = proposalId,
                ChangeKind = kind,
                TargetKind = AiChangeTargetKind.BrandGuideSection,
                TargetId = targetId,
                FieldName = field,
                AfterValue = value,
                ProposedPosition = position,
                SortOrder = order++,
            };

            changes.Add(change);
            return change;
        }

        (Guid ItemId, Guid FirstSetId) Item(
            string itemKind, string text, params (string Field, string? Value)[] fields)
        {
            var targetId = Guid.NewGuid();
            var item = Row(AiChangeKind.Add, targetId, null, text, position: order);
            var first = Guid.Empty;

            foreach (var (field, value) in fields.Where(pair => !string.IsNullOrEmpty(pair.Value)))
            {
                var row = Row(AiChangeKind.Set, targetId, field, value);
                first = first == Guid.Empty ? row.Id : first;
            }

            return (item.Id, first);
        }

        string Cited(params Guid[] passageIds) =>
            string.Join(',', passageIds.Select(passageId => passageId.ToString("N")));

        var voice = Item(
            AiBrandGuideItemKinds.Section,
            "A friend who happens to cook.",
            (AiBrandGuideFields.ItemKind, AiBrandGuideItemKinds.Section),
            (AiBrandGuideFields.Dimension, "voice"),
            (AiBrandGuideFields.Evidence, AiBrandGuideEvidence.Sources.ToString()),
            (AiBrandGuideFields.Citations, Cited(firstPassageId, secondPassageId)));

        var tone = Item(
            AiBrandGuideItemKinds.Section,
            "Warm, but never fussy.",
            (AiBrandGuideFields.ItemKind, AiBrandGuideItemKinds.Section),
            (AiBrandGuideFields.Dimension, "tone"),
            (AiBrandGuideFields.Evidence, AiBrandGuideEvidence.Sources.ToString()),
            (AiBrandGuideFields.Citations, Cited(firstPassageId)));

        var channel = Item(
            AiBrandGuideItemKinds.Section,
            "Short, and lead with the picture.",
            (AiBrandGuideFields.ItemKind, AiBrandGuideItemKinds.Section),
            (AiBrandGuideFields.Dimension, "channel"),
            (AiBrandGuideFields.ChannelKey, "instagram"),
            (AiBrandGuideFields.Evidence, AiBrandGuideEvidence.Questionnaire.ToString()));

        // A rule the guide already holds, so accepting it adds nothing and is reported.
        var existingRule = Item(
            AiBrandGuideItemKinds.Rule,
            ExistingRule,
            (AiBrandGuideFields.ItemKind, AiBrandGuideItemKinds.Rule),
            (AiBrandGuideFields.RuleKind, AiBrandGuideRuleKind.Do.ToString()),
            (AiBrandGuideFields.Evidence, AiBrandGuideEvidence.Questionnaire.ToString()));

        var newRule = Item(
            AiBrandGuideItemKinds.Rule,
            "Never pad the introduction.",
            (AiBrandGuideFields.ItemKind, AiBrandGuideItemKinds.Rule),
            (AiBrandGuideFields.RuleKind, AiBrandGuideRuleKind.Dont.ToString()),
            (AiBrandGuideFields.Evidence, AiBrandGuideEvidence.Sources.ToString()),
            (AiBrandGuideFields.Citations, Cited(secondPassageId)));

        // The one item with nowhere in a guide to go. Kept in the seed deliberately: a real proposal has them.
        var conflict = Item(
            AiBrandGuideItemKinds.Conflict,
            "Two of your documents disagree about contractions.",
            (AiBrandGuideFields.ItemKind, AiBrandGuideItemKinds.Conflict),
            (AiBrandGuideFields.Dimension, "style"),
            (AiBrandGuideFields.Citations, Cited(firstPassageId, secondPassageId)));

        db.AiOperations.Add(new AiOperation
        {
            Id = operationId,
            WorkspaceId = workspaceId,
            TaskType = AiTaskType.BrandGuideProposal,

            // A brand guide is not a recipe: no recipe, no pinned version, and the scope the request seam sets.
            Scope = AiOperationScope.NotApplicable,
            Status = AiOperationStatus.Proposed,
            TaskInputsJson = System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AiBrandGuideProposalInputs.GuideId] = guideId.ToString("D"),
                    [AiBrandGuideProposalInputs.GuideVersionNumber] = "1",
                    [AiBrandGuideProposalInputs.Dimensions] = "channel,style,tone,voice",
                    [AiBrandGuideProposalInputs.ChannelKeys] = "instagram",
                    [AiBrandGuideProposalInputs.SourceVersions] = $"{documentId:N}:1",
                }),
            IdempotencyKey = $"guide-proposal-{operationId}",
            RequestedByMembershipId = member,
            RequestedAt = Now,
            StatusChangedAt = Now,
            AvailableAt = Now,
        });

        db.AiProposals.Add(new AiProposal
        {
            Id = proposalId,
            WorkspaceId = workspaceId,
            AiOperationId = operationId,
            OutputSchemaVersion = "brand.guide-proposal.v1",
            PromptTemplateId = AiTaskCatalog.BrandGuideProposal,
            PromptTemplateVersion = "1.0.0",
            PromptTemplateBodyChecksum = "sha256:seed",
            ProviderName = "test-provider",
            ModelName = "test-model",
            CreatedAt = Now,
        });

        db.AiStructuredChanges.AddRange(changes);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new SeededProposal(
            operationId,
            proposalId,
            guideId,
            documentId,
            voice.ItemId,
            tone.ItemId,
            tone.FirstSetId,
            existingRule.ItemId,
            newRule.ItemId,
            conflict.ItemId,
            [voice.ItemId, tone.ItemId, channel.ItemId, existingRule.ItemId, newRule.ItemId, conflict.ItemId]);
    }

    private static SqlVector<float> Unit(int axis)
    {
        var values = new float[BrandPolicy.EmbeddingDimension];
        values[axis % BrandPolicy.EmbeddingDimension] = 1f;

        return new SqlVector<float>(values);
    }

    private static void Resolve(
        IServiceScope scope, Guid workspaceId, WorkspaceRole role = WorkspaceRole.Editor) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            role,
            "test-account");

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }
}
