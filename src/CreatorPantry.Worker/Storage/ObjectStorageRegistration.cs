using CreatorPantry.Domain.Managers.Storage;

namespace CreatorPantry.Worker.Storage;

public static class ObjectStorageRegistration
{
    /// <summary>
    /// Registers the private object store: Azure Blob Storage when the AppHost has supplied the connection —
    /// Azurite in a local run — and the store that refuses everything when it has not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same shape as the API's, deliberately duplicated rather than shared: each host owns its own
    /// composition, and the alternative was putting the Aspire blob integration into ServiceDefaults, which
    /// every host references — including the gateway and the web host, neither of which touches an object.
    /// </para>
    /// <para>
    /// The fallback exists so <c>WorkerHostCompositionTests</c> can build this container without a storage
    /// container to point at. It does no work: it throws on use, so a deployment that forgot the connection
    /// cannot quietly record extractions whose text went nowhere.
    /// </para>
    /// </remarks>
    public static IHostApplicationBuilder AddCreatorPantryObjectStorage(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

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
