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

    /// <summary>The staged image the request names could not be found in this workspace.</summary>
    public const string AssetSourceNotFound = "media.asset.not_found";

    /// <summary>Private storage could not be reached. Nothing was written, and retrying is the remedy.</summary>
    public const string AssetStorageUnavailable = "media.asset.unavailable";

    /// <summary>The rows could not be committed. Nothing was written, including the object.</summary>
    public const string AssetNotCreated = "media.asset.conflict";
}
