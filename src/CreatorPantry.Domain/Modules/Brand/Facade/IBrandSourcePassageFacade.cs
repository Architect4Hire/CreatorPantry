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
}

internal sealed class BrandSourcePassageFacade(IBrandSourcePassageBusiness business) : IBrandSourcePassageFacade
{
    public Task<BrandSourcePassageSetServiceModel> ListPassagesAsync(
        IReadOnlyList<BrandSourcePassageSelector> selectors, CancellationToken cancellationToken) =>
        business.ListPassagesAsync(selectors, cancellationToken);
}
