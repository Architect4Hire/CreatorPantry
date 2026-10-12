using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Business;

/// <summary>
/// The rules for naming a picture the library has just kept on the piece of work it was kept for.
/// </summary>
public interface ICreativeContextKeptPictureBusiness
{
    /// <summary>
    /// Whether this piece of work could take one more kept picture: null when it could, or the refusal.
    /// </summary>
    /// <remarks>
    /// Asked by the library before it stores a picture a creator is keeping for this work, so a work that
    /// cannot name it is discovered before any byte is written rather than after.
    /// </remarks>
    Task<OperationError?> CheckAsync(Guid contextId, CancellationToken cancellationToken);

    /// <summary>
    /// Names a library asset on this piece of work as a picture it made: null when it is named, or the refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>No concurrency token, unlike <c>ICreativeContextBusiness.AddReferenceAsync</c>.</strong> That
    /// appends what a creator chose while looking at the work, so it must be refused when the work has moved
    /// on. This appends the one thing the server itself just made for it, which is true whatever else has
    /// changed. The save still quotes the row version this call read, so a writer that got there in between is
    /// a refusal and never a lost update.
    /// </para>
    /// <para>
    /// <strong>No lookup of the asset either.</strong> The other route asks the library whether a picture may
    /// be named; here the library is the caller, inside the transaction that is creating it.
    /// </para>
    /// <para>
    /// <strong>Already named is done.</strong> Keeping a picture is repeatable, and a repeat must not fail on
    /// the reference the first attempt wrote.
    /// </para>
    /// <para>
    /// It opens no transaction of its own, so it enlists in the one the library holds: the picture and the
    /// work's pointer to it commit together or not at all.
    /// </para>
    /// </remarks>
    Task<OperationError?> RecordAsync(
        Guid contextId, Guid mediaAssetId, int mediaAssetVersionNumber, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ICreativeContextKeptPictureBusiness"/>
internal sealed class CreativeContextKeptPictureBusiness(ICreativeContextDataLayer dataLayer, IClock clock)
    : ICreativeContextKeptPictureBusiness
{
    public async Task<OperationError?> CheckAsync(Guid contextId, CancellationToken cancellationToken)
    {
        var context = await dataLayer.FindAsync(contextId, cancellationToken);

        if (context is null)
        {
            return NotFound();
        }

        return context.References.Count >= CreativeContextPolicy.MaxReferences ? ReferenceLimit() : null;
    }

    public async Task<OperationError?> RecordAsync(
        Guid contextId, Guid mediaAssetId, int mediaAssetVersionNumber, CancellationToken cancellationToken)
    {
        var context = await dataLayer.FindForUpdateAsync(contextId, cancellationToken);

        if (context is null)
        {
            return NotFound();
        }

        if (context.References.Any(existing =>
                existing.Kind is CreativeContextReferenceKind.DamAsset
                && existing.Purpose is CreativeContextReferencePurpose.Keeper
                && existing.MediaAssetId == mediaAssetId))
        {
            return null;
        }

        if (context.References.Count >= CreativeContextPolicy.MaxReferences)
        {
            return ReferenceLimit();
        }

        var now = clock.UtcNow;

        context.References.Add(new CreativeContextReference
        {
            Id = Guid.NewGuid(),
            CreativeContextId = context.Id,
            Kind = CreativeContextReferenceKind.DamAsset,

            // A picture this work made, never one it takes cues from (AF.4.3).
            Purpose = CreativeContextReferencePurpose.Keeper,
            SortOrder = context.References.Count == 0 ? 0 : context.References.Max(existing => existing.SortOrder) + 1,
            MediaAssetId = mediaAssetId,
            MediaAssetVersionNumber = mediaAssetVersionNumber,
            AddedAt = now,
        });

        // Moved so the root is written, which is what puts the row version into this save's WHERE clause.
        context.UpdatedAt = now;

        return await dataLayer.SaveAsync(context, audit: null, cancellationToken) switch
        {
            CreativeContextWrite.Saved => null,

            CreativeContextWrite.Stale => new OperationError(
                ContentErrorCodes.CreativeContextStale,
                "This creative context changed while the picture was being kept. Try again.",
                new Dictionary<string, string[]>()),

            _ => OperationError.Validation(
                ContentErrorCodes.CreativeContextReferenceUnprocessable,
                "That picture cannot be added to this creative context.",
                [("creativeContextId", "This piece of work could not be made to name the picture.")]),
        };
    }

    private static OperationError NotFound() => new(
        ContentErrorCodes.CreativeContextNotFound,
        "That creative context could not be found.",
        new Dictionary<string, string[]>());

    private static OperationError ReferenceLimit() => new(
        ContentErrorCodes.CreativeContextReferenceLimit,
        $"A creative context names at most {CreativeContextPolicy.MaxReferences} sources. Remove one first.",
        new Dictionary<string, string[]>());
}
