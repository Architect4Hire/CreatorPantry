using CreatorPantry.Domain.Managers.Storage;

namespace CreatorPantry.ApiService.Storage;

public static class ObjectStorageRegistration
{
    /// <summary>
    /// Registers the private object store: Azure Blob Storage when the AppHost has supplied the connection —
    /// Azurite in a local run — and the store that refuses everything when it has not.
    /// </summary>
    /// <remarks>
    /// The fallback is here for the same reason the cache has one: <c>WebApplicationFactory</c> and
    /// <c>SqliteApiHost</c> start the real <c>Program</c>, and an unconditional blob client would make every
    /// endpoint test depend on a storage container. Unlike the cache's, this fallback does no work — it
    /// throws on use — so a deployment that forgot the connection cannot quietly keep creator files nowhere.
    /// </remarks>
    public static IHostApplicationBuilder AddCreatorPantryObjectStorage(this IHostApplicationBuilder builder)
    {
        if (builder.Configuration.GetConnectionString(ObjectStorageConnection.Name) is not null)
        {
            builder.AddAzureBlobServiceClient(ObjectStorageConnection.Name);
            builder.Services.AddAzureBlobPrivateObjectStorage();
        }
        else
        {
            builder.Services.AddPrivateObjectStorage();
        }

        return builder;
    }
}
