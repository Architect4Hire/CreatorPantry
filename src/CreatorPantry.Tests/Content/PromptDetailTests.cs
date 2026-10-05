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
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Ingredients;
using CreatorPantry.Domain.Modules.Measurement;
using CreatorPantry.Domain.Modules.Media;
using CreatorPantry.Domain.Modules.Recipes;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Vocabulary;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// PRM-003's read: what one prompt publishes in full, what it still withholds, and that an unknown id and
/// another workspace's prompt are a single answer rather than two that happen to match.
/// </summary>
/// <remarks>
/// Driven through the facade over a real database, so the workspace query filter — which is what actually makes
/// the two refusals one — is in the path rather than stubbed out. The HTTP contract, the role bar and the
/// <c>Location</c> header are <c>PromptsEndpointTests</c>; the real engine's version of the isolation case is
/// <c>PromptRecordSqlServerTests</c>.
/// </remarks>
public sealed class PromptDetailTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);
    private const string UserId = "user-1";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public PromptDetailTests()
    {
        _connection.Open();

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddOutbox()
            .AddApplicationTime()
            .AddMeasurementModule()
            .AddVocabularyModule()
            .AddIngredientModule()
            .AddRecipesModule()
            .AddContentModule()
            .AddMediaModule()
            .AddLogging()
            .AddDistributedMemoryCache()
            .AddApplicationCache()
            .AddSingleton<IClock>(new StoppedClock())
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddScoped<IAiProposalLookupBusiness, AiProposalLookupBusiness>()
            .AddScoped<IAiProposalLookupFacade, AiProposalLookupFacade>()
            .AddIdempotency(new ConfigurationBuilder().Build())
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AsyncServiceScope ScopeFor(Guid workspaceId)
    {
        var scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            WorkspaceRole.Owner,
            UserId);

        return scope;
    }

    private async Task<Guid> SaveAsync(Guid workspaceId, SavePromptRecordViewModel model)
    {
        await using var scope = ScopeFor(workspaceId);
        var outcome = await scope.ServiceProvider.GetRequiredService<IPromptRecordFacade>()
            .SaveAsync(UserId, model, idempotencyKey: null, Ct);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Code);

        return outcome.Result.Value!.PromptRecordId;
    }

    private async Task<OperationResult<PromptDetailServiceModel>> ReadAsync(Guid workspaceId, Guid promptRecordId)
    {
        await using var scope = ScopeFor(workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IPromptRecordFacade>()
            .GetDetailAsync(promptRecordId, Ct);
    }

    private async Task<PromptDetailServiceModel> ReadFoundAsync(Guid workspaceId, Guid promptRecordId)
    {
        var result = await ReadAsync(workspaceId, promptRecordId);

        Assert.True(result.Succeeded, result.Error?.Code);

        return result.Value!;
    }

    private static SavePromptRecordViewModel Manual(string? text = null) => new()
    {
        ChannelKey = "instagram",
        ImageKind = PromptImageKind.Hero,
        Text = text ?? "Overhead shot of soda bread on a linen cloth, soft window light.",
        Label = "Soda bread hero",
        Source = PromptRecordSource.Manual,
    };

    private static SavePromptRecordViewModel Generated(Guid proposalId) => Manual() with
    {
        Source = PromptRecordSource.ImagePromptComposition,
        GeneratedText = "Overhead shot of soda bread, window light.",
        AiProposalId = proposalId,
        PromptTemplateId = "image.prompt",
        PromptTemplateVersion = "1.0.0",
        PromptTemplateBodyChecksum = $"sha256:{new string('a', 64)}",
    };

    /// <summary>An AI operation and a proposal from it, inserted directly: no model runs in a test.</summary>
    private async Task<Guid> SeedProposalAsync(Guid workspaceId)
    {
        await using var scope = ScopeFor(workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var operationId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();

        db.AiOperations.Add(new AiOperation
        {
            Id = operationId,
            WorkspaceId = workspaceId,
            // ImagePrompt, matching the ImagePromptComposition source Generated() declares: since 12.4a the
            // save refuses a proposal from a different kind of generation, so a seed that said RecipeConcepts
            // would make every generated save here a lineage refusal rather than the thing under test.
            TaskType = AiTaskType.ImagePrompt,
            Scope = AiOperationScope.NotApplicable,
            Status = AiOperationStatus.Proposed,
            IdempotencyKey = $"prompt-detail-{operationId}",
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
            OutputSchemaVersion = "image.prompt.v1",
            PromptTemplateId = "image.prompt",
            PromptTemplateVersion = "1.0.0",
            // Matches what Generated() sends: since 12.4a the save derives the triple from the proposal
            // and refuses a request naming a different one, so a seed that disagreed would make every
            // generated save in this class a lineage refusal rather than the thing under test.
            PromptTemplateBodyChecksum = "sha256:" + new string('a', 64),
            ProviderName = "test-provider",
            ModelName = "test-model",
            CreatedAt = Now,
        });

        await db.SaveChangesAsync(Ct);

        return proposalId;
    }

    private async Task<(Guid RecipeId, Guid VersionId)> SeedRecipeAsync(Guid workspaceId)
    {
        await using var scope = ScopeFor(workspaceId);
        var created = await scope.ServiceProvider.GetRequiredService<IRecipeFacade>().CreateAsync(
            UserId,
            new CreateRecipeViewModel { Title = "Buttermilk Soda Bread" },
            idempotencyKey: null,
            Ct);

        Assert.True(created.Result.Succeeded, created.Result.Error?.Message);

        return (created.Result.Value!.RecipeId, created.Result.Value.VersionId);
    }

    // ---- found -------------------------------------------------------------------------------------------

    /// <summary>
    /// The whole prompt, not a preview. This is the one route that publishes it, so the assertion is on the
    /// length as well as the content — a text five times the preview limit arrives entire.
    /// </summary>
    [Fact]
    public async Task Detail_publishes_the_whole_prompt_rather_than_a_preview()
    {
        var text = string.Join(' ', Enumerable.Repeat("soda bread on linen,", 60));
        Assert.True(text.Length > ContentPolicy.PromptPreviewMaxLength * 4, "the fixture must exceed a preview");

        var id = await SaveAsync(WorkspaceA, Manual(text));

        var detail = await ReadFoundAsync(WorkspaceA, id);

        Assert.Equal(text, detail.Text);
        Assert.Equal(text.Length, detail.Text.Length);
    }

    [Fact]
    public async Task Detail_publishes_the_draft_and_the_template_triple()
    {
        var proposalId = await SeedProposalAsync(WorkspaceA);
        var model = Generated(proposalId);
        var id = await SaveAsync(WorkspaceA, model);

        var detail = await ReadFoundAsync(WorkspaceA, id);

        Assert.Equal(model.GeneratedText, detail.GeneratedText);
        Assert.Equal(proposalId, detail.AiProposalId);
        Assert.Equal(model.PromptTemplateId, detail.PromptTemplateId);
        Assert.Equal(model.PromptTemplateVersion, detail.PromptTemplateVersion);
        Assert.Equal(model.PromptTemplateBodyChecksum, detail.PromptTemplateBodyChecksum);
        Assert.Equal(PromptRecordSource.ImagePromptComposition, detail.Source);
    }

    [Fact]
    public async Task Detail_publishes_the_lineage_pins_a_prompt_was_written_against()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);
        var id = await SaveAsync(
            WorkspaceA, Manual() with { RecipeId = recipe.RecipeId, RecipeVersionId = recipe.VersionId });

        var detail = await ReadFoundAsync(WorkspaceA, id);

        Assert.Equal(recipe.RecipeId, detail.RecipeId);
        Assert.Equal(recipe.VersionId, detail.RecipeVersionId);
    }

    /// <summary>
    /// A manual prompt's absent provenance is published as absent rather than invented.
    /// </summary>
    [Fact]
    public async Task A_manual_prompt_names_no_draft_no_proposal_and_no_template()
    {
        var id = await SaveAsync(WorkspaceA, Manual());

        var detail = await ReadFoundAsync(WorkspaceA, id);

        Assert.Equal(PromptRecordSource.Manual, detail.Source);
        Assert.Null(detail.GeneratedText);
        Assert.Null(detail.AiProposalId);
        Assert.Null(detail.PromptTemplateId);
        Assert.Null(detail.PromptTemplateVersion);
        Assert.Null(detail.PromptTemplateBodyChecksum);
        Assert.Null(detail.RecipeId);
        Assert.Null(detail.RecipeVersionId);
    }

    /// <summary>
    /// What the detail route reports and what the save reported are the same prompt, field for field.
    /// </summary>
    /// <remarks>
    /// The two are separate records deliberately — so detail can gain an author and the asset ids without those
    /// landing on a create response — and this is what stops "separate" turning into "divergent" while they are
    /// still meant to agree. A field added to one and not the other fails here and has to be a decision.
    /// </remarks>
    [Fact]
    public async Task Detail_reports_the_same_prompt_the_save_reported()
    {
        var proposalId = await SeedProposalAsync(WorkspaceA);
        SavedPromptRecordServiceModel saved;

        await using (var scope = ScopeFor(WorkspaceA))
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<IPromptRecordFacade>()
                .SaveAsync(UserId, Generated(proposalId), idempotencyKey: null, Ct);

            Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Code);
            saved = outcome.Result.Value!;
        }

        var detail = await ReadFoundAsync(WorkspaceA, saved.PromptRecordId);

        Assert.Equal(
            new PromptDetailServiceModel(
                saved.PromptRecordId,
                saved.ChannelKey,
                saved.ImageKind,
                saved.Text,
                saved.GeneratedText,
                saved.Label,
                saved.Source,
                saved.AiProposalId,
                saved.RecipeId,
                saved.RecipeVersionId,
                saved.PromptTemplateId,
                saved.PromptTemplateVersion,
                saved.PromptTemplateBodyChecksum,
                saved.CreatedAt),
            detail);
    }

    // ---- what it still withholds -------------------------------------------------------------------------

    /// <summary>
    /// The workspace id and the membership ids never leave the server, and "complete lineage" does not reopen
    /// that.
    /// </summary>
    /// <remarks>
    /// Over the published shape rather than over one response, so a field added later is caught whether or not
    /// a test happens to read it. 12.3a published <c>createdByMembershipId</c> by mistake on the save response;
    /// this is the same rule, pinned on the route that would most plausibly be argued into an exception.
    /// </remarks>
    [Fact]
    public void Detail_publishes_no_workspace_id_no_membership_id_and_no_author()
    {
        var forbidden = typeof(PromptDetailServiceModel)
            .GetProperties()
            .Select(property => property.Name)
            .Where(name =>
                name.Contains("Workspace", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Membership", StringComparison.OrdinalIgnoreCase)
                || name.Contains("CreatedBy", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Author", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(forbidden);
    }

    /// <summary>
    /// No URL, no object path and no bytes, because a prompt record holds none (media.md) — and no asset id,
    /// because nothing can verify one until 12.6 and 12.9.
    /// </summary>
    [Fact]
    public void Detail_publishes_no_url_no_object_path_and_no_asset_id()
    {
        string[] banned = ["Url", "Uri", "Path", "Blob", "Bytes", "Content", "GeneratedImage", "DamAsset"];

        var offenders = typeof(PromptDetailServiceModel)
            .GetProperties()
            .Select(property => property.Name)
            .Where(name => banned.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offenders);
    }

    // ---- not found, and the boundary ---------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_id_is_refused_as_not_found()
    {
        var result = await ReadAsync(WorkspaceA, Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(ContentErrorCodes.PromptNotFound, result.Error!.Code);
        Assert.Empty(result.Error.FieldErrors);
    }

    /// <summary>
    /// The test the microprompt asks for: another workspace's prompt and an id that does not exist are not
    /// merely the same status, they are the same answer.
    /// </summary>
    /// <remarks>
    /// Code, message and field errors are all compared, because a 404 whose body said "that prompt belongs to
    /// another workspace" would disclose exactly what the matching status was hiding. It holds by construction
    /// rather than by matching strings — the query filter means Business never sees the neighbour's row, so
    /// there is only one refusal in the code to begin with — and that is what this pins.
    /// </remarks>
    [Fact]
    public async Task Another_workspaces_prompt_is_refused_exactly_as_an_unknown_id_is()
    {
        var theirs = await SaveAsync(WorkspaceB, Manual("Theirs, about risotto."));

        var borrowed = await ReadAsync(WorkspaceA, theirs);
        var unknown = await ReadAsync(WorkspaceA, Guid.NewGuid());

        Assert.False(borrowed.Succeeded);
        Assert.Equal(unknown.Error!.Code, borrowed.Error!.Code);
        Assert.Equal(unknown.Error.Message, borrowed.Error.Message);
        Assert.Equal(unknown.Error.FieldErrors.Count, borrowed.Error.FieldErrors.Count);

        // And the message says nothing about a workspace, which is the half an equality check cannot see: two
        // identical disclosures would pass the comparison above.
        Assert.DoesNotContain("workspace", borrowed.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The other direction, so the boundary is not merely one-way: each workspace reads its own prompt and is
    /// refused the other's, with both rows present.
    /// </summary>
    [Fact]
    public async Task Each_workspace_reads_only_its_own_prompt()
    {
        var mine = await SaveAsync(WorkspaceA, Manual("Mine."));
        var theirs = await SaveAsync(WorkspaceB, Manual("Theirs."));

        Assert.Equal("Mine.", (await ReadFoundAsync(WorkspaceA, mine)).Text);
        Assert.Equal("Theirs.", (await ReadFoundAsync(WorkspaceB, theirs)).Text);

        Assert.False((await ReadAsync(WorkspaceA, theirs)).Succeeded);
        Assert.False((await ReadAsync(WorkspaceB, mine)).Succeeded);
    }

    /// <summary>
    /// A prompt cannot be read before a workspace is resolved; the query filter refuses rather than answering
    /// for every workspace at once.
    /// </summary>
    [Fact]
    public async Task A_prompt_cannot_be_read_before_a_workspace_is_resolved()
    {
        await using var scope = _provider.CreateAsyncScope();
        var facade = scope.ServiceProvider.GetRequiredService<IPromptRecordFacade>();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => facade.GetDetailAsync(Guid.NewGuid(), Ct));
    }

    /// <summary>
    /// Nothing on the detail path caches, and that is load-bearing rather than incidental.
    /// </summary>
    /// <remarks>
    /// The same rule <c>PromptSearchTests.The_read_path_takes_no_cache_dependency</c> states for the list, and it
    /// matters more here: a cached prompt body keyed without its <c>workspace:{id}:</c> prefix would serve one
    /// creator's craft to another, which is the failure tenancy.md's cache rule exists for. There is no cached
    /// value to isolate, and this is what keeps that true. <c>IPromptRecordRepository</c> is included because
    /// the detail read put it on a read path for the first time.
    /// </remarks>
    [Fact]
    public void The_detail_path_takes_no_cache_dependency()
    {
        Type[] path =
        [
            typeof(IPromptRecordFacade),
            typeof(IPromptRecordBusiness),
            typeof(IPromptRecordDataLayer),
            typeof(IPromptRecordRepository),
        ];

        var offenders = new List<string>();

        foreach (var contract in path)
        {
            var implementation = contract.Assembly.GetTypes()
                .Single(type => type.IsClass && !type.IsAbstract && contract.IsAssignableFrom(type));

            offenders.AddRange(implementation
                .GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Where(parameter => parameter.ParameterType.Name.Contains("Cache", StringComparison.Ordinal))
                .Select(parameter => $"{implementation.Name} -> {parameter.ParameterType.Name}"));
        }

        Assert.Empty(offenders);
    }

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
