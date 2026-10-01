using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Domain.Managers.Storage;

/// <summary>The name the AppHost gives its blob resource, and so the connection a host reads it back under.</summary>
public static class ObjectStorageConnection
{
    public const string Name = "blobs";
}

public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IPrivateObjectStore"/> if nothing has: the store that refuses everything. A module
    /// that keeps objects calls this so its gateway always resolves; a host with real storage registers
    /// <see cref="AddAzureBlobPrivateObjectStorage"/> as well, in either order.
    /// </summary>
    public static IServiceCollection AddPrivateObjectStorage(this IServiceCollection services)
    {
        services.TryAddSingleton<IPrivateObjectStore, UnconfiguredPrivateObjectStore>();

        return services;
    }

    /// <summary>
    /// Registers <see cref="IPrivateObjectStore"/> over Azure Blob Storage. Requires a <see cref="BlobServiceClient"/>,
    /// which the host supplies from the <see cref="ObjectStorageConnection.Name"/> connection.
    /// </summary>
    public static IServiceCollection AddAzureBlobPrivateObjectStorage(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IPrivateObjectStore, AzureBlobPrivateObjectStore>());

        return services;
    }
}
