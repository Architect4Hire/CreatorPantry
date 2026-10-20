using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ingredients.Facade;
using CreatorPantry.Domain.Modules.Ingredients.Gateways;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Nutrition.Data.Entities;
using CreatorPantry.Domain.Modules.Nutrition.Facade;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Nutrition;

/// <summary>
/// A workspace's nutrition mapping for one platform ingredient, through its whole lifecycle (14.2): the automatic
/// candidate, confirmation, rejection, remap, a stale or withdrawn source, roles, and the boundary between two
/// workspaces — on SQL Server, against the migrated schema.
/// </summary>
public sealed class NutritionMappingTests(SqlServerNutritionFixture fixture) : IClassFixture<SqlServerNutritionFixture>
{
    private static readonly Guid WorkspaceA = SqlServerNutritionFixture.WorkspaceA;
    private static readonly Guid WorkspaceB = SqlServerNutritionFixture.WorkspaceB;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ScriptedEvidence Evidence => fixture.Evidence;

    // ---- The automatic candidate ----

    [Fact]
    public async Task A_proposal_is_the_first_approved_hit_held_as_an_unconfirmed_low_confidence_candidate()
    {
        var (ingredient, name) = await IngredientAsync("flour");
        Evidence.Has(Id("a1"), "Wheat flour, white, all-purpose, enriched, bleached", "sr-legacy/2019-04-01");
        Evidence.Has(Id("a2"), "Wheat flour, white, bread, enriched");
        Evidence.Finds(name, Id("a1"), Id("a2"));

        var mapping = Value(await Facade(WorkspaceA, WorkspaceRole.Contributor).ProposeAsync(ingredient, Ct));

        Assert.Equal(NutritionMappingStatus.Candidate, mapping.Status);
        Assert.Equal(NutritionMatchMethod.AutomaticSearch, mapping.MatchMethod);
        Assert.Equal(NutritionMatchConfidence.Low, mapping.Confidence);
        Assert.Equal(
            new NutritionMappingReferenceServiceModel("usda-fdc", Id("a1"), "Wheat flour, white, all-purpose, enriched, bleached", "sr-legacy/2019-04-01"),
            mapping.Reference);
        Assert.Null(mapping.ConfirmedAt);
        Assert.Null(mapping.Portion);
    }

    [Fact]
    public async Task The_only_hit_and_an_exact_name_are_high_confidence_and_still_only_candidates()
    {
        var (only, onlyName) = await IngredientAsync("lone");
        Evidence.Has(Id("b1"), "Something else entirely");
        Evidence.Finds(onlyName, Id("b1"));

        var (exact, exactName) = await IngredientAsync("exact");
        Evidence.Has(Id("b2"), exactName.ToUpperInvariant());
        Evidence.Has(Id("b3"), "Another food");
        Evidence.Finds(exactName, Id("b2"), Id("b3"));

        var facade = Facade(WorkspaceA);
        var single = Value(await facade.ProposeAsync(only, Ct));
        var named = Value(await facade.ProposeAsync(exact, Ct));

        Assert.Equal(NutritionMatchConfidence.High, single.Confidence);
        Assert.Equal(NutritionMatchConfidence.High, named.Confidence);
        Assert.All([single, named], mapping => Assert.Equal(NutritionMappingStatus.Candidate, mapping.Status));
    }

    [Fact]
    public async Task With_nothing_approved_to_propose_the_mapping_is_unresolved_for_want_of_candidates()
    {
        var (ingredient, _) = await IngredientAsync("nothing");

        var mapping = Value(await Facade(WorkspaceA).ProposeAsync(ingredient, Ct));

        Assert.Equal(NutritionMappingStatus.Unresolved, mapping.Status);
        Assert.Equal(NutritionUnresolvedReason.NoCandidates, mapping.UnresolvedReason);
        Assert.Null(mapping.Reference);
    }

    [Fact]
    public async Task A_hit_the_source_no_longer_has_or_approves_is_passed_over()
    {
        var (ingredient, name) = await IngredientAsync("passover");
        Evidence.Answers(Id("c1"), ReferenceEvidenceOutcome.NotFound);
        Evidence.Answers(Id("c2"), ReferenceEvidenceOutcome.NoApprovedSource);
        Evidence.Has(Id("c3"), "Third food");
        Evidence.Finds(name, Id("c1"), Id("c2"), Id("c3"));

        var mapping = Value(await Facade(WorkspaceA).ProposeAsync(ingredient, Ct));

        Assert.Equal(Id("c3"), mapping.Reference!.ExternalRecordId);
    }

    [Fact]
    public async Task Proposing_again_returns_the_candidate_unchanged_without_searching()
    {
        var (ingredient, name) = await IngredientAsync("again");
        Evidence.Has(Id("d1"), "Food d1");
        Evidence.Finds(name, Id("d1"));
        var facade = Facade(WorkspaceA);

        var first = Value(await facade.ProposeAsync(ingredient, Ct));
        var searches = Evidence.Searches;
        var second = Value(await facade.ProposeAsync(ingredient, Ct));

        Assert.Equal(searches, Evidence.Searches);
        Assert.Equal(first.ConcurrencyToken, second.ConcurrencyToken);
    }

    [Fact]
    public async Task A_confirmed_mapping_is_not_proposed_over()
    {
        var (ingredient, _) = await ConfirmedAsync("noover", Id("e1"));

        var result = await Facade(WorkspaceA).ProposeAsync(ingredient, Ct);

        Assert.Equal(NutritionErrorCodes.MappingConflict, result.Error?.Code);
    }

