using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// One embedding run over one <see cref="BrandSourceExtraction"/>: the chunks it produced, cut by a named
/// chunker and embedded by a named model at a fixed width. The unit of replacement, and the thing retrieval
/// selects before it compares a single vector.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the set exists rather than these facts sitting on each chunk.</strong> "A mixed model or
/// dimension index is invalid" is a statement about a <em>collection</em> of vectors, and a collection is not
/// something a row can constrain: every chunk of one run repeats the same model and width, so no unique index
/// over chunk columns can say "at most one current set per extraction per model". Hoisting them here makes
/// that a filtered unique index (see <c>BrandSourceChunkSetConfiguration</c>) instead of a rule the worker is
/// trusted to keep. Retrieval then filters to one set and every vector it compares is commensurable by
/// construction.
/// </para>
/// <para>
/// <strong>The source is pinned by one composite foreign key, not by three columns kept in step.</strong>
/// <see cref="BrandSourceExtractionId"/>, <see cref="BrandSourceDocumentVersionId"/> and
/// <see cref="SourceStatus"/> travel together into <c>AK_BrandSourceExtractions_Workspace_Id_Version_Status</c>,
/// so the database refuses a set whose version disagrees with its extraction's, and — because
/// <see cref="SourceStatus"/> is checked equal to
/// <see cref="BrandSourceExtractionStatus.Succeeded"/> — refuses outright to hang chunks off an extraction
/// that failed or was never reviewed. Neither is a worker check that could be forgotten.
/// </para>
/// <para>
/// <strong>Mutable, where the extraction above it is write-once.</strong> A version and an extraction are
/// history; this is state. <see cref="Status"/> moves and <see cref="RowVersion"/> is what stops two workers
/// both swapping the current set out from under each other.
/// </para>
/// </remarks>
public class BrandSourceChunkSet : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>
    /// The document the chunks came from. Stored rather than joined for, as on
    /// <see cref="BrandSourceExtractionOperation.BrandSourceDocumentId"/>: "everything embedded for this
    /// document" is a read this answers without walking two tables, and the value is pinned transitively by
    /// the version below it.
    /// </summary>
    public Guid BrandSourceDocumentId { get; set; }

    /// <summary>The exact version whose text was chunked. Part of the composite key into the extraction.</summary>
    public Guid BrandSourceDocumentVersionId { get; set; }

    /// <summary>The exact extracted artifact the offsets on every chunk are measured in.</summary>
    public Guid BrandSourceExtractionId { get; set; }

    /// <summary>
    /// The extraction's status, carried here only so the foreign key can require it.
    /// </summary>
    /// <remarks>
    /// Denormalized on purpose, and harmless because an extraction is immutable: the value cannot drift from
    /// the row it was copied from. A check constraint pins it to
    /// <see cref="BrandSourceExtractionStatus.Succeeded"/>, which is the only status that carries text at all.
    /// </remarks>
    public BrandSourceExtractionStatus SourceStatus { get; set; } = BrandSourceExtractionStatus.Succeeded;

    public BrandSourceChunkSetStatus Status { get; set; } = BrandSourceChunkSetStatus.Building;

    /// <summary>Which chunker cut the text, as <c>text/paragraph-1600c-200o@1</c>.</summary>
    public string ChunkerId { get; set; } = string.Empty;

    /// <summary>Which deployment produced the vectors, as <c>text-embedding-3-small</c>.</summary>
    public string EmbeddingModel { get; set; } = string.Empty;

    /// <summary>
    /// The width of every vector in this set. Always <see cref="BrandPolicy.EmbeddingDimension"/> today.
    /// </summary>
    /// <remarks>
    /// Stored although the column's width already fixes it, because the number a set was built at is the fact
    /// that explains an old set after the schema moves on — without it, a widened column would make every
    /// historical set look as though it had always been the new width.
    /// </remarks>
    public int EmbeddingDimension { get; set; } = BrandPolicy.EmbeddingDimension;

    /// <summary>How many chunks this set holds. Zero while <see cref="BrandSourceChunkSetStatus.Building"/>.</summary>
    public int ChunkCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the last chunk was embedded and the set left <see cref="BrandSourceChunkSetStatus.Building"/>.
    /// Null while it is still being built.
    /// </summary>
    public DateTimeOffset? EmbeddedAt { get; set; }

    /// <summary>
    /// Optimistic concurrency token. Not for collaborative editing — for competing workers, as on
    /// <see cref="BrandSourceExtractionOperation.RowVersion"/>: two runs completing at once must not both
    /// believe they became the current set.
    /// </summary>
    public byte[] RowVersion { get; set; } = [];

    public ICollection<BrandSourceChunk> Chunks { get; } = [];
}
