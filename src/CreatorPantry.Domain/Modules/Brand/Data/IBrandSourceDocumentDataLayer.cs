using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Gateways;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Brand.Data;

public enum BrandSourceStoreOutcome
{
    Stored = 1,

    /// <summary>Private storage could not be reached. No row was written.</summary>
    StorageUnavailable = 2,

    /// <summary>What storage measured is not what was inspected. The object was removed and no row was written.</summary>
    ContentChanged = 3,
}

/// <param name="TagNames">The document's tags as the vocabulary spells them, in the order submitted.</param>
public sealed record BrandSourceStoreResult(BrandSourceStoreOutcome Outcome, IReadOnlyList<string> TagNames);

/// <param name="Name">The creator's own spelling, used only if the tag is new.</param>
public sealed record BrandSourceTagInput(string Name, string NormalizedName);

public enum BrandSourceReplaceOutcome
{
    Replaced = 1,

    /// <summary>Private storage could not be reached. No row was written and no object was left behind.</summary>
    StorageUnavailable = 2,

    /// <summary>What storage measured is not what was inspected. The new object was removed and no row was written.</summary>
    ContentChanged = 3,

    /// <summary>
    /// The document moved on since the read this replacement was composed against. No row was written, and
    /// the new object was removed again.
    /// </summary>
    Conflict = 4,
}

/// <param name="TagNames">The document's tags as the vocabulary spells them, alphabetically. Empty for none.</param>
public sealed record BrandSourceReplaceResult(BrandSourceReplaceOutcome Outcome, IReadOnlyList<string> TagNames);

/// <summary>How much of a document there is and what points at it. Two reads that always travel together.</summary>
public sealed record BrandSourceDocumentUsage(
    BrandSourceDocumentSizeRecord Size, IReadOnlyList<BrandSourceHoldRecord> Holds);


