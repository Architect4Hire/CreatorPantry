namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// Where one queued embedding run has got to.
/// </summary>
/// <remarks>
/// The same four-state shape, and the same single backwards edge (<see cref="Running"/> →
/// <see cref="Queued"/>, bounded by <see cref="BrandPolicy.EmbeddingMaxAttempts"/>), as
/// <see cref="BrandSourceExtractionOperationStatus"/>. The state says whether the job ran, not what the
/// vectors are: the chunk set it produced is the record of that.
/// </remarks>
public enum BrandSourceEmbeddingOperationStatus
{
    /// <summary>Waiting to be claimed, from the moment its extraction committed or a lapsed lease returned it.</summary>
    Queued = 1,

    /// <summary>Claimed by a worker that holds a lease on it.</summary>
    Running = 2,

    /// <summary>A chunk set was built, verified and made current. Terminal.</summary>
    Completed = 3,

    /// <summary>Something stopped the work and retrying will not help, or every attempt was spent. Terminal.</summary>
    Failed = 4,

    /// <summary>
    /// There is nothing left to embed — the document was removed, or a newer artifact replaced this one while
    /// the job waited. Terminal, and deliberately not <see cref="Failed"/>.
    /// </summary>
    Cancelled = 5,
}

/// <summary>Why an embedding operation stopped, in terms stable enough to route on. Operator-facing.</summary>
public enum BrandSourceEmbeddingFailureCategory
{
    /// <summary>No embedding deployment is configured for this host. Recoverable by configuring one.</summary>
    ProviderNotConfigured = 1,

    /// <summary>The provider does not say which model produced the vectors, so provenance cannot be written.</summary>
    ModelUnidentified = 2,

    /// <summary>The provider could not be reached or kept refusing, on every attempt this operation had.</summary>
    ProviderUnavailable = 3,

    /// <summary>The provider returned the wrong number of vectors, or vectors of the wrong width.</summary>
    InvalidEmbedding = 4,

    /// <summary>Private storage could not be reached, on every attempt this operation had.</summary>
    Storage = 5,

    /// <summary>The extracted artifact is not there. A committed row naming bytes that are gone.</summary>
    SourceMissing = 6,

    /// <summary>The extracted artifact's bytes no longer hash to the checksum its row recorded.</summary>
    SourceCorrupt = 7,

    /// <summary>The extraction this names cannot be read in its workspace — what a cross-workspace tamper looks like.</summary>
    SourceUnreadable = 8,

    /// <summary>Every attempt was claimed and never completed: a worker died, or kept dying, holding the lease.</summary>
    LeaseAbandoned = 9,

    /// <summary>The extracted text holds nothing to embed — it is blank. A fact about the artifact, not a fault.</summary>
    NoPassages = 10,
}
