using CreatorPantry.Domain.Data;
using CreatorPantry.Domain.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Tenancy;

public class WorkspaceContextTests
{
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly Guid MembershipId = Guid.NewGuid();
    private const string WorkspaceSlug = "sams-kitchen";

    [Fact]
    public void Unresolved_context_reports_not_resolved()
    {
        var context = new WorkspaceContextUnderTest();

        Assert.False(((IWorkspaceContext)context).IsResolved);
    }

    [Theory]
    [MemberData(nameof(Accessors))]
    public void Unresolved_context_throws_on_every_read(Func<IWorkspaceContext, object> access)
    {
        var context = new WorkspaceContextUnderTest();

        Assert.Throws<InvalidOperationException>(() => access(context));
    }

    [Fact]
    public void Resolved_context_returns_exactly_what_was_resolved()
    {
        var context = new WorkspaceContextUnderTest();

        context.Resolver.Resolve(WorkspaceId, WorkspaceSlug, MembershipId, WorkspaceRole.Editor);

        IWorkspaceContext resolved = context;
        Assert.True(resolved.IsResolved);
        Assert.Equal(WorkspaceId, resolved.WorkspaceId);
        Assert.Equal(WorkspaceSlug, resolved.WorkspaceSlug);
        Assert.Equal(MembershipId, resolved.MembershipId);
        Assert.Equal(WorkspaceRole.Editor, resolved.Role);
    }

    [Fact]
    public void A_second_resolution_throws_even_with_identical_values()
    {
        var context = new WorkspaceContextUnderTest();
        context.Resolver.Resolve(WorkspaceId, WorkspaceSlug, MembershipId, WorkspaceRole.Owner);

        Assert.Throws<InvalidOperationException>(
            () => context.Resolver.Resolve(WorkspaceId, WorkspaceSlug, MembershipId, WorkspaceRole.Owner));
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "sams-kitchen", "11111111-1111-1111-1111-111111111111")]
    [InlineData("11111111-1111-1111-1111-111111111111", "", "22222222-2222-2222-2222-222222222222")]
    [InlineData("11111111-1111-1111-1111-111111111111", "   ", "22222222-2222-2222-2222-222222222222")]
    [InlineData("11111111-1111-1111-1111-111111111111", "sams-kitchen", "00000000-0000-0000-0000-000000000000")]
    public void Resolve_rejects_empty_or_blank_arguments(string workspaceId, string workspaceSlug, string membershipId)
    {
        var context = new WorkspaceContextUnderTest();

        Assert.Throws<ArgumentException>(() => context.Resolver.Resolve(
            Guid.Parse(workspaceId), workspaceSlug, Guid.Parse(membershipId), WorkspaceRole.Viewer));
        Assert.False(((IWorkspaceContext)context).IsResolved);
    }

    [Fact]
    public void Two_scopes_never_share_resolved_state()
    {
        var services = new ServiceCollection().AddTenancy().BuildServiceProvider(validateScopes: true);

        using var scopeA = services.CreateScope();
        using var scopeB = services.CreateScope();

        scopeA.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>()
            .Resolve(WorkspaceId, WorkspaceSlug, MembershipId, WorkspaceRole.Owner);

        Assert.True(scopeA.ServiceProvider.GetRequiredService<IWorkspaceContext>().IsResolved);
        Assert.False(scopeB.ServiceProvider.GetRequiredService<IWorkspaceContext>().IsResolved);
    }

    [Fact]
    public void The_read_side_reflects_a_resolution_made_through_the_write_side_in_the_same_scope()
    {
        var services = new ServiceCollection().AddTenancy().BuildServiceProvider(validateScopes: true);
        using var scope = services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>()
            .Resolve(WorkspaceId, WorkspaceSlug, MembershipId, WorkspaceRole.Contributor);

        var read = scope.ServiceProvider.GetRequiredService<IWorkspaceContext>();
        Assert.True(read.IsResolved);
        Assert.Equal(WorkspaceId, read.WorkspaceId);
        Assert.Equal(WorkspaceRole.Contributor, read.Role);
    }

    [Fact]
    public void WorkspaceMembership_is_workspace_owned() =>
        Assert.IsAssignableFrom<IWorkspaceOwned>(new WorkspaceMembership());

    public static TheoryData<Func<IWorkspaceContext, object>> Accessors() => new()
    {
        context => context.WorkspaceId,
        context => context.WorkspaceSlug,
        context => context.MembershipId,
        context => context.Role,
    };

    /// <summary>
    /// A single instance exposed through both interfaces, the same way DI wires one <c>WorkspaceContext</c>
    /// to both per scope. Avoids depending on the internal concrete type directly from the assertions.
    /// </summary>
    private sealed class WorkspaceContextUnderTest : IWorkspaceContext
    {
        private readonly IWorkspaceContext _read;

        public WorkspaceContextUnderTest()
        {
            var services = new ServiceCollection().AddTenancy().BuildServiceProvider();
            _read = services.GetRequiredService<IWorkspaceContext>();
            Resolver = services.GetRequiredService<IWorkspaceContextResolver>();
        }

        public IWorkspaceContextResolver Resolver { get; }

        public bool IsResolved => _read.IsResolved;
        public Guid WorkspaceId => _read.WorkspaceId;
        public string WorkspaceSlug => _read.WorkspaceSlug;
        public Guid MembershipId => _read.MembershipId;
        public WorkspaceRole Role => _read.Role;
    }
}
