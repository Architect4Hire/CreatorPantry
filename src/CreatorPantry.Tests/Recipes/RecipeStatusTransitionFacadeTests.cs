using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// What the transition facade composes: the role floor it enforces, the shape it validates, when it asks for a
/// readiness evaluation, and how the required idempotency key behaves (TESTRUN-005).
/// </summary>
/// <remarks>
/// The machine's shape is <see cref="RecipeStatusTransitionsTests"/> and what Business does with a legal move is
/// <see cref="RecipeTransitionBusinessTests"/>. Under test here is the arrangement around them — which is where
/// the approval's gate has to live, because the evaluation crosses module boundaries Business may not.
/// </remarks>
public sealed class RecipeStatusTransitionFacadeTests
{
    private const string Token = "AQIDBAUGBwg=";

    private static readonly Guid RecipeId = Guid.NewGuid();

    private readonly StubTransitionBusiness _business = new();
    private readonly StubReadinessFacade _readiness = new();
    private readonly RequiringIdempotency _idempotency = new();
    private readonly StubTransitionWorkspace _workspace = new();
    private readonly IRecipeStatusTransitionFacade _facade;

    public RecipeStatusTransitionFacadeTests() =>
        _facade = new ServiceCollection()
            .AddSingleton<IValidator<RecipeReadinessTransitionViewModel>>(
                new RecipeReadinessTransitionViewModelValidator())
            .AddSingleton<IRecipeBusiness>(_business)
            .AddSingleton<IRecipeReadinessFacade>(_readiness)
            .AddSingleton<IWorkspaceContext>(_workspace)
            .AddSingleton<IIdempotentCommandExecutor>(_idempotency)
            .AddSingleton<IRecipeStatusTransitionFacade, RecipeStatusTransitionFacade>()
            .BuildServiceProvider()
            .GetRequiredService<IRecipeStatusTransitionFacade>();

    private static RecipeReadinessTransitionViewModel Request(
        RecipeTransitionTargetViewModel target = RecipeTransitionTargetViewModel.Testing,
        string? reason = null,
        string? token = Token) =>
        new() { TargetStatus = target, Reason = reason, ExpectedConcurrencyToken = token };

    private Task<IdempotentOutcome<RecipeDetailServiceModel>> TransitionAsync(
        RecipeReadinessTransitionViewModel model, string? key = "key-1") =>
        _facade.TransitionAsync(
            "user-1", RecipeId, model, key, TestContext.Current.CancellationToken);

    // ---- The readiness gate ----

    /// <summary>
    /// The approval is the one move that needs an evaluation, and the facade makes it rather than accepting
    /// one — the request has no field for a verdict, and this is why.
    /// </summary>
    [Fact]
    public async Task An_approval_is_evaluated_and_the_result_is_handed_down()
    {
        await TransitionAsync(Request(RecipeTransitionTargetViewModel.Approved));

        Assert.Equal(1, _readiness.Calls);
        Assert.Equal(RecipeId, _readiness.RequestedRecipeId);
        Assert.NotNull(_business.Readiness);
    }

    /// <summary>Every other move is passed straight down with no evaluation at all.</summary>
    [Theory]
    [InlineData(RecipeTransitionTargetViewModel.InDevelopment, RecipeStatus.InDevelopment)]
    [InlineData(RecipeTransitionTargetViewModel.Testing, RecipeStatus.Testing)]
    [InlineData(RecipeTransitionTargetViewModel.ReadyForReview, RecipeStatus.ReadyForReview)]
    [InlineData(RecipeTransitionTargetViewModel.Archived, RecipeStatus.Archived)]
    [InlineData(RecipeTransitionTargetViewModel.Draft, RecipeStatus.Draft)]
    public async Task Another_move_is_not_evaluated(
        RecipeTransitionTargetViewModel target, RecipeStatus expected)
    {
        await TransitionAsync(Request(target));

        Assert.Equal(0, _readiness.Calls);
        Assert.Null(_business.Readiness);

        // And the target reached the domain as the state it names, member by member rather than by a cast.
        Assert.Equal(expected, _business.Target);
    }

