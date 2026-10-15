using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Workflows.Business;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// <c>GET .../workflow-definitions</c> through the real Gateway (13A.13): every workflow in the product's order, in
/// plain words; whether each reader can start each one, by the same rules a start applies; what is missing first and
/// where to fix it; each workspace fact asked once; and two workspaces.
/// </summary>
public sealed class WorkflowDefinitionListEndpointTests : IAsyncLifetime
{
    private const string MemberPassword = "correct horse battery staple";

    /// <summary>A built workflow that waits on the workspace's time zone before it can start.</summary>
    private const string ZoneFirstKey = "test.zone-first";

    /// <summary>One that waits on the time zone and on a recipe.</summary>
    private const string RecipeFirstKey = "test.recipe-first";

    private readonly FactCounter _facts = new();
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.AddSingleton<IWorkflowCatalogue>(new CatalogueWithPrerequisites());

            // The real facts, counted.
            var real = services.Single(descriptor => descriptor.ServiceType == typeof(IWorkflowFacts));
            services.Remove(real);
            services.Add(new ServiceDescriptor(
                typeof(IWorkflowFacts),
                provider => new CountedFacts((IWorkflowFacts)ActivatorUtilities.CreateInstance(provider, real.ImplementationType!), _facts),
                real.Lifetime));
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string DefinitionsIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/workflow-definitions";

    [Fact]
    public async Task Every_workflow_comes_once_at_its_newest_version_in_the_products_order_in_plain_words()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var response = await owner.GetAsync(DefinitionsIn(_fixture.WorkspaceA), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var listed = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray().ToList();

        var expected = new CatalogueWithPrerequisites().Latest();
        Assert.Equal(expected.Select(definition => definition.Key.Value), listed.Select(item => item.GetProperty("key").GetString()));

        var create = listed.Single(item => item.GetProperty("key").GetString() == WorkflowCatalogue.RecipeCreateKey);
        Assert.Equal("Create a recipe", create.GetProperty("goal").GetString());
        Assert.Equal(WorkflowCatalogue.RecipeCreateV1.Description, create.GetProperty("description").GetString());
        Assert.Equal(WorkflowCatalogue.RecipeCreateV1.ExpectedOutcome, create.GetProperty("expectedOutcome").GetString());
        Assert.True(create.GetProperty("available").GetBoolean());
        Assert.True(create.GetProperty("canStart").GetBoolean());
        Assert.Equal(JsonValueKind.Null, create.GetProperty("neededRole").ValueKind);
        Assert.Empty(create.GetProperty("waitingFor").EnumerateArray());

        // Nothing of how a workflow is built: no steps, capabilities, routes or prompts.
        var names = create.EnumerateObject().Select(property => property.Name).ToHashSet();
        foreach (var absent in new[] { "steps", "capabilities", "nextSuggestions", "prerequisites" })
        {
            Assert.DoesNotContain(absent, names);
        }
    }

    [Fact]
    public async Task A_workflow_not_built_yet_says_so_and_cannot_start()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var planner = (await ListAsync(owner, _fixture.WorkspaceA)).Single(item => item.GetProperty("key").GetString() == WorkflowCatalogue.PlanDayAndWeekKey);

