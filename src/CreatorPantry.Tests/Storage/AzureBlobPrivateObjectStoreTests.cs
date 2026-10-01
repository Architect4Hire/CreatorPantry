using System.Security.Cryptography;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using CreatorPantry.Domain.Managers.Storage;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Azurite;

namespace CreatorPantry.Tests.Storage;

/// <summary>
/// The Azure Blob adapter against Azurite, the same emulator a local Aspire run uses (B-05). Holds the real
/// adapter to the contract <see cref="InMemoryPrivateObjectStore"/> stands in for: create-only writes, a
/// measured size and checksum, a limit that leaves nothing behind, and provider faults that surface as the
/// application's own exception.
/// </summary>
public sealed class AzureBlobPrivateObjectStoreTests : IAsyncLifetime
{
    private const string Container = "brand-sources";

    private readonly AzuriteContainer _azurite =
        new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").WithCommand("--skipApiVersionCheck").Build();

    private ServiceProvider? _provider;

    private IPrivateObjectStore Store => _provider!.GetRequiredService<IPrivateObjectStore>();

    public async ValueTask InitializeAsync()
    {
        await _azurite.StartAsync();

        var client = new BlobServiceClient(_azurite.GetConnectionString());
        await client.GetBlobContainerClient(Container).CreateAsync(PublicAccessType.None);

        // Through the registration a host uses, so the test also covers that it wins over the fallback.
        _provider = new ServiceCollection()
            .AddSingleton(client)
            .AddPrivateObjectStorage()
            .AddAzureBlobPrivateObjectStorage()
            .BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        await _azurite.DisposeAsync();
    }

    private static string NewKey() => $"workspaces/{Guid.NewGuid():N}/brand-sources/{Guid.NewGuid():N}/{Guid.NewGuid():N}/original";

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task A_write_reads_back_with_its_bytes_size_checksum_and_media_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = NewKey();

        var write = await Store.PutAsync(Container, key, Bytes("house style"), "application/pdf", 1024, ct);

        var expected = new StoredObject(
            key, 11, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("house style"))), "application/pdf");
        Assert.Equal(ObjectWriteOutcome.Stored, write.Outcome);
        Assert.Equal(expected, write.Object);

        await using var read = await Store.OpenReadAsync(Container, key, ct);
        Assert.NotNull(read);
        Assert.Equal(expected, read.Object);
        using var reader = new StreamReader(read.Content, Encoding.UTF8);
        Assert.Equal("house style", await reader.ReadToEndAsync(ct));
    }

    [Fact]
    public async Task A_second_write_to_the_same_key_is_refused_and_the_first_survives()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = NewKey();
        await Store.PutAsync(Container, key, Bytes("first"), "text/plain", 1024, ct);

        var second = await Store.PutAsync(Container, key, Bytes("second"), "text/plain", 1024, ct);

        Assert.Equal(ObjectWriteOutcome.AlreadyExists, second.Outcome);
        await using var read = await Store.OpenReadAsync(Container, key, ct);
        using var reader = new StreamReader(read!.Content, Encoding.UTF8);
        Assert.Equal("first", await reader.ReadToEndAsync(ct));
    }

    [Theory]
    [InlineData(1025)]
    // Past the SDK's single-request threshold, so the content is staged in blocks before the limit trips.
    [InlineData(6 * 1024 * 1024)]
    public async Task Content_past_the_limit_is_refused_and_no_object_exists(int size)
    {
        var ct = TestContext.Current.CancellationToken;
        var key = NewKey();

        var write = await Store.PutAsync(Container, key, new MemoryStream(new byte[size]), "application/pdf", size - 1, ct);

        Assert.Equal(ObjectWriteOutcome.TooLarge, write.Outcome);
        Assert.Null(await Store.OpenReadAsync(Container, key, ct));
    }

    [Fact]
    public async Task A_large_object_streams_in_blocks_and_keeps_its_measurements()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = NewKey();
        var bytes = RandomNumberGenerator.GetBytes(6 * 1024 * 1024);

        var write = await Store.PutAsync(Container, key, new MemoryStream(bytes), "application/pdf", bytes.Length, ct);

        Assert.Equal(ObjectWriteOutcome.Stored, write.Outcome);
        Assert.Equal(bytes.Length, write.Object!.SizeBytes);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)), write.Object.ContentChecksum);

        await using var read = await Store.OpenReadAsync(Container, key, ct);
        using var buffer = new MemoryStream();
        await read!.Content.CopyToAsync(buffer, ct);
        Assert.Equal(bytes, buffer.ToArray());
    }

    [Fact]
    public async Task A_missing_object_reads_as_null_and_deletes_as_false()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = NewKey();

        Assert.Null(await Store.OpenReadAsync(Container, key, ct));
        Assert.False(await Store.DeleteAsync(Container, key, ct));
    }

    [Fact]
    public async Task A_delete_removes_the_object_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = NewKey();
        await Store.PutAsync(Container, key, Bytes("house style"), "text/plain", 1024, ct);

        Assert.True(await Store.DeleteAsync(Container, key, ct));
        Assert.False(await Store.DeleteAsync(Container, key, ct));
        Assert.Null(await Store.OpenReadAsync(Container, key, ct));
    }

    [Fact]
    public async Task A_container_that_does_not_exist_is_unavailable_not_created()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ObjectStoreUnavailableException>(
            () => Store.PutAsync("not-provisioned", NewKey(), Bytes("x"), "text/plain", 1024, ct));

        var client = _provider!.GetRequiredService<BlobServiceClient>();
        Assert.False((await client.GetBlobContainerClient("not-provisioned").ExistsAsync(ct)).Value);
    }

    [Fact]
    public async Task The_container_is_private()
    {
        var client = _provider!.GetRequiredService<BlobServiceClient>();

        var policy = await client.GetBlobContainerClient(Container).GetAccessPolicyAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PublicAccessType.None, policy.Value.BlobPublicAccess);
    }
}
