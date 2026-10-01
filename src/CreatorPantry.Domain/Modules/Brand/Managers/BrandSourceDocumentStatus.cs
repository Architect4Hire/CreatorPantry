namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// Where a source document stands: <c>Active ⇄ Archived</c>, either of which an Editor may move to
/// <c>Removed</c>, and an Owner may bring a removed document back to <c>Archived</c>. No state deletes a
/// version or its blob.
/// </summary>
public enum BrandSourceDocumentStatus
{
    Active = 1,

    /// <summary>Out of the pickers and out of default context; still readable, and restorable.</summary>
    Archived = 2,

    /// <summary>
    /// A tombstone. The row and its versions stay so a guide version that cited them still resolves; physical
    /// purge is a retention job's decision, never this state's.
    /// </summary>
    /// <remarks>
    /// <strong>Invisible, not gone.</strong> Every route answers 404 for a removed document — the read, the
    /// download, the replacement and the three Editor lifecycle commands alike — so the only way back is an
    /// Owner restoring it from the removed-document list, which is the one place a tombstone is published. A
    /// restore returns it to <see cref="Archived"/> rather than <see cref="Active"/>, so a document somebody
    /// deliberately removed does not reappear in every collaborator's picker on its way back.
    /// </remarks>
    Removed = 3,
}
