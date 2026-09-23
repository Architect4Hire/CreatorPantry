using CreatorPantry.Domain.Modules.Tenancy.Business;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Tests.Auth;

namespace CreatorPantry.Tests.Tenancy;

public class WorkspaceBusinessTests
{
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly Guid MembershipId = Guid.NewGuid();
    private const string Slug = "sams-kitchen";
    private const string UserId = "u1";

    private readonly FakeWorkspaceDataLayer _dataLayer = new();

    [Fact]
    public async Task Member_with_an_active_membership_resolves()
    {
        _dataLayer.Lookup = new WorkspaceMembershipLookup(
            new WorkspaceSummary(WorkspaceId, Slug),
            new MembershipSummary(MembershipId, WorkspaceRole.Editor, WorkspaceMembershipStatus.Active));

        var result = await CreateBusiness().ResolveAsync(UserId, new ResolveWorkspaceViewModel(Slug), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(WorkspaceId, result.Value!.WorkspaceId);
        Assert.Equal(Slug, result.Value.WorkspaceSlug);
        Assert.Equal(MembershipId, result.Value.MembershipId);
        Assert.Equal(WorkspaceRole.Editor, result.Value.Role);
    }

    [Fact]
    public async Task Nonmember_with_no_membership_row_is_not_found()
    {
        _dataLayer.Lookup = new WorkspaceMembershipLookup(new WorkspaceSummary(WorkspaceId, Slug), null);

        var result = await CreateBusiness().ResolveAsync(UserId, new ResolveWorkspaceViewModel(Slug), TestContext.Current.CancellationToken);

        AssertNotFound(result);
    }

    [Theory]
    [InlineData(WorkspaceMembershipStatus.Invited)]
    [InlineData(WorkspaceMembershipStatus.Removed)]
    public async Task Inactive_membership_is_not_found(WorkspaceMembershipStatus status)
    {
        _dataLayer.Lookup = new WorkspaceMembershipLookup(
            new WorkspaceSummary(WorkspaceId, Slug),
            new MembershipSummary(MembershipId, WorkspaceRole.Owner, status));

        var result = await CreateBusiness().ResolveAsync(UserId, new ResolveWorkspaceViewModel(Slug), TestContext.Current.CancellationToken);

        AssertNotFound(result);
    }

    [Fact]
    public async Task Unknown_workspace_is_not_found()
    {
        _dataLayer.Lookup = new WorkspaceMembershipLookup(null, null);

        var result = await CreateBusiness().ResolveAsync(UserId, new ResolveWorkspaceViewModel(Slug), TestContext.Current.CancellationToken);

        AssertNotFound(result);
    }

    [Fact]
    public async Task Unknown_nonmember_and_inactive_cases_produce_the_identical_failure()
    {
        var business = CreateBusiness();

        _dataLayer.Lookup = new WorkspaceMembershipLookup(null, null);
        var unknown = await business.ResolveAsync(UserId, new ResolveWorkspaceViewModel(Slug), TestContext.Current.CancellationToken);

        _dataLayer.Lookup = new WorkspaceMembershipLookup(new WorkspaceSummary(WorkspaceId, Slug), null);
        var nonmember = await business.ResolveAsync(UserId, new ResolveWorkspaceViewModel(Slug), TestContext.Current.CancellationToken);

        _dataLayer.Lookup = new WorkspaceMembershipLookup(
            new WorkspaceSummary(WorkspaceId, Slug), new MembershipSummary(MembershipId, WorkspaceRole.Viewer, WorkspaceMembershipStatus.Removed));
        var inactive = await business.ResolveAsync(UserId, new ResolveWorkspaceViewModel(Slug), TestContext.Current.CancellationToken);

        Assert.Equal(unknown.Error!.Code, nonmember.Error!.Code);
        Assert.Equal(unknown.Error.Message, nonmember.Error.Message);
        Assert.Equal(unknown.Error.Code, inactive.Error!.Code);
        Assert.Equal(unknown.Error.Message, inactive.Error.Message);
    }

    [Fact]
    public async Task The_slug_is_trimmed_before_lookup()
    {
        _dataLayer.Lookup = new WorkspaceMembershipLookup(null, null);

        await CreateBusiness().ResolveAsync(UserId, new ResolveWorkspaceViewModel("  sams-kitchen  "), TestContext.Current.CancellationToken);

        Assert.Equal((Slug, UserId), _dataLayer.Calls.Single());
    }

    private static void AssertNotFound(OperationResult<ResolvedWorkspaceServiceModel> result)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(TenancyErrorCodes.WorkspaceNotFound, result.Error!.Code);
        Assert.Empty(result.Error.FieldErrors);
    }

