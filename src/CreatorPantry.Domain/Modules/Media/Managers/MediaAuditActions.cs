namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// What the Media module's audit entries are called.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deletion is the first DAM action to be audited, and the deviation is deliberate.</strong> 12.9a's create
/// and 12.9d's patch both write audit <em>fields</em> and no audit <em>event</em>, deferring DAM auditing to the
/// creative-production audit prompt. auth.md names "destructive deletion" explicitly among the operations that
/// require an audit event, so this one does not wait: an asset leaving every collaborator's library with no record
/// of who removed it is the case that rule exists for.
/// </para>
/// <para>
/// The entity name rather than a route segment, following <c>RecipeAuditActions</c>, so an entry stays readable
/// after a route is versioned or renamed.
/// </para>
/// </remarks>
public static class MediaAuditActions
{
    /// <inheritdoc cref="MediaAuditActions"/>
    public const string ResourceType = "MediaAsset";

    /// <summary>An asset was soft-deleted (DAM-005). The bytes were not touched.</summary>
    public const string Deleted = "mediaAsset.deleted";
}
