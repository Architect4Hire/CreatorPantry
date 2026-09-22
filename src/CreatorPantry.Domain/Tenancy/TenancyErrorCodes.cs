namespace CreatorPantry.Domain.Tenancy;

/// <summary>Stable error codes for workspace resolution. Renaming a code is a breaking API change.</summary>
public static class TenancyErrorCodes
{
    public const string WorkspaceResolutionInvalidRequest = "tenancy.workspace_resolution.invalid_request";

    /// <summary>Unknown slug, no membership, or an inactive membership: deliberately indistinguishable.</summary>
    public const string WorkspaceNotFound = "tenancy.workspace.not_found";

    public const string WorkspaceInvalidRequest = "tenancy.workspace.invalid_request";

    /// <summary>Every derived slug candidate for the chosen name was already taken; the caller should retry.</summary>
    public const string WorkspaceSlugUnavailable = "tenancy.workspace.conflict";
}
