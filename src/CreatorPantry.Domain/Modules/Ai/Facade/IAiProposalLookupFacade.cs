using CreatorPantry.Domain.Modules.Ai.Business;

namespace CreatorPantry.Domain.Modules.Ai.Facade;

/// <summary>
/// The cross-module entry point for resolving an <c>AiProposal</c> id by itself, for a module that stores one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A class of its own rather than a method on <see cref="IAiProposalFacade"/>,</strong> for the reason
/// that interface's <c>SummarizeOutstandingAsync</c> records: <c>AiProposalBusiness</c> depends on
/// <c>IRecipeFacade</c>, a quota gate and the task options, so a caller wanting one existence check would
/// resolve the whole request stack to get it. This resolves a repository and nothing else.
/// </para>
/// <para>
/// <strong>And keyed by the proposal's own id,</strong> which <see cref="IAiProposalFacade.GetAsync"/> is not:
/// that one names a recipe and a <em>request</em>, and an image-prompt proposal (12.4, 12.4a, 12.5) need not
/// belong to a recipe at all. The id another module stores is <c>AiProposal.Id</c>, which is the one with the
/// workspace-paired alternate key that foreign keys point at.
/// </para>
/// <para>
/// It exists because <c>PromptRecord</c> is immutable: 12.3a has to resolve its provenance pin before writing a
/// row that can never be corrected, and the alternative — reading this module's tables from another module's
/// repository — is the cross-module reach the boundary rules exist to stop.
/// </para>
/// </remarks>
public interface IAiProposalLookupFacade
{
    /// <inheritdoc cref="IAiProposalLookupBusiness.ExistsAsync"/>
    /// <remarks>
    /// No role check: <c>Viewer</c> is the lowest role and a resolved workspace context already is the
    /// authorization, as for every other read on this module's boundary. It discloses nothing beyond whether
    /// the caller's own workspace holds the id they already have.
    /// </remarks>
    Task<bool> ExistsAsync(Guid aiProposalId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiProposalLookupFacade"/>
internal sealed class AiProposalLookupFacade(IAiProposalLookupBusiness business) : IAiProposalLookupFacade
{
    public Task<bool> ExistsAsync(Guid aiProposalId, CancellationToken cancellationToken) =>
        business.ExistsAsync(aiProposalId, cancellationToken);
}
