using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Ingredients;
using CreatorPantry.Domain.Modules.Measurement;
using CreatorPantry.Domain.Modules.Media;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
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
/// What a prompt save does: what it stores, what the server decides rather than the request, which lineage pins
/// it resolves, and what it refuses. Over a real database and the real recipe module, because resolving the pins
/// through their owning facades is the behaviour under test.
/// </summary>
/// <remarks>
/// The schema's own guarantees — the composite lineage keys, the filtered unique index, the check constraints,
/// write-once — are <c>PromptRecordAggregateTests</c> and <c>PromptRecordSqlServerTests</c>. This is the seam
/// above them: the mutation half of the isolation coverage tenancy.md asks for, which 12.3 could not test
/// because no seam existed.
/// </remarks>
public sealed class PromptRecordBusinessTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);
    private const string UserId = "user-1";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public PromptRecordBusinessTests()
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

            // The AI module's narrow proposal lookup, and only it: the content module resolves a prompt's
            // provenance pin through this and needs nothing else from that module, which is the whole reason
            // the lookup is a class of its own rather than a method on IAiProposalFacade.
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

    private static SavePromptRecordViewModel Manual() => new()
    {
        ChannelKey = "instagram",
        ImageKind = PromptImageKind.Hero,
        Text = "Overhead shot of soda bread on a linen cloth, soft window light.",
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

    private AsyncServiceScope ScopeFor(Guid workspaceId, Guid? membershipId = null)
    {
        var scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            membershipId ?? Guid.NewGuid(),
            WorkspaceRole.Owner,
            UserId);

        return scope;
    }

    /// <summary>One save in its own scope, the way a request arrives.</summary>
    private async Task<Domain.Managers.Results.OperationResult<SavedPromptRecordServiceModel>> SaveAsync(
        Guid workspaceId, SavePromptRecordViewModel model, Guid? membershipId = null)
    {
        await using var scope = ScopeFor(workspaceId, membershipId);

        return await scope.ServiceProvider.GetRequiredService<IPromptRecordBusiness>().SaveAsync(model, Ct);
    }

    private async Task<SavedPromptRecordServiceModel> SavedAsync(
        Guid workspaceId, SavePromptRecordViewModel model, Guid? membershipId = null)
    {
        var result = await SaveAsync(workspaceId, model, membershipId);

        Assert.True(result.Succeeded, result.Error?.Code);

        return result.Value!;
    }

    private async Task<(Guid RecipeId, Guid VersionId)> SeedRecipeAsync(Guid workspaceId)
    {
        await using var scope = ScopeFor(workspaceId);
        var created = await scope.ServiceProvider.GetRequiredService<IRecipeFacade>().CreateAsync(
            UserId,
            new CreateRecipeViewModel
            {
                Title = "Buttermilk Soda Bread",
                IngredientGroups =
                [
                    new RecipeIngredientGroupInputViewModel
                    {
                        Ingredients = [new RecipeIngredientInputViewModel { DisplayText = "400ml buttermilk" }],
                    },
                ],
            },
            idempotencyKey: null,
            Ct);

        Assert.True(created.Result.Succeeded, created.Result.Error?.Message);

        return (created.Result.Value!.RecipeId, created.Result.Value.VersionId);
    }

    /// <summary>An AI operation and a proposal from it, inserted directly: no model runs in a test.</summary>
    private Task<Guid> SeedProposalAsync(Guid workspaceId) =>
        SeedProposalAsync(workspaceId, recipeId: null);

    private async Task<Guid> SeedProposalAsync(Guid workspaceId, Guid? recipeId)
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
            RecipeId = recipeId,
            Scope = AiOperationScope.NotApplicable,
            Status = AiOperationStatus.Proposed,
            IdempotencyKey = $"prompt-{operationId}",
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

    /// <summary>One staged generated image of that workspace, with the operation that produced it.</summary>
    /// <remarks>
    /// Inserted directly, like the proposal above: 12.6 lands the tables and the facade that stages an image
    /// arrives with 12.7's worker. What a prompt save needs is a row the lookup can resolve and the composite
    /// foreign key can name. WorkspaceId is left to the ownership interceptor.
    /// </remarks>
    private async Task<Guid> SeedGeneratedImageAsync(Guid workspaceId)
    {
        await using var scope = ScopeFor(workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var operationId = Guid.NewGuid();
        var imageId = Guid.NewGuid();

        db.GeneratedImageOperations.Add(new GeneratedImageOperation
        {
            Id = operationId,
            Status = GeneratedImageOperationStatus.Succeeded,
            PromptText = "Overhead shot of soda bread on linen.",
            VariantCount = 1,
            IdempotencyKey = $"image-{operationId}",
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = Now,
            StatusChangedAt = Now,
            AvailableAt = Now,
        });

        db.GeneratedImages.Add(new GeneratedImage
        {
            Id = imageId,
            GeneratedImageOperationId = operationId,
            VariantIndex = 0,
            Status = GeneratedImageStatus.Staged,
            ObjectKey = $"staging/{workspaceId:N}/{imageId:N}.png",
            MediaType = "image/png",
            Width = 1024,
            Height = 1024,
            SizeBytes = 2048,
            ContentChecksum = "sha256:" + new string('a', 64),
            ProviderName = "test-provider",
            ModelName = "test-model",
            RetentionExpiresAt = Now + MediaPolicy.StagedImageTimeToLive,
            CreatedAt = Now,
            StatusChangedAt = Now,
        });

        await db.SaveChangesAsync(Ct);

        return imageId;
    }

    private async Task<int> CountAsync(Guid workspaceId)
    {
        await using var scope = ScopeFor(workspaceId);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .PromptRecords.CountAsync(Ct);
    }

    // ---- what is stored ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_prompt_the_creator_wrote_is_saved()
    {
        var saved = await SavedAsync(WorkspaceA, Manual());

        Assert.NotEqual(Guid.Empty, saved.PromptRecordId);
        Assert.Equal("instagram", saved.ChannelKey);
        Assert.Equal(PromptImageKind.Hero, saved.ImageKind);
        Assert.Equal(PromptRecordSource.Manual, saved.Source);
        Assert.Equal("Soda bread hero", saved.Label);
        Assert.Null(saved.GeneratedText);
        Assert.Null(saved.AiProposalId);
        Assert.Null(saved.RecipeId);
        Assert.Equal(1, await CountAsync(WorkspaceA));
    }

    /// <summary>
    /// The author and the timestamp are the server's, and the row's workspace is the resolved one — never input,
    /// because the request has no field for any of the three.
    /// </summary>
    [Fact]
    public async Task The_author_the_time_and_the_workspace_are_the_servers_to_decide()
    {
        var membershipId = Guid.NewGuid();

        var saved = await SavedAsync(WorkspaceA, Manual(), membershipId);

        Assert.Equal(Now, saved.CreatedAt);

        await using var scope = ScopeFor(WorkspaceA);
        var stored = await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .PromptRecords.SingleAsync(record => record.Id == saved.PromptRecordId, Ct);

        Assert.Equal(WorkspaceA, stored.WorkspaceId);

        // Read from the row rather than the response, because the response deliberately does not carry it: the
        // membership ids never leave the server (tenancy.md), so the only way to see that authorship was stamped
        // from the resolved context rather than invented is to look at what was stored.
        Assert.Equal(membershipId, stored.CreatedByMembershipId);
    }

    /// <summary>
    /// And the response says nothing about who saved it. A rule about what must <em>not</em> be published:
    /// <c>WorkspaceId</c> and the membership ids stay server-side, and a field added here would ship an
    /// identifier no route can turn into a person.
    /// </summary>
    [Fact]
    public void The_response_publishes_no_workspace_or_membership_id()
    {
        var properties = typeof(SavedPromptRecordServiceModel).GetProperties().Select(property => property.Name);

        Assert.DoesNotContain("WorkspaceId", properties);
        Assert.DoesNotContain("CreatedByMembershipId", properties);
    }

    /// <summary>
    /// Two columns rather than a flag: the library can still answer what the creator changed.
    /// </summary>
    [Fact]
    public async Task A_models_draft_is_kept_beside_the_prompt_that_was_used()
    {
        var proposalId = await SeedProposalAsync(WorkspaceA);

        var saved = await SavedAsync(WorkspaceA, Generated(proposalId) with
        {
            Text = "Overhead shot of soda bread on a linen cloth, with a knife.",
            GeneratedText = "Overhead shot of soda bread.",
        });

        Assert.Equal("Overhead shot of soda bread on a linen cloth, with a knife.", saved.Text);
        Assert.Equal("Overhead shot of soda bread.", saved.GeneratedText);
        Assert.Equal(proposalId, saved.AiProposalId);
        Assert.Equal("image.prompt", saved.PromptTemplateId);
    }

    [Fact]
    public async Task The_text_is_stored_trimmed_and_reported_as_stored()
    {
        var saved = await SavedAsync(WorkspaceA, Manual() with
        {
            Text = "  Overhead shot, soft light.  ",
            Label = "  Hero  ",
            ChannelKey = " instagram ",
        });

        Assert.Equal("Overhead shot, soft light.", saved.Text);
        Assert.Equal("Hero", saved.Label);
        Assert.Equal("instagram", saved.ChannelKey);
    }

    // ---- the lineage pins --------------------------------------------------------------------------------

    [Fact]
    public async Task A_recipe_and_version_pin_in_this_workspace_is_accepted()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var saved = await SavedAsync(WorkspaceA, Manual() with
        {
            RecipeId = recipe.RecipeId,
            RecipeVersionId = recipe.VersionId,
        });

        Assert.Equal(recipe.RecipeId, saved.RecipeId);
        Assert.Equal(recipe.VersionId, saved.RecipeVersionId);
    }

    [Fact]
    public async Task A_recipe_pin_may_stand_without_a_version()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var saved = await SavedAsync(WorkspaceA, Manual() with { RecipeId = recipe.RecipeId });

        Assert.Equal(recipe.RecipeId, saved.RecipeId);
        Assert.Null(saved.RecipeVersionId);
    }

    [Fact]
    public async Task An_unknown_recipe_is_refused_and_nothing_is_written()
    {
        var result = await SaveAsync(WorkspaceA, Manual() with { RecipeId = Guid.NewGuid() });

        Assert.False(result.Succeeded);
        Assert.Equal(ContentErrorCodes.PromptLineageUnprocessable, result.Error!.Code);
        Assert.Contains("recipeId", result.Error.FieldErrors.Keys);
        Assert.Equal(0, await CountAsync(WorkspaceA));
    }

    [Fact]
    public async Task A_version_of_a_different_recipe_is_refused()
    {
        var first = await SeedRecipeAsync(WorkspaceA);
        var second = await SeedRecipeAsync(WorkspaceA);

        var result = await SaveAsync(WorkspaceA, Manual() with
        {
            RecipeId = first.RecipeId,
            RecipeVersionId = second.VersionId,
        });

        Assert.False(result.Succeeded);
        Assert.Equal(ContentErrorCodes.PromptLineageUnprocessable, result.Error!.Code);
        Assert.Contains("recipeVersionId", result.Error.FieldErrors.Keys);
    }

    [Fact]
    public async Task A_proposal_in_this_workspace_is_accepted()
    {
        var proposalId = await SeedProposalAsync(WorkspaceA);

        var saved = await SavedAsync(WorkspaceA, Generated(proposalId));

        Assert.Equal(proposalId, saved.AiProposalId);
    }

    /// <summary>
    /// The stored template triple is the proposal's, not the request's (12.3a's owed decision, settled in
    /// 12.4a).
    /// </summary>
    /// <remarks>
    /// The server already holds the triple on the proposal that wrote the draft, and the prompt row is
    /// immutable — so a value the client got wrong could only ever be erased, never corrected. Deriving it
    /// makes the stored lineage a fact rather than a claim.
    /// </remarks>
    [Fact]
    public async Task The_stored_template_triple_comes_from_the_proposal()
    {
        var proposalId = await SeedProposalAsync(WorkspaceA);

        var saved = await SavedAsync(WorkspaceA, Generated(proposalId));

        Assert.Equal("image.prompt", saved.PromptTemplateId);
        Assert.Equal("1.0.0", saved.PromptTemplateVersion);
        Assert.Equal("sha256:" + new string('a', 64), saved.PromptTemplateBodyChecksum);
    }

    [Theory]
    [InlineData("promptTemplateId")]
    [InlineData("promptTemplateVersion")]
    [InlineData("promptTemplateBodyChecksum")]
    public async Task A_template_value_that_contradicts_the_proposal_is_refused(string field)
    {
        var proposalId = await SeedProposalAsync(WorkspaceA);

        // One field at a time, so each is proved to be checked rather than one standing in for three.
        var model = field switch
        {
            "promptTemplateId" => Generated(proposalId) with { PromptTemplateId = "some.other.template" },
            "promptTemplateVersion" => Generated(proposalId) with { PromptTemplateVersion = "9.9.9" },
            _ => Generated(proposalId) with
            {
                PromptTemplateBodyChecksum = "sha256:" + new string('b', 64),
            },
        };

        var result = await SaveAsync(WorkspaceA, model);

        Assert.False(result.Succeeded);
        Assert.Equal(ContentErrorCodes.PromptLineageUnprocessable, result.Error!.Code);
        Assert.Contains(field, result.Error.FieldErrors.Keys);
        Assert.Equal(0, await CountAsync(WorkspaceA));
    }

    /// <summary>
    /// A proposal about one recipe is not where a prompt about another came from.
    /// </summary>
    /// <remarks>
    /// Before 12.4a any proposal the workspace held satisfied any recipe pin, so a prompt could permanently
    /// claim a provenance that had nothing to do with the dish it names.
    /// </remarks>
    [Fact]
    public async Task A_proposal_about_a_different_recipe_is_refused()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);
        var other = await SeedRecipeAsync(WorkspaceA);

        var proposalId = await SeedProposalAsync(WorkspaceA, recipeId: other.RecipeId);

        var model = Generated(proposalId) with { RecipeId = recipe.RecipeId };

        var result = await SaveAsync(WorkspaceA, model);

        Assert.False(result.Succeeded);
        Assert.Equal(ContentErrorCodes.PromptLineageUnprocessable, result.Error!.Code);
        Assert.Contains("aiProposalId", result.Error.FieldErrors.Keys);
        Assert.Equal(0, await CountAsync(WorkspaceA));
    }

    /// <summary>A proposal about the recipe the prompt pins is accepted, so the refusal above is the rule.</summary>
    [Fact]
    public async Task A_proposal_about_the_pinned_recipe_is_accepted()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);
        var proposalId = await SeedProposalAsync(WorkspaceA, recipeId: recipe.RecipeId);

        var model = Generated(proposalId) with { RecipeId = recipe.RecipeId };

        var saved = await SavedAsync(WorkspaceA, model);

        Assert.Equal(recipe.RecipeId, saved.RecipeId);
        Assert.Equal(proposalId, saved.AiProposalId);
    }

    /// <summary>
    /// A proposal that named no recipe can still be the source of a prompt that pins one.
    /// </summary>
    /// <remarks>
    /// IMG-001's own requests may pin no recipe — a creator plans shoots for recipes they have not written —
    /// so requiring the proposal to name the same recipe would refuse the commonest real case. The rule is
    /// only that two <em>stated</em> subjects must not disagree.
    /// </remarks>
    [Fact]
    public async Task A_proposal_that_named_no_recipe_is_accepted_beside_a_recipe_pin()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);
        var proposalId = await SeedProposalAsync(WorkspaceA, recipeId: null);

        var model = Generated(proposalId) with { RecipeId = recipe.RecipeId };

        var saved = await SavedAsync(WorkspaceA, model);

        Assert.Equal(recipe.RecipeId, saved.RecipeId);
    }

    [Fact]
    public async Task An_unknown_proposal_is_refused()
    {
        var result = await SaveAsync(WorkspaceA, Generated(Guid.NewGuid()));

        Assert.False(result.Succeeded);
        Assert.Equal(ContentErrorCodes.PromptLineageUnprocessable, result.Error!.Code);
        Assert.Contains("aiProposalId", result.Error.FieldErrors.Keys);
        Assert.Equal(0, await CountAsync(WorkspaceA));
    }

    // ---- two workspaces ----------------------------------------------------------------------------------

    /// <summary>
    /// Another workspace's recipe is refused, and in the same words as one that does not exist. The pin is
    /// validated through the recipe facade inside the resolved workspace, so the query filter is what makes the
    /// two indistinguishable — a prompt save cannot be used to ask what a neighbour owns.
    /// </summary>
    [Fact]
    public async Task Another_workspaces_recipe_is_refused_exactly_as_an_unknown_one_is()
    {
        var theirs = await SeedRecipeAsync(WorkspaceB);

        var borrowed = await SaveAsync(WorkspaceA, Manual() with { RecipeId = theirs.RecipeId });
        var unknown = await SaveAsync(WorkspaceA, Manual() with { RecipeId = Guid.NewGuid() });

        Assert.False(borrowed.Succeeded);
        AssertSameRefusal(unknown, borrowed, "recipeId");
        Assert.Equal(0, await CountAsync(WorkspaceA));
    }

    /// <inheritdoc cref="Another_workspaces_recipe_is_refused_exactly_as_an_unknown_one_is"/>
    [Fact]
    public async Task Another_workspaces_version_is_refused_exactly_as_an_unknown_one_is()
    {
        var mine = await SeedRecipeAsync(WorkspaceA);
        var theirs = await SeedRecipeAsync(WorkspaceB);

        var borrowed = await SaveAsync(WorkspaceA, Manual() with
        {
            RecipeId = mine.RecipeId,
            RecipeVersionId = theirs.VersionId,
        });

        var unknown = await SaveAsync(WorkspaceA, Manual() with
        {
            RecipeId = mine.RecipeId,
            RecipeVersionId = Guid.NewGuid(),
        });

        Assert.False(borrowed.Succeeded);
        AssertSameRefusal(unknown, borrowed, "recipeVersionId");
        Assert.Equal(0, await CountAsync(WorkspaceA));
    }

    /// <inheritdoc cref="Another_workspaces_recipe_is_refused_exactly_as_an_unknown_one_is"/>
    [Fact]
    public async Task Another_workspaces_proposal_is_refused_exactly_as_an_unknown_one_is()
    {
        var theirs = await SeedProposalAsync(WorkspaceB);

        var borrowed = await SaveAsync(WorkspaceA, Generated(theirs));
        var unknown = await SaveAsync(WorkspaceA, Generated(Guid.NewGuid()));

        Assert.False(borrowed.Succeeded);
        AssertSameRefusal(unknown, borrowed, "aiProposalId");
        Assert.Equal(0, await CountAsync(WorkspaceA));
    }

    /// <inheritdoc cref="Another_workspaces_recipe_is_refused_exactly_as_an_unknown_one_is"/>
    /// <remarks>
    /// The pin 12.3a refused to accept at all until something could verify it. The composite foreign key is the
    /// authority, so a borrowed id is unrepresentable rather than merely refused — but a storage exception is
    /// not an answer a creator can act on, and this row can never be corrected once written.
    /// </remarks>
    [Fact]
    public async Task Another_workspaces_generated_image_is_refused_exactly_as_an_unknown_one_is()
    {
        var theirs = await SeedGeneratedImageAsync(WorkspaceB);

        var borrowed = await SaveAsync(WorkspaceA, Manual() with { GeneratedImageId = theirs });
        var unknown = await SaveAsync(WorkspaceA, Manual() with { GeneratedImageId = Guid.NewGuid() });

        Assert.False(borrowed.Succeeded);
        AssertSameRefusal(unknown, borrowed, "generatedImageId");
        Assert.Equal(0, await CountAsync(WorkspaceA));
    }

    /// <summary>And this workspace's own image is stored, so the refusals above are the filter and not the field.</summary>
    [Fact]
    public async Task A_prompt_may_name_a_generated_image_of_its_own_workspace()
    {
        var mine = await SeedGeneratedImageAsync(WorkspaceA);

        var saved = await SavedAsync(WorkspaceA, Manual() with { GeneratedImageId = mine });

        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var stored = await db.PromptRecords.AsNoTracking()
            .SingleAsync(record => record.Id == saved.PromptRecordId, Ct);

        Assert.Equal(mine, stored.GeneratedImageId);
    }

    /// <summary>
    /// The two refusals are the same refusal: same code, same message, same field, same field text. Asserted
    /// in full rather than by code alone, because the regression worth catching is wording that distinguishes
    /// them — "that belongs to another workspace" would pass a code-only assertion and leak the answer.
    /// </summary>
    private static void AssertSameRefusal(
        Domain.Managers.Results.OperationResult<SavedPromptRecordServiceModel> unknown,
        Domain.Managers.Results.OperationResult<SavedPromptRecordServiceModel> borrowed,
        string field)
    {
        Assert.Equal(ContentErrorCodes.PromptLineageUnprocessable, borrowed.Error!.Code);
        Assert.Equal(unknown.Error!.Code, borrowed.Error.Code);
        Assert.Equal(unknown.Error.Message, borrowed.Error.Message);
        Assert.Equal(unknown.Error.FieldErrors.Keys, borrowed.Error.FieldErrors.Keys);
        Assert.Equal(unknown.Error.FieldErrors[field], borrowed.Error.FieldErrors[field]);
    }

    /// <summary>
    /// The new cross-module read, asked directly: a proposal resolves in its own workspace and not in the
    /// other, and an id that was never issued resolves nowhere.
    /// </summary>
    /// <remarks>
    /// Tested on its own as well as through a prompt save, because it is the seam that carries the
    /// validate-before-insert guarantee for an immutable row — and because it takes a bare <c>Guid</c> and
    /// returns a bare <c>bool</c>, so nothing about its signature would stop a later change adding a workspace
    /// predicate, an <c>IgnoreQueryFilters</c>, or a read that answers for any workspace at all.
    /// </remarks>
    [Fact]
    public async Task The_proposal_lookup_answers_only_for_the_resolved_workspace()
    {
        var mine = await SeedProposalAsync(WorkspaceA);
        var theirs = await SeedProposalAsync(WorkspaceB);

        await using (var scope = ScopeFor(WorkspaceA))
        {
            var lookup = scope.ServiceProvider.GetRequiredService<IAiProposalLookupFacade>();

            Assert.True(await lookup.ExistsAsync(mine, Ct));
            Assert.False(await lookup.ExistsAsync(theirs, Ct));
            Assert.False(await lookup.ExistsAsync(Guid.NewGuid(), Ct));
            Assert.False(await lookup.ExistsAsync(Guid.Empty, Ct));
        }

        await using (var scope = ScopeFor(WorkspaceB))
        {
            var lookup = scope.ServiceProvider.GetRequiredService<IAiProposalLookupFacade>();

            Assert.True(await lookup.ExistsAsync(theirs, Ct));
            Assert.False(await lookup.ExistsAsync(mine, Ct));
        }
    }

    /// <summary>
    /// And it fails closed with no workspace resolved, rather than answering for every workspace at once.
    /// </summary>
    [Fact]
    public async Task The_proposal_lookup_cannot_be_asked_before_a_workspace_is_resolved()
    {
        var mine = await SeedProposalAsync(WorkspaceA);

        await using var scope = _provider.CreateAsyncScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IAiProposalLookupFacade>();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => lookup.ExistsAsync(mine, Ct));
    }

    /// <summary>
    /// A save in one workspace is invisible in the other, and each library counts only its own.
    /// </summary>
    [Fact]
    public async Task A_prompt_saved_in_one_workspace_is_not_in_the_others_library()
    {
        var mine = await SavedAsync(WorkspaceA, Manual());
        await SavedAsync(WorkspaceB, Manual() with { Text = "Theirs." });

        Assert.Equal(1, await CountAsync(WorkspaceA));
        Assert.Equal(1, await CountAsync(WorkspaceB));

        await using var scope = ScopeFor(WorkspaceB);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        Assert.False(await db.PromptRecords.AnyAsync(record => record.Id == mine.PromptRecordId, Ct));
    }

    /// <summary>
    /// Nothing can be saved before a workspace is resolved. The interceptor refuses the insert rather than
    /// inventing an owner, which is what makes a background job without a resolved context a loud failure.
    /// </summary>
    [Fact]
    public async Task A_prompt_cannot_be_saved_before_a_workspace_is_resolved()
    {
        await using var scope = _provider.CreateAsyncScope();
        var business = scope.ServiceProvider.GetRequiredService<IPromptRecordBusiness>();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => business.SaveAsync(Manual(), Ct));
    }

    // ---- the Business backstop ---------------------------------------------------------------------------

    /// <summary>
    /// Business re-asks the shape rules, for a caller that reached it without the facade's validator — a worker,
    /// a plugin, or DAM-001 calling in-transaction.
    /// </summary>
    [Fact]
    public async Task Business_refuses_a_malformed_prompt_without_the_edge_validator()
    {
        var result = await SaveAsync(WorkspaceA, Manual() with { Text = "  " });

        Assert.False(result.Succeeded);
        Assert.Equal(ContentErrorCodes.PromptInvalid, result.Error!.Code);
        Assert.Contains("text", result.Error.FieldErrors.Keys);
        Assert.Equal(0, await CountAsync(WorkspaceA));
    }

    [Fact]
    public async Task Business_refuses_provenance_that_disagrees_with_itself()
    {
        var proposalId = await SeedProposalAsync(WorkspaceA);

        // A manual prompt naming the proposal that would make it generated: CK_PromptRecords_AiProposal_Source
        // would refuse it too, but as a storage exception rather than a field error.
        var result = await SaveAsync(WorkspaceA, Manual() with { AiProposalId = proposalId });

        Assert.False(result.Succeeded);
        Assert.Equal(ContentErrorCodes.PromptInvalid, result.Error!.Code);
        Assert.Contains("aiProposalId", result.Error.FieldErrors.Keys);
    }

    [Fact]
    public async Task Business_refuses_an_unknown_channel()
    {
        var result = await SaveAsync(WorkspaceA, Manual() with { ChannelKey = "mastodon" });

        Assert.False(result.Succeeded);
        Assert.Equal(ContentErrorCodes.PromptInvalid, result.Error!.Code);
        Assert.Contains("channelKey", result.Error.FieldErrors.Keys);
    }

    // ---- immutability ------------------------------------------------------------------------------------

    /// <summary>
    /// What the seam above the schema inherits: a saved prompt cannot be edited or deleted, so a second save is
    /// the only way to record a reworked prompt.
    /// </summary>
    [Fact]
    public async Task A_saved_prompt_cannot_be_edited_afterwards()
    {
        var saved = await SavedAsync(WorkspaceA, Manual());

        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var stored = await db.PromptRecords.SingleAsync(record => record.Id == saved.PromptRecordId, Ct);
        stored.Label = "Renamed";

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
    }

    // ---- the transaction, and what DAM-001 will rely on --------------------------------------------------

    /// <summary>
    /// A save joins a transaction its caller already opened, so rolling that transaction back takes the prompt
    /// with it. This is the linkage to DAM-001 stated as a test: the asset row and the prompt row commit
    /// together or not at all.
    /// </summary>
    /// <remarks>
    /// It matters because the record is immutable. A prompt written <em>after</em> an asset committed could
    /// never be compensated — <c>ImmutableRecordInterceptor</c> refuses every delete — so "inside the caller's
    /// transaction" is the only correct place for this write, and nothing but a test keeps a later refactor
    /// from opening a transaction of its own here and quietly breaking that.
    /// </remarks>
    [Fact]
    public async Task A_save_joins_a_transaction_its_caller_already_opened_and_rolls_back_with_it()
    {
        await using (var scope = ScopeFor(WorkspaceA))
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            var result = await scope.ServiceProvider.GetRequiredService<IPromptRecordBusiness>()
                .SaveAsync(Manual(), Ct);

            Assert.True(result.Succeeded, result.Error?.Code);

            // Visible inside the transaction, which is what makes the rollback below meaningful rather than a
            // test of a save that never happened.
            Assert.Equal(1, await db.PromptRecords.CountAsync(Ct));

            await transaction.RollbackAsync(Ct);
        }

        Assert.Equal(0, await CountAsync(WorkspaceA));
    }

    /// <summary>
    /// A refused insert leaves nothing staged, so a later save on the same scope cannot pick the row up and
    /// write it after the fact.
    /// </summary>
    /// <remarks>
    /// Reached by going round Business's pin validation and handing the data layer a recipe id that is not
    /// there, which is the shape of the race the layer is written for. Only the record this call added is
    /// detached — not the whole change tracker — because the caller may be DAM-001 mid-transaction with its own
    /// entities staged.
    /// </remarks>
    [Fact]
    public async Task A_refused_insert_leaves_nothing_staged_for_a_later_save_to_write()
    {
        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var saved = await scope.ServiceProvider.GetRequiredService<IPromptRecordDataLayer>().SaveAsync(
            new PromptRecord
            {
                Id = Guid.NewGuid(),
                ChannelKey = "instagram",
                ImageKind = PromptImageKind.Hero,
                Text = "Overhead shot.",
                Source = PromptRecordSource.Manual,
                RecipeId = Guid.NewGuid(),
                CreatedByMembershipId = Guid.NewGuid(),
                CreatedAt = Now,
            },
            Ct);

        Assert.False(saved);
        Assert.Empty(db.ChangeTracker.Entries<PromptRecord>());

        await db.SaveChangesAsync(Ct);

        Assert.Equal(0, await CountAsync(WorkspaceA));
    }

    /// <summary>
    /// A collision whose pins still resolve is a conflict: nothing was written, and the same request may
    /// succeed next time — a deadlock victim or a command timeout arrives this way.
    /// </summary>
    [Fact]
    public async Task A_collision_whose_pins_still_resolve_is_a_conflict()
    {
        var result = await ClassifyCollisionAsync(alwaysResolves: true);

        Assert.False(result.Succeeded);
        Assert.Equal(ContentErrorCodes.PromptConflict, result.Error!.Code);
    }

    /// <summary>
    /// A collision whose pin has gone is the lineage refusal instead, because that one cannot end differently
    /// however many times it is retried — the same reasoning
    /// <c>WorkspaceWeeklyThemeDataLayer.LostToAnotherWriterAsync</c> records for a week replace.
    /// </summary>
    [Fact]
    public async Task A_collision_whose_pin_has_vanished_is_the_lineage_refusal()
    {
        var result = await ClassifyCollisionAsync(alwaysResolves: false);

        Assert.False(result.Succeeded);
        Assert.Equal(ContentErrorCodes.PromptLineageUnprocessable, result.Error!.Code);
        Assert.Contains("aiProposalId", result.Error.FieldErrors.Keys);
    }

    /// <summary>
    /// Business with a data layer that always refuses, so the branch that classifies a collision can be
    /// reached. <paramref name="alwaysResolves"/> false makes the proposal pin resolve on the way in and not on
    /// the way out, which is a pin that vanished mid-request.
    /// </summary>
    private async Task<Domain.Managers.Results.OperationResult<SavedPromptRecordServiceModel>>
        ClassifyCollisionAsync(bool alwaysResolves)
    {
        var proposalId = await SeedProposalAsync(WorkspaceA);

        await using var scope = ScopeFor(WorkspaceA);
        var business = new PromptRecordBusiness(
            new RefusingDataLayer(),
            new Domain.Managers.Reference.ContentChannelCatalog(),
            scope.ServiceProvider.GetRequiredService<IRecipeFacade>(),
            alwaysResolves ? new AlwaysResolves() : new ResolvesOnce(),

            // Nothing in this test names a generated image or a DAM asset, so neither lookup is asked.
            new NoGeneratedImages(),
            new NoMediaAssets(),
            scope.ServiceProvider.GetRequiredService<IWorkspaceContext>(),
            new StoppedClock());

        return await business.SaveAsync(Generated(proposalId), Ct);
    }

    private sealed class RefusingDataLayer : IPromptRecordDataLayer
    {
        public Task<bool> SaveAsync(PromptRecord record, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<(IReadOnlyList<PromptSummaryRecord> Rows, bool HasMore, int? Total)> SearchAsync(
            PromptSearchCriteria criteria, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This double exists to refuse a save; nothing here lists.");

        public Task<PromptRecord?> GetDetailAsync(Guid promptRecordId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This double exists to refuse a save; nothing here reads.");

        public Task<PromptTextRecord?> GetTextDownloadAsync(
            Guid promptRecordId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This double exists to refuse a save; nothing here downloads.");
    }

    /// <summary>
    /// A proposal that resolves, recording the same template triple <c>Generated</c> sends.
    /// </summary>
    /// <remarks>
    /// The triple has to agree: since 12.4a the save derives it from the proposal and refuses a request that
    /// contradicts it, so a double answering some other template would make every generated save in this class
    /// a lineage refusal.
    /// </remarks>
    private sealed class AlwaysResolves : IAiProposalLookupFacade
    {
        public Task<bool> ExistsAsync(Guid aiProposalId, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<AiProposalLineageServiceModel?> FindLineageAsync(
            Guid aiProposalId, CancellationToken cancellationToken) =>
            Task.FromResult<AiProposalLineageServiceModel?>(new AiProposalLineageServiceModel(
                AiTaskType.ImagePrompt, "image.prompt", "1.0.0",
                $"sha256:{new string('a', 64)}", null, null));
    }

    /// <summary>Resolves the first time it is asked and not the second: a pin taken away mid-request.</summary>
    private sealed class ResolvesOnce : IAiProposalLookupFacade
    {
        private int _asked;

        public Task<bool> ExistsAsync(Guid aiProposalId, CancellationToken cancellationToken) =>
            Task.FromResult(_asked++ == 0);

        public Task<AiProposalLineageServiceModel?> FindLineageAsync(
            Guid aiProposalId, CancellationToken cancellationToken) =>
            Task.FromResult(_asked++ == 0
                ? new AiProposalLineageServiceModel(
                    AiTaskType.ImagePrompt, "image.prompt", "1.0.0",
                    $"sha256:{new string('a', 64)}", null, null)
                : null);
    }

    /// <summary>A lookup nothing asks, for the collision tests that name no image.</summary>
    private sealed class NoGeneratedImages : IGeneratedImageLookupFacade
    {
        public Task<bool> ExistsAsync(Guid generatedImageId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("No test here names a generated image.");
    }

    /// <inheritdoc cref="NoGeneratedImages"/>
    private sealed class NoMediaAssets : IMediaAssetLookupFacade
    {
        public Task<bool> ExistsAsync(Guid mediaAssetId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("No test here names a DAM asset.");
    }

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }
}
