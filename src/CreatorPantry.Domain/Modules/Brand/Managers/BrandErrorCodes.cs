namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>Stable, machine-readable codes for the brand module. The <c>.not_found</c> suffix maps to 404.</summary>
public static class BrandErrorCodes
{
    /// <summary>
    /// The workspace has no brand profile yet. Distinct from the workspace itself being unknown or
    /// inaccessible, which the tenancy pipeline answers earlier with its own code, so a caller who reaches this
    /// one is already a member and learns nothing about any other workspace.
    /// </summary>
    public const string ProfileNotFound = "brand.profile.not_found";

    /// <summary>The request failed shape or domain validation. Falls through to 400.</summary>
    public const string InvalidRequest = "brand.profile.invalid_request";

    /// <summary>The caller's role is below Editor. Maps to 403.</summary>
    public const string Forbidden = "brand.profile.forbidden";

    /// <summary>The profile moved on since the state the edit was composed against. Maps to 409.</summary>
    public const string Conflict = "brand.profile.conflict";

    /// <summary>A profile already exists for this workspace; edit it instead. Maps to 409.</summary>
    public const string AlreadyExistsConflict = "brand.profile.exists.conflict";

    /// <summary>
    /// Logo links cannot be accepted yet: the server cannot verify an asset id belongs to this workspace until
    /// the media seam exists. Maps to 422.
    /// </summary>
    public const string AssetsUnprocessable = "brand.assets.unprocessable";
}
