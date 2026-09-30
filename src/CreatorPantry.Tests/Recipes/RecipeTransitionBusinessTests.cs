using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// What the transition command does with a move the machine has, and what it refuses (TESTRUN-005). Against a
/// recording DataLayer: what is under test is the decision, not the save.
/// </summary>
/// <remarks>
/// The machine's shape is <see cref="RecipeStatusTransitionsTests"/> and the atomicity of the save is
/// <c>RecipeDataLayerTests</c>. Here: the order the checks run in, what each refusal says, and what the
/// transition row ends up holding.
/// </remarks>
public sealed class RecipeTransitionBusinessTests
{
    private static readonly Guid ActorMembershipId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Eight bytes, base64 — the token a read publishes for <see cref="Stored"/>.</summary>
    private const string Token = "AQIDBAUGBwg=";

    private readonly StubTransitionDataLayer _dataLayer = new();
    private readonly StubRoleWorkspaceContext _workspace = new(ActorMembershipId);
    private readonly IRecipeBusiness _business;

    public RecipeTransitionBusinessTests() =>
        _business = new ServiceCollection()
            .AddSingleton<IRecipeDataLayer>(_dataLayer)
            .AddSingleton<IWorkspaceContext>(_workspace)
            .AddSingleton<IClock>(new StubTransitionClock(Now))
            .AddSingleton<IRecipeBusiness, RecipeBusiness>()
            .BuildServiceProvider()
            .GetRequiredService<IRecipeBusiness>();

    private static readonly Guid CurrentVersionId = Guid.NewGuid();

    private static Recipe Stored(RecipeStatus status) => new()
    {
        Id = Guid.NewGuid(),
        Title = "Olive oil cake",
        Status = status,
        CreatedAt = Now.AddDays(-2),
        UpdatedAt = Now.AddDays(-1),
        CreatedByMembershipId = Guid.NewGuid(),
        UpdatedByMembershipId = Guid.NewGuid(),
        RowVersion = [1, 2, 3, 4, 5, 6, 7, 8],
    };

    /// <summary>A clear evaluation of <paramref name="recipe"/>, as the facade would have gathered it.</summary>
    private static RecipeReadinessServiceModel Clear(Recipe recipe, params string[] blockingRuleIds) =>
        new(
            recipe.Id,
            CurrentVersionId,
            4,
            RecipeConcurrencyToken.From(recipe.RowVersion),
            RecipeReadinessCatalogue.Version,
            blockingRuleIds.Length > 0,
            blockingRuleIds.Length,
            0,
            [
                .. blockingRuleIds.Select(id => new RecipeReadinessFinding(
                    id, RecipeReadinessStatus.Blocker, "Summary.", "Detail.", [], false)),
            ],
            [],
            []);

    private Task<OperationResult<RecipeDetailServiceModel>> TransitionAsync(
        Recipe recipe,
        RecipeStatus target,
        string? reason = null,
        RecipeReadinessServiceModel? readiness = null,
        string? token = Token)
    {
        _dataLayer.Detail = new TaggedRecipe(new CompleteRecipe(recipe, null), []);

        return _business.TransitionAsync(
            recipe.Id, target, reason, readiness, "user-1", token, TestContext.Current.CancellationToken);
    }

    // ---- Valid moves ----

