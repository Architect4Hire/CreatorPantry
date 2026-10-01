using CreatorPantry.Domain.Modules.Brand.Business;

namespace CreatorPantry.Domain.Modules.Brand.Facade;

/// <summary>
/// The application boundary for running a queued extraction. Reached by the Worker, and by nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No HTTP route reaches this.</strong> There is no ViewModel, no validator and no controller action,
/// because an extraction is not something a creator asks for directly: it is queued by the upload that produced
/// the version. The read of the extracted text, and the creator's correction of it, are their own seam.
/// </para>
/// <para>
/// <strong>And no role check</strong>, which is the one thing worth stating out loud on a facade. A worker runs
/// as <c>WorkspaceServiceIdentity</c>, whose role is <c>Viewer</c>, so a membership gate would refuse the only
/// caller this boundary has. What authorizes the call is not a role but the path it arrived by: the workspace was
/// resolved and validated from the claimed row before this was reached, and every read below is filtered by that
/// resolved context. Nothing here accepts a workspace id — not from an argument, not from the claim, not from
/// anywhere.
/// </para>
/// <para>
/// It is still a facade rather than the worker calling Business directly, and for the reason backend.md gives:
/// this is the application boundary, and a worker is one of the three kinds of caller that is supposed to arrive
/// at one.
/// </para>
/// </remarks>
public interface IBrandSourceExtractionFacade
{
    /// <summary>
    /// Runs one claimed extraction to a conclusion: reads the version, parses it, and records the outcome.
    /// </summary>
    /// <param name="operationId">The operation the claim named. Read through the resolved workspace, so an id from another workspace is simply absent.</param>
    /// <param name="leaseToken">The claim's token, quoted on every write so a worker that lost the lease writes nothing.</param>
    Task<BrandSourceExtractionRunOutcome> ExecuteAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken);
}

/// <remarks>
/// Straight through, and deliberately: there is nothing to validate because both arguments come from a claim this
/// process made rather than from a request, nothing to cache because an extraction runs once, and no policy to
/// evaluate for the reason the interface's remarks give. A facade that adds nothing is still the boundary.
/// </remarks>
internal sealed class BrandSourceExtractionFacade(IBrandSourceExtractionBusiness business)
    : IBrandSourceExtractionFacade
{
    public Task<BrandSourceExtractionRunOutcome> ExecuteAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken) =>
        business.ExecuteAsync(operationId, leaseToken, cancellationToken);
}
