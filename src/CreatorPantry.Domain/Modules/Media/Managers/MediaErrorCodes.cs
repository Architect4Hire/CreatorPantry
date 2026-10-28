namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// The Media module's stable, machine-readable error codes.
/// </summary>
/// <remarks>
/// Part of the API contract once a route returns one (api-contract.md): a client branches on the code, so
/// renaming one is a breaking change even though the message beside it is free to be reworded.
/// </remarks>
public static class MediaErrorCodes
{
    /// <summary>The caller's membership role does not allow generating images in this workspace.</summary>
    public const string GenerationForbidden = "media.generation.forbidden";

    /// <summary>The request was not well formed. The field errors say which part.</summary>
    public const string GenerationInvalidRequest = "media.generation.invalid_request";

    /// <summary>
    /// The request names a prompt proposal that is not in this workspace. Maps to 422.
    /// </summary>
    /// <remarks>
    /// One answer for an id that names nothing and for another workspace's proposal, naming only the field
    /// (tenancy.md).
    /// </remarks>
    public const string GenerationProposalUnprocessable = "media.generation.proposal.unprocessable";

    /// <summary>
    /// The request names a recipe, or a version of one, that is not in this workspace. Maps to 422.
    /// </summary>
    /// <remarks>
    /// One answer for an id that names nothing, another workspace's recipe, and a version that is not that
    /// recipe's, naming only the field (tenancy.md).
    /// </remarks>
    public const string GenerationRecipeUnprocessable = "media.generation.recipe.unprocessable";

    /// <summary>
    /// The request names a piece of work that is not in this workspace. Maps to 422. One answer for an id
    /// that names nothing and for another workspace's.
    /// </summary>
    public const string GenerationContextUnprocessable = "media.generation.context.unprocessable";

    /// <summary>
    /// The request or quote names a quality class this host does not offer (AP.3.1). Maps to 422, naming
    /// <c>qualityClass</c>: well formed — it is one of the five — and still not something this host can make.
    /// </summary>
    public const string GenerationQualityClassUnprocessable = "media.generation.quality_class.unprocessable";

    /// <summary>
    /// The request or quote names a quality class this host offers but the account's plan does not allow
    /// (PRICE-006/007, AP.4.3). Maps to 403, naming <c>qualityClass</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 403 and not 422, for the reason <c>ai.quota.suspended</c> is a 403: the class is a real one this host can
    /// make, and what refuses it is what the account is entitled to. <c>retry</c> is therefore <c>none</c> — the
    /// same request can never succeed, where a 429 would invite a wait that changes nothing.
    /// </para>
    /// <para>
    /// The problem names the plan that would allow it, in the sentence and in <c>requiresPlan</c> (its key) and
    /// <c>requiresPlanName</c>, with <c>qualityClass</c> for a client that words the class itself. A class no
    /// plan allows carries neither, and says so in one sentence rather than naming a plan that does not exist.
    /// </para>
    /// <para>
    /// Refused at the request and at the quote it prices, and never against a replayed key: a replay's answer is
    /// the operation it already bought, at the class it was bought at. Not re-checked when the worker runs — the
    /// credits were held at the request, so a downgrade in between does not cancel pictures already paid for.
    /// </para>
    /// </remarks>
    public const string GenerationQualityClassForbidden = "media.generation.quality_class.forbidden";

    /// <summary>
    /// The prompt and its avoid list are longer than the model the chosen quality class sends to takes, and a
    /// shorter prompt would fit (AP.3.2). Maps to 422, naming <c>promptText</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Refused, never cut: a prompt shortened at a character boundary is a picture of something the creator did
    /// not ask for. The problem carries <c>characterLimit</c>, <c>characterCount</c> and <c>promptBudget</c> — how
    /// long the prompt may be with the avoid list kept whole — so a client can say what to do or ask for a
    /// compact variant to that budget, without parsing a sentence.
    /// </para>
    /// <para>
    /// Checked after the class is chosen and never against a replayed key, whose answer is the operation it
    /// already bought.
    /// </para>
    /// </remarks>
    public const string GenerationPromptUnprocessable = "media.generation.prompt.unprocessable";

    /// <summary>
    /// The avoid list alone leaves too little of the chosen class's limit for any prompt (AP.3.2). Maps to 422,
    /// naming <c>avoidText</c>, with the same figures as <see cref="GenerationPromptUnprocessable"/>.
    /// </summary>
    /// <remarks>
    /// Apart from <see cref="GenerationPromptUnprocessable"/> because the remedy is different: a shorter prompt
    /// cannot fix it, and the avoid list is the creator's own and is never shortened for them.
    /// </remarks>
    public const string GenerationAvoidUnprocessable = "media.generation.avoid.unprocessable";