    /// <summary>
    /// Every forward move and every reopen, at the role the machine asks for, lands the recipe in the target
    /// state and records the pair.
    /// </summary>
    [Theory]
    [InlineData(RecipeStatus.Draft, RecipeStatus.InDevelopment, WorkspaceRole.Contributor)]
    [InlineData(RecipeStatus.InDevelopment, RecipeStatus.Testing, WorkspaceRole.Contributor)]
    [InlineData(RecipeStatus.Testing, RecipeStatus.ReadyForReview, WorkspaceRole.Contributor)]
    [InlineData(RecipeStatus.Testing, RecipeStatus.InDevelopment, WorkspaceRole.Editor)]
    [InlineData(RecipeStatus.ReadyForReview, RecipeStatus.InDevelopment, WorkspaceRole.Editor)]
    [InlineData(RecipeStatus.Approved, RecipeStatus.InDevelopment, WorkspaceRole.Editor)]
    [InlineData(RecipeStatus.Draft, RecipeStatus.Archived, WorkspaceRole.Editor)]
    [InlineData(RecipeStatus.Approved, RecipeStatus.Archived, WorkspaceRole.Editor)]
    [InlineData(RecipeStatus.Archived, RecipeStatus.Draft, WorkspaceRole.Editor)]
    public async Task A_legal_move_is_applied_and_recorded(
        RecipeStatus from, RecipeStatus to, WorkspaceRole role)
    {
        _workspace.Role = role;
        var recipe = Stored(from);

        var result = await TransitionAsync(recipe, to, reason: "Because the crumb was wrong.");

        Assert.True(result.Succeeded);
        Assert.Equal(to, recipe.Status);
        Assert.Equal(from, _dataLayer.Transition!.FromStatus);
        Assert.Equal(to, _dataLayer.Transition.ToStatus);
    }

    /// <summary>
    /// The row records who and when from the resolved context and the clock — never from a request field —
    /// and stamps the recipe with the same instant so the two cannot disagree.
    /// </summary>
    [Fact]
    public async Task The_actor_and_the_instant_come_from_the_context_and_the_clock()
    {
        var recipe = Stored(RecipeStatus.Draft);

        await TransitionAsync(recipe, RecipeStatus.InDevelopment);

        Assert.Equal(ActorMembershipId, _dataLayer.Transition!.ActorMembershipId);
        Assert.Equal(Now, _dataLayer.Transition.OccurredAt);
        Assert.Equal(Now, recipe.UpdatedAt);
        Assert.Equal(ActorMembershipId, recipe.UpdatedByMembershipId);
    }

    /// <summary>The machine's version travels on the row, so an old move stays readable.</summary>
    [Fact]
    public async Task The_row_records_the_machine_version()
    {
        var recipe = Stored(RecipeStatus.Draft);

        await TransitionAsync(recipe, RecipeStatus.InDevelopment);

        Assert.Equal(RecipeStatusTransitions.Version, _dataLayer.Transition!.MachineVersion);
    }

    /// <summary>A reason is stored trimmed, and whitespace is stored as no reason at all.</summary>
    [Fact]
    public async Task A_reason_is_trimmed_and_blank_becomes_null()
    {
        _workspace.Role = WorkspaceRole.Editor;

        await TransitionAsync(Stored(RecipeStatus.Approved), RecipeStatus.InDevelopment, reason: "  Too dense.  ");
        Assert.Equal("Too dense.", _dataLayer.Transition!.Reason);

        await TransitionAsync(Stored(RecipeStatus.Draft), RecipeStatus.Archived, reason: "   ");
        Assert.Null(_dataLayer.Transition.Reason);
    }

    /// <summary>A move that is not an approval carries none of the approval columns and writes no version.</summary>
    [Fact]
    public async Task An_ordinary_move_writes_no_version_and_no_approval_columns()
    {
        var recipe = Stored(RecipeStatus.InDevelopment);

        await TransitionAsync(recipe, RecipeStatus.Testing);

        Assert.Null(_dataLayer.TransitionVersion);
        Assert.Null(_dataLayer.Transition!.ReadinessRuleSetVersion);
        Assert.Null(_dataLayer.Transition.ReadinessEvaluatedVersionId);
        Assert.Null(_dataLayer.Transition.CreatedVersionId);
    }

    // ---- Repeats ----

