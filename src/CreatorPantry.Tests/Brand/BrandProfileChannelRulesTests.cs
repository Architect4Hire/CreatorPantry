using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Patching;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand;
using CreatorPantry.Domain.Modules.Media;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// What becomes of channel keys a profile already stores when the catalogue retires the channel (12.1): an
/// unrelated edit must keep working, and the retired channel must not be chosen again.
/// </summary>
public sealed class BrandProfileChannelRulesTests : IDisposable
{
    private static readonly Guid Workspace = Guid.NewGuid();

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    private readonly ServiceProvider _provider;

    public BrandProfileChannelRulesTests()
    {
        _connection.Open();

        // "tiktok" is retired; the rest are live.
        var catalog = new ContentChannelCatalog(
        [
            new ContentChannel("instagram", "Instagram"),
            new ContentChannel("tiktok", "TikTok", IsActive: false),
            new ContentChannel("pinterest", "Pinterest"),
        ]);

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddApplicationTime()
            .AddAudit()
            .AddIdempotency(new ConfigurationBuilder().Build())
            .AddSingleton<IContentChannelCatalog>(catalog)
            .AddBrandModule()

            // The profile facade asks the Media module whether a submitted logo is in this workspace's library
            // (12.10k), so its lookup seam has to be resolvable here as it is in every real host.
            .AddMediaModule()
            .AddLogging()
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Database.EnsureCreated();
        db.Workspaces.Add(new Workspace { Id = Workspace, Name = "W", Slug = "w", CreatedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private AsyncServiceScope NewScope()
    {
        var scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            Workspace, "w", Guid.NewGuid(), WorkspaceRole.Owner, "acct");

        return scope;
    }

    private static BrandChannelDefaultInput Channel(string key) => new() { ChannelKey = key };

    private async Task<BrandProfileServiceModel> CreateWithAsync(params string[] keys)
    {
        await using var scope = NewScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IBrandProfileFacade>().CreateAsync(
            "user",
            new CreateBrandProfileViewModel { BrandName = "B", ChannelDefaults = [.. keys.Select(Channel)] },
            null,
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);

        return outcome.Result.Value!;
    }

    [Fact]
    public async Task A_retired_channel_cannot_be_chosen_on_create()
    {
        await using var scope = NewScope();

        var outcome = await scope.ServiceProvider.GetRequiredService<IBrandProfileFacade>().CreateAsync(
            "user",
            new CreateBrandProfileViewModel { BrandName = "B", ChannelDefaults = [Channel("tiktok")] },
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(BrandErrorCodes.InvalidRequest, outcome.Result.Error?.Code);
        Assert.Contains("channelDefaults[0].ChannelKey", outcome.Result.Error!.FieldErrors.Keys);
    }

    [Fact]
    public async Task An_unknown_channel_is_refused_with_a_field_error()
    {
        await using var scope = NewScope();

        var outcome = await scope.ServiceProvider.GetRequiredService<IBrandProfileFacade>().CreateAsync(
            "user",
            new CreateBrandProfileViewModel { BrandName = "B", ChannelDefaults = [Channel("myspace")] },
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(BrandErrorCodes.InvalidRequest, outcome.Result.Error?.Code);
    }

    [Fact]
    public async Task A_retired_channel_already_stored_survives_an_unrelated_and_a_resubmitted_edit()
    {
        // Stored while it was live: write the row directly, since the catalogue above already retires it.
        await using (var scope = NewScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            var now = DateTimeOffset.UtcNow;
            var profile = new CreatorPantry.Domain.Modules.Brand.Data.Entities.BrandProfile
            {
                Id = Guid.NewGuid(), BrandName = "B", CreatedAt = now, UpdatedAt = now,
                CreatedByMembershipId = Guid.NewGuid(), UpdatedByMembershipId = Guid.NewGuid(),
            };
            profile.ChannelDefaults.Add(new CreatorPantry.Domain.Modules.Brand.Data.Entities.BrandChannelDefault
            {
                Id = Guid.NewGuid(), BrandProfileId = profile.Id, ChannelKey = "tiktok", SortOrder = 0,
            });
            db.BrandProfiles.Add(profile);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        string token;
        await using (var scope = NewScope())
        {
            token = (await scope.ServiceProvider.GetRequiredService<IBrandProfileFacade>()
                .GetAsync(TestContext.Current.CancellationToken)).Value!.ConcurrencyToken;
        }

        await using var edit = NewScope();
        var facade = edit.ServiceProvider.GetRequiredService<IBrandProfileFacade>();

        // An unrelated edit, and then the same list resubmitted with the retired key still in it.
        var unrelated = await facade.UpdateAsync(
            "user",
            new UpdateBrandProfileViewModel { ExpectedConcurrencyToken = token, Locale = PatchField<string?>.Submitted("en-US") },
            null,
            TestContext.Current.CancellationToken);
        Assert.True(unrelated.Result.Succeeded, unrelated.Result.Error?.Message);

        var resubmitted = await facade.UpdateAsync(
            "user",
            new UpdateBrandProfileViewModel
            {
                ExpectedConcurrencyToken = unrelated.Result.Value!.ConcurrencyToken,
                ChannelDefaults = PatchField<IReadOnlyList<BrandChannelDefaultInput?>?>.Submitted(
                    [Channel("tiktok"), Channel("instagram")]),
            },
            null,
            TestContext.Current.CancellationToken);
        Assert.True(resubmitted.Result.Succeeded, resubmitted.Result.Error?.Message);
        Assert.Equal(["tiktok", "instagram"], resubmitted.Result.Value!.ChannelDefaults.Select(c => c.ChannelKey));
    }

    [Fact]
    public async Task A_retired_channel_not_already_stored_is_refused_on_update()
    {
        var created = await CreateWithAsync("instagram");

        await using var scope = NewScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IBrandProfileFacade>().UpdateAsync(
            "user",
            new UpdateBrandProfileViewModel
            {
                ExpectedConcurrencyToken = created.ConcurrencyToken,
                ChannelDefaults = PatchField<IReadOnlyList<BrandChannelDefaultInput?>?>.Submitted(
                    [Channel("instagram"), Channel("tiktok")]),
            },
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(BrandErrorCodes.InvalidRequest, outcome.Result.Error?.Code);
    }
}
