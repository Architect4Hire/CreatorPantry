namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// The Media module's stable, machine-readable error codes.
/// </summary>
/// <remarks>
/// Part of the API contract once a route returns one (api-contract.md): a client branches on the code, so
/// renaming one is a breaking change even though the message beside it is free to be reworded.
/// </remarks>
public static class MediaErrorCodes
{
    /// <summary>The caller's membership role does not allow generating images in this workspace.</summary>
    public const string GenerationForbidden = "media.generation.forbidden";

    /// <summary>The request was not well formed. The field errors say which part.</summary>
    public const string GenerationInvalidRequest = "media.generation.invalid_request";

    /// <summary>No such generated image in the resolved workspace. Also a neighbour's, deliberately.</summary>
    public const string StagedImageNotFound = "media.staged_image.not_found";

    /// <summary>The caller's membership role does not allow declining generated images.</summary>
    public const string StagedImageForbidden = "media.staged_image.forbidden";

    /// <summary>The image is kept or expired, and neither can be declined.</summary>
    public const string StagedImageNotRejectable = "media.staged_image.conflict";

    /// <summary>The image exists and its bytes could not be reached. Retrying is the remedy.</summary>
    public const string StagedImageStorageUnavailable = "media.staged_image.unavailable";

    /// <summary>The asset creation request was not well formed. The field errors say which part.</summary>
    public const string AssetInvalidRequest = "media.asset.invalid_request";

    /// <summary>The caller's membership role does not allow creating assets in this workspace.</summary>
    public const string AssetForbidden = "media.asset.forbidden";

    /// <summary>The bytes are not something this library can store, whatever they were called.</summary>
    public const string AssetUnsupported = "media.asset.unprocessable";

    /// <summary>
    /// Something the request named by id is not in this workspace: the asset itself on a read (DAM-003), or the
    /// staged image a creation was to be made from (DAM-001).
    /// </summary>
    /// <remarks>
    /// One code for both, deliberately. A client cannot act differently on them — neither is worth retrying, and
    /// both are resolved by going back to a list — and splitting it would publish a distinction whose only use
    /// would be telling a caller which of two things they cannot see exists (tenancy.md). An unknown id, another
    /// workspace's asset and a tombstone the caller did not ask for all arrive here as one answer.
    /// </remarks>
    public const string AssetNotFound = "media.asset.not_found";

    /// <summary>Private storage could not be reached. Nothing was written, and retrying is the remedy.</summary>
    public const string AssetStorageUnavailable = "media.asset.unavailable";

    /// <summary>The rows could not be committed. Nothing was written, including the object.</summary>
    public const string AssetNotCreated = "media.asset.conflict";

    /// <summary>A library search named a filter this server could not read.</summary>
    public const string AssetSearchInvalidRequest = "media.asset_search.invalid_request";

    /// <summary>A cursor was issued for different filters, a different ordering or another workspace.</summary>
    public const string AssetCursorInvalidRequest = "media.asset_search.cursor_invalid_request";
}
