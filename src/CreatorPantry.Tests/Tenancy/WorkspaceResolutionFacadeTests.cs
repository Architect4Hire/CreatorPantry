using CreatorPantry.Domain.Modules.Tenancy.Business;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Tests.Tenancy;

public class WorkspaceResolutionFacadeTests
{
    private const string UserId = "u1";

    private readonly FakeWorkspaceBusiness _business = new();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Sams-Kitchen")] // uppercase is not a valid slug
    [InlineData("-sams-kitchen")] // leading hyphen
    [InlineData("sams--kitchen")] // doubled hyphen
    public async Task Malformed_slug_fails_without_calling_business(string slug)
    {
        var result = await CreateFacade().ResolveAsync(UserId, new ResolveWorkspaceViewModel(slug), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(TenancyErrorCodes.WorkspaceResolutionInvalidRequest, result.Error!.Code);
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task Well_formed_slug_delegates_to_business()
    {
        var expected = OperationResult<ResolvedWorkspaceServiceModel>.Success(
            new ResolvedWorkspaceServiceModel(Guid.NewGuid(), "sams-kitchen", Guid.NewGuid(), WorkspaceRole.Owner));
        _business.Result = expected;

        var result = await CreateFacade().ResolveAsync(UserId, new ResolveWorkspaceViewModel("sams-kitchen"), TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
        Assert.Equal(1, _business.Calls);
        Assert.Equal(UserId, _business.LastUserId);
    }

    [Fact]
    public async Task Missing_caller_id_throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CreateFacade().ResolveAsync(
            " ", new ResolveWorkspaceViewModel("sams-kitchen"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveForOperation_delegates_straight_to_business_with_no_shape_validation()
    {
        var workspaceId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        var expected = OperationResult<ResolvedWorkspaceServiceModel>.Success(
            new ResolvedWorkspaceServiceModel(workspaceId, "sams-kitchen", membershipId, WorkspaceRole.Viewer));
        _business.OperationResult = expected;

        var result = await CreateFacade().ResolveForOperationAsync(
            workspaceId, membershipId, TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
        Assert.Equal((workspaceId, membershipId), _business.LastOperationArgs);
    }

    /// <summary>
    /// Unlike <see cref="IWorkspaceResolutionFacade.ResolveAsync"/>, this method populates the ambient context
    /// itself -- its one caller lives in another module and may not reach <see cref="IWorkspaceContextResolver"/>
    /// directly.
    /// </summary>
    [Fact]
    public async Task A_successful_resolution_populates_the_ambient_workspace_context()
    {
        var workspaceId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        _business.OperationResult = OperationResult<ResolvedWorkspaceServiceModel>.Success(
            new ResolvedWorkspaceServiceModel(workspaceId, "sams-kitchen", membershipId, WorkspaceRole.Contributor));

        var context = new WorkspaceContext();
        await CreateFacade(context).ResolveForOperationAsync(
            workspaceId, membershipId, TestContext.Current.CancellationToken);

        Assert.True(context.IsResolved);
        Assert.Equal(workspaceId, context.WorkspaceId);
        Assert.Equal(membershipId, context.MembershipId);
        Assert.Equal(WorkspaceRole.Contributor, context.Role);
    }

    [Fact]
    public async Task A_failed_resolution_leaves_the_ambient_context_unresolved()
    {
        _business.OperationResult = OperationResult<ResolvedWorkspaceServiceModel>.Failure(
            new OperationError(TenancyErrorCodes.WorkspaceNotFound, "not found", new Dictionary<string, string[]>()));

        var context = new WorkspaceContext();
        await CreateFacade(context).ResolveForOperationAsync(
            Guid.NewGuid(), Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.False(context.IsResolved);
    }

    private WorkspaceResolutionFacade CreateFacade(WorkspaceContext? contextResolver = null) =>
        new(new ResolveWorkspaceViewModelValidator(), _business, contextResolver ?? new WorkspaceContext());

    private sealed class FakeWorkspaceBusiness : IWorkspaceBusiness
    {
        public OperationResult<ResolvedWorkspaceServiceModel> Result { get; set; } =
            OperationResult<ResolvedWorkspaceServiceModel>.Failure(new OperationError("unused", "unused", new Dictionary<string, string[]>()));

        public int Calls { get; private set; }

        public string? LastUserId { get; private set; }

        public Task<OperationResult<ResolvedWorkspaceServiceModel>> ResolveAsync(
            string userId, ResolveWorkspaceViewModel model, CancellationToken cancellationToken)
        {
            Calls++;
            LastUserId = userId;
            return Task.FromResult(Result);
        }

        public OperationResult<ResolvedWorkspaceServiceModel> OperationResult { get; set; } =
            OperationResult<ResolvedWorkspaceServiceModel>.Failure(new OperationError("unused", "unused", new Dictionary<string, string[]>()));

        public (Guid WorkspaceId, Guid MembershipId)? LastOperationArgs { get; private set; }

        public Task<OperationResult<ResolvedWorkspaceServiceModel>> ResolveForOperationAsync(
            Guid workspaceId, Guid membershipId, CancellationToken cancellationToken)
        {
            LastOperationArgs = (workspaceId, membershipId);
            return Task.FromResult(OperationResult);
        }

        public Task<IReadOnlyList<MyWorkspaceMembershipServiceModel>> GetMyMembershipsAsync(string userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by WorkspaceResolutionFacadeTests.");

        public Task<IReadOnlyDictionary<Guid, string>> FindMemberDisplayNamesAsync(
            IReadOnlyCollection<Guid> membershipIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by WorkspaceResolutionFacadeTests.");

        public Task<OperationResult<WorkspaceServiceModel>> CreateAsync(
            string userId, CreateWorkspaceViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by WorkspaceResolutionFacadeTests.");

        public Task<WorkspaceServiceModel> GetCurrentAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by WorkspaceResolutionFacadeTests.");

        public Task<WorkspaceServiceModel> RenameCurrentAsync(UpdateWorkspaceViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by WorkspaceResolutionFacadeTests.");
    }
}
