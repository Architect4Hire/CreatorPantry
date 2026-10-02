using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// A durable request to chunk one extracted artifact and embed it: the queue row the Worker claims.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Identifiers and state only.</strong> The row names the extraction and carries no text, so claiming
/// it — which happens before any workspace is resolved — reveals nothing about a creator's document.
/// </para>
/// <para>
/// <strong>One live operation per extraction.</strong> A filtered unique index admits at most one
/// <see cref="BrandSourceEmbeddingOperationStatus.Queued"/> or
/// <see cref="BrandSourceEmbeddingOperationStatus.Running"/> row per extraction, which is what makes staging
/// one beside every commit, and enqueueing one for a model change, safe to repeat. Terminal rows stay as the
/// history of what was tried.
/// </para>
/// <para>
/// <strong>The model is recorded when the work runs, not when it is queued.</strong> The host that commits an
/// extraction — often the API — has no embedding deployment and cannot say which model will embed it; the
/// Worker does, so <see cref="EmbeddingModel"/> is null until it has looked.
/// </para>
/// </remarks>
public class BrandSourceEmbeddingOperation : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>The document the artifact belongs to. Stored for the same reason a chunk set stores it.</summary>
    public Guid BrandSourceDocumentId { get; set; }

    /// <summary>The exact extracted artifact to chunk. Never a "latest" pointer: the job is pinned.</summary>
    public Guid BrandSourceExtractionId { get; set; }

    /// <summary>The version that extraction read; part of the composite key into it.</summary>
    public Guid BrandSourceDocumentVersionId { get; set; }

    /// <summary>
    /// The extraction's status, carried only so the foreign key can require
    /// <see cref="BrandSourceExtractionStatus.Succeeded"/>. An extraction is immutable, so it cannot drift.
    /// </summary>
    public BrandSourceExtractionStatus SourceStatus { get; set; } = BrandSourceExtractionStatus.Succeeded;

    public BrandSourceEmbeddingOperationStatus Status { get; set; } = BrandSourceEmbeddingOperationStatus.Queued;

    /// <summary>The deployment that embedded it, as the provider reports it. Null until the work first runs.</summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>The set this run built, once it completed.</summary>
    public Guid? BrandSourceChunkSetId { get; set; }

    /// <summary>How many times a worker has claimed this operation.</summary>
    public int Attempts { get; set; }

    /// <summary>When the operation may next be claimed. A requeued operation waits out its backoff.</summary>
    public DateTimeOffset AvailableAt { get; set; }

    public Guid? LeasedBy { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public BrandSourceEmbeddingFailureCategory? FailureCategory { get; set; }

    /// <summary>Operator-facing and never creator content.</summary>
    public string? FailureSummary { get; set; }

    public DateTimeOffset QueuedAt { get; set; }

    public DateTimeOffset StatusChangedAt { get; set; }

    /// <summary>Set once on the first claim and never cleared.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Stops two workers both settling the operation.</summary>
    public byte[] RowVersion { get; set; } = [];
}
