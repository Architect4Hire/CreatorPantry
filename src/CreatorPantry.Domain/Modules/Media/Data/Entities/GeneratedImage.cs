using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Domain.Modules.Media.Data.Entities;

/// <summary>
/// One image a provider returned, staged privately while the creator decides (IMG-005, IMG-006).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A row exists only once there are bytes to describe.</strong> Variants are inserted as they arrive
/// rather than reserved when the request is made, so no row ever claims an object key, a checksum or
/// dimensions it does not have — and a request that failed leaves no rows describing images that never
/// existed. <c>UX_GeneratedImages_Operation_Variant</c> is what stops a retry staging the same variant twice.
/// </para>
/// <para>
/// <strong>The bytes are in private staging storage and there is no URL here.</strong>
/// <see cref="ObjectKey"/> is an opaque server-generated name inside a private container: it is never built
/// from creator text, never returned to a browser, and never a public or permanent address. Reaching the
/// bytes is an authorized read through a facade (12.8), the same shape the brand source library uses.
/// <c>GeneratedImageModelShapeTests</c> fails on a property named like a URL.
/// </para>
/// <para>
/// <strong>No DAM asset id.</strong> A kept image becomes a DAM asset in 12.9, and that is where the link
/// belongs: a nullable id here would be a column nothing could write or verify until then — exactly what
/// <c>PromptRecord.GeneratedImageId</c> was, and the reason it stayed unwritable until this prompt.
/// </para>
/// <para>
/// <strong>No alt text and no rights metadata either</strong>, which media.md requires of an <em>asset</em>.
/// A staged image is a candidate, not an asset: alt text describes what a creator chose to publish and is
/// authored when they choose it (12.9), and inventing it here would mean describing pixels nothing has
/// analysed — the claim ai.md forbids. Rights follow the same line: the provider and model that produced
/// the bytes are recorded below, which is the provenance a staged image actually has.
/// </para>
/// </remarks>
public sealed class GeneratedImage : IWorkspaceOwned
{
    public Guid Id { get; set; }

    /// <summary>Stamped by <c>WorkspaceOwnershipInterceptor</c> from the resolved context, never by hand.</summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>The request that produced it, workspace-paired so a neighbour's is unrepresentable.</summary>
    public Guid GeneratedImageOperationId { get; set; }

    /// <summary>
    /// Which of the request's variants this is, from zero.
    /// </summary>
    /// <remarks>
    /// Unique within the operation. It is the identity a retry collides on: a worker that staged variant two,
    /// lost its response and ran again loses the index rather than storing a duplicate.
    /// </remarks>
    public int VariantIndex { get; set; }

    public GeneratedImageStatus Status { get; set; }

    /// <summary>
    /// The opaque name of the object in private staging storage.
    /// </summary>
    /// <remarks>
    /// Unique across the table: one row per object, so two rows pointing at the same bytes — which would make
    /// a rejection delete an image someone kept — is unrepresentable rather than merely unlikely.
    /// </remarks>
    public string ObjectKey { get; set; } = string.Empty;

    /// <summary>The type established from the returned bytes, never one a provider merely declared.</summary>
    public string MediaType { get; set; } = string.Empty;

    public int Width { get; set; }

    public int Height { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>
    /// The checksum of the stored bytes, with its algorithm prefix.
    /// </summary>
    /// <remarks>
    /// What makes a staged image's identity verifiable: 12.8's retrieval can state a strong entity tag from
    /// it, and a byte stream that no longer matches is a storage fault rather than a new image.
    /// </remarks>
    public string ContentChecksum { get; set; } = string.Empty;

    /// <summary>The provider that returned it.</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>The model that produced it.</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>The deployment that served it, when the provider distinguishes one.</summary>
    public string? ModelDeployment { get; set; }

    /// <summary>
    /// When the sweep may expire this image, set from <see cref="MediaPolicy.StagedImageTimeToLive"/> at
    /// staging.
    /// </summary>
    /// <remarks>
    /// Stored rather than computed so the sweep is a keyed read on a filtered index rather than a scan. It is
    /// not cleared when an image is kept: the deadline it had is a historical fact, and the expiry index is
    /// filtered to <see cref="GeneratedImageStatus.Staged"/> so a kept row is never expired. Its bytes are
    /// still collected, by the purge rather than the expiry — see <see cref="GeneratedImageStatus.Kept"/>.
    /// </remarks>
    public DateTimeOffset RetentionExpiresAt { get; set; }

    /// <summary>
    /// When the retention sweep removed this image's bytes from staging storage. Null while they are there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row outlives its bytes on purpose. A creator who generated four images and rejected three should
    /// still be able to see that they did, and <c>PromptRecord</c> may hold a foreign key to a row here that
    /// nothing may delete — so "the bytes are gone" is a fact recorded on the row rather than the row's
    /// absence.
    /// </para>
    /// <para>
    /// It is also what makes the sweep finite. Without it, every rejected image ever would be a candidate
    /// for deletion on every pass, and the sweep would spend a storage call per row per minute forever,
    /// learning nothing. With it, the purge queue empties.
    /// </para>
    /// </remarks>
    public DateTimeOffset? ObjectDeletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the status last moved — chosen, rejected or expired.</summary>
    public DateTimeOffset StatusChangedAt { get; set; }
}