    [Fact]
    public async Task A_proposal_does_not_overrule_a_creator_who_said_no_reference_fits()
    {
        var (ingredient, name) = await IngredientAsync("declined");
        Evidence.Has(Id("declined1"), "Food declined1");
        Evidence.Finds(name, Id("declined1"));
        Value(await Facade(WorkspaceA).MarkUnresolvedAsync(ingredient, new(), Ct));

        var result = await Facade(WorkspaceA, WorkspaceRole.Contributor).ProposeAsync(ingredient, Ct);

        Assert.Equal(NutritionErrorCodes.MappingConflict, result.Error?.Code);
        Assert.Equal(NutritionUnresolvedReason.CreatorDeclined, Value(await Facade(WorkspaceA).GetAsync(ingredient, Ct)).UnresolvedReason);
    }

    [Fact]
    public async Task A_source_that_cannot_be_asked_writes_nothing_and_concludes_nothing()
    {
        var (ingredient, name) = await IngredientAsync("outage");
        Evidence.Has(Id("f1"), "Food f1");
        Evidence.Finds(name, Id("f1"));

        Evidence.Down = true;
        try
        {
            var result = await Facade(WorkspaceA).ProposeAsync(ingredient, Ct);
            Assert.Equal(NutritionErrorCodes.SourceUnavailable, result.Error?.Code);
        }
        finally
        {
            Evidence.Down = false;
        }

        Assert.Equal(NutritionErrorCodes.MappingNotFound, (await Facade(WorkspaceA).GetAsync(ingredient, Ct)).Error?.Code);
    }

    [Fact]
    public async Task An_ingredient_the_catalogue_does_not_offer_cannot_be_mapped()
    {
        var (retired, _) = await IngredientAsync("retired", isActive: false);
        var facade = Facade(WorkspaceA);

        Assert.Equal(NutritionErrorCodes.IngredientUnprocessable, (await facade.ProposeAsync(retired, Ct)).Error?.Code);
        Assert.Equal(NutritionErrorCodes.IngredientUnprocessable, (await facade.ProposeAsync(Guid.NewGuid(), Ct)).Error?.Code);
        Assert.Equal(NutritionErrorCodes.IngredientUnprocessable, (await facade.MarkUnresolvedAsync(Guid.NewGuid(), new(), Ct)).Error?.Code);
    }

    // ---- Confirmation ----

