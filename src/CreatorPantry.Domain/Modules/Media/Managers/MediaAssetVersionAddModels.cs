namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>A creator's new version of an asset they already have (DAM-010).</summary>
/// <param name="Content">Opened for reading. The caller owns disposing it.</param>
/// <param name="FileName">The creator's own filename, kept for display only. Never used to build a key.</param>
/// <param name="UserId">The authenticated caller, for the idempotency scope. Never request input.</param>
/// <remarks>
/// <strong>Bytes only.</strong> No metadata: a patch is JSON with a concurrency token and merge semantics, an upload
/// is multipart, and form fields cannot express absent-versus-clear — which is exactly the ambiguity
/// <c>PatchField</c> exists to remove. Changing what an asset says about itself is 12.9d's route.
/// </remarks>
public sealed record MediaAssetVersionUpload(Stream Content, string? FileName, string UserId);

/// <summary>What adding a version did.</summary>
public enum MediaAssetVersionAddOutcome
{
    Added = 1,

    /// <summary>
    /// There is no live asset in this workspace to add a version to.
    /// </summary>
    /// <remarks>
    /// One outcome for an unknown id, another workspace's asset and a tombstone, so a caller cannot use this route to
    /// learn that an asset exists where they cannot see it (tenancy.md).
    /// </remarks>
    NotFound = 2,

    /// <summary>
    /// Somebody else took this version number first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two concurrent uploads compute the same object key, because a version's key is
    /// <c>workspaces/{ws}/media-assets/{asset}/{n}</c> and nothing in it varies per request. The store is create-only,
    /// so the second write is refused — and <strong>the loser must not compensate</strong>: the object under that key
    /// is the winner's, and deleting it would destroy a committed version's bytes.
    /// </para>
    /// <para>
    /// Retryable by re-reading the next number, which is why it is distinct from
    /// <see cref="StorageUnavailable"/>: nothing is wrong with storage, the caller simply lost a race.
    /// </para>
    /// </remarks>
    VersionTaken = 3,

    /// <summary>Storage could not be written. Nothing was committed, and nothing was left behind.</summary>
    StorageUnavailable = 4,

    /// <summary>The rows could not be committed. The object written for them has been removed.</summary>
    NotCommitted = 5,
}

/// <param name="Version">Present exactly when the outcome is <see cref="MediaAssetVersionAddOutcome.Added"/>.</param>
public sealed record MediaAssetVersionAdd(
    MediaAssetVersionAddOutcome Outcome, MediaAssetVersionServiceModel? Version = null);
