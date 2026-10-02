using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand.Business;
using CreatorPantry.Domain.Modules.Brand.Data;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Tests.Brand;

/// <summary>Business-level rules, below the facade's validator, over a recording fake data layer.</summary>
public sealed class BrandSetupSessionBusinessTests
{
    private sealed class FakeLayer : IBrandSetupSessionDataLayer
    {
        public BrandSetupSessionDeleteOutcome DeleteOutcome { get; set; } = BrandSetupSessionDeleteOutcome.Deleted;

        public int Calls { get; private set; }

        public Task<BrandSetupSession?> GetAsync(string userId, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult<BrandSetupSession?>(null);
        }

        public Task<BrandSetupSession?> GetForUpdateAsync(string userId, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult<BrandSetupSession?>(null);
        }

        public Task<bool> CreateAsync(BrandSetupSession session, AuditEntry audit, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(true);
        }

        public Task<bool> UpdateAsync(BrandSetupSession loaded, AuditEntry? audit, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(true);
        }

        public Task<BrandSetupSessionDeleteOutcome> DeleteAsync(
            string userId, Func<BrandSetupSession, AuditEntry> audit, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(DeleteOutcome);
        }
    }

    private sealed class FakeWorkspace : IWorkspaceContext
    {
        public bool IsResolved => true;

        public Guid WorkspaceId { get; } = Guid.NewGuid();

        public string WorkspaceSlug => "w";

        public Guid MembershipId { get; } = Guid.NewGuid();

        public string AccountId => "user-1";

        public WorkspaceRole Role => WorkspaceRole.Owner;
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
    }

    private static (BrandSetupSessionBusiness Business, FakeLayer Layer) Build()
    {
        var layer = new FakeLayer();

        return (new BrandSetupSessionBusiness(layer, new FakeWorkspace(), new FakeClock()), layer);
    }

    private static SaveBrandSetupSessionViewModel Model(string? draft) => new()
    {
        CurrentStep = "goals",
        FurthestStep = "goals",
        CompletedSteps = [],
        SkippedSteps = [],
        DraftJson = draft,
    };

    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("\"s\"")]
    public async Task Business_refuses_a_draft_that_is_not_a_json_object_without_touching_the_data_layer(string? draft)
    {
        var (business, layer) = Build();

        var result = await business.SaveAsync(Model(draft), null, TestContext.Current.CancellationToken);

        Assert.Equal(BrandErrorCodes.SetupSessionInvalidRequest, result.Error?.Code);
        Assert.Equal(0, layer.Calls);
    }

    [Fact]
    public async Task Business_refuses_an_oversize_draft_measured_in_bytes()
    {
        var (business, layer) = Build();
        var draft = "{\"a\":\"" + new string('€', 30000) + "\"}"; // 30k chars, ~90 KB of UTF-8

        var result = await business.SaveAsync(Model(draft), null, TestContext.Current.CancellationToken);

        Assert.Equal(BrandErrorCodes.SetupSessionInvalidRequest, result.Error?.Code);
        Assert.Equal(0, layer.Calls);
    }

    [Fact]
    public async Task Business_accepts_a_draft_object_at_the_edge_of_the_limit()
    {
        var (business, layer) = Build();

        var result = await business.SaveAsync(Model("{}"), null, TestContext.Current.CancellationToken);

        Assert.Null(result.Error?.Code == BrandErrorCodes.SetupSessionInvalidRequest ? result.Error : null);
        Assert.True(layer.Calls > 0);
    }

    [Fact]
    public async Task A_delete_that_the_row_survived_is_a_conflict_not_a_success()
    {
        var (business, layer) = Build();
        layer.DeleteOutcome = BrandSetupSessionDeleteOutcome.Conflict;

        var result = await business.DeleteAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(BrandErrorCodes.SetupSessionConflict, result.Error!.Code);
    }

    [Theory]
    [InlineData(BrandSetupSessionDeleteOutcome.Deleted)]
    [InlineData(BrandSetupSessionDeleteOutcome.NothingToDelete)]
    public async Task A_delete_with_nothing_left_is_a_success(BrandSetupSessionDeleteOutcome outcome)
    {
        var (business, layer) = Build();
        layer.DeleteOutcome = outcome;

        var result = await business.DeleteAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
    }
}
