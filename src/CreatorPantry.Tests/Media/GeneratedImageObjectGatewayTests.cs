using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Gateways;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// The staging object gateway's own rules: keys come from the resolved workspace, and a key that is not
/// this workspace's is refused before storage is touched.
/// </summary>
/// <remarks>
/// Separate from the worker tests because this is the boundary itself rather than the job that uses it.
/// <c>GeneratedImageObjectKeyTests</c> covers the grammar; this covers what the gateway does with it.
/// </remarks>
public sealed class GeneratedImageObjectGatewayTests
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly InMemoryPrivateObjectStore _store = new();

    [Fact]
    public async Task A_write_lands_under_the_resolved_workspaces_own_prefix()
    {
        var operationId = Guid.NewGuid();

        var write = await GatewayFor(WorkspaceA).PutVariantAsync(
            operationId, 1, BrandSourceSampleFiles.Png(), "image/png", TestContext.Current.CancellationToken);

        Assert.Equal(GeneratedImageObjectWriteOutcome.Stored, write.Outcome);
        Assert.Equal(GeneratedImageObjectKey.For(WorkspaceA, operationId, 1), write.Object!.ObjectKey);
        Assert.StartsWith("sha256:", write.Object.ContentChecksum, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_write_to_one_key_never_overwrites_the_first()
    {
        var operationId = Guid.NewGuid();
        var gateway = GatewayFor(WorkspaceA);

        await gateway.PutVariantAsync(
            operationId, 0, BrandSourceSampleFiles.Png(), "image/png", TestContext.Current.CancellationToken);

        var second = await gateway.PutVariantAsync(
            operationId, 0, BrandSourceSampleFiles.Jpeg(), "image/jpeg", TestContext.Current.CancellationToken);

        Assert.Equal(GeneratedImageObjectWriteOutcome.AlreadyExists, second.Outcome);
        Assert.Single(_store.Keys);
    }

    [Fact]
    public async Task Bytes_past_the_limit_store_nothing()
    {
        var write = await GatewayFor(WorkspaceA).PutVariantAsync(
            Guid.NewGuid(),
            0,
            new byte[MediaPolicy.ImageMaxBytes + 1],
            "image/png",
            TestContext.Current.CancellationToken);

        Assert.Equal(GeneratedImageObjectWriteOutcome.TooLarge, write.Outcome);
        Assert.Empty(_store.Keys);
    }

    [Fact]
    public async Task Unreachable_storage_is_a_result_rather_than_an_exception()
    {
        _store.Unavailable = true;

        var write = await GatewayFor(WorkspaceA).PutVariantAsync(
            Guid.NewGuid(), 0, BrandSourceSampleFiles.Png(), "image/png", TestContext.Current.CancellationToken);

        Assert.Equal(GeneratedImageObjectWriteOutcome.Unavailable, write.Outcome);
    }

    /// <summary>The audit's gap: the workspace comparison on delete had no test of its own.</summary>
    [Fact]
    public async Task A_neighbours_key_is_refused_and_its_object_is_left_alone()
    {
        var operationId = Guid.NewGuid();

        var write = await GatewayFor(WorkspaceB).PutVariantAsync(
            operationId, 0, BrandSourceSampleFiles.Png(), "image/png", TestContext.Current.CancellationToken);

        Assert.Equal(GeneratedImageObjectWriteOutcome.Stored, write.Outcome);

        // Workspace A asks for workspace B's object by its exact key. Finding a key is not authorization.
        var deleted = await GatewayFor(WorkspaceA)
            .DeleteAsync(write.Object!.ObjectKey, TestContext.Current.CancellationToken);

        Assert.False(deleted);
        Assert.Single(_store.Keys);

        // And its real owner can still remove it, so the refusal is about the caller, not the key.
        Assert.True(await GatewayFor(WorkspaceB)
            .DeleteAsync(write.Object.ObjectKey, TestContext.Current.CancellationToken));
        Assert.Empty(_store.Keys);
    }

    [Fact]
    public async Task A_key_that_is_not_one_of_ours_is_refused_rather_than_passed_to_storage()
    {
        _store.Unavailable = true;

        // Unavailable would throw if the store were reached, so a false here proves it was not.
        Assert.False(await GatewayFor(WorkspaceA)
            .DeleteAsync("../../etc/passwd", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Deleting_a_variant_that_was_never_written_is_a_no_op()
    {
        Assert.False(await GatewayFor(WorkspaceA)
            .DeleteVariantAsync(Guid.NewGuid(), 0, TestContext.Current.CancellationToken));
    }

    private IGeneratedImageObjectGateway GatewayFor(Guid workspaceId) =>
        new GeneratedImageObjectGateway(
            _store,
            new StubWorkspaceContext(workspaceId),
            NullLogger<GeneratedImageObjectGateway>.Instance);

    private sealed class StubWorkspaceContext(Guid workspaceId) : IWorkspaceContext
    {
        public bool IsResolved => true;

        public Guid WorkspaceId { get; } = workspaceId;

        public string WorkspaceSlug => "workspace";

        public Guid MembershipId => WorkspaceServiceIdentity.MembershipId;

        public string AccountId => WorkspaceServiceIdentity.AccountId;

        public WorkspaceRole Role => WorkspaceRole.Owner;
    }
}