public interface IBrandSourceDocumentDataLayer
{
    /// <inheritdoc cref="IMalwareScanGateway.ScanAsync"/>
    Task<MalwareScanVerdict> ScanAsync(Stream content, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the private object, then the document, its first version, its tags, its queued extraction and the
    /// audit entry as one save. The object comes first so that no committed row can name bytes that are not
    /// there; if the save fails the object is removed again before the failure propagates.
    /// </summary>
    /// <param name="version">
    /// Carries the inspected media type, size and checksum; storage must report the same size and checksum.
    /// Its <c>ObjectKey</c> is set here.
    /// </param>
    /// <param name="extraction">
    /// The queued extraction for <paramref name="version"/>, staged in the same save.
    /// </param>
    /// <remarks>
    /// The extraction row travelling with the version is what makes the queue durable without a second hop: a
    /// committed version always has work queued for it and an uncommitted one never does, which is the property
    /// an outbox exists to arrange for an event and which is had for free here because the consumer is this same
    /// database.
    /// </remarks>
    Task<BrandSourceStoreResult> StoreAsync(
        BrandSourceDocument document,
        BrandSourceDocumentVersion version,
        IReadOnlyList<BrandSourceTagInput> tags,
        BrandSourceExtractionOperation extraction,
        AuditEntry audit,
        Stream content,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes the object an upload wrote, unless its version row turns out to be committed. For a failure
    /// that surfaced after <see cref="StoreAsync"/> returned — the caller's transaction did not commit, or may
    /// not have. Never throws: an object it cannot remove is logged and left, unreferenced and private.
    /// </summary>
    Task AbandonAsync(Guid documentId, Guid versionId);

    /// <summary>One page of the library and the tag names of the documents on it.</summary>
    Task<BrandSourceDocumentListPage> ListAsync(BrandSourceDocumentListCriteria criteria, CancellationToken cancellationToken);

    /// <summary>One document with its current version and tag names, or null when the workspace has no such document.</summary>
    Task<BrandSourceDocumentDetail?> FindDetailAsync(Guid documentId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IBrandSourceDocumentRepository.FindStatusAsync"/>
    Task<BrandSourceDocumentStatus?> FindStatusAsync(Guid documentId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IBrandSourceDocumentRepository.ListVersionsAsync"/>
    Task<(IReadOnlyList<BrandSourceDocumentVersionSummaryRecord> Rows, bool HasMore)> ListVersionsAsync(
        BrandSourceDocumentVersionListCriteria criteria, CancellationToken cancellationToken);

    /// <inheritdoc cref="IBrandSourceDocumentRepository.FindVersionListContextAsync"/>
    Task<BrandSourceDocumentVersionListContextRecord?> FindVersionListContextAsync(
        Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    /// Opens one numbered version's stored bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two steps that belong together: the metadata row says where the bytes are, and only then are they
    /// opened. Composed here rather than in Business because Business may not hold an object key long enough
    /// to pass it anywhere — this is the layer whose job is to turn one into a stream.
    /// </para>
    /// <para>
    /// <see cref="BrandSourceDownloadOutcome.Missing"/> covers a document or version the workspace does not
    /// have. <see cref="BrandSourceDownloadOutcome.ObjectMissing"/> is a committed row whose object is not
    /// there, which is a different thing and must not be answered as a missing document: the document exists
    /// and a creator can still read its metadata.
    /// </para>
    /// </remarks>
    Task<BrandSourceDownloadResult> OpenVersionAsync(
        Guid documentId, int versionNumber, CancellationToken cancellationToken);

    /// <inheritdoc cref="IBrandSourceDocumentRepository.FindForUpdateAsync"/>
    Task<BrandSourceDocument?> FindForUpdateAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the new version's private object, then the version row, the document's changed counter and
    /// stamps, and the audit entry as one save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same order as <see cref="StoreAsync"/> and for the same reason: no committed row may name bytes
    /// that are not there. <strong>The previous version's object is never named on any path</strong> — the
    /// key is derived from the new version's id, so an in-place overwrite is unrepresentable rather than
    /// merely avoided, and nothing here deletes.
    /// </para>
    /// <para>
    /// A conflict is compensated like a failure, because it is one: the object was written before the save
    /// that lost, so returning without removing it would leave a private blob no row will ever name.
    /// </para>
    /// </remarks>
    /// <param name="document">
    /// Tracked, with its counter and stamps already advanced by Business. The row version it was loaded with
    /// is what the UPDATE is conditioned on.
    /// </param>
    /// <param name="version">Carries the inspected media type, size and checksum. Its <c>ObjectKey</c> is set here.</param>
    /// <param name="extraction">
    /// The queued extraction for the new version, staged in the same save. The previous version's extraction
    /// history is untouched: this names the new version, and the unique index is per version.
    /// </param>
    Task<BrandSourceReplaceResult> ReplaceAsync(
        BrandSourceDocument document,
        BrandSourceDocumentVersion version,
        BrandSourceExtractionOperation extraction,
        AuditEntry audit,
        Stream content,
        CancellationToken cancellationToken);

    /// <summary>
    /// Saves one lifecycle move: the document's changed status and stamps, and the audit entry, as one save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No object is written and none is removed — that is the whole point of these commands. Archiving,
    /// removing and restoring all leave every version row and every stored object exactly where it was, so
    /// this has none of the compensation the upload and the replacement need.
    /// </para>
    /// <para>
    /// The conflict answer is reached the same way as a replacement's: the document's UPDATE carries the row
    /// version it was loaded with, and a failed save asks whether the row moved on.
    /// </para>
    /// </remarks>
    /// <param name="document">Tracked, with its status and stamps already set by Business.</param>
    /// <returns>False when the document moved on since it was read; true when the move was saved.</returns>
    Task<bool> SaveTransitionAsync(BrandSourceDocument document, AuditEntry audit, CancellationToken cancellationToken);

    /// <inheritdoc cref="IBrandSourceDocumentRepository.HoldsAsync"/>
    Task<BrandSourceDocumentUsage> UsageAsync(Guid documentId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IBrandSourceDocumentRepository.ListRemovedAsync"/>
    Task<(IReadOnlyList<RemovedBrandSourceDocumentRecord> Rows, bool HasMore)> ListRemovedAsync(
        RemovedBrandSourceDocumentListCriteria criteria, CancellationToken cancellationToken);


}

/// <param name="TagNames">The document's tags as the vocabulary spells them, alphabetically. Empty for none.</param>
public sealed record BrandSourceDocumentDetail(
    BrandSourceDocumentDetailRecord Record, IReadOnlyList<string> TagNames);

public enum BrandSourceDownloadOutcome
{
    Opened = 1,

    /// <summary>No such document, or no such version of it, in the resolved workspace.</summary>
    Missing = 2,

    /// <summary>The version row is committed and its object is not there. A fault, not a missing document.</summary>
    ObjectMissing = 3,

    /// <summary>Private storage could not be reached. Nothing is known about the object either way.</summary>
    StorageUnavailable = 4,
}

/// <param name="Download">Present exactly when <paramref name="Outcome"/> is <see cref="BrandSourceDownloadOutcome.Opened"/>. The caller disposes it.</param>
public sealed record BrandSourceDownloadResult(
    BrandSourceDownloadOutcome Outcome, BrandSourceDownload? Download = null);

/// <param name="TagNames">By document id. A document with no tags is absent.</param>
public sealed record BrandSourceDocumentListPage(
    IReadOnlyList<BrandSourceDocumentSummaryRecord> Rows,
    bool HasMore,
    IReadOnlyDictionary<Guid, IReadOnlyList<string>> TagNames);

internal sealed class BrandSourceDocumentDataLayer(
    IBrandSourceDocumentRepository documents,
    IBrandSourceExtractionRepository extractions,
    IBrandSourceObjectGateway objects,
    IMalwareScanGateway scanner,
    IAuditWriter auditWriter,
    IWorkspaceContext workspace,
    CreatorPantryDbContext context,
    ILogger<BrandSourceDocumentDataLayer> logger) : IBrandSourceDocumentDataLayer
{
    // What this request has already written, by version. A retrying execution strategy re-runs the whole
    // unit, and the second run finds the object the first one stored.
    private readonly Dictionary<Guid, BrandSourceObject> _written = [];

    public Task<MalwareScanVerdict> ScanAsync(Stream content, CancellationToken cancellationToken) =>
        scanner.ScanAsync(content, cancellationToken);

    public async Task<BrandSourceStoreResult> StoreAsync(
        BrandSourceDocument document,
        BrandSourceDocumentVersion version,
        IReadOnlyList<BrandSourceTagInput> tags,
        BrandSourceExtractionOperation extraction,
        AuditEntry audit,
        Stream content,
        CancellationToken cancellationToken)
    {
        switch (await WriteOriginalAsync(document, version, content, cancellationToken))
        {
            case ObjectWriteStep.Unavailable:
                return new BrandSourceStoreResult(BrandSourceStoreOutcome.StorageUnavailable, []);

            case ObjectWriteStep.ContentChanged:
                return new BrandSourceStoreResult(BrandSourceStoreOutcome.ContentChanged, []);
        }

        try
        {
            var tagNames = await AttachTagsAsync(document, tags, cancellationToken);

            document.Versions.Add(version);
            documents.Add(document);

            // Staged here, so the queue row cannot commit without the version it names and the version cannot
            // commit without work queued for it.
            extractions.Enqueue(extraction);
            auditWriter.Record(audit);

            // One save: the document, its version, its tags, its queued extraction and the audit row commit
            // together or not at all.
            await context.SaveChangesAsync(cancellationToken);

            return new BrandSourceStoreResult(BrandSourceStoreOutcome.Stored, tagNames);
        }
        catch
        {
            // No row was written, so the object must not outlive the attempt. Nothing may stay staged either.
            context.ChangeTracker.Clear();
            await RemoveAsync(document.Id, version.Id);
            throw;
        }
    }

    public async Task AbandonAsync(Guid documentId, Guid versionId)
    {
        try
        {
            context.ChangeTracker.Clear();

            // A commit that reported failure may still have landed. If the row is there the object stays:
            // an unreferenced object is untidy, a row without its object is the failure this order exists
            // to prevent.
            if (await documents.VersionExistsAsync(versionId, CancellationToken.None))
            {
                return;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Brand source upload could not be reconciled; its object is left in place. workspaceId={WorkspaceId} documentId={DocumentId} versionId={VersionId}",
                workspace.WorkspaceId, documentId, versionId);
            return;
        }

        await RemoveAsync(documentId, versionId);
    }

    public Task<BrandSourceDocument?> FindForUpdateAsync(Guid documentId, CancellationToken cancellationToken) =>
        documents.FindForUpdateAsync(documentId, cancellationToken);

    public async Task<BrandSourceReplaceResult> ReplaceAsync(
        BrandSourceDocument document,
        BrandSourceDocumentVersion version,
        BrandSourceExtractionOperation extraction,
        AuditEntry audit,
        Stream content,
        CancellationToken cancellationToken)
    {
        // Captured before the save, because a successful save replaces it with the token the server just
        // generated, and this is the value a losing writer needs to recognise that it lost. Captured before
        // the object write too, so a clear of the change tracker cannot take it with the entity.
        var readWith = document.RowVersion;
        var documentId = document.Id;

        switch (await WriteOriginalAsync(document, version, content, cancellationToken))
        {
            case ObjectWriteStep.Unavailable:
                return new BrandSourceReplaceResult(BrandSourceReplaceOutcome.StorageUnavailable, []);

            case ObjectWriteStep.ContentChanged:
                return new BrandSourceReplaceResult(BrandSourceReplaceOutcome.ContentChanged, []);
        }

        try
        {
            // The version is appended; the document's counter and stamps were advanced by Business and are
            // already tracked as modified. Nothing is added to the context: the root is loaded, not new, and
            // the previous version is not touched at all.
            document.Versions.Add(version);

            // The new version's own queued extraction. The previous version's extraction history is untouched —
            // the unique index is per version, and this names the one this request created.
            extractions.Enqueue(extraction);
            auditWriter.Record(audit);

            // One save: the new version, its queued extraction, the document's UPDATE and the audit row commit
            // together or not at all. That UPDATE carries the row version, which is what decides the race.
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // Nothing committed, so the object this attempt wrote must not outlive it — on the conflict path
            // as much as on the fault path, because a conflict is a write that lost, not a write that never
            // happened. The previous version's object is untouched either way: the key names this version.
            context.ChangeTracker.Clear();
            await RemoveAsync(documentId, version.Id);

            // Two guards on one race: the row version on the document's UPDATE, and the unique index on
            // (workspace, document, version number). Either may fire first, so ask the question that matters:
            // has the row this replacement was composed against moved on? If so it is a conflict whichever
            // guard caught it. The same reasoning, and the same shape, as the brand profile's edit.
            var current = await documents.CurrentRowVersionAsync(documentId, CancellationToken.None);
            if (exception is DbUpdateConcurrencyException
                || current is null
                || !current.AsSpan().SequenceEqual(readWith))
            {
                return new BrandSourceReplaceResult(BrandSourceReplaceOutcome.Conflict, []);
            }

            throw;
        }
        catch
        {
            context.ChangeTracker.Clear();
            await RemoveAsync(documentId, version.Id);
            throw;
        }

        // Read after the save rather than carried through it: the response states the document's tags, and a
        // replacement does not change them, so this is the one query that says what they are.
        var tagNames = await documents.TagNamesAsync([documentId], cancellationToken);

        return new BrandSourceReplaceResult(
            BrandSourceReplaceOutcome.Replaced, tagNames.TryGetValue(documentId, out var names) ? names : []);
    }


    public async Task<bool> SaveTransitionAsync(
        BrandSourceDocument document, AuditEntry audit, CancellationToken cancellationToken)
    {
        // Captured before the save, because a successful save replaces it with the token the server just
        // generated, and this is the value a losing writer needs to recognise that it lost.
        var readWith = document.RowVersion;
        var documentId = document.Id;

        auditWriter.Record(audit);

        try
        {
            // One save: the document's UPDATE and the audit row commit together or not at all. That UPDATE
            // carries the row version, which is what decides the race.
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // Nothing committed, and nothing may stay staged for the next save on this scope to commit.
            // There is no object to compensate: a lifecycle move writes none.
            context.ChangeTracker.Clear();

            var current = await documents.CurrentRowVersionAsync(documentId, CancellationToken.None);
            if (exception is DbUpdateConcurrencyException
                || current is null
                || !current.AsSpan().SequenceEqual(readWith))
            {
                return false;
            }

            throw;
        }

        return true;
    }

    public async Task<BrandSourceDocumentUsage> UsageAsync(Guid documentId, CancellationToken cancellationToken) =>
        new(
            await documents.SizeAsync(documentId, cancellationToken),
            await documents.HoldsAsync(documentId, cancellationToken));

    public Task<(IReadOnlyList<RemovedBrandSourceDocumentRecord> Rows, bool HasMore)> ListRemovedAsync(
        RemovedBrandSourceDocumentListCriteria criteria, CancellationToken cancellationToken) =>
        documents.ListRemovedAsync(criteria, cancellationToken);

    public async Task<BrandSourceDocumentListPage> ListAsync(
        BrandSourceDocumentListCriteria criteria, CancellationToken cancellationToken)
    {
        var (rows, hasMore) = await documents.ListAsync(criteria, cancellationToken);

        // A second read for the page's own documents, at most a page of ids, rather than a subquery per row.
        var tagNames = await documents.TagNamesAsync([.. rows.Select(row => row.Id)], cancellationToken);

        return new BrandSourceDocumentListPage(rows, hasMore, tagNames);
    }

    public async Task<BrandSourceDocumentDetail?> FindDetailAsync(
        Guid documentId, CancellationToken cancellationToken)
    {
        if (await documents.FindDetailAsync(documentId, cancellationToken) is not { } record)
        {
            return null;
        }

        // The same second read the list makes, for one id. Asked only once the document is known to exist, so
        // an unreadable id costs one query rather than two.
        var tagNames = await documents.TagNamesAsync([documentId], cancellationToken);

        return new BrandSourceDocumentDetail(
            record, tagNames.TryGetValue(documentId, out var names) ? names : []);
    }

    public Task<BrandSourceDocumentStatus?> FindStatusAsync(Guid documentId, CancellationToken cancellationToken) =>
        documents.FindStatusAsync(documentId, cancellationToken);

    public Task<(IReadOnlyList<BrandSourceDocumentVersionSummaryRecord> Rows, bool HasMore)> ListVersionsAsync(
        BrandSourceDocumentVersionListCriteria criteria, CancellationToken cancellationToken) =>
        documents.ListVersionsAsync(criteria, cancellationToken);

    public Task<BrandSourceDocumentVersionListContextRecord?> FindVersionListContextAsync(
        Guid documentId, CancellationToken cancellationToken) =>
        documents.FindVersionListContextAsync(documentId, cancellationToken);

    public async Task<BrandSourceDownloadResult> OpenVersionAsync(
        Guid documentId, int versionNumber, CancellationToken cancellationToken)
    {
        if (await documents.FindVersionObjectAsync(documentId, versionNumber, cancellationToken) is not { } version)
        {
            return new BrandSourceDownloadResult(BrandSourceDownloadOutcome.Missing);
        }

        BrandSourceObjectContent? content;

        try
        {
            content = await objects.OpenReadAsync(version.ObjectKey, cancellationToken);
        }
        catch (ObjectStoreUnavailableException exception)
        {
            // Translated here, like the store's other failures: this is the layer that knows an object read
            // was attempted, and a provider exception is not something Business has a word for.
            logger.LogError(
                exception,
                "Private storage could not be reached reading version {VersionNumber} of brand source document {DocumentId}.",
                versionNumber,
                documentId);

            return new BrandSourceDownloadResult(BrandSourceDownloadOutcome.StorageUnavailable);
        }

        if (content is null)
        {
            // A committed row naming bytes that are not there. StoreAsync is built so this cannot happen —
            // the object is written before the row and removed again if the save fails — so reaching here
            // means something outside the application removed it. Logged at error because nothing in the
            // request can put it right, and answered as a fault rather than as a missing document.
            logger.LogError(
                "Version {VersionNumber} of brand source document {DocumentId} has no stored object.",
                versionNumber,
                documentId);

            return new BrandSourceDownloadResult(BrandSourceDownloadOutcome.ObjectMissing);
        }

        // The stream is handed on without being read here, and the metadata beside it is the row's rather than
        // the store's: the row is what the rest of the application has agreed about this version, and a store
        // that disagreed about a size would be a fault to find, not a number to publish.
        return new BrandSourceDownloadResult(
            BrandSourceDownloadOutcome.Opened,
            new BrandSourceDownload(
                content,
                content.Content,
                BrandSourceDownloadFileName.For(version.Title, version.VersionNumber, version.MediaType),
                version.MediaType,
                version.SizeBytes,
                version.ContentChecksum));
    }

    /// <summary>How far <see cref="WriteOriginalAsync"/> got. <c>Written</c> means the key is on the version.</summary>
    private enum ObjectWriteStep
    {
        Written = 1,
        Unavailable = 2,
        ContentChanged = 3,
    }

    /// <summary>
    /// Writes one version's original bytes and records where they went, before any row names them.
    /// </summary>
    /// <remarks>
    /// Shared by the first upload and every replacement, so the two cannot drift on the thing they must agree
    /// about: that storage measured the same bytes that were inspected and scanned. The key is derived from
    /// the version's id, so a replacement writes a new object and can neither overwrite nor delete the one
    /// the previous version named.
    /// </remarks>
    private async Task<ObjectWriteStep> WriteOriginalAsync(
        BrandSourceDocument document, BrandSourceDocumentVersion version, Stream content, CancellationToken cancellationToken)
    {
        content.Position = 0;

        // The inspected size is the limit: one byte more is already not the file that was scanned.
        var write = await objects.PutOriginalAsync(
            document.Id, version.Id, content, version.MediaType, version.SizeBytes, cancellationToken);

        BrandSourceObject stored;

        switch (write.Outcome)
        {
            case BrandSourceObjectWriteOutcome.Stored:
                stored = write.Object!;
                _written[version.Id] = stored;
                break;

            case BrandSourceObjectWriteOutcome.AlreadyExists when _written.TryGetValue(version.Id, out var earlier):
                stored = earlier;
                break;

            case BrandSourceObjectWriteOutcome.Unavailable:
                // Nothing can be assumed stored, and nothing can be assumed absent either.
                await RemoveAsync(document.Id, version.Id);
                return ObjectWriteStep.Unavailable;

            case BrandSourceObjectWriteOutcome.TooLarge:
                return ObjectWriteStep.ContentChanged;

            default:
                throw new InvalidOperationException($"Unexpected brand source object write outcome '{write.Outcome}'.");
        }

        if (stored.SizeBytes != version.SizeBytes
            || !string.Equals(stored.ContentChecksum, version.ContentChecksum, StringComparison.Ordinal))
        {
            await RemoveAsync(document.Id, version.Id);
            return ObjectWriteStep.ContentChanged;
        }

        version.ObjectKey = stored.ObjectKey;

        return ObjectWriteStep.Written;
    }

    private async Task<IReadOnlyList<string>> AttachTagsAsync(
        BrandSourceDocument document, IReadOnlyList<BrandSourceTagInput> tags, CancellationToken cancellationToken)
    {
        if (tags.Count == 0)
        {
            return [];
        }

        var existing = (await documents.FindTagsAsync([.. tags.Select(tag => tag.NormalizedName)], cancellationToken))
            .ToDictionary(tag => tag.NormalizedName, StringComparer.Ordinal);
        var names = new List<string>(tags.Count);

        foreach (var input in tags)
        {
            if (!existing.TryGetValue(input.NormalizedName, out var tag))
            {
                tag = new BrandSourceTag
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = document.WorkspaceId,
                    Name = input.Name,
                    NormalizedName = input.NormalizedName,
                    CreatedAt = document.CreatedAt,
                };
                documents.Add(tag);
            }

            names.Add(tag.Name);
            document.Tags.Add(new BrandSourceDocumentTag
            {
                WorkspaceId = document.WorkspaceId,
                BrandSourceDocumentId = document.Id,
                BrandSourceTagId = tag.Id,
            });
        }

        return names;
    }

    /// <summary>Compensation, so it is not cancelled with the request and does not replace the failure it follows.</summary>
    private async Task RemoveAsync(Guid documentId, Guid versionId)
    {
        _written.Remove(versionId);

        try
        {
            await objects.DeleteAsync(
                BrandSourceObjectKey.ForOriginal(workspace.WorkspaceId, documentId, versionId), CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Brand source object could not be removed after a failed upload; it is unreferenced. workspaceId={WorkspaceId} documentId={DocumentId} versionId={VersionId}",
                workspace.WorkspaceId, documentId, versionId);
        }
    }
}
