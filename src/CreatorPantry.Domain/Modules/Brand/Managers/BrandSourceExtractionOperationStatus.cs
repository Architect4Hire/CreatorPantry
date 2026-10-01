namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// Where one queued extraction has got to.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This says whether the job ran, not what it found.</strong> That split is the whole design: an image
/// and a scanned PDF are <see cref="Completed"/> operations that produced an
/// <see cref="BrandSourceExtractionStatus.Unsupported"/> extraction row, and a corrupt document is a
/// <see cref="Completed"/> operation that produced a <see cref="BrandSourceExtractionStatus.Failed"/> one. None
/// of those is worth retrying — the same bytes give the same answer — and folding them into this enum would put
/// them on the retry path. <see cref="Failed"/> here means something stopped the work rather than something was
/// learned about the document.
/// </para>
/// <para>
/// Three of the four states are terminal, and <see cref="Running"/> → <see cref="Queued"/> is the only edge
/// that goes backwards: lease recovery, bounded by <see cref="BrandPolicy.ExtractionMaxAttempts"/>.
/// </para>
/// </remarks>
public enum BrandSourceExtractionOperationStatus
{
    /// <summary>Waiting to be claimed, from the moment its version committed or a lapsed lease returned it.</summary>
    Queued = 1,

    /// <summary>Claimed by a worker that holds a lease on it.</summary>
    Running = 2,

    /// <summary>
    /// The work was done and an extraction row records the outcome — text, or a review state. Terminal.
    /// </summary>
    Completed = 3,

    /// <summary>
    /// Something stopped the work rather than something was learned: storage was unreachable every time, or
    /// every attempt was spent. No extraction row was written. Terminal.
    /// </summary>
    Failed = 4,

    /// <summary>
    /// There is no longer anything to extract — the document was removed while this waited. Terminal, and
    /// deliberately not <see cref="Failed"/>: nothing went wrong, so nothing should look as though it did.
    /// </summary>
    Cancelled = 5,
}

/// <summary>
/// Why an extraction operation stopped, in terms stable enough to route on.
/// </summary>
/// <remarks>
/// Operator-facing and small. The creator-facing explanation of a document that could not be read is the
/// extraction row's reason, which is a different question with a different audience: this one is only ever asked
/// about an operation that produced no extraction at all.
/// </remarks>
public enum BrandSourceExtractionFailureCategory
{
    /// <summary>Private storage could not be reached, on every attempt this operation had.</summary>
    Storage = 1,

    /// <summary>
    /// The version's object is not there. A committed row naming bytes that are gone — a fault outside the
    /// application, since the upload writes the object before the row.
    /// </summary>
    SourceMissing = 2,

    /// <summary>
    /// Every attempt was claimed and never completed: a worker died, or kept dying, holding the lease.
    /// </summary>
    LeaseAbandoned = 3,

    /// <summary>
    /// The version this names cannot be read in its workspace. Reached when a claim row and the workspace it
    /// resolves to disagree, which is the state a cross-workspace tamper would produce.
    /// </summary>
    SourceUnreadable = 4,
}