    [Fact]
    public async Task Confirming_a_candidate_records_who_and_when_and_audits_without_content()
    {
        var (ingredient, name) = await IngredientAsync("confirm");
        Evidence.Has(Id("g1"), "Secret-sounding food description");
        Evidence.Finds(name, Id("g1"));
        var candidate = Value(await Facade(WorkspaceA).ProposeAsync(ingredient, Ct));
        var editor = Guid.NewGuid();

        var confirmed = Value(await Facade(WorkspaceA, membershipId: editor).ConfirmAsync(
            ingredient, new ConfirmNutritionMappingViewModel { ExpectedConcurrencyToken = candidate.ConcurrencyToken }, Ct));

        Assert.Equal(NutritionMappingStatus.Confirmed, confirmed.Status);
        Assert.Equal(fixture.Clock.UtcNow, confirmed.ConfirmedAt);
        Assert.Equal(NutritionMatchMethod.AutomaticSearch, confirmed.MatchMethod);
        Assert.NotEqual(candidate.ConcurrencyToken, confirmed.ConcurrencyToken);

        var row = await RowAsync(WorkspaceA, ingredient);
        Assert.Equal(editor, row.ConfirmedByMembershipId);

        var audit = Assert.Single(await AuditAsync(WorkspaceA, confirmed.Id));
        Assert.Equal(NutritionAuditActions.Confirmed, audit.Action);
        Assert.DoesNotContain("Secret-sounding", audit.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(name, audit.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_confirmation_quoting_an_old_version_is_stale_and_writes_nothing()
    {
        var (ingredient, name) = await IngredientAsync("stale");
        Evidence.Has(Id("h1"), "Food h1");
        Evidence.Finds(name, Id("h1"));
        var candidate = Value(await Facade(WorkspaceA).ProposeAsync(ingredient, Ct));

        // Somebody else confirms first.
        Value(await Facade(WorkspaceA).ConfirmAsync(ingredient, new() { ExpectedConcurrencyToken = candidate.ConcurrencyToken }, Ct));
        var late = await Facade(WorkspaceA).RejectAsync(ingredient, new() { ExpectedConcurrencyToken = candidate.ConcurrencyToken }, Ct);

        Assert.Equal(NutritionErrorCodes.MappingStale, late.Error?.Code);
        Assert.Equal(NutritionMappingStatus.Confirmed, Value(await Facade(WorkspaceA).GetAsync(ingredient, Ct)).Status);
    }

    [Fact]
    public async Task Only_a_candidate_or_a_review_can_be_confirmed()
    {
        var (ingredient, _) = await IngredientAsync("unresolvedconfirm");
        var unresolved = Value(await Facade(WorkspaceA).MarkUnresolvedAsync(ingredient, new(), Ct));

        var result = await Facade(WorkspaceA).ConfirmAsync(ingredient, new() { ExpectedConcurrencyToken = unresolved.ConcurrencyToken }, Ct);

        Assert.Equal(NutritionErrorCodes.MappingConflict, result.Error?.Code);
    }

    [Fact]
    public async Task A_portion_that_is_exactly_the_references_own_is_the_sources_and_any_other_is_the_creators()
    {
        var cup = new PortionEvidence(1m, 125m, "cup-us", null, "cup");
        var (ingredient, name) = await IngredientAsync("portion");
        Evidence.Has(Id("i1"), "Food i1", "sr-legacy/2019-04-01", cup);
        Evidence.Finds(name, Id("i1"));
        var candidate = Value(await Facade(WorkspaceA).ProposeAsync(ingredient, Ct));

        var sourced = Value(await Facade(WorkspaceA).ConfirmAsync(ingredient, new()
        {
            ExpectedConcurrencyToken = candidate.ConcurrencyToken,
            Portion = new() { MeasurementUnitId = SqlServerNutritionFixture.CupId, Amount = 1m, Grams = 125m },
        }, Ct));

        Assert.Equal(
            new NutritionMappingPortionServiceModel(SqlServerNutritionFixture.CupId, 1m, 125m, "cup", NutritionPortionOrigin.Reference),
            sourced.Portion);

        // The creator's own eggs: a count unit, and a weight the source never gave. Remapped to the same
        // reference to change the portion of a confirmed mapping.
        var own = Value(await Facade(WorkspaceA).RemapAsync(ingredient, new()
        {
            ExpectedConcurrencyToken = sourced.ConcurrencyToken,
            SourceCode = "usda-fdc",
            ExternalRecordId = Id("i1"),
            Portion = new() { MeasurementUnitId = SqlServerNutritionFixture.EachId, Amount = 1m, Grams = 55m },
        }, Ct));

        Assert.Equal(
            new NutritionMappingPortionServiceModel(SqlServerNutritionFixture.EachId, 1m, 55m, null, NutritionPortionOrigin.Creator),
            own.Portion);
    }

    [Theory]
    [InlineData("mass")]
    [InlineData("temperature")]
    [InlineData("unknown")]
    public async Task A_portion_in_a_mass_temperature_or_unknown_unit_is_refused(string kind)
    {
        var unit = kind switch
        {
            "mass" => SqlServerNutritionFixture.GramId,
            "temperature" => SqlServerNutritionFixture.CelsiusId,
            _ => Guid.NewGuid(),
        };
        var (ingredient, name) = await IngredientAsync($"badportion-{kind}");
        Evidence.Has(Id($"j-{kind}"), "Food j");
        Evidence.Finds(name, Id($"j-{kind}"));
        var candidate = Value(await Facade(WorkspaceA).ProposeAsync(ingredient, Ct));

        var result = await Facade(WorkspaceA).ConfirmAsync(ingredient, new()
        {
            ExpectedConcurrencyToken = candidate.ConcurrencyToken,
            Portion = new() { MeasurementUnitId = unit, Amount = 1m, Grams = 10m },
        }, Ct);

        Assert.Equal(NutritionErrorCodes.PortionUnprocessable, result.Error?.Code);
        Assert.Equal(NutritionMappingStatus.Candidate, Value(await Facade(WorkspaceA).GetAsync(ingredient, Ct)).Status);
    }

    [Theory]
    [InlineData(null, 1, 10)]
    [InlineData("not-a-token", 1, 10)]
    [InlineData("AAAAAAAAAAA=", 0, 10)]
    [InlineData("AAAAAAAAAAA=", 1, -1)]
    [InlineData("AAAAAAAAAAA=", 1, 10.00001)]
    public async Task A_confirmation_that_cannot_be_read_is_invalid(string? token, double amount, double grams)
    {
        var result = await Facade(WorkspaceA).ConfirmAsync(Guid.NewGuid(), new()
        {
            ExpectedConcurrencyToken = token,
            Portion = new() { MeasurementUnitId = SqlServerNutritionFixture.CupId, Amount = (decimal)amount, Grams = (decimal)grams },
        }, Ct);

        Assert.Equal(NutritionErrorCodes.MappingInvalid, result.Error?.Code);
    }

    // ---- Rejection ----

    [Fact]
    public async Task A_rejected_reference_is_never_proposed_again_and_running_out_leaves_it_unresolved()
    {
        var (ingredient, name) = await IngredientAsync("reject");
        Evidence.Has(Id("k1"), "Food k1");
        Evidence.Has(Id("k2"), "Food k2");
        Evidence.Finds(name, Id("k1"), Id("k2"));
        var facade = Facade(WorkspaceA);

        var first = Value(await facade.ProposeAsync(ingredient, Ct));
        var rejected = Value(await facade.RejectAsync(ingredient, new() { ExpectedConcurrencyToken = first.ConcurrencyToken }, Ct));

        Assert.Equal(NutritionMappingStatus.Unresolved, rejected.Status);
        Assert.Equal(NutritionUnresolvedReason.ReferenceRejected, rejected.UnresolvedReason);
        Assert.Equal(1, rejected.RejectedReferenceCount);

        var second = Value(await facade.ProposeAsync(ingredient, Ct));
        Assert.Equal(Id("k2"), second.Reference!.ExternalRecordId);

        Value(await facade.RejectAsync(ingredient, new() { ExpectedConcurrencyToken = second.ConcurrencyToken }, Ct));
        var none = Value(await facade.ProposeAsync(ingredient, Ct));

        Assert.Equal(NutritionUnresolvedReason.NoCandidates, none.UnresolvedReason);
        Assert.Equal(2, none.RejectedReferenceCount);
        Assert.Contains(NutritionAuditActions.Rejected, (await AuditAsync(WorkspaceA, none.Id)).Select(entry => entry.Action));
    }

    [Fact]
    public async Task A_confirmed_reference_can_be_rejected_and_its_confirmation_goes_with_it()
    {
        var (ingredient, token) = await ConfirmedAsync("rejectconfirmed", Id("l1"));

        var rejected = Value(await Facade(WorkspaceA).RejectAsync(ingredient, new() { ExpectedConcurrencyToken = token }, Ct));

        Assert.Equal(NutritionMappingStatus.Unresolved, rejected.Status);
        Assert.Null(rejected.ConfirmedAt);
        Assert.Null(rejected.Reference);
    }

    [Fact]
    public async Task There_is_nothing_to_reject_on_an_unresolved_mapping()
    {
        var (ingredient, _) = await IngredientAsync("nothingtoreject");
        var unresolved = Value(await Facade(WorkspaceA).MarkUnresolvedAsync(ingredient, new(), Ct));

        var result = await Facade(WorkspaceA).RejectAsync(ingredient, new() { ExpectedConcurrencyToken = unresolved.ConcurrencyToken }, Ct);

        Assert.Equal(NutritionErrorCodes.MappingConflict, result.Error?.Code);
    }

    // ---- Remap ----

    [Fact]
    public async Task A_remap_overrides_the_proposal_and_is_confirmed_as_the_creators_choice()
    {
        var (ingredient, name) = await IngredientAsync("remap");
        Evidence.Has(Id("m1"), "Food m1");
        Evidence.Has(Id("m2"), "Food m2", "foundation/2024-04-18");
        Evidence.Finds(name, Id("m1"));
        var candidate = Value(await Facade(WorkspaceA).ProposeAsync(ingredient, Ct));

        var remapped = Value(await Facade(WorkspaceA).RemapAsync(ingredient, new()
        {
            ExpectedConcurrencyToken = candidate.ConcurrencyToken,
            SourceCode = "usda-fdc",
            ExternalRecordId = Id("m2"),
        }, Ct));

        Assert.Equal(NutritionMappingStatus.Confirmed, remapped.Status);
        Assert.Equal(NutritionMatchMethod.CreatorSelected, remapped.MatchMethod);
        Assert.Null(remapped.Confidence);
        Assert.Equal(new NutritionMappingReferenceServiceModel("usda-fdc", Id("m2"), "Food m2", "foundation/2024-04-18"), remapped.Reference);
        Assert.Contains(NutritionAuditActions.Remapped, (await AuditAsync(WorkspaceA, remapped.Id)).Select(entry => entry.Action));
    }

    [Fact]
    public async Task A_remap_with_no_mapping_yet_makes_a_confirmed_one()
    {
        var (ingredient, _) = await IngredientAsync("remapnew");
        Evidence.Has(Id("n1"), "Food n1");

        var remapped = Value(await Facade(WorkspaceA).RemapAsync(ingredient, new() { SourceCode = "usda-fdc", ExternalRecordId = Id("n1") }, Ct));

        Assert.Equal(NutritionMappingStatus.Confirmed, remapped.Status);
    }

    [Theory]
    [InlineData(ReferenceEvidenceOutcome.NotFound)]
    [InlineData(ReferenceEvidenceOutcome.NoApprovedSource)]
    public async Task A_remap_to_anything_but_an_approved_record_is_refused(ReferenceEvidenceOutcome outcome)
    {
        var (ingredient, _) = await IngredientAsync($"remapbad-{outcome}");
        Evidence.Answers(Id($"o-{outcome}"), outcome);

        var result = await Facade(WorkspaceA).RemapAsync(ingredient, new() { SourceCode = "usda-fdc", ExternalRecordId = Id($"o-{outcome}") }, Ct);

        Assert.Equal(NutritionErrorCodes.ReferenceUnprocessable, result.Error?.Code);
    }

    [Fact]
    public async Task A_remap_to_a_source_that_is_not_approved_is_refused_without_asking_it()
    {
        var (ingredient, _) = await IngredientAsync("remapothersource");

        var result = await Facade(WorkspaceA).RemapAsync(ingredient, new() { SourceCode = "open-food-facts", ExternalRecordId = "123" }, Ct);

        Assert.Equal(NutritionErrorCodes.ReferenceUnprocessable, result.Error?.Code);
    }

    [Fact]
    public async Task Remapping_to_a_rejected_reference_withdraws_the_rejection_and_another_food_drops_the_portion()
    {
        var cup = new PortionEvidence(1m, 125m, "cup-us", null, "cup");
        var (ingredient, name) = await IngredientAsync("remaprejected");
        Evidence.Has(Id("p1"), "Food p1", "sr-legacy/2019-04-01", cup);
        Evidence.Has(Id("p2"), "Food p2");
        Evidence.Finds(name, Id("p1"));
        var facade = Facade(WorkspaceA);

        var candidate = Value(await facade.ProposeAsync(ingredient, Ct));
        var rejected = Value(await facade.RejectAsync(ingredient, new() { ExpectedConcurrencyToken = candidate.ConcurrencyToken }, Ct));
        var back = Value(await facade.RemapAsync(ingredient, new()
        {
            ExpectedConcurrencyToken = rejected.ConcurrencyToken,
            SourceCode = "usda-fdc",
            ExternalRecordId = Id("p1"),
            Portion = new() { MeasurementUnitId = SqlServerNutritionFixture.CupId, Amount = 1m, Grams = 125m },
        }, Ct));

        Assert.Equal(0, back.RejectedReferenceCount);
        Assert.NotNull(back.Portion);

        var elsewhere = Value(await facade.RemapAsync(ingredient, new()
        {
            ExpectedConcurrencyToken = back.ConcurrencyToken,
            SourceCode = "usda-fdc",
            ExternalRecordId = Id("p2"),
        }, Ct));

        Assert.Null(elsewhere.Portion);
    }

    // ---- Unresolved ----

    [Fact]
    public async Task Marking_a_confirmed_mapping_unresolved_clears_its_reference_portion_and_confirmation()
    {
        var (ingredient, token) = await ConfirmedAsync("markunresolved", Id("q1"));

        var marked = Value(await Facade(WorkspaceA).MarkUnresolvedAsync(ingredient, new() { ExpectedConcurrencyToken = token }, Ct));

        Assert.Equal(NutritionMappingStatus.Unresolved, marked.Status);
        Assert.Equal(NutritionUnresolvedReason.CreatorDeclined, marked.UnresolvedReason);
        Assert.Null(marked.Reference);
        Assert.Null(marked.ConfirmedAt);
        Assert.Null(marked.MatchMethod);
    }

    // ---- A stale or withdrawn source ----

    [Fact]
    public async Task A_newer_version_puts_a_confirmed_mapping_under_review_and_confirming_accepts_it()
    {
        var (ingredient, _) = await ConfirmedAsync("newer", Id("r1"), "sr-legacy/2019-04-01");
        Evidence.Has(Id("r1"), "Food r1", "sr-legacy/2026-04-01");

        var review = Value(await Facade(WorkspaceA).RefreshAsync(ingredient, Ct));

        Assert.Equal(NutritionMappingStatus.NeedsReview, review.Status);
        Assert.Equal("sr-legacy/2019-04-01", review.Reference!.SourceVersion);
        Assert.Equal("sr-legacy/2026-04-01", review.ReviewSourceVersion);
        Assert.NotNull(review.ConfirmedAt);
        Assert.Contains(NutritionAuditActions.FlaggedForReview, (await AuditAsync(WorkspaceA, review.Id)).Select(entry => entry.Action));

        var accepted = Value(await Facade(WorkspaceA).ConfirmAsync(ingredient, new() { ExpectedConcurrencyToken = review.ConcurrencyToken }, Ct));

        Assert.Equal(NutritionMappingStatus.Confirmed, accepted.Status);
        Assert.Equal("sr-legacy/2026-04-01", accepted.Reference!.SourceVersion);
        Assert.Null(accepted.ReviewSourceVersion);
    }

    [Fact]
    public async Task The_same_version_changes_nothing()
    {
        var (ingredient, token) = await ConfirmedAsync("sameversion", Id("s1"));

        var refreshed = Value(await Facade(WorkspaceA).RefreshAsync(ingredient, Ct));

        Assert.Equal(NutritionMappingStatus.Confirmed, refreshed.Status);
        Assert.Equal(token, refreshed.ConcurrencyToken);
    }

    [Fact]
    public async Task A_candidate_moves_to_a_newer_version_without_a_review()
    {
        var (ingredient, name) = await IngredientAsync("candidatenewer");
        Evidence.Has(Id("t1"), "Food t1", "sr-legacy/2019-04-01");
        Evidence.Finds(name, Id("t1"));
        Value(await Facade(WorkspaceA).ProposeAsync(ingredient, Ct));
        Evidence.Has(Id("t1"), "Food t1, renamed", "sr-legacy/2026-04-01");

        var refreshed = Value(await Facade(WorkspaceA).RefreshAsync(ingredient, Ct));

        Assert.Equal(NutritionMappingStatus.Candidate, refreshed.Status);
        Assert.Equal(new NutritionMappingReferenceServiceModel("usda-fdc", Id("t1"), "Food t1, renamed", "sr-legacy/2026-04-01"), refreshed.Reference);
    }

    [Theory]
    [InlineData(ReferenceEvidenceOutcome.NotFound)]
    [InlineData(ReferenceEvidenceOutcome.NoApprovedSource)]
    public async Task A_record_the_source_withdrew_leaves_the_mapping_unresolved(ReferenceEvidenceOutcome outcome)
    {
        var (ingredient, _) = await ConfirmedAsync($"withdrawn-{outcome}", Id($"u-{outcome}"));
        Evidence.Answers(Id($"u-{outcome}"), outcome);

        var refreshed = Value(await Facade(WorkspaceA).RefreshAsync(ingredient, Ct));

        Assert.Equal(NutritionMappingStatus.Unresolved, refreshed.Status);
        Assert.Equal(NutritionUnresolvedReason.ReferenceWithdrawn, refreshed.UnresolvedReason);
        Assert.Contains(NutritionAuditActions.ReferenceWithdrawn, (await AuditAsync(WorkspaceA, refreshed.Id)).Select(entry => entry.Action));
    }

    [Fact]
    public async Task An_outage_during_a_refresh_is_not_read_as_a_withdrawal()
    {
        var (ingredient, token) = await ConfirmedAsync("refreshoutage", Id("v1"));

        Evidence.Down = true;
        try
        {
            Assert.Equal(NutritionErrorCodes.SourceUnavailable, (await Facade(WorkspaceA).RefreshAsync(ingredient, Ct)).Error?.Code);
        }
        finally
        {
            Evidence.Down = false;
        }

        var unchanged = Value(await Facade(WorkspaceA).GetAsync(ingredient, Ct));
        Assert.Equal(NutritionMappingStatus.Confirmed, unchanged.Status);
        Assert.Equal(token, unchanged.ConcurrencyToken);
    }

    // ---- Roles ----

    [Fact]
    public async Task A_viewer_reads_a_contributor_proposes_and_only_an_editor_decides()
    {
        var (ingredient, name) = await IngredientAsync("roles");
        Evidence.Has(Id("w1"), "Food w1");
        Evidence.Finds(name, Id("w1"));

        Assert.Equal(NutritionErrorCodes.MappingForbidden, (await Facade(WorkspaceA, WorkspaceRole.Viewer).ProposeAsync(ingredient, Ct)).Error?.Code);
        Assert.Equal(NutritionErrorCodes.MappingForbidden, (await Facade(WorkspaceA, WorkspaceRole.Viewer).RefreshAsync(ingredient, Ct)).Error?.Code);

        var candidate = Value(await Facade(WorkspaceA, WorkspaceRole.Contributor).ProposeAsync(ingredient, Ct));
        Assert.Equal(candidate.Id, Value(await Facade(WorkspaceA, WorkspaceRole.Viewer).GetAsync(ingredient, Ct)).Id);

        var contributor = Facade(WorkspaceA, WorkspaceRole.Contributor);
        var token = new NutritionMappingDecisionViewModel { ExpectedConcurrencyToken = candidate.ConcurrencyToken };
        Assert.Equal(NutritionErrorCodes.MappingForbidden, (await contributor.ConfirmAsync(ingredient, new() { ExpectedConcurrencyToken = candidate.ConcurrencyToken }, Ct)).Error?.Code);
        Assert.Equal(NutritionErrorCodes.MappingForbidden, (await contributor.RejectAsync(ingredient, token, Ct)).Error?.Code);
        Assert.Equal(NutritionErrorCodes.MappingForbidden, (await contributor.MarkUnresolvedAsync(ingredient, token, Ct)).Error?.Code);
        Assert.Equal(NutritionErrorCodes.MappingForbidden, (await contributor.RemapAsync(ingredient, new() { SourceCode = "usda-fdc", ExternalRecordId = Id("w1") }, Ct)).Error?.Code);

        // A refresh can withdraw a confirmed reference and the portion an Editor set, so it is an Editor's too.
        Assert.Equal(NutritionErrorCodes.MappingForbidden, (await contributor.RefreshAsync(ingredient, Ct)).Error?.Code);

        Assert.Equal(NutritionMappingStatus.Candidate, Value(await Facade(WorkspaceA).GetAsync(ingredient, Ct)).Status);
    }

    // ---- Two workspaces ----

    [Fact]
    public async Task Two_workspaces_map_the_same_ingredient_independently_and_neither_sees_the_others()
    {
        var (ingredient, name) = await IngredientAsync("isolation");
        Evidence.Has(Id("x1"), "Food x1");
        Evidence.Has(Id("x2"), "Food x2");
        Evidence.Finds(name, Id("x1"), Id("x2"));

        // A rejects the first proposal and confirms the second, with a portion of its own.
        var a = Facade(WorkspaceA);
        var aFirst = Value(await a.ProposeAsync(ingredient, Ct));
        var aRejected = Value(await a.RejectAsync(ingredient, new() { ExpectedConcurrencyToken = aFirst.ConcurrencyToken }, Ct));
        var aSecond = Value(await a.ProposeAsync(ingredient, Ct));
        var aConfirmed = Value(await a.ConfirmAsync(ingredient, new()
        {
            ExpectedConcurrencyToken = aSecond.ConcurrencyToken,
            Portion = new() { MeasurementUnitId = SqlServerNutritionFixture.EachId, Amount = 1m, Grams = 42m },
        }, Ct));
        Assert.Equal(Id("x2"), aConfirmed.Reference!.ExternalRecordId);

        // B has no mapping at all: not A's, not a copy of it.
        var b = Facade(WorkspaceB);
        Assert.Equal(NutritionErrorCodes.MappingNotFound, (await b.GetAsync(ingredient, Ct)).Error?.Code);
        Assert.Empty(await b.ListAsync([ingredient], Ct));

        // A's rejection does not reach B: B is offered the very reference A turned down, and A's portion is not B's.
        var bProposal = Value(await b.ProposeAsync(ingredient, Ct));
        Assert.Equal(Id("x1"), bProposal.Reference!.ExternalRecordId);
        Assert.Equal(0, bProposal.RejectedReferenceCount);
        Assert.Null(bProposal.Portion);
        Assert.NotEqual(aConfirmed.Id, bProposal.Id);

        // A's token is no key to B's row, and B's decisions leave A's as they were.
        Assert.Equal(NutritionErrorCodes.MappingStale, (await b.ConfirmAsync(ingredient, new() { ExpectedConcurrencyToken = aConfirmed.ConcurrencyToken }, Ct)).Error?.Code);
        Value(await b.MarkUnresolvedAsync(ingredient, new() { ExpectedConcurrencyToken = bProposal.ConcurrencyToken }, Ct));

        var aAfter = Value(await a.GetAsync(ingredient, Ct));
        Assert.Equal(NutritionMappingStatus.Confirmed, aAfter.Status);
        Assert.Equal(1, aAfter.RejectedReferenceCount);
        Assert.Equal(42m, aAfter.Portion!.Grams);
        Assert.Equal(aConfirmed.ConcurrencyToken, aAfter.ConcurrencyToken);
        Assert.Equal([aAfter.Id], (await a.ListAsync([ingredient], Ct)).Select(mapping => mapping.Id));

        // At the table: one row per workspace, each carrying its own workspace, and A's rejection only in A.
        await using var platform = fixture.PlatformScope();
        var db = platform.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var rows = await db.NutritionMappings.IgnoreQueryFilters().Where(row => row.IngredientId == ingredient).ToListAsync(Ct);
        Assert.Equal(new[] { WorkspaceA, WorkspaceB }.Order(), rows.Select(row => row.WorkspaceId).Order());
        var mappingIds = rows.Select(mapping => mapping.Id).ToList();
        var rejections = await db.NutritionMappingRejections.IgnoreQueryFilters()
            .Where(row => mappingIds.Contains(row.NutritionMappingId)).ToListAsync(Ct);
        Assert.NotEmpty(rejections);
        Assert.All(rejections, rejection => Assert.Equal(WorkspaceA, rejection.WorkspaceId));
        Assert.NotNull(aRejected);
    }

    [Fact]
    public async Task A_workspace_with_no_mapping_cannot_reach_the_one_another_workspace_has()
    {
        var (ingredient, recordId) = (await ConfirmedAsync("isolation-nomapping", Id("y1")), Id("y1"));
        var aBefore = Value(await Facade(WorkspaceA).GetAsync(ingredient.Ingredient, Ct));
        var aToken = new NutritionMappingDecisionViewModel { ExpectedConcurrencyToken = aBefore.ConcurrencyToken };

        // B holds A's token and A's ingredient, and none of it finds A's row.
        var b = Facade(WorkspaceB);
        Assert.Equal(NutritionErrorCodes.MappingNotFound, (await b.ConfirmAsync(ingredient.Ingredient, new() { ExpectedConcurrencyToken = aBefore.ConcurrencyToken }, Ct)).Error?.Code);
        Assert.Equal(NutritionErrorCodes.MappingNotFound, (await b.RejectAsync(ingredient.Ingredient, aToken, Ct)).Error?.Code);
        Assert.Equal(NutritionErrorCodes.MappingNotFound, (await b.RefreshAsync(ingredient.Ingredient, Ct)).Error?.Code);
        Assert.Empty(await AuditAsync(WorkspaceB, aBefore.Id));

        // What B can do is make its own: a remap writes B's row and leaves A's exactly as it was.
        var bOwn = Value(await b.RemapAsync(ingredient.Ingredient, new() { SourceCode = "usda-fdc", ExternalRecordId = recordId }, Ct));
        Assert.NotEqual(aBefore.Id, bOwn.Id);

        var aAfter = Value(await Facade(WorkspaceA).GetAsync(ingredient.Ingredient, Ct));
        Assert.Equal(aBefore, aAfter);
    }

    [Fact]
    public async Task What_leaves_for_the_source_is_the_platform_ingredients_name_whichever_workspace_asks()
    {
        var (ingredient, name) = await IngredientAsync("searchterm");
        Evidence.Has(Id("z1"), "Food z1");
        Evidence.Finds(name, Id("z1"));
        var before = Evidence.Terms.Count;

        Value(await Facade(WorkspaceA).ProposeAsync(ingredient, Ct));
        Value(await Facade(WorkspaceB).ProposeAsync(ingredient, Ct));

        Assert.Equal([name, name], Evidence.Terms.Skip(before));
    }

    // ---- The schema holds the rules too ----

    public static TheoryData<string> Violations() =>
    [
        "unresolved-with-reference",
        "candidate-without-reference",
        "partial-portion",
        "confidence-on-a-creator-choice",
        "review-without-version",
        "confirmed-without-confirmation",
        "portion-on-unresolved",
    ];

    [Theory]
    [MemberData(nameof(Violations))]
    public async Task A_row_that_breaks_a_lifecycle_rule_is_refused_by_the_database(string violation)
    {
        var (ingredient, _) = await IngredientAsync($"constraint-{violation}");
        var mapping = ValidCandidate(ingredient);

        switch (violation)
        {
            case "unresolved-with-reference":
                mapping.Status = NutritionMappingStatus.Unresolved;
                mapping.UnresolvedReason = NutritionUnresolvedReason.CreatorDeclined;
                mapping.Confidence = null;
                break;
            case "candidate-without-reference":
                mapping.ExternalRecordId = null;
                break;
            case "partial-portion":
                mapping.PortionMeasurementUnitId = SqlServerNutritionFixture.CupId;
                mapping.PortionAmount = 1m;
                mapping.PortionOrigin = NutritionPortionOrigin.Creator;
                break;
            case "confidence-on-a-creator-choice":
                mapping.MatchMethod = NutritionMatchMethod.CreatorSelected;
                break;
            case "review-without-version":
                mapping.Status = NutritionMappingStatus.NeedsReview;
                mapping.ConfirmedAt = SqlServerNutritionFixture.Now;
                mapping.ConfirmedByMembershipId = Guid.NewGuid();
                break;
            case "confirmed-without-confirmation":
                mapping.Status = NutritionMappingStatus.Confirmed;
                break;
            case "portion-on-unresolved":
                mapping.Status = NutritionMappingStatus.Unresolved;
                mapping.UnresolvedReason = NutritionUnresolvedReason.CreatorDeclined;
                mapping.ReferenceSourceId = null;
                mapping.SourceCode = null;
                mapping.ExternalRecordId = null;
                mapping.ReferenceDescription = null;
                mapping.SourceVersion = null;
                mapping.MatchMethod = null;
                mapping.Confidence = null;
                mapping.PortionMeasurementUnitId = SqlServerNutritionFixture.CupId;
                mapping.PortionAmount = 1m;
                mapping.PortionGrams = 120m;
                mapping.PortionOrigin = NutritionPortionOrigin.Creator;
                break;
        }

        await using var scope = fixture.ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.NutritionMappings.Add(mapping);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task The_valid_row_the_violations_start_from_is_accepted()
    {
        var (ingredient, _) = await IngredientAsync("constraint-valid");

        await using var scope = fixture.ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.NutritionMappings.Add(ValidCandidate(ingredient));

        Assert.Equal(1, await db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_second_mapping_of_the_same_ingredient_in_one_workspace_is_refused()
    {
        var (ingredient, _) = await IngredientAsync("constraint-unique");

        await using (var first = fixture.ScopeFor(WorkspaceA))
        {
            var db = first.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.NutritionMappings.Add(ValidCandidate(ingredient));
            await db.SaveChangesAsync(Ct);
        }

        await using var second = fixture.ScopeFor(WorkspaceA);
        var again = second.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        again.NutritionMappings.Add(ValidCandidate(ingredient));

        await Assert.ThrowsAsync<DbUpdateException>(() => again.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task The_ingredients_facade_will_not_ask_a_source_that_is_not_approved_for_nutrition()
    {
        await using var scope = fixture.ScopeFor(WorkspaceA);
        var ingredients = scope.ServiceProvider.GetRequiredService<IIngredientFacade>();

        var answer = await ingredients.GetNutritionReferenceAsync("open-food-facts", "123", Ct);

        Assert.Equal(NutritionReferenceStatus.NoApprovedSource, answer.Status);
    }

    [Fact]
    public void The_mapping_widths_hold_everything_the_evidence_contract_can_return()
    {
        Assert.Equal(DensityPolicy.SourceCodeMaxLength, NutritionMappingPolicy.SourceCodeMaxLength);
        Assert.Equal(EvidencePolicy.ExternalRecordIdMaxLength, NutritionMappingPolicy.ExternalRecordIdMaxLength);
        Assert.Equal(EvidencePolicy.DescriptionMaxLength, NutritionMappingPolicy.DescriptionMaxLength);
        Assert.Equal(EvidencePolicy.SourceLabelMaxLength, NutritionMappingPolicy.PortionLabelMaxLength);
    }

    // ---- Helpers ----

    private INutritionMappingFacade Facade(Guid workspaceId, WorkspaceRole role = WorkspaceRole.Editor, Guid? membershipId = null) =>
        // Each call is its own request; the scope lives as long as the test, which is all a test needs.
        fixture.ScopeFor(workspaceId, role, membershipId).ServiceProvider.GetRequiredService<INutritionMappingFacade>();

    private async Task<(Guid Id, string Name)> IngredientAsync(string stem, bool isActive = true)
    {
        var name = $"{stem} {Guid.NewGuid():N}";
        return (await fixture.NewIngredientAsync(name, isActive), name);
    }

    /// <summary>A record id unique to this test run, so tests never share a scripted record.</summary>
    private static string Id(string stem) => $"{Math.Abs(stem.GetHashCode()) % 1_000_000:D6}{Suffix}";

    private static readonly string Suffix = (Random.Shared.Next(100_000, 999_999)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task<(Guid Ingredient, string Token)> ConfirmedAsync(string stem, string recordId, string version = "sr-legacy/2019-04-01")
    {
        var (ingredient, name) = await IngredientAsync(stem);
        Evidence.Has(recordId, $"Food {recordId}", version);
        Evidence.Finds(name, recordId);

        var candidate = Value(await Facade(WorkspaceA).ProposeAsync(ingredient, Ct));
        var confirmed = Value(await Facade(WorkspaceA).ConfirmAsync(ingredient, new() { ExpectedConcurrencyToken = candidate.ConcurrencyToken }, Ct));

        return (ingredient, confirmed.ConcurrencyToken);
    }

    private static NutritionMapping ValidCandidate(Guid ingredientId) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = WorkspaceA,
        IngredientId = ingredientId,
        Status = NutritionMappingStatus.Candidate,
        MatchMethod = NutritionMatchMethod.AutomaticSearch,
        Confidence = NutritionMatchConfidence.Low,
        ReferenceSourceId = SqlServerNutritionFixture.FdcSourceId,
        SourceCode = "usda-fdc",
        ExternalRecordId = "169761",
        ReferenceDescription = "Wheat flour",
        SourceVersion = "sr-legacy/2019-04-01",
        CreatedAt = SqlServerNutritionFixture.Now,
        CreatedByMembershipId = Guid.NewGuid(),
        UpdatedAt = SqlServerNutritionFixture.Now,
        UpdatedByMembershipId = Guid.NewGuid(),
    };

    private async Task<NutritionMapping> RowAsync(Guid workspaceId, Guid ingredientId)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .NutritionMappings.AsNoTracking().SingleAsync(row => row.IngredientId == ingredientId, Ct);
    }

    /// <summary>The mapping's audit entries. The clock is fixed, so their order is not asserted.</summary>
    private async Task<List<AuditLog>> AuditAsync(Guid workspaceId, Guid mappingId)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        var id = mappingId.ToString("D");
        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AuditLogs.AsNoTracking()
            .Where(entry => entry.ResourceType == NutritionAuditActions.ResourceType && entry.ResourceId == id)
            .ToListAsync(Ct);
    }

    private static NutritionMappingServiceModel Value(Domain.Managers.Results.OperationResult<NutritionMappingServiceModel> result)
    {
        Assert.True(result.Succeeded, $"{result.Error?.Code}: {result.Error?.Message}");
        return result.Value!;
    }
}