    /// <summary>
    /// Asking for the state the recipe is already in succeeds and writes nothing. A repeat, not a jump — and
    /// the reason a replayed approval cannot mint a second version.
    /// </summary>
    [Theory]
    [InlineData(RecipeStatus.Draft)]
    [InlineData(RecipeStatus.Approved)]
    [InlineData(RecipeStatus.Archived)]
    public async Task Asking_for_the_current_state_succeeds_and_writes_nothing(RecipeStatus state)
    {
        var recipe = Stored(state);

        var result = await TransitionAsync(recipe, state);

        Assert.True(result.Succeeded);
        Assert.Equal(0, _dataLayer.Calls);
        Assert.Null(_dataLayer.Transition);
        Assert.Equal(Now.AddDays(-1), recipe.UpdatedAt);
    }

    // ---- Invalid jumps ----

    /// <summary>A move the machine does not have is refused, and the recipe is untouched.</summary>
    [Theory]
    [InlineData(RecipeStatus.Draft, RecipeStatus.Approved)]
    [InlineData(RecipeStatus.Draft, RecipeStatus.Testing)]
    [InlineData(RecipeStatus.Testing, RecipeStatus.Approved)]
    [InlineData(RecipeStatus.Archived, RecipeStatus.Approved)]
    [InlineData(RecipeStatus.InDevelopment, RecipeStatus.Draft)]
    public async Task An_invalid_jump_is_refused(RecipeStatus from, RecipeStatus to)
    {
        var recipe = Stored(from);

        var result = await TransitionAsync(recipe, to, reason: "Trying it on.");

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.TransitionInvalidRequest, result.Error!.Code);
        Assert.Equal(from, recipe.Status);
        Assert.Equal(0, _dataLayer.Calls);
    }

    /// <summary>
    /// The refusal names the moves that are available instead, so a client can say what to do rather than
    /// only that this failed.
    /// </summary>
    [Fact]
    public async Task An_invalid_jump_says_where_the_recipe_could_go()
    {
        var result = await TransitionAsync(Stored(RecipeStatus.Draft), RecipeStatus.Approved);

        var detail = Assert.Single(result.Error!.FieldErrors["transition"]);
        Assert.Contains(nameof(RecipeStatus.InDevelopment), detail);
        Assert.Contains(nameof(RecipeStatus.Archived), detail);
    }

    // ---- Roles ----

    /// <summary>
    /// A role below the move's bar is forbidden, not a conflict: the request is well formed and the recipe is
    /// in the right state, and it is the caller who may not do this.
    /// </summary>
    [Theory]
    [InlineData(WorkspaceRole.Viewer, RecipeStatus.Draft, RecipeStatus.InDevelopment)]
    [InlineData(WorkspaceRole.Contributor, RecipeStatus.ReadyForReview, RecipeStatus.Approved)]
    [InlineData(WorkspaceRole.Contributor, RecipeStatus.Approved, RecipeStatus.InDevelopment)]
    [InlineData(WorkspaceRole.Contributor, RecipeStatus.Draft, RecipeStatus.Archived)]
    public async Task A_role_below_the_bar_is_forbidden(
        WorkspaceRole role, RecipeStatus from, RecipeStatus to)
    {
        _workspace.Role = role;
        var recipe = Stored(from);

        var result = await TransitionAsync(recipe, to, reason: "Please.", readiness: Clear(recipe));

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.TransitionForbidden, result.Error!.Code);
        Assert.Equal(from, recipe.Status);
        Assert.Equal(0, _dataLayer.Calls);
    }

    /// <summary>
    /// Role is checked before the reason, so a Contributor asking for an approval is told they may not rather
    /// than told to write a sentence and then told they may not.
    /// </summary>
    [Fact]
    public async Task The_role_is_checked_before_the_reason()
    {
        _workspace.Role = WorkspaceRole.Contributor;

        var result = await TransitionAsync(Stored(RecipeStatus.Approved), RecipeStatus.InDevelopment);

        Assert.Equal(RecipeErrorCodes.TransitionForbidden, result.Error!.Code);
    }

    // ---- Reason ----

    /// <summary>A reopen with no reason is refused; the history would not explain itself otherwise.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_reopen_without_a_reason_is_refused(string? reason)
    {
        _workspace.Role = WorkspaceRole.Editor;
        var recipe = Stored(RecipeStatus.Approved);

        var result = await TransitionAsync(recipe, RecipeStatus.InDevelopment, reason);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.TransitionInvalidRequest, result.Error!.Code);
        Assert.Equal(RecipeStatus.Approved, recipe.Status);
        Assert.Equal(0, _dataLayer.Calls);
    }

    /// <summary>Every other move is fine without one.</summary>
    [Theory]
    [InlineData(RecipeStatus.Draft, RecipeStatus.InDevelopment)]
    [InlineData(RecipeStatus.Draft, RecipeStatus.Archived)]
    public async Task Another_move_needs_no_reason(RecipeStatus from, RecipeStatus to)
    {
        _workspace.Role = WorkspaceRole.Editor;

        Assert.True((await TransitionAsync(Stored(from), to)).Succeeded);
    }

    // ---- The readiness gate ----

    /// <summary>A clear evaluation approves the recipe and captures the version the approval names.</summary>
    [Fact]
    public async Task An_approval_over_a_clear_evaluation_writes_a_ready_version()
    {
        _workspace.Role = WorkspaceRole.Editor;
        var recipe = Stored(RecipeStatus.ReadyForReview);

        var result = await TransitionAsync(
            recipe, RecipeStatus.Approved, reason: "Ship it.", readiness: Clear(recipe));

        Assert.True(result.Succeeded);
        Assert.Equal(RecipeStatus.Approved, recipe.Status);

        Assert.Equal(RecipeVersionSource.ReadinessApproval, _dataLayer.TransitionVersion!.Source);
        Assert.Equal(RecipeVersionReadiness.Ready, _dataLayer.TransitionVersion.Readiness);
        Assert.Equal("Ship it.", _dataLayer.TransitionVersion.Reason);
    }

    /// <summary>
    /// The approval records which catalogue cleared it and which version was judged. "There were no blockers"
    /// means nothing without the rules that were asked.
    /// </summary>
    [Fact]
    public async Task An_approval_records_the_evaluation_it_relied_on()
    {
        _workspace.Role = WorkspaceRole.Editor;
        var recipe = Stored(RecipeStatus.ReadyForReview);

        await TransitionAsync(recipe, RecipeStatus.Approved, readiness: Clear(recipe));

        Assert.Equal(RecipeReadinessCatalogue.Version, _dataLayer.Transition!.ReadinessRuleSetVersion);
        Assert.Equal(CurrentVersionId, _dataLayer.Transition.ReadinessEvaluatedVersionId);
    }

    /// <summary>
    /// Blockers outstanding is a conflict, and the blocking rule ids travel with it so a client can name them.
    /// </summary>
    [Fact]
    public async Task An_approval_over_blockers_is_refused_and_names_them()
    {
        _workspace.Role = WorkspaceRole.Editor;
        var recipe = Stored(RecipeStatus.ReadyForReview);

        var result = await TransitionAsync(
            recipe,
            RecipeStatus.Approved,
            readiness: Clear(
                recipe,
                RecipeReadinessCatalogue.IngredientsPresent,
                RecipeReadinessCatalogue.MediaHeroMissing));

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.TransitionBlockedConflict, result.Error!.Code);
        Assert.Equal(
            [RecipeReadinessCatalogue.IngredientsPresent, RecipeReadinessCatalogue.MediaHeroMissing],
            result.Error.FieldErrors["blockingRules"]);

        Assert.Equal(RecipeStatus.ReadyForReview, recipe.Status);
        Assert.Equal(0, _dataLayer.Calls);
    }

    /// <summary>
    /// An evaluation of content that has since changed is not a fresh evaluation, whatever it says. Refused
    /// as a conflict, which tells the approver to go and look.
    /// </summary>
    [Fact]
    public async Task An_approval_over_a_stale_evaluation_conflicts()
    {
        _workspace.Role = WorkspaceRole.Editor;
        var recipe = Stored(RecipeStatus.ReadyForReview);

        var somebodyElsesRecipe = Stored(RecipeStatus.ReadyForReview);
        somebodyElsesRecipe.RowVersion = [9, 9, 9, 9, 9, 9, 9, 9];

        var result = await TransitionAsync(
            recipe, RecipeStatus.Approved, readiness: Clear(somebodyElsesRecipe));

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeConflict, result.Error!.Code);
        Assert.Equal(RecipeStatus.ReadyForReview, recipe.Status);
        Assert.Equal(0, _dataLayer.Calls);
    }

    /// <summary>
    /// No evaluation at all is refused rather than assumed clear. Unreachable through the facade, and the
    /// assumption in the other direction would be an approval nothing gated.
    /// </summary>
    [Fact]
    public async Task An_approval_with_no_evaluation_is_refused()
    {
        _workspace.Role = WorkspaceRole.Editor;
        var recipe = Stored(RecipeStatus.ReadyForReview);

        var result = await TransitionAsync(recipe, RecipeStatus.Approved, readiness: null);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.TransitionInvalidRequest, result.Error!.Code);
        Assert.Equal(0, _dataLayer.Calls);
    }

    /// <summary>
    /// An evaluation supplied for a move that does not need one is ignored, blockers and all. Only the
    /// approval is gated, and a blocked recipe may still be shelved or moved along.
    /// </summary>
    [Fact]
    public async Task A_move_that_needs_no_evaluation_ignores_one_with_blockers()
    {
        _workspace.Role = WorkspaceRole.Editor;
        var recipe = Stored(RecipeStatus.Draft);

        var result = await TransitionAsync(
            recipe,
            RecipeStatus.Archived,
            readiness: Clear(recipe, RecipeReadinessCatalogue.IngredientsPresent));

        Assert.True(result.Succeeded);
        Assert.Equal(RecipeStatus.Archived, recipe.Status);
        Assert.Null(_dataLayer.Transition!.ReadinessRuleSetVersion);
    }

    // ---- Concurrency and visibility ----

    /// <summary>
    /// The token is checked before anything else, including the "already there" answer: a creator quoting a
    /// stale token has not seen what the recipe looks like now, and "already approved" would hide a
    /// collaborator's work from them.
    /// </summary>
    [Fact]
    public async Task A_stale_token_conflicts_even_when_the_recipe_is_already_there()
    {
        var recipe = Stored(RecipeStatus.Approved);

        var result = await TransitionAsync(recipe, RecipeStatus.Approved, token: "CQkJCQkJCQk=");

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeConflict, result.Error!.Code);
        Assert.Equal(0, _dataLayer.Calls);
    }

    /// <summary>A recipe the caller cannot see is a 404, before any rule is consulted.</summary>
    [Fact]
    public async Task An_invisible_recipe_is_not_found()
    {
        _dataLayer.Detail = null;

        var result = await _business.TransitionAsync(
            Guid.NewGuid(),
            RecipeStatus.InDevelopment,
            null,
            null,
            "user-1",
            Token,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, result.Error!.Code);
    }

    /// <summary>A save that lost the race is a conflict, and the caller is told so.</summary>
    [Fact]
    public async Task A_save_that_loses_the_race_conflicts()
    {
        _dataLayer.Conflict = true;

        var result = await TransitionAsync(Stored(RecipeStatus.Draft), RecipeStatus.InDevelopment);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeConflict, result.Error!.Code);
    }

    // ---- The audit entry ----

    /// <summary>
    /// Archive, restore and approval each get their own audit code, and every other move shares one — the
    /// three auth.md and REC-006 name are the three somebody asks about on their own.
    /// </summary>
    [Theory]
    [InlineData(RecipeStatus.Draft, RecipeStatus.Archived, RecipeAuditActions.Archived)]
    [InlineData(RecipeStatus.Approved, RecipeStatus.Archived, RecipeAuditActions.Archived)]
    [InlineData(RecipeStatus.Archived, RecipeStatus.Draft, RecipeAuditActions.Unarchived)]
    [InlineData(RecipeStatus.Draft, RecipeStatus.InDevelopment, RecipeAuditActions.StatusTransitioned)]
    [InlineData(RecipeStatus.Approved, RecipeStatus.InDevelopment, RecipeAuditActions.StatusTransitioned)]
    public async Task Each_move_is_audited_under_its_own_code(
        RecipeStatus from, RecipeStatus to, string expected)
    {
        _workspace.Role = WorkspaceRole.Editor;

        await TransitionAsync(Stored(from), to, reason: "Because.");

        Assert.Equal(expected, _dataLayer.Audit!.Action);
    }

    /// <inheritdoc cref="Each_move_is_audited_under_its_own_code"/>
    [Fact]
    public async Task An_approval_is_audited_under_its_own_code()
    {
        _workspace.Role = WorkspaceRole.Editor;
        var recipe = Stored(RecipeStatus.ReadyForReview);

        await TransitionAsync(recipe, RecipeStatus.Approved, readiness: Clear(recipe));

        Assert.Equal(RecipeAuditActions.Approved, _dataLayer.Audit!.Action);
    }

    /// <summary>
    /// The entry carries the two state names and nothing else about the recipe. AuditLog requires its
    /// references to stay safe to display, and the reason is the creator's own words.
    /// </summary>
    [Fact]
    public async Task The_audit_entry_names_the_states_and_never_the_reason()
    {
        _workspace.Role = WorkspaceRole.Editor;

        await TransitionAsync(Stored(RecipeStatus.Approved), RecipeStatus.InDevelopment, reason: "Salt was wrong.");

        var audit = _dataLayer.Audit!;

        Assert.Equal(nameof(RecipeStatus.Approved), audit.BeforeReference);
        Assert.Equal(nameof(RecipeStatus.InDevelopment), audit.AfterReference);
        Assert.DoesNotContain("Salt was wrong.", audit.Summary);
        Assert.Equal(RecipeAuditActions.ResourceType, audit.ResourceType);
    }

    private sealed class StubTransitionDataLayer : StubRecipeDataLayerBase
    {
        public TaggedRecipe? Detail { get; set; }

        public bool Conflict { get; set; }

        public int Calls { get; private set; }

        public RecipeStatusTransition? Transition { get; private set; }

        public RecipeVersionFacts? TransitionVersion { get; private set; }

        public AuditEntry? Audit { get; private set; }

        public override Task<TaggedRecipe?> GetForUpdateAsync(Guid recipeId, CancellationToken cancellationToken) =>
            Task.FromResult(Detail);

        public override Task<(bool Committed, RecipeVersion? Version)> TryTransitionAsync(
            TaggedRecipe loaded,
            RecipeStatusTransition transition,
            RecipeVersionFacts? version,
            AuditEntry audit,
            CancellationToken cancellationToken)
        {
            Calls++;
            Transition = transition;
            TransitionVersion = version;
            Audit = audit;

            return Task.FromResult<(bool, RecipeVersion?)>((!Conflict, null));
        }
    }

    private sealed class StubRoleWorkspaceContext(Guid membershipId) : IWorkspaceContext
    {
        public bool IsResolved => true;

        public Guid WorkspaceId { get; } = Guid.NewGuid();

        public string WorkspaceSlug => "workspace-a";

        public Guid MembershipId { get; } = membershipId;

        /// <summary>Settable, unlike the other tests' stub: the role is what half of these tests vary.</summary>
        public WorkspaceRole Role { get; set; } = WorkspaceRole.Owner;

        public string AccountId => "account-a";
    }

    private sealed class StubTransitionClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
