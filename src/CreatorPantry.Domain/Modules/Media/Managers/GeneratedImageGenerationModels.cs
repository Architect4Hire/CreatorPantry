namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// One image-generation request, as the application reports it back.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No prompt text.</strong> The caller supplied it and the row keeps it for provenance; echoing it
/// through every status read would put private creator content into response bodies and logs that have no
/// use for it. 12.8's detail surface is where a creator reads their own request back.
/// </para>
/// <para>
/// <strong>No object key.</strong> Nothing here is or becomes an address (media.md).
/// </para>
/// </remarks>
public sealed record GeneratedImageOperationServiceModel(
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
    Guid? RecipeId = null,
    Guid? RecipeVersionId = null,
    Guid? CreativeContextId = null,
    GeneratedImageQualityClass? QualityClass = null);

/// <summary>
/// What a caller must supply to request images. A trusted-caller shape, not an HTTP ViewModel.
/// </summary>
/// <remarks>
/// <para>
/// There is no controller for this yet, and this is deliberately not shaped as though there were. When one
/// arrives it brings its own ViewModel and FluentValidation rules, and maps to this — the seam stays the
/// same whether the caller is a controller, an AI plugin or a test.
/// </para>
/// <para>
/// <strong>No workspace id, and there never will be one.</strong> The workspace comes from the resolved
/// context. A request-supplied one is the thing tenancy.md forbids outright, and leaving the field off the
/// type is a stronger guarantee than validating it away.
/// </para>
/// </remarks>
/// <param name="AiProposalId">The IMG-002 proposal the prompt was composed from, when it was composed.</param>
/// <param name="QualityClass">
/// How good a picture to make (AP.3.1); null for the host's default class. Never a model: what a class sends is
/// configuration.
/// </param>
/// <param name="IdempotencyKey">
/// What makes a repeated request return the first answer. Required, because image generation is the most
/// expensive call this product makes and a lost response must never become a second charge.
/// </param>
public sealed record GeneratedImageRequest(
    string PromptText,
    string? AvoidText,
    Guid? AiProposalId,
    int VariantCount,
    string IdempotencyKey,
    GeneratedImageSubject? Subject = null,
    GeneratedImageQualityClass? QualityClass = null);

/// <summary>
/// What a run of pictures was asked for: a recipe, an exact version of it, a piece of work — any or none.
/// </summary>
/// <remarks>
/// Recorded on the run and never read by the worker, which sends the prompt as written. Each id is resolved
/// inside the route's workspace before anything is queued.
/// </remarks>
public sealed record GeneratedImageSubject(Guid? RecipeId, Guid? RecipeVersionId, Guid? CreativeContextId)
{
    /// <summary>A run asked for nothing in particular.</summary>
    public static GeneratedImageSubject None { get; } = new(null, null, null);
}
