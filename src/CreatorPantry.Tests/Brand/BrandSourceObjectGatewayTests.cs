using System.Security.Cryptography;
using System.Text;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Brand.Gateways;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// The brand source object gateway over an in-memory store: the key it generates, what it will and will not
/// read back, and that one workspace cannot reach another's objects through it. Both workspaces share one
/// store, as they share one container.
/// </summary>
public sealed class BrandSourceObjectGatewayTests
{
    private const long Limit = 1024;

    private static readonly Guid WorkspaceA = Guid.NewGuid();
    private static readonly Guid WorkspaceB = Guid.NewGuid();

    private readonly InMemoryPrivateObjectStore _store = new();

    private BrandSourceObjectGateway GatewayFor(Guid workspaceId) =>
        new(_store, new StubWorkspace(workspaceId), NullLogger<BrandSourceObjectGateway>.Instance);

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    private static async Task<string> ReadAsync(BrandSourceObjectContent content)
    {
        using var reader = new StreamReader(content.Content, Encoding.UTF8);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private async Task<BrandSourceObject> PutAsync(Guid workspaceId, string text, Guid? documentId = null, Guid? versionId = null)
    {
        var write = await GatewayFor(workspaceId).PutOriginalAsync(
            documentId ?? Guid.NewGuid(), versionId ?? Guid.NewGuid(), Bytes(text), "application/pdf", Limit, TestContext.Current.CancellationToken);

        Assert.Equal(BrandSourceObjectWriteOutcome.Stored, write.Outcome);
        return write.Object!;
    }

    [Fact]
    public async Task A_put_stores_under_a_generated_key_and_reports_what_was_measured()
    {
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        var stored = await PutAsync(WorkspaceA, "house style", documentId, versionId);

        Assert.Equal($"workspaces/{WorkspaceA:N}/brand-sources/{documentId:N}/{versionId:N}/original", stored.ObjectKey);
        Assert.Equal(11, stored.SizeBytes);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("house style"))), stored.ContentChecksum);
        Assert.Equal("application/pdf", stored.MediaType);

        // What the SQL columns from 11A.1 require of a pointer.
        Assert.True(stored.ObjectKey.Length <= BrandPolicy.ObjectKeyMaxLength);
        Assert.DoesNotContain("://", stored.ObjectKey);
    }

    [Fact]
    public async Task A_stored_object_reads_back_with_the_same_bytes_and_metadata()
    {
        var stored = await PutAsync(WorkspaceA, "house style");

        await using var read = await GatewayFor(WorkspaceA).OpenReadAsync(stored.ObjectKey, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Equal(stored, read.Object);
        Assert.Equal("house style", await ReadAsync(read));
    }

    [Fact]
    public async Task A_put_never_overwrites_an_existing_object()
    {
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var first = await PutAsync(WorkspaceA, "first", documentId, versionId);
        var gateway = GatewayFor(WorkspaceA);

        var second = await gateway.PutOriginalAsync(documentId, versionId, Bytes("second"), "application/pdf", Limit, TestContext.Current.CancellationToken);

        Assert.Equal(BrandSourceObjectWriteOutcome.AlreadyExists, second.Outcome);
        Assert.Null(second.Object);
        await using var read = await gateway.OpenReadAsync(first.ObjectKey, TestContext.Current.CancellationToken);
        Assert.Equal("first", await ReadAsync(read!));
    }

    [Fact]
    public async Task Content_past_the_limit_is_refused_and_leaves_nothing_behind()
    {
        var write = await GatewayFor(WorkspaceA).PutOriginalAsync(
            Guid.NewGuid(), Guid.NewGuid(), new MemoryStream(new byte[Limit + 1]), "application/pdf", Limit, TestContext.Current.CancellationToken);

        Assert.Equal(BrandSourceObjectWriteOutcome.TooLarge, write.Outcome);
        Assert.Empty(_store.Keys);
    }

    [Fact]
    public async Task Content_exactly_at_the_limit_is_stored()
    {
        var write = await GatewayFor(WorkspaceA).PutOriginalAsync(
            Guid.NewGuid(), Guid.NewGuid(), new MemoryStream(new byte[Limit]), "application/pdf", Limit, TestContext.Current.CancellationToken);

        Assert.Equal(BrandSourceObjectWriteOutcome.Stored, write.Outcome);
        Assert.Equal(Limit, write.Object!.SizeBytes);
    }

    [Fact]
    public async Task Extracted_text_is_stored_beside_its_version_one_object_per_ordinal()
    {
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var gateway = GatewayFor(WorkspaceA);
        var ct = TestContext.Current.CancellationToken;

        var first = await gateway.PutExtractedTextAsync(documentId, versionId, 1, Bytes("extracted"), Limit, ct);
        var corrected = await gateway.PutExtractedTextAsync(documentId, versionId, 2, Bytes("corrected"), Limit, ct);
        var repeat = await gateway.PutExtractedTextAsync(documentId, versionId, 1, Bytes("again"), Limit, ct);

        Assert.Equal($"workspaces/{WorkspaceA:N}/brand-sources/{documentId:N}/{versionId:N}/extracted/1", first.Object!.ObjectKey);
        Assert.Equal("text/plain; charset=utf-8", first.Object.MediaType);
        Assert.EndsWith("/extracted/2", corrected.Object!.ObjectKey);
        Assert.Equal(BrandSourceObjectWriteOutcome.AlreadyExists, repeat.Outcome);

        await using var read = await gateway.OpenReadAsync(first.Object.ObjectKey, ct);
        Assert.Equal("extracted", await ReadAsync(read!));
    }

    [Fact]
    public async Task A_copy_makes_an_independent_original_for_another_version()
    {
        var source = await PutAsync(WorkspaceA, "house style");
        var gateway = GatewayFor(WorkspaceA);
        var ct = TestContext.Current.CancellationToken;
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        var copy = await gateway.CopyOriginalAsync(source.ObjectKey, documentId, versionId, ct);

        Assert.Equal(BrandSourceObjectWriteOutcome.Stored, copy.Outcome);
        Assert.Equal($"workspaces/{WorkspaceA:N}/brand-sources/{documentId:N}/{versionId:N}/original", copy.Object!.ObjectKey);
        Assert.Equal(source with { ObjectKey = copy.Object.ObjectKey }, copy.Object);

        // Independent: removing the source leaves the copy readable.
        await gateway.DeleteAsync(source.ObjectKey, ct);
        await using var read = await gateway.OpenReadAsync(copy.Object.ObjectKey, ct);
        Assert.Equal("house style", await ReadAsync(read!));
    }

    [Fact]
    public async Task A_copy_onto_an_existing_object_or_from_a_missing_one_changes_nothing()
    {
        var source = await PutAsync(WorkspaceA, "source");
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var taken = await PutAsync(WorkspaceA, "taken", documentId, versionId);
        var gateway = GatewayFor(WorkspaceA);
        var ct = TestContext.Current.CancellationToken;
        var missing = BrandSourceObjectKey.ForOriginal(WorkspaceA, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(BrandSourceObjectWriteOutcome.AlreadyExists, (await gateway.CopyOriginalAsync(source.ObjectKey, documentId, versionId, ct)).Outcome);
        Assert.Equal(BrandSourceObjectWriteOutcome.SourceNotFound, (await gateway.CopyOriginalAsync(missing, Guid.NewGuid(), Guid.NewGuid(), ct)).Outcome);

        await using var read = await gateway.OpenReadAsync(taken.ObjectKey, ct);
        Assert.Equal("taken", await ReadAsync(read!));
        Assert.Equal(2, _store.Keys.Count);
    }

    [Fact]
    public async Task A_delete_removes_the_object_and_repeating_it_is_harmless()
    {
        var stored = await PutAsync(WorkspaceA, "house style");
        var gateway = GatewayFor(WorkspaceA);
        var ct = TestContext.Current.CancellationToken;

        await gateway.DeleteAsync(stored.ObjectKey, ct);
        await gateway.DeleteAsync(stored.ObjectKey, ct);

        Assert.Null(await gateway.OpenReadAsync(stored.ObjectKey, ct));
        Assert.Empty(_store.Keys);
    }

    [Fact]
    public async Task One_workspace_cannot_read_copy_or_delete_anothers_object()
    {
        var ownedByB = await PutAsync(WorkspaceB, "workspace B's voice");
        var gatewayA = GatewayFor(WorkspaceA);
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await gatewayA.OpenReadAsync(ownedByB.ObjectKey, ct));
        Assert.Equal(
            BrandSourceObjectWriteOutcome.SourceNotFound,
            (await gatewayA.CopyOriginalAsync(ownedByB.ObjectKey, Guid.NewGuid(), Guid.NewGuid(), ct)).Outcome);
        await gatewayA.DeleteAsync(ownedByB.ObjectKey, ct);

        // Still there, untouched, and nothing was copied out of it.
        Assert.Equal([ownedByB.ObjectKey], _store.Keys);
        await using var read = await GatewayFor(WorkspaceB).OpenReadAsync(ownedByB.ObjectKey, ct);
        Assert.Equal("workspace B's voice", await ReadAsync(read!));
    }

    [Fact]
    public async Task The_same_document_and_version_ids_in_two_workspaces_are_two_objects()
    {
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        var a = await PutAsync(WorkspaceA, "A's file", documentId, versionId);
        var b = await PutAsync(WorkspaceB, "B's file", documentId, versionId);

        Assert.NotEqual(a.ObjectKey, b.ObjectKey);
        await using var read = await GatewayFor(WorkspaceA).OpenReadAsync(a.ObjectKey, TestContext.Current.CancellationToken);
        Assert.Equal("A's file", await ReadAsync(read!));
    }

    public static TheoryData<string> UnsafeKeys()
    {
        var w = WorkspaceA.ToString("N");
        var id = Guid.NewGuid().ToString("N");
        var valid = $"workspaces/{w}/brand-sources/{id}/{id}/original";

        return new TheoryData<string>
        {
            "",
            "original",
            $"workspaces/{w}/brand-sources/{id}/{id}/../{id}/original",
            $"workspaces/{w}/brand-sources/{id}/../../{WorkspaceB:N}/brand-sources/{id}/{id}/original",
            $"../{valid}",
            $"/{valid}",
            $"{valid}/",
            $"{valid}\n",
            $"{valid}/../original",
            valid.Replace('/', '\\'),
            valid.Replace("/original", "%2Foriginal"),
            valid.Replace("original", "%2e%2e"),
            $"https://account.blob.core.windows.net/brand-sources/{valid}",
            $"workspaces/{w.ToUpperInvariant()}/brand-sources/{id}/{id}/original",
            $"workspaces/{WorkspaceA:D}/brand-sources/{id}/{id}/original",
            $"workspaces/{w}/brand-sources/{id}/{id}/original.pdf",
            $"workspaces/{w}/brand-sources/{id}/{id}/extracted/0",
            $"workspaces/{w}/brand-sources/{id}/{id}/extracted/-1",
            $"workspaces/{w}/brand-sources/{id}/{id}/extracted/1/2",
            $"workspaces/{w}/brand-sources/{id}/{id}/extracted/99999999999",
            $"workspaces/{w}/media/{id}/{id}/original",
            valid + new string('a', 600),
        };
    }

    [Theory]
    [MemberData(nameof(UnsafeKeys))]
    public async Task A_key_the_gateway_would_not_have_written_reaches_nothing(string objectKey)
    {
        // An object planted at the exact key asked for, so a pass means the gateway refused — not that the
        // store happened to hold nothing there.
        await _store.PutAsync(BrandSourceObjectKey.Container, objectKey, Bytes("planted"), "text/plain", Limit, TestContext.Current.CancellationToken);
        var gateway = GatewayFor(WorkspaceA);
        var ct = TestContext.Current.CancellationToken;

        Assert.False(BrandSourceObjectKey.TryParse(objectKey, out _));
        Assert.Null(await gateway.OpenReadAsync(objectKey, ct));
        Assert.Equal(
            BrandSourceObjectWriteOutcome.SourceNotFound,
            (await gateway.CopyOriginalAsync(objectKey, Guid.NewGuid(), Guid.NewGuid(), ct)).Outcome);
        await gateway.DeleteAsync(objectKey, ct);

        Assert.Equal([objectKey], _store.Keys);
    }

    [Fact]
    public async Task Extracted_text_cannot_be_copied_in_as_an_original()
    {
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var gateway = GatewayFor(WorkspaceA);
        var ct = TestContext.Current.CancellationToken;
        var text = await gateway.PutExtractedTextAsync(documentId, versionId, 1, Bytes("extracted"), Limit, ct);

        var copy = await gateway.CopyOriginalAsync(text.Object!.ObjectKey, Guid.NewGuid(), Guid.NewGuid(), ct);

        Assert.Equal(BrandSourceObjectWriteOutcome.SourceNotFound, copy.Outcome);
    }

    [Fact]
    public void A_key_round_trips_through_its_own_grammar()
    {
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        Assert.True(BrandSourceObjectKey.TryParse(BrandSourceObjectKey.ForOriginal(WorkspaceA, documentId, versionId), out var original));
        Assert.Equal(new BrandSourceObjectKeyParts(WorkspaceA, documentId, versionId, null), original);

        Assert.True(BrandSourceObjectKey.TryParse(BrandSourceObjectKey.ForExtractedText(WorkspaceA, documentId, versionId, 7), out var text));
        Assert.Equal(new BrandSourceObjectKeyParts(WorkspaceA, documentId, versionId, 7), text);
    }

    [Fact]
    public async Task A_key_is_never_built_from_an_empty_identifier_or_a_non_positive_ordinal()
    {
        var gateway = GatewayFor(WorkspaceA);
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(() => gateway.PutOriginalAsync(Guid.Empty, Guid.NewGuid(), Bytes("x"), "text/plain", Limit, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.PutOriginalAsync(Guid.NewGuid(), Guid.Empty, Bytes("x"), "text/plain", Limit, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gateway.PutExtractedTextAsync(Guid.NewGuid(), Guid.NewGuid(), 0, Bytes("x"), Limit, ct));
        Assert.Empty(_store.Keys);
    }

    [Fact]
    public async Task An_unreachable_store_is_an_outcome_for_writes_and_an_exception_for_reads()
    {
        var stored = await PutAsync(WorkspaceA, "house style");
        var gateway = GatewayFor(WorkspaceA);
        var ct = TestContext.Current.CancellationToken;
        _store.Unavailable = true;

        var put = await gateway.PutOriginalAsync(Guid.NewGuid(), Guid.NewGuid(), Bytes("x"), "text/plain", Limit, ct);
        var copy = await gateway.CopyOriginalAsync(stored.ObjectKey, Guid.NewGuid(), Guid.NewGuid(), ct);

        Assert.Equal(BrandSourceObjectWriteOutcome.Unavailable, put.Outcome);
        Assert.Equal(BrandSourceObjectWriteOutcome.Unavailable, copy.Outcome);
        await Assert.ThrowsAsync<ObjectStoreUnavailableException>(() => gateway.OpenReadAsync(stored.ObjectKey, ct));
        await Assert.ThrowsAsync<ObjectStoreUnavailableException>(() => gateway.DeleteAsync(stored.ObjectKey, ct));
    }

    [Fact]
    public async Task With_no_resolved_workspace_nothing_is_written()
    {
        var gateway = new BrandSourceObjectGateway(_store, new StubWorkspace(null), NullLogger<BrandSourceObjectGateway>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.PutOriginalAsync(
            Guid.NewGuid(), Guid.NewGuid(), Bytes("x"), "text/plain", Limit, TestContext.Current.CancellationToken));
        Assert.Empty(_store.Keys);
    }

    /// <summary>The surface carries no address and no provider type — only application-owned values.</summary>
    [Fact]
    public void The_gateway_surface_exposes_no_url_and_no_provider_type()
    {
        var surface = new[] { typeof(IBrandSourceObjectGateway), typeof(IPrivateObjectStore), typeof(BrandSourceObject), typeof(StoredObject), typeof(BrandSourceObjectContent), typeof(StoredObjectContent) };

        var types = surface
            .SelectMany(type => type.GetMethods().SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType))
                .Concat(type.GetProperties().Select(p => p.PropertyType)))
            .SelectMany(type => type.IsGenericType ? type.GetGenericArguments().Append(type) : [type])
            .ToList();

        Assert.DoesNotContain(types, type => type == typeof(Uri) || (type.Namespace ?? string.Empty).StartsWith("Azure", StringComparison.Ordinal));
        Assert.DoesNotContain(
            surface.SelectMany(type => type.GetMembers()).Select(member => member.Name),
            name => name.Contains("Url", StringComparison.OrdinalIgnoreCase) || name.Contains("Uri", StringComparison.OrdinalIgnoreCase) || name.Contains("Sas", StringComparison.Ordinal));
    }

    private sealed class StubWorkspace(Guid? workspaceId) : IWorkspaceContext
    {
        public bool IsResolved => workspaceId is not null;

        public Guid WorkspaceId => workspaceId ?? throw new InvalidOperationException("No workspace has been resolved.");

        public string WorkspaceSlug => "workspace";

        public Guid MembershipId { get; } = Guid.NewGuid();

        public string AccountId => "acct";

        public WorkspaceRole Role => WorkspaceRole.Owner;
    }
}
