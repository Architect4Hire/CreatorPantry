namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// One image-generation operation and the images it has produced so far (IMG-003, IMG-005).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists at all.</strong> 12.7's <c>POST</c> answers <c>202</c> with
/// <see cref="GeneratedImageOperationServiceModel"/>, which carries a status and two counts but no image
/// identities — so a client had the operation and no way to learn a single thing in it. Progress, a contact
/// sheet, a download and a decline were all unreachable. This is the read that makes them reachable, and 12.6's
/// own note anticipated it ("a detail surface is where a creator reads their own request back").
/// </para>
/// <para>
/// <strong>It repeats the <c>202</c> body's fields deliberately.</strong> <see cref="StagedCount"/> equals
/// <c>Images.Count</c>, because a row exists only once there are bytes to describe it — so the count is
/// redundant on its own terms. It is here so that a client decodes one operation shape rather than two, and so a
/// progress meter reads a number rather than deriving one.
/// </para>
/// <para>
/// <strong>No prompt text</strong>, for the reason <see cref="GeneratedImageOperationServiceModel"/> gives: the
/// caller supplied it, the row keeps it for provenance, and echoing private creator content through a status
/// read that has no use for it would put it in response bodies and logs. A surface built for a creator to read
/// their own request back can add it as a compatible addition.
/// </para>
/// <para>
/// <strong>No object key, no checksum, no address of any kind.</strong> Nothing here is or becomes one
/// (media.md). The repository projects rather than materialising entities, so a later edit cannot reintroduce a
/// key by accident.
/// </para>
/// </remarks>
/// <param name="ProviderName">
/// Null until a worker has claimed the operation. Provenance, the same as a proposal's: a creator reviewing
/// generated content is entitled to know what produced it. A creator-facing surface need not render it, and
/// EASE-004 asks that it does not.
/// </param>
/// <param name="FailureSummary">
/// Present only on a failure, and never a provider's words.
/// </param>
/// <remarks>
/// <strong>That last point is structural rather than a promise.</strong> <c>GeneratedImageResult</c> carries an
/// outcome and bytes and nothing else, so a provider message has nowhere to travel — every summary is a fixed
/// sentence written by Business and truncated by the data layer. A provider payload could not reach here without
/// a new field on the gateway's own result type.
/// </remarks>
public sealed record GeneratedImageOperationDetailServiceModel(
    Guid Id,
    GeneratedImageOperationStatus Status,
    int VariantCount,
    int StagedCount,
    string? ProviderName,
    string? ModelName,
    string? FailureCategory,
    string? FailureSummary,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<StagedImageServiceModel> Images);

/// <summary>
/// One staged image of an operation, as a client needs to show and act on it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A row exists only once there are bytes</strong>, so the presence of one of these means an image
/// arrived — which is what lets a contact sheet fill in as variants land rather than appearing all at once.
/// </para>
/// <para>
/// <strong><see cref="Status"/> is the whole story about availability.</strong> `Staged` is the only state with
/// bytes a client should ask for; `Kept`, `Rejected` and `Expired` are terminal, and a surface renders each as
/// itself rather than as a broken image. Whether the sweep has already taken a declined image's bytes is not
/// published, because nothing a client may do depends on it.
/// </para>
/// </remarks>
/// <param name="VariantIndex">
/// Which of the asked-for variants this is, from zero. Stable, so a contact sheet keeps its order as rows arrive.
/// </param>
/// <param name="MediaType">
/// Established from the returned bytes at staging, never from anything a provider declared.
/// </param>
/// <param name="RetentionExpiresAt">
/// When the sweep becomes entitled to remove the bytes. Published so a creator can be told their candidates are
/// not permanent before they lose them, rather than afterwards.
/// </param>
public sealed record StagedImageServiceModel(
    Guid Id,
    int VariantIndex,
    GeneratedImageStatus Status,
    string MediaType,
    int Width,
    int Height,
    long SizeBytes,
    DateTimeOffset RetentionExpiresAt,
    DateTimeOffset CreatedAt,
    long? WebSizeBytes = null,
    long? ThumbnailSizeBytes = null);
