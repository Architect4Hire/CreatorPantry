using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Media.Gateways;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Domain.Modules.Media.Data;

/// <summary>
/// Opens a picture's rendition for reading when it has one, for the two data layers that serve pictures.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Called only after the picture itself has been found servable.</strong> Whether a caller may see
/// a staged image or a library version is decided where it always was; this adds nothing to that and takes
/// nothing away. A rendition is never a softer way in to a picture than its original.
/// </para>
/// <para>
/// <strong>Null means "serve the original", for every reason there is no rendition to serve:</strong> none
/// made yet, one recorded as not compressed, a row whose bytes are not there, or storage that would not
/// answer. None of them is the caller's problem, and if storage really is down the original's own read is
/// what reports it.
/// </para>
/// </remarks>
internal static class MediaRenditionOpener
{
    public static async Task<StoredObjectContent?> TryOpenAsync(
        IMediaRenditionRepository renditions,
        IMediaRenditionObjectGateway objects,
        MediaRenditionSource source,
        MediaRenditionPurpose purpose,
        CancellationToken cancellationToken)
    {
        var recorded = await renditions.FindRecordedAsync(source, purpose, cancellationToken);

        if (recorded is not { Status: MediaRenditionStatus.Ready, ObjectKey: { } objectKey })
        {
            return null;
        }

        try
        {
            return await objects.OpenReadAsync(objectKey, cancellationToken);
        }
        catch (ObjectStoreUnavailableException)
        {
            return null;
        }
    }
}
