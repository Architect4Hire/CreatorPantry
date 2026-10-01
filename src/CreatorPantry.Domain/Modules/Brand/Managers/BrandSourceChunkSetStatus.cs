namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// Where one set of brand-source chunks stands: being built, serving retrieval, or waiting to be removed.
/// </summary>
/// <remarks>
/// Three states rather than a boolean because replacement has to be safe without ever being partial. A
/// re-embedding builds a whole new set beside the one in use, and only when every chunk is written does the
/// new set become <see cref="Current"/> and the old one <see cref="Superseded"/> — one transaction, so no
/// reader ever sees two current sets or none.
/// </remarks>
public enum BrandSourceChunkSetStatus
{
    /// <summary>Chunks are still being written. Never read for retrieval; may coexist with a current set.</summary>
    Building = 1,

    /// <summary>The set retrieval reads. At most one per extraction and embedding model.</summary>
    Current = 2,

    /// <summary>
    /// Replaced, and awaiting deletion. Excluded from the uniqueness rule, so any number may exist: a worker
    /// that died after the swap leaves one here for the maintenance sweep rather than blocking the next run.
    /// </summary>
    Superseded = 3,
}
