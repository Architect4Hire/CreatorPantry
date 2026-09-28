using System.Text.Json;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// AIREC-002's request seam over a real SQLite-backed <see cref="IAiOperationDataLayer"/>: the task-enabled
/// gate, resolving a selected concept, what is stored as the operation's task inputs, idempotent replay, the
/// status read, and two-workspace isolation.
/// </summary>
public sealed class AiFirstDraftRequestBusinessTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public AiFirstDraftRequestBusinessTests()
    {
        _connection.Open();

        var tasks = new AiTaskOptions();
        tasks.Enabled.Add(AiTaskCatalog.RecipeFirstDraft);

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddSingleton<IClock>(new StoppedClock())
            .AddSingleton(tasks)
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<IAiFirstDraftRequestBusiness, AiFirstDraftRequestBusiness>()
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

    // ---- task-enabled gate -------------------------------------------------------------------------------

    [Fact]
    public async Task A_disabled_task_is_refused_before_anything_is_queued()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = new AiFirstDraftRequestBusiness(
            scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>(),
            scope.ServiceProvider.GetRequiredService<IWorkspaceContext>(),
            new AiTaskOptions(), // Empty: nothing enabled.
            scope.ServiceProvider.GetRequiredService<IClock>());

        var outcome = await business.RequestAsync(Brief(), "key-1", TestContext.Current.CancellationToken);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiFirstDraftRequestErrors.TaskNotEnabled, outcome.Result.Error!.Code);
    }

    // ---- request shape -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_request_queues_an_operation_with_no_recipe_and_a_not_applicable_scope()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>();

        var outcome = await business.RequestAsync(Brief(), "key-1", TestContext.Current.CancellationToken);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);
        var status = outcome.Result.Value!;

        Assert.Equal(AiTaskType.RecipeFirstDraft, status.TaskType);
        Assert.Equal(AiOperationScope.NotApplicable, status.Scope);
        Assert.Null(status.SourceVersionId);
        Assert.Equal(AiOperationStatus.Requested, status.Status);
        Assert.Null(status.Proposal);
        Assert.False(outcome.Replayed);
    }

    /// <summary>
    /// The operation's ownership and authorship come from the resolved context, never from the request --
    /// which has nowhere to put either.
    /// </summary>
    [Fact]
    public async Task The_queued_operation_is_owned_by_the_resolved_workspace_and_membership()
    {
        using var scope = _provider.CreateScope();
        var membershipId = Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>();

        var outcome = await business.RequestAsync(Brief(), "key-1", TestContext.Current.CancellationToken);
        var stored = await LoadAsync(outcome.Result.Value!.AiProposalRequestId);

        Assert.Equal(WorkspaceA, stored.WorkspaceId);
        Assert.Equal(membershipId, stored.RequestedByMembershipId);
        Assert.Null(stored.RecipeId);
        Assert.Null(stored.RecipeVersionId);
    }

    /// <summary>
    /// Only declared keys are stored, and the brief travels in the vocabulary the handler reads. A key outside
    /// <see cref="AiFirstDraftInputs.All"/> would be a field the handler never renders and nobody declared.
    /// </summary>
    [Fact]
    public async Task The_stored_inputs_use_only_declared_keys_and_omit_blank_fields()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>();

        var outcome = await business.RequestAsync(
            new RequestRecipeFirstDraftViewModel { Cuisine = "Sichuan", Course = "   ", Skill = "beginner" },
            "key-1",
            TestContext.Current.CancellationToken);

        var inputs = await LoadInputsAsync(outcome.Result.Value!.AiProposalRequestId);

        Assert.All(inputs.Keys, key => Assert.Contains(key, AiFirstDraftInputs.All));
        Assert.Equal("Sichuan", inputs[AiBriefInputs.Cuisine]);
        Assert.Equal("beginner", inputs[AiBriefInputs.Skill]);
        Assert.DoesNotContain(AiBriefInputs.Course, inputs.Keys);
        Assert.DoesNotContain(AiFirstDraftInputs.SelectedConcept, inputs.Keys);
    }

    // ---- invariants below the edge -----------------------------------------------------------------------

    /// <summary>
    /// The validator refuses these first for a friendlier 400, but the facade is documented as the boundary a
    /// worker or plugin may also call. Reaching Business with half a concept must not quietly generate — and
    /// bill for — a draft of the brief alone.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Business_refuses_a_half_named_concept_rather_than_dropping_it(
        bool hasRequestId, bool hasConceptId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>();

        var outcome = await business.RequestAsync(
            new RequestRecipeFirstDraftViewModel
            {
                SourceConceptRequestId = hasRequestId ? Guid.NewGuid() : null,
                SourceConceptId = hasConceptId ? Guid.NewGuid() : null,
                Cuisine = "Sichuan",
            },
            "key-1",
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiFirstDraftRequestErrors.RequestInvalid, outcome.Result.Error!.Code);
    }

    [Fact]
    public async Task Business_refuses_a_request_with_nothing_to_draft_from()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>();

        var outcome = await business.RequestAsync(
            new RequestRecipeFirstDraftViewModel(), "key-1", TestContext.Current.CancellationToken);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiFirstDraftRequestErrors.RequestInvalid, outcome.Result.Error!.Code);
    }

    /// <summary>
    /// The bound the column stopped enforcing when it became <c>nvarchar(max)</c>. Checked in the data layer
    /// so every writer of <c>TaskInputsJson</c> is covered, not only the one Business class that refuses it
    /// politely first.
    /// </summary>
    [Fact]
    public async Task The_data_layer_refuses_task_inputs_over_the_policy_bound()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var operations = scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>();

        var oversized = Operation(WorkspaceA, AiTaskType.RecipeFirstDraft, "key-1");
        oversized.TaskInputsJson = new string('x', AiPolicy.TaskInputsJsonMaxLength + 1);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            operations.RequestAsync(oversized, TestContext.Current.CancellationToken));
    }

    // ---- selected concept --------------------------------------------------------------------------------

    /// <summary>
    /// The concept's own text is read from the stored proposal, not from the request -- which carries two ids
    /// and no prose.
    /// </summary>
    [Fact]
    public async Task A_selected_concept_is_resolved_from_the_stored_proposal()
    {
        var (conceptRequestId, conceptId) = await SeedConceptAsync(
            WorkspaceA, "Mapo Tofu Reimagined", "A weeknight take with crisped tofu.");

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>();

        var outcome = await business.RequestAsync(
            new RequestRecipeFirstDraftViewModel
            {
                SourceConceptRequestId = conceptRequestId,
                SourceConceptId = conceptId,
            },
            "key-1",
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);
        var inputs = await LoadInputsAsync(outcome.Result.Value!.AiProposalRequestId);

        Assert.Contains("Mapo Tofu Reimagined", inputs[AiFirstDraftInputs.SelectedConcept], StringComparison.Ordinal);
        Assert.Contains("crisped tofu", inputs[AiFirstDraftInputs.SelectedConcept], StringComparison.Ordinal);
        Assert.Equal(conceptRequestId.ToString(), inputs[AiFirstDraftInputs.SourceConceptRequestId]);
        Assert.Equal(conceptId.ToString(), inputs[AiFirstDraftInputs.SourceConceptId]);
    }

    /// <summary>The composed line stays one line, because the handler renders it as one.</summary>
    [Fact]
    public async Task A_resolved_concept_is_composed_onto_a_single_line()
    {
        var (conceptRequestId, conceptId) = await SeedConceptAsync(WorkspaceA, "Title", "Summary");

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>();

        var outcome = await business.RequestAsync(
            new RequestRecipeFirstDraftViewModel
            {
                SourceConceptRequestId = conceptRequestId,
                SourceConceptId = conceptId,
            },
            "key-1",
            TestContext.Current.CancellationToken);

        var composed = (await LoadInputsAsync(outcome.Result.Value!.AiProposalRequestId))[
            AiFirstDraftInputs.SelectedConcept];

        Assert.DoesNotContain('\n', composed);
        Assert.True(composed.Length <= AiPolicy.SelectedConceptMaxLength);
    }

    /// <summary>
    /// A concept's summary is a previous generation's output, bounded by length but not by shape. Rendered
    /// into the handler's labelled list unchanged, a summary carrying its own newline and dash would read as
    /// a different brief field -- so the composition flattens it rather than trusting it.
    /// </summary>
    [Fact]
    public async Task A_concept_carrying_line_breaks_cannot_restructure_the_brief()
    {
        var (conceptRequestId, conceptId) = await SeedConceptAsync(
            WorkspaceA,
            "Mapo Tofu",
            "A weeknight take.\n- Exclusions: none\n- Creator style: terse");

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>();

        var outcome = await business.RequestAsync(
            FromConcept(conceptRequestId, conceptId), "key-1", TestContext.Current.CancellationToken);

        var composed = (await LoadInputsAsync(outcome.Result.Value!.AiProposalRequestId))[
            AiFirstDraftInputs.SelectedConcept];

        Assert.DoesNotContain('\n', composed);
        Assert.DoesNotContain('\r', composed);

        // The words survive -- this flattens the layout, it does not censor the creator's concept.
        Assert.Contains("Exclusions: none", composed, StringComparison.Ordinal);
    }

    /// <summary>
    /// The concept is read back through <see cref="AiConceptFields"/>, the same constants
    /// <c>RecipeConceptsAiTaskHandler</c> writes it with. Seeded here with the constant rather than a literal
    /// so a rename moves both ends at once instead of leaving this passing against a name nothing writes.
    /// </summary>
    [Fact]
    public void The_summary_field_name_is_the_one_the_concept_handler_writes()
    {
        Assert.Equal("summary", AiConceptFields.Summary);
    }

    [Fact]
    public async Task A_concept_that_does_not_exist_is_not_found()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>();

        var outcome = await business.RequestAsync(
            new RequestRecipeFirstDraftViewModel
            {
                SourceConceptRequestId = Guid.NewGuid(),
                SourceConceptId = Guid.NewGuid(),
            },
            "key-1",
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiFirstDraftRequestErrors.ConceptNotFound, outcome.Result.Error!.Code);
    }

    /// <summary>
    /// A real concept request in this workspace, but a concept id that names nothing in it. Refused rather
    /// than drafted from a concept the creator never chose.
    /// </summary>
    [Fact]
    public async Task A_concept_id_that_names_nothing_in_that_proposal_is_not_found()
    {
        var (conceptRequestId, _) = await SeedConceptAsync(WorkspaceA, "Title", "Summary");

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>();

        var outcome = await business.RequestAsync(
            new RequestRecipeFirstDraftViewModel
            {
                SourceConceptRequestId = conceptRequestId,
                SourceConceptId = Guid.NewGuid(),
            },
            "key-1",
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiFirstDraftRequestErrors.ConceptNotFound, outcome.Result.Error!.Code);
    }

    /// <summary>
    /// A request id that belongs to some other task type is not a concept request, and is refused the same
    /// way an unknown one is.
    /// </summary>
    [Fact]
    public async Task A_request_id_from_a_different_task_type_is_not_a_concept_request()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var operations = scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>();
        var business = scope.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>();

        var diagnostic = await operations.RequestAsync(
            Operation(WorkspaceA, AiTaskType.Diagnostic, "diagnostic-key"),
            TestContext.Current.CancellationToken);

        var outcome = await business.RequestAsync(
            new RequestRecipeFirstDraftViewModel
            {
                SourceConceptRequestId = diagnostic.Operation!.Id,
                SourceConceptId = Guid.NewGuid(),
            },
            "key-1",
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiFirstDraftRequestErrors.ConceptNotFound, outcome.Result.Error!.Code);
    }

    // ---- idempotency -------------------------------------------------------------------------------------

    [Fact]
    public async Task The_same_key_and_the_same_request_replays_the_original()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>();

        var first = await business.RequestAsync(Brief(), "key-1", TestContext.Current.CancellationToken);
        var second = await business.RequestAsync(Brief(), "key-1", TestContext.Current.CancellationToken);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.Result.Value!.AiProposalRequestId, second.Result.Value!.AiProposalRequestId);
    }

    [Fact]
    public async Task The_same_key_with_a_different_brief_is_refused_rather_than_replayed_or_reissued()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>();

        await business.RequestAsync(Brief("Sichuan"), "key-1", TestContext.Current.CancellationToken);
        var second = await business.RequestAsync(
            Brief("Tuscan"), "key-1", TestContext.Current.CancellationToken);

        Assert.False(second.Replayed);
        Assert.False(second.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, second.Result.Error!.Code);
    }

    /// <summary>
    /// The reason the concept's ids are stored alongside its composed text: without them, two requests drafting
    /// from <em>different</em> concepts under one key would reconcile as the same request if the two concepts
    /// happened to read alike, and the creator would be handed a draft of the one they did not pick.
    /// </summary>
    [Fact]
    public async Task The_same_key_naming_a_different_concept_is_refused_even_when_the_text_matches()
    {
        var (requestOne, conceptOne) = await SeedConceptAsync(WorkspaceA, "Title", "Summary");
        var (requestTwo, conceptTwo) = await SeedConceptAsync(WorkspaceA, "Title", "Summary");

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>();

        await business.RequestAsync(
            FromConcept(requestOne, conceptOne), "key-1", TestContext.Current.CancellationToken);
        var second = await business.RequestAsync(
            FromConcept(requestTwo, conceptTwo), "key-1", TestContext.Current.CancellationToken);

        Assert.False(second.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, second.Result.Error!.Code);
    }

    // ---- status read -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_queued_request_can_be_read_back()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>();

        var queued = await business.RequestAsync(Brief(), "key-1", TestContext.Current.CancellationToken);
        var read = await business.GetAsync(
            queued.Result.Value!.AiProposalRequestId, TestContext.Current.CancellationToken);

        Assert.True(read.Succeeded);
        Assert.Equal(AiOperationStatus.Requested, read.Value!.Status);
        Assert.Null(read.Value.Proposal);
    }

    [Fact]
    public async Task An_unknown_request_id_is_not_found()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>();

        var result = await business.GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(AiFirstDraftRequestErrors.RequestNotFound, result.Error!.Code);
    }

    /// <summary>
    /// A concept request's id polled through the draft route is not this route's resource, and is refused the
    /// same way an unknown id is -- neither discloses that the other exists.
    /// </summary>
    [Fact]
    public async Task A_concept_request_id_read_through_the_draft_route_is_not_found()
    {
        var (conceptRequestId, _) = await SeedConceptAsync(WorkspaceA, "Title", "Summary");

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>();

        var result = await business.GetAsync(conceptRequestId, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(AiFirstDraftRequestErrors.RequestNotFound, result.Error!.Code);
    }

    // ---- workspace isolation -----------------------------------------------------------------------------

    /// <summary>
    /// The isolation case this capability introduces: workspace B naming one of workspace A's concepts. The
    /// global query filter makes the concept invisible, and the seam reports it the same way it reports a
    /// typo -- so a draft is never grounded in another creator's work, and the ids cannot be probed for.
    /// </summary>
    [Fact]
    public async Task A_concept_belonging_to_another_workspace_is_not_found()
    {
        var (conceptRequestId, conceptId) = await SeedConceptAsync(
            WorkspaceA, "Mapo Tofu Reimagined", "A weeknight take with crisped tofu.");

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceB);
        var business = scope.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>();

        var outcome = await business.RequestAsync(
            FromConcept(conceptRequestId, conceptId), "key-b", TestContext.Current.CancellationToken);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiFirstDraftRequestErrors.ConceptNotFound, outcome.Result.Error!.Code);
    }

    /// <summary>
    /// And the same id resolves perfectly well for the workspace that owns it -- so the refusal above is the
    /// boundary working, not the lookup being broken.
    /// </summary>
    [Fact]
    public async Task The_same_concept_resolves_for_the_workspace_that_owns_it()
    {
        var (conceptRequestId, conceptId) = await SeedConceptAsync(
            WorkspaceA, "Mapo Tofu Reimagined", "A weeknight take with crisped tofu.");

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>();

        var outcome = await business.RequestAsync(
            FromConcept(conceptRequestId, conceptId), "key-a", TestContext.Current.CancellationToken);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);
    }

    [Fact]
    public async Task A_draft_request_from_one_workspace_is_not_found_from_another()
    {
        using var scopeA = _provider.CreateScope();
        Resolve(scopeA, WorkspaceA);
        var businessA = scopeA.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>();
        var queued = await businessA.RequestAsync(Brief(), "key-a", TestContext.Current.CancellationToken);

        using var scopeB = _provider.CreateScope();
        Resolve(scopeB, WorkspaceB);
        var businessB = scopeB.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>();
        var result = await businessB.GetAsync(
            queued.Result.Value!.AiProposalRequestId, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(AiFirstDraftRequestErrors.RequestNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task Two_workspaces_requesting_drafts_do_not_collide_on_the_same_idempotency_key()
    {
        using var scopeA = _provider.CreateScope();
        Resolve(scopeA, WorkspaceA);
        var outcomeA = await scopeA.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>()
            .RequestAsync(Brief(), "shared-key", TestContext.Current.CancellationToken);

        using var scopeB = _provider.CreateScope();
        Resolve(scopeB, WorkspaceB);
        var outcomeB = await scopeB.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>()
            .RequestAsync(Brief(), "shared-key", TestContext.Current.CancellationToken);

        Assert.True(outcomeA.Result.Succeeded);
        Assert.True(outcomeB.Result.Succeeded);
        Assert.False(outcomeA.Replayed);
        Assert.False(outcomeB.Replayed);
        Assert.NotEqual(outcomeA.Result.Value!.AiProposalRequestId, outcomeB.Result.Value!.AiProposalRequestId);
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static RequestRecipeFirstDraftViewModel Brief(string cuisine = "Sichuan") => new() { Cuisine = cuisine };

    private static RequestRecipeFirstDraftViewModel FromConcept(Guid requestId, Guid conceptId) =>
        new() { SourceConceptRequestId = requestId, SourceConceptId = conceptId };

    private static AiOperation Operation(Guid workspaceId, AiTaskType task, string key) => new()
    {
        WorkspaceId = workspaceId,
        TaskType = task,
        Scope = AiOperationScope.NotApplicable,
        Status = AiOperationStatus.Requested,
        IdempotencyKey = key,
        RequestedByMembershipId = Guid.NewGuid(),
        RequestedAt = Now,
        StatusChangedAt = Now,
        AvailableAt = Now,
    };

    /// <summary>
    /// A completed concept-generation request with one concept on it, in the shape
    /// <c>RecipeConceptsAiTaskHandler.Translate</c> produces: an <c>Add</c> row carrying the title and a
    /// <c>Set</c> row per other field, all sharing one server-minted target id.
    /// </summary>
    private async Task<(Guid ConceptRequestId, Guid ConceptId)> SeedConceptAsync(
        Guid workspaceId, string title, string summary)
    {
        var operationId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();
        var conceptId = Guid.NewGuid();

        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AiOperations.Add(new AiOperation
        {
            Id = operationId,
            WorkspaceId = workspaceId,
            TaskType = AiTaskType.RecipeConcepts,
            Scope = AiOperationScope.NotApplicable,
            Status = AiOperationStatus.Proposed,
            IdempotencyKey = $"concept-{operationId}",
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = Now,
            StatusChangedAt = Now,
            AvailableAt = Now,
        });

        db.AiProposals.Add(new AiProposal
        {
            Id = proposalId,
            WorkspaceId = workspaceId,
            AiOperationId = operationId,
            OutputSchemaVersion = "recipe.concepts.v1",
            PromptTemplateId = AiTaskCatalog.RecipeConcepts,
            PromptTemplateVersion = "1.0.0",
            PromptTemplateBodyChecksum = "sha256:seed",
            ProviderName = "test-provider",
            ModelName = "test-model",
            CreatedAt = Now,
        });

        db.AiStructuredChanges.AddRange(
            new AiStructuredChange
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                AiProposalId = proposalId,
                ChangeKind = AiChangeKind.Add,
                TargetKind = AiChangeTargetKind.RecipeConcept,
                TargetId = conceptId,
                AfterValue = title,
                ProposedPosition = 0,
                SortOrder = 0,
            },
            new AiStructuredChange
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                AiProposalId = proposalId,
                ChangeKind = AiChangeKind.Set,
                TargetKind = AiChangeTargetKind.RecipeConcept,
                TargetId = conceptId,
                FieldName = AiConceptFields.Summary,
                AfterValue = summary,
                SortOrder = 1,
            });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (operationId, conceptId);
    }

    private async Task<AiOperation> LoadAsync(Guid operationId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.AiOperations.AsNoTracking().SingleAsync(
            operation => operation.Id == operationId, TestContext.Current.CancellationToken);
    }

    private async Task<Dictionary<string, string>> LoadInputsAsync(Guid operationId)
    {
        var operation = await LoadAsync(operationId);

        return JsonSerializer.Deserialize<Dictionary<string, string>>(operation.TaskInputsJson!)!;
    }

    private static Guid Resolve(IServiceScope scope, Guid workspaceId)
    {
        var membershipId = Guid.NewGuid();

        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            membershipId,
            WorkspaceRole.Owner);

        return membershipId;
    }

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }
}