    /// <summary>
    /// A refused evaluation is the answer, which for an invisible recipe is the 404 this route must give
    /// anyway — and Business is never reached, so nothing is loaded for somebody who cannot see it.
    /// </summary>
    [Fact]
    public async Task A_refused_evaluation_is_returned_and_business_is_not_called()
    {
        _readiness.Error = new OperationError(
            RecipeErrorCodes.RecipeNotFound, "That recipe could not be found.", new Dictionary<string, string[]>());

        var outcome = await TransitionAsync(Request(RecipeTransitionTargetViewModel.Approved));

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, outcome.Result.Error!.Code);
        Assert.Equal(0, _business.Calls);
        Assert.False(outcome.Replayed);
    }

    // ---- What reaches Business ----

    [Fact]
    public async Task The_request_is_passed_down_intact()
    {
        await TransitionAsync(Request(RecipeTransitionTargetViewModel.InDevelopment, reason: "Too dense."));

        Assert.Equal(RecipeId, _business.RequestedRecipeId);
        Assert.Equal(RecipeStatus.InDevelopment, _business.Target);
        Assert.Equal("Too dense.", _business.Reason);
        Assert.Equal("user-1", _business.ActorUserId);
        Assert.Equal(Token, _business.Token);
    }

    /// <summary>
    /// The reason is canonicalized once, before the fingerprint is taken and before Business sees it, so
    /// "same request" and "same stored sentence" cannot come apart.
    /// </summary>
    [Theory]
    [InlineData("  Too dense.  ", "Too dense.")]
    [InlineData("   ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public async Task The_reason_is_trimmed_and_blank_becomes_no_reason(string? sent, string? expected)
    {
        await TransitionAsync(Request(reason: sent));

        Assert.Equal(expected, _business.Reason);
    }

    // ---- The role floor ----

    /// <summary>
    /// A Viewer has no move at all, so they are refused before their body is looked at — and before the
    /// recipe is read, so the answer cannot depend on whether it exists.
    /// </summary>
    /// <remarks>
    /// The floor rather than the bar for a particular move: which role a move needs depends on where the
    /// recipe is, which Business establishes. Enforced here as well as by the controller policy because this
    /// boundary is also reached by workers and AI plugins, which no MVC policy protects.
    /// </remarks>
    [Fact]
    public async Task A_viewer_is_refused_before_anything_is_read()
    {
        _workspace.Role = WorkspaceRole.Viewer;

        var outcome = await TransitionAsync(Request(RecipeTransitionTargetViewModel.Approved, token: "not-a-token"));

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(RecipeErrorCodes.TransitionForbidden, outcome.Result.Error!.Code);
        Assert.Equal(0, _readiness.Calls);
        Assert.Equal(0, _business.Calls);
        Assert.Equal(0, _idempotency.Calls);
    }

    [Theory]
    [InlineData(WorkspaceRole.Contributor)]
    [InlineData(WorkspaceRole.Editor)]
    [InlineData(WorkspaceRole.Owner)]
    public async Task Everybody_above_a_viewer_gets_past_the_floor(WorkspaceRole role)
    {
        _workspace.Role = role;

        await TransitionAsync(Request());

        Assert.Equal(1, _business.Calls);
    }

    // ---- Shape ----

    /// <summary>
    /// Shape is refused with field errors before the recipe is read, and in particular before an evaluation is
    /// made: a malformed request is not worth two cross-module reads.
    /// </summary>
    [Theory]
    [InlineData(null, Token, nameof(RecipeReadinessTransitionViewModel.TargetStatus))]
    [InlineData(RecipeTransitionTargetViewModel.Approved, null, nameof(RecipeReadinessTransitionViewModel.ExpectedConcurrencyToken))]
    [InlineData(RecipeTransitionTargetViewModel.Approved, "not base64 at all", nameof(RecipeReadinessTransitionViewModel.ExpectedConcurrencyToken))]
    public async Task An_invalid_body_is_refused_naming_the_field(
        RecipeTransitionTargetViewModel? target, string? token, string field)
    {
        var outcome = await TransitionAsync(new RecipeReadinessTransitionViewModel
        {
            TargetStatus = target,
            ExpectedConcurrencyToken = token,
        });

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(RecipeErrorCodes.TransitionInvalidRequest, outcome.Result.Error!.Code);
        Assert.Contains(Camel(field), outcome.Result.Error.FieldErrors.Keys);

        Assert.Equal(0, _readiness.Calls);
        Assert.Equal(0, _business.Calls);
    }

    /// <summary>An undefined target is a malformed request, not an invalid jump.</summary>
    [Fact]
    public async Task An_undefined_target_is_refused_as_a_bad_request()
    {
        var outcome = await TransitionAsync(Request((RecipeTransitionTargetViewModel)99));

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(RecipeErrorCodes.TransitionInvalidRequest, outcome.Result.Error!.Code);
        Assert.Equal(0, _business.Calls);
    }

    /// <summary>
    /// A reason beyond the column's length is shape, so the validator refuses it. Whether a reason is
    /// <em>required</em> is not shape, and Business owns that.
    /// </summary>
    [Fact]
    public async Task An_over_long_reason_is_refused()
    {
        var outcome = await TransitionAsync(
            Request(reason: new string('x', RecipePolicy.TransitionReasonMaxLength + 1)));

        Assert.False(outcome.Result.Succeeded);
        Assert.Contains(
            Camel(nameof(RecipeReadinessTransitionViewModel.Reason)),
            outcome.Result.Error!.FieldErrors.Keys);
    }

    // ---- The required key ----

    /// <summary>
    /// The key is required here and accepted everywhere else. A caller who loses the response to an approval
    /// cannot tell whether the recipe was approved, and the blind retry that follows is the one request that
    /// must not be able to write twice.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_key_is_refused()
    {
        var outcome = await TransitionAsync(Request(), key: null);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyRequiredCode, outcome.Result.Error!.Code);
        Assert.Equal(0, _business.Calls);
    }

    /// <summary>
    /// The same key and the same request replay the first answer — and do not evaluate again. That matters
    /// for the approval specifically: asking the rules a second time could produce a different verdict, and a
    /// replay has to return what happened rather than what would happen now.
    /// </summary>
    [Fact]
    public async Task A_replay_returns_the_first_answer_without_evaluating_again()
    {
        _business.Detail = Detail();

        var first = await TransitionAsync(Request(RecipeTransitionTargetViewModel.Approved), key: "key-9");
        var second = await TransitionAsync(Request(RecipeTransitionTargetViewModel.Approved), key: "key-9");

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);

        Assert.Equal(1, _business.Calls);
        Assert.Equal(1, _readiness.Calls);
    }

    /// <summary>The same key with a different request is a reused key, not a replay.</summary>
    [Fact]
    public async Task The_same_key_with_a_different_request_is_refused()
    {
        _business.Detail = Detail();

        await TransitionAsync(Request(RecipeTransitionTargetViewModel.Testing), key: "key-9");
        var second = await TransitionAsync(
            Request(RecipeTransitionTargetViewModel.Archived), key: "key-9");

        Assert.False(second.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, second.Result.Error!.Code);
    }

    /// <summary>
    /// The reason is part of what makes two requests the same one, because it is stored: two requests that
    /// would write different history are not the same request however alike the move looks.
    /// </summary>
    [Fact]
    public async Task A_different_reason_under_the_same_key_is_refused()
    {
        _business.Detail = Detail();

        await TransitionAsync(Request(reason: "One reason"), key: "key-9");
        var second = await TransitionAsync(Request(reason: "Another reason"), key: "key-9");

        Assert.False(second.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, second.Result.Error!.Code);
    }

    /// <summary>
    /// The scope the key lives in: the caller, the workspace, and an operation name that is the route's alone.
    /// Without the workspace, one key would span every workspace the caller belongs to.
    /// </summary>
    [Fact]
    public async Task The_key_is_scoped_to_the_caller_the_workspace_and_this_operation()
    {
        await TransitionAsync(Request());

        var command = _idempotency.Command!;

        Assert.Equal("user-1", command.UserId);
        Assert.Equal(_workspace.WorkspaceId, command.WorkspaceId);
        Assert.Equal("recipes.readinessTransitions.create", command.Operation);
        Assert.True(command.KeyRequired);
    }

    // ---- Helpers ----

    private static string Camel(string field) => char.ToLowerInvariant(field[0]) + field[1..];

    private static RecipeDetailServiceModel Detail() =>
        RecipeDetailMapper.ToDetail(new TaggedRecipe(
            new CompleteRecipe(
                new Recipe { Id = RecipeId, Title = "Olive oil cake", RowVersion = [1, 2, 3, 4, 5, 6, 7, 8] },
                null),
            []));

    private sealed class StubReadinessFacade : IRecipeReadinessFacade
    {
        public int Calls { get; private set; }

        public Guid RequestedRecipeId { get; private set; }

        public OperationError? Error { get; set; }

        public Task<OperationResult<RecipeReadinessServiceModel>> EvaluateAsync(
            Guid recipeId, CancellationToken cancellationToken)
        {
            Calls++;
            RequestedRecipeId = recipeId;

            return Task.FromResult(Error is null
                ? OperationResult<RecipeReadinessServiceModel>.Success(new RecipeReadinessServiceModel(
                    recipeId, Guid.NewGuid(), 1, Token, RecipeReadinessCatalogue.Version,
                    false, 0, 0, [], [], []))
                : OperationResult<RecipeReadinessServiceModel>.Failure(Error));
        }
    }

    private sealed class StubTransitionBusiness : StubRecipeBusinessBase
    {
        public int Calls { get; private set; }

        public Guid RequestedRecipeId { get; private set; }

        public RecipeStatus Target { get; private set; }

        public string? Reason { get; private set; }

        public RecipeReadinessServiceModel? Readiness { get; private set; }

        public string? ActorUserId { get; private set; }

        public string? Token { get; private set; }

        /// <summary>What a successful move answers with, or null to answer a refusal.</summary>
        public RecipeDetailServiceModel? Detail { get; set; }

        public override Task<OperationResult<RecipeDetailServiceModel>> TransitionAsync(
            Guid recipeId,
            RecipeStatus target,
            string? reason,
            RecipeReadinessServiceModel? readiness,
            string actorUserId,
            string? expectedConcurrencyToken,
            CancellationToken cancellationToken)
        {
            Calls++;
            (RequestedRecipeId, Target, Reason, Readiness) = (recipeId, target, reason, readiness);
            (ActorUserId, Token) = (actorUserId, expectedConcurrencyToken);

            return Task.FromResult(Detail is null
                ? OperationResult<RecipeDetailServiceModel>.Failure(new OperationError(
                    RecipeErrorCodes.RecipeNotFound,
                    "Not the subject of this test.",
                    new Dictionary<string, string[]>()))
                : OperationResult<RecipeDetailServiceModel>.Success(Detail));
        }
    }

    /// <summary>
    /// An executor that honours <see cref="IdempotentCommand.KeyRequired"/>, which the recipe facade's own test
    /// double does not need to — this is the first command in the module that sets it.
    /// </summary>
    private sealed class RequiringIdempotency : IIdempotentCommandExecutor
    {
        private readonly Dictionary<string, (string Fingerprint, object Value)> _committed = new(StringComparer.Ordinal);

        public int Calls { get; private set; }

        public IdempotentCommand? Command { get; private set; }

        public async Task<IdempotentOutcome<T>> ExecuteAsync<T>(
            IdempotentCommand command,
            Func<CancellationToken, Task<OperationResult<T>>> operation,
            CancellationToken cancellationToken)
        {
            Calls++;
            Command = command;

            if (command.Key is null)
            {
                return command.KeyRequired
                    ? Failed<T>(
                        IdempotencyPolicy.KeyRequiredCode,
                        $"This request requires an {IdempotencyPolicy.KeyHeader} header.")
                    : new IdempotentOutcome<T>(await operation(cancellationToken), Replayed: false);
            }

            var fingerprint = JsonSerializer.Serialize(command.Fingerprint);

            if (_committed.TryGetValue(command.Key, out var existing))
            {
                return existing.Fingerprint == fingerprint
                    ? new IdempotentOutcome<T>(OperationResult<T>.Success((T)existing.Value), Replayed: true)
                    : Failed<T>(
                        IdempotencyPolicy.KeyReusedCode,
                        "That idempotency key was already used with a different request.");
            }

            var result = await operation(cancellationToken);

            if (result.Succeeded)
            {
                _committed[command.Key] = (fingerprint, result.Value!);
            }

            return new IdempotentOutcome<T>(result, Replayed: false);
        }

        private static IdempotentOutcome<T> Failed<T>(string code, string message) =>
            new(
                OperationResult<T>.Failure(new OperationError(code, message, new Dictionary<string, string[]>())),
                Replayed: false);
    }

    private sealed class StubTransitionWorkspace : IWorkspaceContext
    {
        public bool IsResolved => true;

        public Guid WorkspaceId { get; } = Guid.NewGuid();

        public string WorkspaceSlug => "workspace-a";

        public Guid MembershipId { get; } = Guid.NewGuid();

        /// <summary>Settable: the floor is one of the things this file is about.</summary>
        public WorkspaceRole Role { get; set; } = WorkspaceRole.Editor;

        public string AccountId => "account-a";
    }
}
