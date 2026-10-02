using CreatorPantry.Domain.Modules.Brand.Business;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Facade;

/// <summary>
/// Reads bounded passages of the workspace's own source documents, for a caller that needs to ground an answer
/// in them and cite what it used.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Its own facade, not a method on <c>IBrandSourceEmbeddingFacade</c>.</strong> That one is the seam the
/// Worker's embedding driver calls, is not exposed over HTTP, and is about producing chunk sets; this is a read
/// of what they produced, with a different caller and a different contract.
/// </para>
/// <para>
/// <strong>Not exposed over HTTP either.</strong> No creator asks for passages: this exists so the AI module can
/// assemble a grounded prompt from material the resolved workspace already owns. Cross-module traffic is facade
/// to facade, so the AI module calls this interface and never the chunk tables.
/// </para>
/// <para>
/// Role is deliberately not checked here. The caller is a background task running under a workspace the worker
/// already resolved and validated, and the material is the workspace's own source documents, which any member
/// may read.
/// </para>
/// </remarks>
public interface IBrandSourcePassageFacade
{
    /// <summary>
    /// The passages the resolved workspace can supply for the named document versions, and which of them it
    /// could supply none for.
    /// </summary>
    /// <param name="selectors">
    /// Document and version-number pairs. Deduplicated and capped server-side; a pair this workspace cannot
    /// read simply supplies nothing, and the reason is not reported.
    /// </param>
    Task<BrandSourcePassageSetServiceModel> ListPassagesAsync(
        IReadOnlyList<BrandSourcePassageSelector> selectors, CancellationToken cancellationToken);

    /// <summary>
    /// Which of these documents' current versions have indexed text a generation could read — their ids only.
    /// </summary>
    /// <remarks>
    /// For deciding whether a document would be used, never for using it: no passage text leaves, and the list is
    /// read in as many batches as it needs rather than cut at the grounding limit.
    /// </remarks>
    Task<IReadOnlyList<Guid>> ListDocumentsWithTextAsync(
        IReadOnlyList<BrandSourcePassageSelector> selectors, CancellationToken cancellationToken);

    /// <summary>
    /// Which source document version each of the named passages came from. No passage text is read.
    /// </summary>
    /// <param name="passageIds">
    /// Passage ids, as <see cref="BrandSourcePassageServiceModel.PassageId"/> published them. Deduplicated and
    /// capped server-side; one this workspace cannot read is simply absent from the answer, and the reason is
    /// not reported.
    /// </param>
    /// <remarks>
    /// <para>
    /// Exists so a caller holding citations can store provenance: 11A.18 turns the passages an accepted
    /// proposal cited into the <c>BrandStyleGuideSourceLink</c> rows of the guide version it writes, and a link
    /// names a document version rather than a passage. Only this module can answer it, because the chunk tables
    /// are its own — which is why it is a facade method and not something the AI module works out for itself.
    /// </para>
    /// <para>
    /// A read, so no role is checked, for the reason <see cref="ListPassagesAsync"/> gives. It resolves
    /// superseded sets as well as current ones: a citation pinned to the version it was read from has to stay
    /// resolvable after the document is replaced, or pinning would be pointless.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<BrandSourcePassageOriginServiceModel>> ResolveOriginsAsync(
        IReadOnlyList<Guid> passageIds, CancellationToken cancellationToken);
}

internal sealed class BrandSourcePassageFacade(IBrandSourcePassageBusiness business) : IBrandSourcePassageFacade
{
    public Task<BrandSourcePassageSetServiceModel> ListPassagesAsync(
        IReadOnlyList<BrandSourcePassageSelector> selectors, CancellationToken cancellationToken) =>
        business.ListPassagesAsync(selectors, cancellationToken);

    public Task<IReadOnlyList<Guid>> ListDocumentsWithTextAsync(
        IReadOnlyList<BrandSourcePassageSelector> selectors, CancellationToken cancellationToken) =>
        business.ListDocumentsWithTextAsync(selectors, cancellationToken);

    public Task<IReadOnlyList<BrandSourcePassageOriginServiceModel>> ResolveOriginsAsync(
        IReadOnlyList<Guid> passageIds, CancellationToken cancellationToken) =>
        business.ResolveOriginsAsync(passageIds, cancellationToken);
}
