using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand;
using CreatorPantry.Domain.Modules.Media;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Content;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Vocabulary;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Tests.Reference;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The content-seed generator end to end below HTTP: what it selects, what it pins, what it refuses, how it
/// degrades, and that it writes nothing.
/// </summary>
public sealed class ContentSeedBusinessTests : IAsyncLifetime
{
    private static readonly Guid WorkspaceA = Guid.NewGuid();

    private static readonly Guid WorkspaceB = Guid.NewGuid();

    private static readonly DateTimeOffset Now = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);

    private const string Actor = "user-1";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    private ServiceProvider _provider = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A token source that hands out a value a test chose, so "no token supplied" is still deterministic.</summary>
    private sealed class FixedTokenSource : IContentSeedTokenSource
    {
        public string Token { get; set; } = "fixed-token";

        public int Calls { get; private set; }

        public string Next()
        {
            Calls++;

            return Token;
        }
    }

    private readonly FixedTokenSource _tokens = new();

    private readonly PhotographyStyleCatalog _styles = new(
    [
        new("overhead-flat-lay", "Overhead flat-lay"),
        new("dark-and-moody", "Dark and moody"),
        new("retired-polaroid", "Retired polaroid", IsActive: false),
    ]);

    private readonly OccasionCatalog _occasions = new(
    [
        new("weeknight", "Weeknight"),
        new("entertaining", "Entertaining"),
        new("retired-fondue-night", "Retired fondue night", IsActive: false),
    ]);

    private readonly ContentChannelCatalog _channels = new(
    [
        new("blog", "Blog"),
        new("instagram", "Instagram"),
        new("tiktok", "TikTok"),
        new("retired-vine", "Retired Vine", IsActive: false),
    ]);

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync(Ct);

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddApplicationTime()
            .AddAudit()
            .AddIdempotency(new ConfigurationBuilder().Build())
            .AddVocabularyModule()
            .AddBrandModule()

            // The brand profile facade asks the Media module whether a submitted logo is in this workspace's
            // library (12.10k), so that lookup has to be resolvable wherever the brand module is composed.
            .AddMediaModule()
            .AddLogging()
            .AddContentModule()
            .AddSingleton<CachedPageReader>()
            .AddSingleton<Domain.Managers.Caching.IApplicationCache, FakeApplicationCache>()
            .AddSingleton<IContentSeedTokenSource>(_tokens)
            .AddSingleton<IPhotographyStyleCatalog>(_styles)
            .AddSingleton<IOccasionCatalog>(_occasions)
            .AddSingleton<IContentChannelCatalog>(_channels)
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        await db.Database.EnsureCreatedAsync(Ct);

        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "workspace-a", CreatedAt = Now },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "workspace-b", CreatedAt = Now });

        // Reference vocabulary, small and explicit rather than the real seed set: these tests are about selection
        // and refusal, and a retired entry in each catalogue is the part that matters.
        db.Cuisines.AddRange(
            Vocabulary<Cuisine>("thai", "Thai"),
            Vocabulary<Cuisine>("italian", "Italian"),
            Vocabulary<Cuisine>("retired-fusion", "Retired fusion", active: false));
        db.Courses.AddRange(
            Vocabulary<Course>("main-course", "Main Course"),
            Vocabulary<Course>("dessert", "Dessert"));
        db.CookingTechniques.AddRange(
            new CookingTechnique { Id = Guid.NewGuid(), Code = "stir-fry", DisplayName = "Stir-Fry", IsActive = true },
            new CookingTechnique { Id = Guid.NewGuid(), Code = "bake", DisplayName = "Bake", IsActive = true },
            new CookingTechnique
            {
                Id = Guid.NewGuid(),
                Code = "ferment",
                DisplayName = "Ferment",
                IsActive = true,
                RequiresSafetyCaution = true,
            });

        await db.SaveChangesAsync(Ct);
    }

    private static T Vocabulary<T>(string code, string displayName, bool active = true)
        where T : ControlledVocabulary, new() =>
        new() { Id = Guid.NewGuid(), Code = code, DisplayName = displayName, IsActive = active };

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private AsyncServiceScope ScopeFor(Guid workspaceId)
    {
        var scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            WorkspaceRole.Owner,
            Actor);

        return scope;
    }

    private async Task<ContentSeedServiceModel> SeedAsync(Guid workspaceId, ContentSeedQueryViewModel? query = null)
    {
        await using var scope = ScopeFor(workspaceId);
        var result = await scope.ServiceProvider.GetRequiredService<IContentSeedBusiness>()
            .GenerateAsync(query ?? new ContentSeedQueryViewModel(), Ct);

        Assert.True(result.Succeeded, result.Error?.Code);

        return result.Value!;
    }

    private async Task<Domain.Managers.Results.OperationError> RefusalAsync(
        Guid workspaceId, ContentSeedQueryViewModel query)
    {
        await using var scope = ScopeFor(workspaceId);
        var result = await scope.ServiceProvider.GetRequiredService<IContentSeedBusiness>()
            .GenerateAsync(query, Ct);

        Assert.False(result.Succeeded);

        return result.Error!;
    }

    [Fact]
    public async Task A_seed_fills_every_facet_and_echoes_its_token()
    {
        var seed = await SeedAsync(WorkspaceA);

        Assert.Equal("fixed-token", seed.Token);
        Assert.NotNull(seed.Cuisine);
        Assert.NotNull(seed.DishType);
        Assert.NotNull(seed.Method);
        Assert.NotNull(seed.PhotographyStyle);
        Assert.NotNull(seed.Channel);
        Assert.NotNull(seed.Occasion);
        Assert.NotEmpty(seed.Description);

        // Nothing was pinned, so nothing claims to have been.
        Assert.All(
            new[] { seed.Cuisine, seed.DishType, seed.PhotographyStyle, seed.Channel, seed.Occasion },
            facet => Assert.False(facet!.Pinned));
        Assert.False(seed.Method!.Pinned);
        Assert.False(seed.Day.Pinned);
    }

    [Fact]
    public async Task The_same_token_returns_the_same_seed()
    {
        var first = await SeedAsync(WorkspaceA, new ContentSeedQueryViewModel { Token = "spring-bakes" });
        var again = await SeedAsync(WorkspaceA, new ContentSeedQueryViewModel { Token = "spring-bakes" });

        Assert.Equal(first, again);
    }

    [Fact]
    public async Task A_supplied_token_is_used_instead_of_minting_one()
    {
        var before = _tokens.Calls;

        var seed = await SeedAsync(WorkspaceA, new ContentSeedQueryViewModel { Token = "  spring-bakes  " });

        Assert.Equal("spring-bakes", seed.Token);
        Assert.Equal(before, _tokens.Calls);
    }

    [Fact]
    public async Task Different_tokens_generally_return_different_seeds()
    {
        var seeds = new List<string>();
        foreach (var index in Enumerable.Range(0, 12))
        {
            var seed = await SeedAsync(WorkspaceA, new ContentSeedQueryViewModel { Token = $"token-{index}" });
            seeds.Add(seed.Description);
        }

        Assert.True(seeds.Distinct().Count() > 1, "Twelve tokens produced one seed.");
    }

    [Fact]
    public async Task Every_facet_can_be_pinned()
    {
        var seed = await SeedAsync(WorkspaceA, new ContentSeedQueryViewModel
        {
            Token = "spring-bakes",
            Cuisine = "thai",
            DishType = "dessert",
            Method = "bake",
            PhotographyStyle = "dark-and-moody",
            Channel = "tiktok",
            Day = DayOfWeek.Saturday,
            Occasion = "entertaining",
        });

        Assert.Equal("thai", seed.Cuisine!.Key);
        Assert.Equal("dessert", seed.DishType!.Key);
        Assert.Equal("bake", seed.Method!.Key);
        Assert.Equal("dark-and-moody", seed.PhotographyStyle!.Key);
        Assert.Equal("tiktok", seed.Channel!.Key);
        Assert.Equal(DayOfWeek.Saturday, seed.Day.Day);
        Assert.Equal("entertaining", seed.Occasion!.Key);

        // And each says it was the creator's choice rather than the generator's.
        Assert.True(seed.Cuisine.Pinned);
        Assert.True(seed.DishType.Pinned);
        Assert.True(seed.Method.Pinned);
        Assert.True(seed.PhotographyStyle.Pinned);
        Assert.True(seed.Channel.Pinned);
        Assert.True(seed.Day.Pinned);
        Assert.True(seed.Occasion.Pinned);
    }

    [Fact]
    public async Task Pinning_one_facet_leaves_the_rest_to_the_token()
    {
        var free = await SeedAsync(WorkspaceA, new ContentSeedQueryViewModel { Token = "spring-bakes" });
        var pinned = await SeedAsync(WorkspaceA, new ContentSeedQueryViewModel { Token = "spring-bakes", Cuisine = "thai" });

        Assert.Equal("thai", pinned.Cuisine!.Key);
        Assert.Equal(free.DishType!.Key, pinned.DishType!.Key);
        Assert.Equal(free.Method!.Key, pinned.Method!.Key);
        Assert.Equal(free.Day.Day, pinned.Day.Day);
    }

    [Theory]
    [InlineData(nameof(ContentSeedQueryViewModel.Cuisine), "klingon")]
    [InlineData(nameof(ContentSeedQueryViewModel.DishType), "second-breakfast")]
    [InlineData(nameof(ContentSeedQueryViewModel.Method), "sonic-blast")]
    [InlineData(nameof(ContentSeedQueryViewModel.PhotographyStyle), "daguerreotype")]
    [InlineData(nameof(ContentSeedQueryViewModel.Channel), "myspace")]
    [InlineData(nameof(ContentSeedQueryViewModel.Occasion), "coronation")]
    public async Task An_unknown_pinned_key_is_refused(string field, string value)
    {
        var query = new ContentSeedQueryViewModel
        {
            Cuisine = field == nameof(ContentSeedQueryViewModel.Cuisine) ? value : null,
            DishType = field == nameof(ContentSeedQueryViewModel.DishType) ? value : null,
            Method = field == nameof(ContentSeedQueryViewModel.Method) ? value : null,
            PhotographyStyle = field == nameof(ContentSeedQueryViewModel.PhotographyStyle) ? value : null,
            Channel = field == nameof(ContentSeedQueryViewModel.Channel) ? value : null,
            Occasion = field == nameof(ContentSeedQueryViewModel.Occasion) ? value : null,
        };

        var error = await RefusalAsync(WorkspaceA, query);

        Assert.Equal(ContentErrorCodes.ContentSeedInvalid, error.Code);
        Assert.Contains(char.ToLowerInvariant(field[0]) + field[1..], error.FieldErrors.Keys);
    }

    [Fact]
    public async Task A_retired_pinned_key_is_refused_as_retired()
    {
        // The code-owned catalogues keep retired entries, so they can say which it is rather than calling a real
        // key unknown.
        var style = await RefusalAsync(WorkspaceA, new ContentSeedQueryViewModel { PhotographyStyle = "retired-polaroid" });
        Assert.Contains("no longer available", style.FieldErrors["photographyStyle"][0]);

        var occasion = await RefusalAsync(WorkspaceA, new ContentSeedQueryViewModel { Occasion = "retired-fondue-night" });
        Assert.Contains("no longer available", occasion.FieldErrors["occasion"][0]);

        var channel = await RefusalAsync(WorkspaceA, new ContentSeedQueryViewModel { Channel = "retired-vine" });
        Assert.Contains("no longer available", channel.FieldErrors["channel"][0]);

        // A retired database-backed entry is simply absent from the active read, so it is refused as one answer.
        var cuisine = await RefusalAsync(WorkspaceA, new ContentSeedQueryViewModel { Cuisine = "retired-fusion" });
        Assert.Equal(ContentErrorCodes.ContentSeedInvalid, cuisine.Code);
    }

    [Fact]
    public async Task A_retired_entry_is_never_selected_at_random()
    {
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in Enumerable.Range(0, 60))
        {
            var seed = await SeedAsync(WorkspaceA, new ContentSeedQueryViewModel { Token = $"token-{index}" });
            chosen.Add(seed.Cuisine!.Key);
            chosen.Add(seed.PhotographyStyle!.Key);
            chosen.Add(seed.Occasion!.Key);
            chosen.Add(seed.Channel!.Key);
        }

        Assert.DoesNotContain("retired-fusion", chosen);
        Assert.DoesNotContain("retired-polaroid", chosen);
        Assert.DoesNotContain("retired-fondue-night", chosen);
        Assert.DoesNotContain("retired-vine", chosen);
    }

    [Fact]
    public async Task An_empty_catalogue_yields_a_seed_with_fewer_parts()
    {
        // The generator degrades rather than refusing. Discovered for real: the API test host does not seed
        // reference data, and the first endpoint test got a seed with three facets missing rather than an error.
        await using (var scope = ScopeFor(WorkspaceA))
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.Cuisines.RemoveRange(await db.Cuisines.ToListAsync(Ct));
            await db.SaveChangesAsync(Ct);
        }

        var seed = await SeedAsync(WorkspaceA, new ContentSeedQueryViewModel { Token = "spring-bakes" });

        Assert.Null(seed.Cuisine);
        Assert.NotNull(seed.DishType);
        Assert.NotEmpty(seed.Description);

        // And the sentence still reads: the subject falls back to the dish type alone.
        Assert.StartsWith("Develop a ", seed.Description);
    }

    [Fact]
    public async Task A_day_that_is_not_a_day_is_refused()
    {
        var error = await RefusalAsync(WorkspaceA, new ContentSeedQueryViewModel { Day = (DayOfWeek)9 });

        Assert.Equal(ContentErrorCodes.ContentSeedInvalid, error.Code);
        Assert.Contains("day", error.FieldErrors.Keys);
    }

    [Fact]
    public async Task A_safety_critical_method_carries_its_caution()
    {
        var seed = await SeedAsync(WorkspaceA, new ContentSeedQueryViewModel { Method = "ferment" });

        Assert.Equal("ferment", seed.Method!.Key);
        Assert.True(seed.Method.RequiresSafetyCaution);

        // And an ordinary one says only that no caution was attached, never that it is safe.
        var ordinary = await SeedAsync(WorkspaceA, new ContentSeedQueryViewModel { Method = "bake" });
        Assert.False(ordinary.Method!.RequiresSafetyCaution);
    }

    [Fact]
    public async Task The_caution_is_in_the_sentence_a_creator_reads_not_only_in_the_flag()
    {
        // The defect this test exists for: the description is what gets read, copied and shared, so a caution that
        // lived only in the structured flag beside it would reach nobody if a client forgot to render the flag.
        var flagged = await SeedAsync(WorkspaceA, new ContentSeedQueryViewModel { Method = "ferment" });

        Assert.Contains("Follow tested, authoritative guidance", flagged.Description);

        // And it says where to get guidance rather than that anything is safe. Checked as "is safe" and "safely"
        // rather than the bare substring, because the caution legitimately contains the words "food-safety".
        Assert.DoesNotContain("is safe", flagged.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("safely", flagged.Description, StringComparison.OrdinalIgnoreCase);

        var ordinary = await SeedAsync(WorkspaceA, new ContentSeedQueryViewModel { Method = "bake" });
        Assert.DoesNotContain("Follow tested, authoritative guidance", ordinary.Description);
    }

    [Fact]
    public async Task No_randomly_drawn_safety_critical_method_ever_arrives_without_its_caution()
    {
        // The random path is the one with no creator intent behind it, so it is the one worth sweeping.
        var flaggedDraws = 0;

        foreach (var index in Enumerable.Range(0, 120))
        {
            var seed = await SeedAsync(WorkspaceA, new ContentSeedQueryViewModel { Token = $"token-{index}" });

            if (seed.Method!.RequiresSafetyCaution)
            {
                flaggedDraws++;
                Assert.Contains("Follow tested, authoritative guidance", seed.Description);
            }
            else
            {
                Assert.DoesNotContain("Follow tested, authoritative guidance", seed.Description);
            }
        }

        // One of this fixture's three techniques is flagged, so the sweep must actually have hit some; a sweep that
        // drew none would pass the loop above while proving nothing.
        Assert.True(flaggedDraws > 0, "No random draw landed on a flagged technique, so nothing was proved.");
    }

    [Fact]
    public async Task A_workspace_with_no_theme_on_the_chosen_day_gets_a_day_and_no_theme()
    {
        // The degrade path 12.1 asked for: most days have no theme, and that is normal rather than an error.
        var seed = await SeedAsync(WorkspaceA, new ContentSeedQueryViewModel { Day = DayOfWeek.Wednesday });

        Assert.Equal(DayOfWeek.Wednesday, seed.Day.Day);
        Assert.Null(seed.Day.Theme);
        Assert.Contains("Wednesday", seed.Description);
    }

    [Fact]
    public async Task The_workspaces_own_theme_is_used_for_the_chosen_day()
    {
        await ReplaceWeekAsync(WorkspaceA, DayOfWeek.Tuesday, "taco-tuesday", "Taco Tuesday");

        var seed = await SeedAsync(WorkspaceA, new ContentSeedQueryViewModel { Day = DayOfWeek.Tuesday });

        Assert.Equal("taco-tuesday", seed.Day.Theme!.Key);
        Assert.Equal("Taco Tuesday", seed.Day.Theme.DisplayName);
        Assert.Contains("Taco Tuesday", seed.Description);
    }

    [Fact]
    public async Task A_retired_theme_is_not_used()
    {
        await ReplaceWeekAsync(WorkspaceA, DayOfWeek.Tuesday, "taco-tuesday", "Taco Tuesday");
        await ClearWeekAsync(WorkspaceA);

        var seed = await SeedAsync(WorkspaceA, new ContentSeedQueryViewModel { Day = DayOfWeek.Tuesday });

        Assert.Null(seed.Day.Theme);
    }

    [Fact]
    public async Task One_workspaces_theme_never_reaches_the_others_seed()
    {
        await ReplaceWeekAsync(WorkspaceB, DayOfWeek.Tuesday, "taco-tuesday", "B's Taco Tuesday");

        var fromA = await SeedAsync(WorkspaceA, new ContentSeedQueryViewModel { Day = DayOfWeek.Tuesday });
        var fromB = await SeedAsync(WorkspaceB, new ContentSeedQueryViewModel { Day = DayOfWeek.Tuesday });

        Assert.Null(fromA.Day.Theme);
        Assert.Equal("B's Taco Tuesday", fromB.Day.Theme!.DisplayName);
        Assert.DoesNotContain("Taco", fromA.Description);
    }

    [Fact]
    public async Task Declared_brand_channels_narrow_the_channel_facet()
    {
        // The one grounded weight: a workspace that said where it publishes is not offered a ninth channel.
        await SeedBrandAsync(WorkspaceA, "blog", "instagram");

        var chosen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in Enumerable.Range(0, 40))
        {
            chosen.Add((await SeedAsync(WorkspaceA, new ContentSeedQueryViewModel { Token = $"token-{index}" })).Channel!.Key);
        }

        Assert.Equal(["blog", "instagram"], chosen.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_workspace_with_no_brand_profile_draws_from_the_whole_catalogue()
    {
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in Enumerable.Range(0, 60))
        {
            chosen.Add((await SeedAsync(WorkspaceB, new ContentSeedQueryViewModel { Token = $"token-{index}" })).Channel!.Key);
        }

        // All three active channels, and never the retired one.
        Assert.Equal(["blog", "instagram", "tiktok"], chosen.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task One_workspaces_declared_channels_do_not_narrow_the_others()
    {
        // The brand profile is workspace-owned, so A declaring two channels must leave B drawing from all of them.
        await SeedBrandAsync(WorkspaceA, "blog");

        var fromA = new HashSet<string>(StringComparer.Ordinal);
        var fromB = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in Enumerable.Range(0, 60))
        {
            var token = new ContentSeedQueryViewModel { Token = $"token-{index}" };
            fromA.Add((await SeedAsync(WorkspaceA, token)).Channel!.Key);
            fromB.Add((await SeedAsync(WorkspaceB, token)).Channel!.Key);
        }

        Assert.Equal(["blog"], fromA);
        Assert.Equal(["blog", "instagram", "tiktok"], fromB.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_channel_a_brand_declared_but_the_catalogue_retired_is_not_offered()
    {
        await SeedBrandAsync(WorkspaceB, "retired-vine", "tiktok");

        var chosen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in Enumerable.Range(0, 30))
        {
            chosen.Add((await SeedAsync(WorkspaceB, new ContentSeedQueryViewModel { Token = $"token-{index}" })).Channel!.Key);
        }

        Assert.Equal(["tiktok"], chosen);
    }

    [Fact]
    public async Task A_seed_writes_nothing()
    {
        await SeedAsync(WorkspaceA);
        await SeedAsync(WorkspaceA, new ContentSeedQueryViewModel { Token = "spring-bakes", Cuisine = "thai" });
        await RefusalAsync(WorkspaceA, new ContentSeedQueryViewModel { Cuisine = "klingon" });

        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        // No persistence and no audit: reading an idea is not an event.
        Assert.Empty(await db.AuditLogs.ToListAsync(Ct));
        Assert.Empty(await db.IdempotencyRecords.ToListAsync(Ct));
        Assert.Empty(await db.OutboxMessages.ToListAsync(Ct));
        Assert.Empty(await db.ContentProposals.ToListAsync(Ct));

        // And no AI call: an operation row is what every generation leaves behind, so none means none happened.
        Assert.Empty(await db.AiOperations.ToListAsync(Ct));
        Assert.Empty(await db.AiProposals.ToListAsync(Ct));
        Assert.Empty(await db.AiExecutionMetadata.ToListAsync(Ct));
    }

    [Fact]
    public async Task The_description_names_the_facets_and_nothing_else()
    {
        await ReplaceWeekAsync(WorkspaceA, DayOfWeek.Friday, "fakeaway-friday", "Fakeaway Friday");

        var seed = await SeedAsync(WorkspaceA, new ContentSeedQueryViewModel
        {
            Cuisine = "thai",
            DishType = "main-course",
            Method = "stir-fry",
            PhotographyStyle = "overhead-flat-lay",
            Channel = "instagram",
            Day = DayOfWeek.Friday,
            Occasion = "weeknight",
        });

        Assert.Equal(
            "Develop a Thai main course using the stir-fry method, with weeknight in mind. "
                + "Shot: Overhead flat-lay. Publish for Fakeaway Friday on Instagram.",
            seed.Description);
    }

    private async Task ReplaceWeekAsync(Guid workspaceId, DayOfWeek day, string key, string displayName)
    {
        await using var scope = ScopeFor(workspaceId);
        var result = await scope.ServiceProvider.GetRequiredService<IWorkspaceWeeklyThemeBusiness>().ReplaceAsync(
            Actor,
            new ReplaceWeeklyThemesViewModel
            {
                Themes = [new WeeklyThemeInput { Day = day, Key = key, DisplayName = displayName }],
            },
            Ct);

        Assert.True(result.Succeeded, result.Error?.Code);
    }

    private async Task ClearWeekAsync(Guid workspaceId)
    {
        await using var scope = ScopeFor(workspaceId);
        var result = await scope.ServiceProvider.GetRequiredService<IWorkspaceWeeklyThemeBusiness>()
            .ReplaceAsync(Actor, new ReplaceWeeklyThemesViewModel { Themes = [] }, Ct);

        Assert.True(result.Succeeded, result.Error?.Code);
    }

    private async Task SeedBrandAsync(Guid workspaceId, params string[] channelKeys)
    {
        await using var scope = ScopeFor(workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var profile = new BrandProfile
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            BrandName = "Sam's Kitchen",
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = Guid.NewGuid(),
            UpdatedByMembershipId = Guid.NewGuid(),
            ChannelDefaults = [.. channelKeys.Select((key, index) => new BrandChannelDefault
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                ChannelKey = key,
                SortOrder = index,
            })],
        };

        db.BrandProfiles.Add(profile);
        await db.SaveChangesAsync(Ct);
    }
}