    /// <summary>
    /// No image-generation operation of this workspace has that id.
    /// </summary>
    /// <remarks>
    /// <strong>Also the answer for another workspace's operation</strong>, in the same words, so neither
    /// discloses the other (tenancy.md). The global query filter makes the two genuinely one case rather than
    /// merely reported as one. Carries no field errors: the id came from the route, so there is no field to name.
    /// </remarks>
    /// <summary>
    /// This host cannot price the pictures the request would make, so it will not queue them (AP.2.5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// 503, from the <c>.unavailable</c> suffix, and it belongs there: an image model with no credit rate is a
    /// misconfiguration of this deployment rather than anything about the caller or their allowance. Startup
    /// refuses a host that can request an unrated picture, so reaching this means a model no sizing profile
    /// covers.
    /// </para>
    /// <para>
    /// Deliberately not an exhausted-allowance refusal, which would tell a creator to wait for a period reset
    /// that will not change the answer, and deliberately carries no figures: their balance is not the problem.
    /// </para>
    /// </remarks>
    public const string GenerationUnpriceable = "media.generation.unavailable";

    public const string GenerationOperationNotFound = "media.generation_operation.not_found";

    /// <summary>No such generated image in the resolved workspace. Also a neighbour's, deliberately.</summary>
    public const string StagedImageNotFound = "media.staged_image.not_found";

    /// <summary>The caller's membership role does not allow declining generated images.</summary>
    public const string StagedImageForbidden = "media.staged_image.forbidden";

    /// <summary>The image is kept or expired, and neither can be declined.</summary>
    public const string StagedImageNotRejectable = "media.staged_image.conflict";

    /// <summary>The image exists and its bytes could not be reached. Retrying is the remedy.</summary>
    public const string StagedImageStorageUnavailable = "media.staged_image.unavailable";

    /// <summary>A staged-image request with a parameter this route does not accept.</summary>
    public const string StagedImageInvalidRequest = "media.staged_image.invalid_request";

    /// <summary>The asset creation request was not well formed. The field errors say which part.</summary>
    public const string AssetInvalidRequest = "media.asset.invalid_request";

    /// <summary>The caller's membership role does not allow creating assets in this workspace.</summary>
    public const string AssetForbidden = "media.asset.forbidden";

    /// <summary>The bytes are not something this library can store, whatever they were called.</summary>
    public const string AssetUnsupported = "media.asset.unprocessable";

    /// <summary>
    /// Something the request named by id is not in this workspace: the asset itself on a read (DAM-003), or the
    /// staged image a creation was to be made from (DAM-001).
    /// </summary>
    /// <remarks>
    /// One code for both, deliberately. A client cannot act differently on them — neither is worth retrying, and
    /// both are resolved by going back to a list — and splitting it would publish a distinction whose only use
    /// would be telling a caller which of two things they cannot see exists (tenancy.md). An unknown id, another
    /// workspace's asset and a tombstone the caller did not ask for all arrive here as one answer.
    /// </remarks>
    public const string AssetNotFound = "media.asset.not_found";

    /// <summary>Private storage could not be reached. Nothing was written, and retrying is the remedy.</summary>
    public const string AssetStorageUnavailable = "media.asset.unavailable";

    /// <summary>The rows could not be committed. Nothing was written, including the object.</summary>
    public const string AssetNotCreated = "media.asset.conflict";

    /// <summary>
    /// The edit quoted a concurrency token the asset has moved past: somebody else saved first (DAM-004).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="AssetNotCreated"/> although both are 409s, because the recovery differs and that
    /// is the only thing an error code is for. A failed commit is worth retrying as-is; a stale token is not — the
    /// client has to re-read, see what the other edit did, and decide. One code for both would tell a caller the
    /// status and nothing they could act on.
    /// </remarks>
    public const string AssetStaleToken = "media.asset.stale.conflict";

    /// <summary>
    /// Another upload committed this version number first (DAM-010). Retryable as-is.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="AssetStaleToken"/> and <see cref="AssetNotCreated"/> although all three are 409s,
    /// because the recovery differs and that is what an error code is for: this one is retried unchanged and will take
    /// the next number, where a stale token needs a re-read first and a failed commit needs nothing but a retry of the
    /// same request.
    /// </remarks>
    public const string AssetVersionTaken = "media.asset.version_taken.conflict";

    /// <summary>A library search named a filter this server could not read.</summary>
    public const string AssetSearchInvalidRequest = "media.asset_search.invalid_request";

    /// <summary>A cursor was issued for different filters, a different ordering or another workspace.</summary>
    public const string AssetCursorInvalidRequest = "media.asset_search.cursor_invalid_request";
}