        Assert.False(planner.GetProperty("available").GetBoolean());
        Assert.False(planner.GetProperty("canStart").GetBoolean());
        Assert.Equal("Your day and week planner isn't built yet.", planner.GetProperty("unavailableReason").GetString());
        Assert.Equal(JsonValueKind.Null, planner.GetProperty("neededRole").ValueKind);
    }

    [Fact]
    public async Task Who_can_start_what_follows_the_first_step_and_never_lets_a_viewer_start()
    {
        using var viewer = await _fixture.SignInAsync(await AddMemberAsync(WorkspaceRole.Viewer), MemberPassword, Ct);
        using var contributor = await _fixture.SignInAsync(await AddMemberAsync(WorkspaceRole.Contributor), MemberPassword, Ct);
        using var editor = await _fixture.SignInAsync(await AddMemberAsync(WorkspaceRole.Editor), MemberPassword, Ct);

        var asViewer = await ListAsync(viewer, _fixture.WorkspaceA);
        var asContributor = await ListAsync(contributor, _fixture.WorkspaceA);
        var asEditor = await ListAsync(editor, _fixture.WorkspaceA);

        // Improving a recipe begins by choosing one, which a Viewer could do — but a start takes a Contributor.
        Assert.False(Item(asViewer, WorkflowCatalogue.RecipeImproveKey).GetProperty("canStart").GetBoolean());
        Assert.Equal("Contributor", Item(asViewer, WorkflowCatalogue.RecipeImproveKey).GetProperty("neededRole").GetString());
        Assert.True(Item(asContributor, WorkflowCatalogue.RecipeImproveKey).GetProperty("canStart").GetBoolean());

        // Setting up a brand voice begins with a step only an Editor can take.
        Assert.False(Item(asContributor, WorkflowCatalogue.BrandVoiceKey).GetProperty("canStart").GetBoolean());
        Assert.Equal("Editor", Item(asContributor, WorkflowCatalogue.BrandVoiceKey).GetProperty("neededRole").GetString());
        Assert.True(Item(asEditor, WorkflowCatalogue.BrandVoiceKey).GetProperty("canStart").GetBoolean());

        Assert.All(asViewer, item => Assert.False(item.GetProperty("canStart").GetBoolean()));
    }

    [Fact]
    public async Task What_a_workflow_needs_first_is_said_with_where_to_sort_it_and_each_fact_is_asked_once()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        _facts.ZoneChecks = 0;

        var listed = await ListAsync(owner, _fixture.WorkspaceA);

        var zoneFirst = Item(listed, ZoneFirstKey);
        Assert.False(zoneFirst.GetProperty("canStart").GetBoolean());
        Assert.Equal(JsonValueKind.Null, zoneFirst.GetProperty("neededRole").ValueKind);
        var zone = Assert.Single(zoneFirst.GetProperty("waitingFor").EnumerateArray());
        Assert.Equal("Set your workspace’s time zone in brand settings first.", zone.GetProperty("message").GetString());
        Assert.Equal("brand", zone.GetProperty("route").GetString());

        var recipeFirst = Item(listed, RecipeFirstKey).GetProperty("waitingFor").EnumerateArray().ToList();
        Assert.Equal(["Set your workspace’s time zone in brand settings first.", "This needs a recipe first."],
            recipeFirst.Select(need => need.GetProperty("message").GetString()));
        Assert.Equal("recipes", recipeFirst[1].GetProperty("route").GetString());

        // Two workflows wait on the time zone; the brand module is asked once.
        Assert.Equal(1, _facts.ZoneChecks);
    }

    [Fact]
    public async Task One_workspaces_time_zone_starts_its_workflows_and_no_one_elses()
    {
        await SetTimeZoneAsync(_fixture.WorkspaceB);
        using var ownerA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);

        Assert.True(Item(await ListAsync(ownerB, _fixture.WorkspaceB), ZoneFirstKey).GetProperty("canStart").GetBoolean());
        Assert.Empty(Item(await ListAsync(ownerB, _fixture.WorkspaceB), ZoneFirstKey).GetProperty("waitingFor").EnumerateArray());

        Assert.False(Item(await ListAsync(ownerA, _fixture.WorkspaceA), ZoneFirstKey).GetProperty("canStart").GetBoolean());
    }

    [Fact]
    public async Task Another_workspace_is_refused_as_unknown()
    {
        using var ownerA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var response = await ownerA.GetAsync(DefinitionsIn(_fixture.WorkspaceB), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Helpers ----

    private static async Task<List<JsonElement>> ListAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var response = await client.GetAsync(DefinitionsIn(workspace), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray()];
    }

    private static JsonElement Item(IEnumerable<JsonElement> listed, string key) =>
        listed.Single(item => item.GetProperty("key").GetString() == key);

    private async Task SetTimeZoneAsync(SeededWorkspace workspace)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.BrandProfiles.Add(new BrandProfile
        {
            Id = Guid.NewGuid(),
            BrandName = "Kitchen B",
            ShortDescription = "Weeknight cooking.",
            DefaultAudience = "Home cooks",
            Locale = "en-US",
            TimeZoneId = "America/Chicago",
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByMembershipId = Guid.NewGuid(),
            UpdatedByMembershipId = Guid.NewGuid(),
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task<string> AddMemberAsync(WorkspaceRole role)
    {
        var email = $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@example.com";
        var userId = await _fixture.Api.CreateUserAsync(email, MemberPassword);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _fixture.WorkspaceA.Id,
            UserId = userId,
            Role = role,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);

        return email;
    }

    /// <summary>The shipped catalogue plus two built workflows that need something before they start.</summary>
    private sealed class CatalogueWithPrerequisites : IWorkflowCatalogue
    {
        public IReadOnlyList<WorkflowDefinition> All { get; } =
        [
            .. WorkflowCatalogue.All,
            WorkflowCatalogue.PlanAndPublishV1 with
            {
                Key = ZoneFirstKey,
                Prerequisites = [new WorkflowPrerequisite.WorkspaceFact(WorkflowWorkspaceFact.SchedulingZoneSet)],
            },
            WorkflowCatalogue.PlanAndPublishV1 with
            {
                Key = RecipeFirstKey,
                Prerequisites =
                [
                    new WorkflowPrerequisite.WorkspaceFact(WorkflowWorkspaceFact.SchedulingZoneSet),
                    new WorkflowPrerequisite.ResourceLinked(WorkflowResourceKind.Recipe),
                ],
            },
        ];

        public WorkflowDefinition? Find(WorkflowKey key, int version) =>
            All.FirstOrDefault(definition => definition.Key == key && definition.Version == version);

        public IReadOnlyList<WorkflowDefinition> Latest() =>
            [.. All.GroupBy(definition => definition.Key).Select(family => family.MaxBy(definition => definition.Version)!)];
    }

    private sealed class FactCounter
    {
        public int ZoneChecks;
    }

    /// <summary>The real facts, with the time-zone check counted.</summary>
    private sealed class CountedFacts(IWorkflowFacts inner, FactCounter counter) : IWorkflowFacts
    {
        public Task<bool> ExistsAsync(WorkflowResourceKind kind, Guid id, CancellationToken cancellationToken) =>
            inner.ExistsAsync(kind, id, cancellationToken);

        public Task<bool> SchedulingZoneSetAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref counter.ZoneChecks);
            return inner.SchedulingZoneSetAsync(cancellationToken);
        }

        public Task<IReadOnlyList<string>> UnmetAsync(
            WorkflowCompletionRule rule,
            WorkflowRun run,
            IReadOnlyDictionary<WorkflowResourceKind, Guid> links,
            CancellationToken cancellationToken) =>
            inner.UnmetAsync(rule, run, links, cancellationToken);
    }
}
