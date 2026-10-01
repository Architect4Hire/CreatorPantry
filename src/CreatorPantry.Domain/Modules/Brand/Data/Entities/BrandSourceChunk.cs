using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.Data.SqlTypes;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// One passage of a creator's brand source material and the vector that finds it: the retrieval unit behind
/// task-relevant grounding.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Derived data, not history.</strong> Everything here can be rebuilt from the extracted artifact the
/// set names, which is why a chunk is neither immutable nor versioned — a re-embedding simply builds a new set
/// and the old chunks go. That is also what keeps the vector-index decision reversible: moving to approximate
/// search later costs a re-run of the embedding worker, not a data migration.
/// </para>
/// <para>
/// <strong>The slice is recorded twice, in two units, for two different reasons.</strong>
/// <see cref="StartByteOffset"/> and <see cref="ByteLength"/> locate the passage in the immutable artifact, so
/// provenance survives independently of anything copied out of it and the text can always be re-derived;
/// <see cref="Text"/> is the copy retrieval actually reads, because <c>IPrivateObjectStore</c> has no ranged
/// read and fetching a four-megabyte artifact to serve sixteen hundred characters is not a retrieval path.
/// <see cref="ContentChecksum"/> is what ties the two together: a stored text that no longer hashes to the
/// slice it claims is detectable rather than merely wrong.
/// </para>
/// <para>
/// <strong>Creator prose in SQL, deliberately.</strong> The extraction <em>operation</em> row holds no
/// document text and that remains right — it is telemetry. This is a content row, in the same category as a
/// guide section's body, and it carries the usual obligation: it is untrusted prompt content wherever it is
/// later used (ai.md), and it is never logged.
/// </para>
/// <para>
/// <strong>What the embedding column cannot do.</strong> SQL Server allows no check, default, key, unique or
/// foreign-key constraint on a <c>vector</c> column and no comparison operator over one, so every invariant
/// this module would normally pin in DDL lives on the columns beside it — the set's dimension, the chunk's
/// offsets and checksum. The column is <c>NOT NULL</c>, which is the one constraint available, and a chunk is
/// written already embedded so there is no partial state to describe.
/// </para>
/// </remarks>
public class BrandSourceChunk : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>The embedding run this chunk belongs to, which owns its model, width and chunker.</summary>
    public Guid BrandSourceChunkSetId { get; set; }

    /// <summary>1 for the first chunk of the artifact, in reading order. Unique within the set.</summary>
    public int Ordinal { get; set; }

    /// <summary>Where the passage starts in the artifact, in UTF-8 bytes from its first byte.</summary>
    /// <remarks>
    /// Bytes rather than characters because the artifact is stored UTF-8 and only a byte offset can address a
    /// ranged read, should the object store ever gain one. The chunker is obliged to cut on code-point
    /// boundaries; <see cref="ContentChecksum"/> is what catches it if it does not.
    /// </remarks>
    public long StartByteOffset { get; set; }

    /// <summary>How many UTF-8 bytes of the artifact the passage spans.</summary>
    public int ByteLength { get; set; }

    /// <summary><c>sha256:</c> and the digest of the slice's bytes, as read from the artifact.</summary>
    public string ContentChecksum { get; set; } = string.Empty;

    /// <summary>The passage itself, exactly as the artifact has it. Never rewritten, never normalized.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// The embedding of <see cref="Text"/>, at the width and from the model its set records.
    /// </summary>
    /// <remarks>
    /// A native <c>vector(1536)</c>, not a JSON array or a blob of floats, so the distance is computed by the
    /// engine — <c>EF.Functions.VectorDistance</c> — beneath the workspace query filter, rather than by
    /// loading every candidate into this process.
    /// </remarks>
    public SqlVector<float> Embedding { get; set; }
}