    [Fact]
    public async Task GetMyMemberships_maps_every_row()
    {
        var otherWorkspaceId = Guid.NewGuid();
        _dataLayer.Memberships =
        [
            new WorkspaceMembershipRow(new WorkspaceRecord(WorkspaceId, "Sam's Kitchen", Slug, SqliteAuthServices.Now), MembershipId, WorkspaceRole.Owner, WorkspaceMembershipStatus.Active),
            new WorkspaceMembershipRow(new WorkspaceRecord(otherWorkspaceId, "Other", "other", SqliteAuthServices.Now), Guid.NewGuid(), WorkspaceRole.Viewer, WorkspaceMembershipStatus.Invited),
        ];

        var memberships = await CreateBusiness().GetMyMembershipsAsync(UserId, TestContext.Current.CancellationToken);

        Assert.Equal(2, memberships.Count);
        var mine = memberships.Single(m => m.WorkspaceId == WorkspaceId);
        Assert.Equal(Slug, mine.WorkspaceSlug);
        Assert.Equal("Sam's Kitchen", mine.WorkspaceName);
        Assert.Equal(MembershipId, mine.MembershipId);
        Assert.Equal(WorkspaceRole.Owner, mine.Role);
        Assert.Equal(WorkspaceMembershipStatus.Active, mine.Status);
    }

    [Fact]
    public async Task Create_derives_a_kebab_case_slug_from_the_name()
    {
        var result = await CreateBusiness().CreateAsync(UserId, new CreateWorkspaceViewModel("Sam's   Kitchen!!"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("sams-kitchen", _dataLayer.CreateCalls.Single().Slug);
    }

    [Fact]
    public async Task Create_appends_a_numeric_suffix_when_the_base_slug_is_taken()
    {
        _dataLayer.ExistingSlugs.Add("sams-kitchen");
        _dataLayer.ExistingSlugs.Add("sams-kitchen-2");

        var result = await CreateBusiness().CreateAsync(UserId, new CreateWorkspaceViewModel("Sam's Kitchen"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("sams-kitchen-3", _dataLayer.CreateCalls.Single().Slug);
    }

    [Fact]
    public async Task Create_fails_when_every_candidate_slug_is_taken()
    {
        for (var attempt = 0; attempt < 25; attempt++)
        {
            _dataLayer.ExistingSlugs.Add(attempt == 0 ? "sams-kitchen" : $"sams-kitchen-{attempt + 1}");
        }

        var result = await CreateBusiness().CreateAsync(UserId, new CreateWorkspaceViewModel("Sam's Kitchen"), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(TenancyErrorCodes.WorkspaceSlugUnavailable, result.Error!.Code);
        Assert.Empty(_dataLayer.CreateCalls);
    }

    [Fact]
    public async Task Create_stamps_the_caller_as_the_new_workspaces_owner()
    {
        var result = await CreateBusiness().CreateAsync(UserId, new CreateWorkspaceViewModel("Sam's Kitchen"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(WorkspaceRole.Owner, result.Value!.Role);
        Assert.Equal(UserId, _dataLayer.CreateCalls.Single().OwnerUserId);
    }

    [Fact]
    public async Task GetCurrent_reads_from_the_resolved_workspace_context()
    {
        _dataLayer.WorkspaceById = new WorkspaceRecord(WorkspaceId, "Sam's Kitchen", Slug, SqliteAuthServices.Now);
        var context = ResolvedContext();

        var result = await CreateBusiness(context).GetCurrentAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceId, result.WorkspaceId);
        Assert.Equal("Sam's Kitchen", result.Name);
        Assert.Equal(MembershipId, result.MembershipId);
        Assert.Equal(WorkspaceRole.Editor, result.Role);
    }

    [Fact]
    public async Task RenameCurrent_renames_the_resolved_workspace_not_a_client_supplied_id()
    {
        var context = ResolvedContext();

        var result = await CreateBusiness(context).RenameCurrentAsync(new UpdateWorkspaceViewModel("New Name"), TestContext.Current.CancellationToken);

        Assert.Equal((WorkspaceId, "New Name"), _dataLayer.RenameCalls.Single());
        Assert.Equal("New Name", result.Name);
        Assert.Equal(WorkspaceRole.Editor, result.Role);
    }

    private static WorkspaceContext ResolvedContext()
    {
        var context = new WorkspaceContext();
        context.Resolve(WorkspaceId, Slug, MembershipId, WorkspaceRole.Editor);
        return context;
    }

    private WorkspaceBusiness CreateBusiness(IWorkspaceContext? workspaceContext = null) =>
        new(_dataLayer, new FixedClock(), workspaceContext ?? new WorkspaceContext());
}
