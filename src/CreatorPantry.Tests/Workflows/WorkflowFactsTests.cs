using System.Reflection;
using System.Runtime.CompilerServices;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Publishing.Facade;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Workflows.Business;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// How each completion rule reads the module that owns its fact (13A.8), against answers this test controls: met,
/// unmet and the edges — a rule the workspace switched off, a booking whose outcome nobody knows, a guide that is not
/// the active one. The end-to-end route is <see cref="WorkflowStepCompletionEndpointTests"/>; these are the states
/// that would take another module's whole workflow to reach through it.
/// </summary>
public sealed class WorkflowFactsTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid RecipeId = Guid.NewGuid();
    private static readonly Guid GuideId = Guid.NewGuid();
    private static readonly Guid ContextId = Guid.NewGuid();
    private static readonly Guid DraftId = Guid.NewGuid();
    private static readonly Guid RecordId = Guid.NewGuid();

    private static readonly WorkflowRun Run = new() { Id = Guid.NewGuid(), StartedAt = Started };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Readiness ----

    [Fact]
    public async Task A_readiness_rule_is_met_when_satisfied_not_applicable_or_switched_off_by_the_workspace()
    {
        var facts = Facts(readiness: Readiness(
            [Finding("recipe.ingredients.present", RecipeReadinessStatus.Satisfied), Finding("recipe.time.stated", RecipeReadinessStatus.NotApplicable)],
            disabled: ["recipe.yield.stated"]));

        Assert.Empty(await UnmetAsync(facts, new WorkflowCompletionRule.ReadinessRulesMet(
            ["recipe.ingredients.present", "recipe.time.stated", "recipe.yield.stated"])));
    }

    [Fact]
    public async Task A_readiness_rule_with_a_finding_against_it_is_unmet_in_the_recipe_modules_own_words()
    {
        var facts = Facts(readiness: Readiness(
            [
                Finding("recipe.instructions.present", RecipeReadinessStatus.Blocker, detail: "The method needs at least one step."),
                Finding("recipe.description.present", RecipeReadinessStatus.Recommendation, detail: null, summary: "The recipe has a description."),
            ]));

        Assert.Equal(
            ["The method needs at least one step.", "The recipe has a description."],
            await UnmetAsync(facts, new WorkflowCompletionRule.ReadinessRulesMet(["recipe.instructions.present", "recipe.description.present"])));
    }

    [Fact]
    public async Task A_readiness_rule_with_no_verdict_at_all_is_unmet_never_assumed()
    {
        var facts = Facts(readiness: Readiness([]));

        var unmet = Assert.Single(await UnmetAsync(facts, new WorkflowCompletionRule.ReadinessRulesMet(["recipe.ingredients.present"])));
        Assert.Contains("couldn't be checked", unmet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_readiness_rule_on_a_run_with_no_recipe_says_so()
    {
        var unmet = await Facts().UnmetAsync(
            new WorkflowCompletionRule.ReadinessRulesMet(["recipe.ingredients.present"]), Run, new Dictionary<WorkflowResourceKind, Guid>(), Ct);

        Assert.Contains("recipe", Assert.Single(unmet), StringComparison.Ordinal);
    }

    // ---- Recipe state ----

    [Fact]
    public async Task A_recipe_status_rule_reads_the_status_as_stored()
    {
        var inReview = Facts(recipe: Recipe(RecipeStatus.ReadyForReview, versionAt: Started));
        var testing = Facts(recipe: Recipe(RecipeStatus.Testing, versionAt: Started));
        var rule = new WorkflowCompletionRule.RecipeStatusIn([RecipeStatus.ReadyForReview, RecipeStatus.Approved]);

        Assert.Empty(await UnmetAsync(inReview, rule));
        Assert.Equal(["The recipe is still being tested."], await UnmetAsync(testing, rule));
    }

    [Fact]
    public async Task A_recipe_counts_as_saved_during_the_run_only_with_a_version_from_after_it_started()
    {
        var rule = new WorkflowCompletionRule.RecipeSavedDuringRun();

        Assert.Empty(await UnmetAsync(Facts(recipe: Recipe(RecipeStatus.Draft, versionAt: Started.AddMinutes(5))), rule));
        Assert.Equal(["Save a change to the recipe first."], await UnmetAsync(Facts(recipe: Recipe(RecipeStatus.Draft, versionAt: Started.AddMinutes(-5))), rule));
    }

    // ---- A piece of writing ----

    [Fact]
    public async Task A_long_form_rule_reads_the_pieces_state()
    {
        var rule = new WorkflowCompletionRule.LongFormDraftStatusIn([LongFormDraftStatus.Accepted]);

        Assert.Empty(await UnmetAsync(Facts(draft: Draft(LongFormDraftStatus.Accepted)), rule));
        Assert.Equal(["The piece is still written but not accepted."], await UnmetAsync(Facts(draft: Draft(LongFormDraftStatus.Assembled)), rule));
    }

    // ---- Brand guide ----

    [Fact]
    public async Task A_guide_is_switched_on_only_when_it_is_the_one_the_workspace_has_active()
    {
        var rule = new WorkflowCompletionRule.BrandGuideIsActive();

        Assert.Empty(await UnmetAsync(Facts(active: Shaped<BrandActiveStyleGuideServiceModel>(new { GuideId })), rule));
        Assert.Equal(
            ["Switch the guide on first."],
            await UnmetAsync(Facts(active: Shaped<BrandActiveStyleGuideServiceModel>(new { GuideId = Guid.NewGuid() })), rule));
        Assert.Equal(["Switch the guide on first."], await UnmetAsync(Facts(active: null), rule));
    }

    // ---- Posts ----

    [Fact]
    public async Task Posts_are_kept_when_at_least_one_channel_is_accepted()
    {
        var rule = new WorkflowCompletionRule.SocialPostAccepted();

        Assert.Empty(await UnmetAsync(Facts(package: Package(ContentProposalStatus.Proposed, ContentProposalStatus.Accepted)), rule));
        Assert.Equal(["Keep at least one post first."], await UnmetAsync(Facts(package: Package(ContentProposalStatus.Proposed)), rule));

        // A context nothing has been written for yet has no package at all.
        Assert.Equal(["Keep at least one post first."], await UnmetAsync(Facts(package: null), rule));
    }

    // ---- Bookings ----

    [Fact]
    public async Task A_booking_counts_by_the_ledgers_own_state_and_an_unknown_outcome_never_counts()
    {
        var rule = new WorkflowCompletionRule.PublishingRecordStatusIn([PublishingRecordStatus.Scheduled, PublishingRecordStatus.Published]);

        Assert.Empty(await UnmetAsync(Facts(record: Record(PublishingRecordStatus.Scheduled, uncertain: false)), rule));
        Assert.Equal(["The booking is not booked — it failed."], await UnmetAsync(Facts(record: Record(PublishingRecordStatus.Failed, uncertain: false)), rule));

        var uncertain = Assert.Single(await UnmetAsync(Facts(record: Record(PublishingRecordStatus.Submitting, uncertain: true)), rule));
        Assert.Contains("couldn't tell", uncertain, StringComparison.Ordinal);
    }

    // ---- Rules met by the step itself ----

    [Fact]
    public async Task An_acknowledged_or_linking_step_has_nothing_further_to_check()
    {
        Assert.Empty(await UnmetAsync(Facts(), new WorkflowCompletionRule.Acknowledged()));
        Assert.Empty(await UnmetAsync(Facts(), new WorkflowCompletionRule.ResourceLinked(WorkflowResourceKind.Recipe)));
    }

    [Fact]
    public async Task A_guide_and_a_booking_are_checked_through_their_own_modules()
    {
        var facts = Facts(guideExists: true, record: Record(PublishingRecordStatus.Pending, uncertain: false));

        Assert.True(await facts.ExistsAsync(WorkflowResourceKind.BrandStyleGuide, GuideId, Ct));
        Assert.True(await facts.ExistsAsync(WorkflowResourceKind.PublishingRecord, RecordId, Ct));
        Assert.False(await Facts(guideExists: false).ExistsAsync(WorkflowResourceKind.BrandStyleGuide, GuideId, Ct));
    }

    // ---- Helpers ----

    private static Task<IReadOnlyList<string>> UnmetAsync(WorkflowFacts facts, WorkflowCompletionRule rule) =>
        facts.UnmetAsync(rule, Run, new Dictionary<WorkflowResourceKind, Guid>
        {
            [WorkflowResourceKind.Recipe] = RecipeId,
            [WorkflowResourceKind.BrandStyleGuide] = GuideId,
            [WorkflowResourceKind.CreativeContext] = ContextId,
            [WorkflowResourceKind.LongFormDraft] = DraftId,
            [WorkflowResourceKind.PublishingRecord] = RecordId,
        }, Ct);

    /// <summary>Facts over stand-in facades: each answers only what the test gave it, and anything else throws.</summary>
    private static WorkflowFacts Facts(
        RecipeReadinessServiceModel? readiness = null,
        RecipeDetailServiceModel? recipe = null,
        LongFormDraftServiceModel? draft = null,
        BrandActiveStyleGuideServiceModel? active = null,
        SocialPackageServiceModel? package = null,
        PublishingRecordServiceModel? record = null,
        bool guideExists = false) =>
        new(
            Stub.For<IRecipeFacade>(("GetDetailAsync", _ => Answer(recipe))),
            Stub.For<IRecipeReadinessFacade>(("EvaluateAsync", _ => Answer(readiness))),
            Stub.For<IMediaAssetLookupFacade>(),
            Stub.For<IContentCardLookupFacade>(),
            Stub.For<ICreativeContextLookupFacade>(),
            Stub.For<ILongFormDraftFacade>(("GetAsync", _ => Answer(draft))),
            Stub.For<IBrandStyleGuideFacade>(
                ("GetActiveAsync", _ => Task.FromResult(OperationResult<BrandActiveStyleGuideServiceModel?>.Success(active))),
                ("GetAsync", _ => guideExists
                    ? Task.FromResult(OperationResult<BrandStyleGuideDetailServiceModel>.Success(Shaped<BrandStyleGuideDetailServiceModel>(new { Id = GuideId })))
                    : Task.FromResult(OperationResult<BrandStyleGuideDetailServiceModel>.Failure(NotFound())))),
            Stub.For<IBrandProfileFacade>(),
            Stub.For<ISocialPackageFacade>(("GetAsync", _ => Task.FromResult(OperationResult<SocialPackageServiceModel?>.Success(package)))),
            Stub.For<IPublishingRecordFacade>(("GetAsync", _ => Answer(record))),
            Stub.For<IAiConceptRequestFacade>(),
            Stub.For<IAiFirstDraftRequestFacade>());

    private static Task<OperationResult<T>> Answer<T>(T? value) where T : class =>
        Task.FromResult(value is null ? OperationResult<T>.Failure(NotFound()) : OperationResult<T>.Success(value));

    private static OperationError NotFound() => new("test.not_found", "Not found.", new Dictionary<string, string[]>());

    private static RecipeReadinessServiceModel Readiness(IReadOnlyList<RecipeReadinessFinding> findings, IReadOnlyList<string>? disabled = null) =>
        Shaped<RecipeReadinessServiceModel>(new { RecipeId, Findings = findings, DisabledRuleIds = disabled ?? [] });

    private static RecipeReadinessFinding Finding(string ruleId, RecipeReadinessStatus status, string? detail = null, string summary = "The rule.") =>
        new(ruleId, status, summary, detail, [], SeverityOverridden: false);

    private static RecipeDetailServiceModel Recipe(RecipeStatus status, DateTimeOffset versionAt) =>
        Shaped<RecipeDetailServiceModel>(new
        {
            Status = status,
            CurrentVersion = Shaped<RecipeVersionSummaryServiceModel>(new { CreatedAt = versionAt, VersionNumber = 2 }),
        });

    private static LongFormDraftServiceModel Draft(LongFormDraftStatus status) =>
        Shaped<LongFormDraftServiceModel>(new { Id = DraftId, Status = status });

    private static SocialPackageServiceModel Package(params ContentProposalStatus[] channels) =>
        Shaped<SocialPackageServiceModel>(new
        {
            CreativeContextId = ContextId,
            Channels = (IReadOnlyList<SocialChannelServiceModel>)[.. channels.Select(status => Shaped<SocialChannelServiceModel>(new { ChannelKey = "instagram", Status = status }))],
        });

    private static PublishingRecordServiceModel Record(PublishingRecordStatus status, bool uncertain) =>
        Shaped<PublishingRecordServiceModel>(new { Id = RecordId, Status = status, OutcomeUncertain = uncertain });

    /// <summary>
    /// A service model with only the members a rule reads, the rest left at their defaults — so a test names what it
    /// depends on, and a field added to another module's model does not reach in here.
    /// </summary>
    private static T Shaped<T>(object values)
    {
        var instance = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        foreach (var value in values.GetType().GetProperties())
        {
            var property = typeof(T).GetProperty(value.Name)
                ?? throw new InvalidOperationException($"{typeof(T).Name} has no {value.Name}.");
            var field = typeof(T).GetField($"<{value.Name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

            if (property.SetMethod is not null)
            {
                property.SetValue(instance, value.GetValue(values));
            }
            else
            {
                (field ?? throw new InvalidOperationException($"{typeof(T).Name}.{value.Name} cannot be set.")).SetValue(instance, value.GetValue(values));
            }
        }

        return instance;
    }

    /// <summary>A facade that answers the named methods and throws for any other, so an unexpected call is loud.</summary>
    public class Stub : DispatchProxy
    {
        private readonly Dictionary<string, Func<object?[], object?>> _answers = new(StringComparer.Ordinal);

        public static T For<T>(params (string Method, Func<object?[], object?> Answer)[] answers)
            where T : class
        {
            var proxy = Create<T, Stub>();
            var stub = (Stub)(object)proxy;
            foreach (var (method, answer) in answers)
            {
                stub._answers[method] = answer;
            }

            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            _answers.TryGetValue(targetMethod!.Name, out var answer)
                ? answer(args ?? [])
                : throw new NotSupportedException($"{targetMethod.DeclaringType?.Name}.{targetMethod.Name} was not expected here.");
    }
}
