using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// One durable instruction to read a <see cref="BrandSourceDocumentVersion"/> as text, and everything that
/// happened while that was attempted. Workspace-owned, and the queue itself rather than a record of one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The row is the queue.</strong> It is written in the same <c>SaveChangesAsync</c> as the version it
/// names, so a committed version always has work queued for it and an uncommitted one never does — which is
/// what the outbox exists to arrange for an event, obtained here without a second durable hop because the
/// consumer is this same database. A unique index on (workspace, version) makes a replayed upload unable to
/// queue the same version twice at the database rather than only in the code above it.
/// </para>
/// <para>
/// <strong>There is nowhere here to put a document's text, and that is the point.</strong> Not a title, not a
/// filename, not a snippet, not a parser's complaint about the bytes it choked on. What this records is the
/// shape of an attempt — when, how often, by which parser, how it ended — which is everything needed to explain
/// a run without quoting what was run over. The text is a private blob; the creator-facing account of what was
/// found is the <see cref="BrandSourceExtraction"/> row.
/// </para>
/// <para>
/// <strong>Mutable, unlike every other row in this part of the module.</strong> A version and an extraction are
/// write-once because they are history; this is state, and a queue whose rows could not change would not be one.
/// The lease columns are what keep two workers from both finishing it, and <see cref="RowVersion"/> is what
/// keeps two workers from both claiming it.
/// </para>
/// </remarks>
public class BrandSourceExtractionOperation : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>
    /// The document the version belongs to. Stored rather than joined for, because the extracted text's object
    /// key is built from both ids and the claim must carry them before any workspace is resolved.
    /// </summary>
    public Guid BrandSourceDocumentId { get; set; }

    /// <summary>The exact version to read. One operation per version, enforced by a unique index.</summary>
    public Guid BrandSourceDocumentVersionId { get; set; }

    public BrandSourceExtractionOperationStatus Status { get; set; } = BrandSourceExtractionOperationStatus.Queued;

    /// <summary>
    /// The extraction this operation produced, once it has. Null while queued, and for one that produced none.
    /// </summary>
    /// <remarks>
    /// Not a foreign key. An extraction row is immutable history that may outlive any queue row's usefulness,
    /// and a constraint here would mean a reference in the direction opposite to the one that matters: the
    /// extraction is the record, and this is a pointer to it for anyone asking what a run did.
    /// </remarks>
    public Guid? BrandSourceExtractionId { get; set; }

    /// <summary>
    /// Which parser read the document, as <c>pdf/pdfpig@0.1.16</c>. Null until a parser was chosen.
    /// </summary>
    /// <remarks>
    /// The provenance of the artifact. A library upgrade can change what a document's text comes out as, so an
    /// artifact nobody can attribute to a named parser is one nobody can re-derive or explain a change in.
    /// </remarks>
    public string? ExtractorId { get; set; }

    /// <summary>How many times a worker has claimed this, including the claim in progress.</summary>
    public int Attempts { get; set; }

    /// <summary>
    /// Not claimable before this instant: the queue time initially, then a backoff instant after a requeue.
    /// </summary>
    public DateTimeOffset AvailableAt { get; set; }

    /// <summary>The claim token of the worker currently holding this, while <see cref="Status"/> is Running.</summary>
    /// <remarks>
    /// Quoted on every write that completes the operation. A worker whose lease has since been taken by another
    /// must not be able to write an extraction over the top of the winner's — the token is what makes that
    /// detectable rather than last-writer-wins.
    /// </remarks>
    public Guid? LeasedBy { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>Why the work stopped. Set in exactly <see cref="BrandSourceExtractionOperationStatus.Failed"/>.</summary>
    public BrandSourceExtractionFailureCategory? FailureCategory { get; set; }

    /// <summary>
    /// A short operator-facing note on the failure. Never document content, never a parser's quotation of it.
    /// </summary>
    public string? FailureSummary { get; set; }

    public DateTimeOffset QueuedAt { get; set; }

    /// <summary>When <see cref="Status"/> last changed. What a sweep reads, whatever state the row is in.</summary>
    public DateTimeOffset StatusChangedAt { get; set; }

    /// <summary>
    /// When a worker first began this. Stays set if lease recovery returns the operation to the queue: it
    /// records that work started, not that it is still running.
    /// </summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>When the operation reached a terminal state. Set in exactly those states, and never unset.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Optimistic concurrency token. Not for collaborative editing — for competing workers: two claiming one
    /// queued operation must not both win, and the loser needs a conflict rather than a silent overwrite.
    /// </summary>
    public byte[] RowVersion { get; set; } = [];
}
