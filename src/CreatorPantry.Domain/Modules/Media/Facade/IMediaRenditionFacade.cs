using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Business;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Managers;

namespace CreatorPantry.Domain.Modules.Media.Facade;

/// <summary>
/// The application boundary for making renditions. Used by the rendition job, and by nothing a person calls.
/// </summary>
public interface IMediaRenditionFacade
{
    /// <inheritdoc cref="IMediaRenditionBusiness.ProduceAsync"/>
    Task<MediaRenditionProduceSummary> ProduceAsync(
        MediaRenditionSource source, bool finalAttempt, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMediaRenditionFacade"/>
/// <remarks>
/// <strong>The service identity only.</strong> Making a rendition is something the platform does for a
/// workspace, not something a member asks for, so a context resolved for anyone else is answered with
/// nothing done — the same rule the retention sweep's facade holds. The workspace itself is whatever the
/// caller resolved through the ordinary tenancy path; it is never an argument here.
/// </remarks>
internal sealed class MediaRenditionFacade(
    IMediaRenditionBusiness business, IWorkspaceContext workspace) : IMediaRenditionFacade
{
    public Task<MediaRenditionProduceSummary> ProduceAsync(
        MediaRenditionSource source, bool finalAttempt, CancellationToken cancellationToken) =>
        workspace.MembershipId == WorkspaceServiceIdentity.MembershipId && source.IsValid
            ? business.ProduceAsync(source, finalAttempt, cancellationToken)
            : Task.FromResult(MediaRenditionProduceSummary.Nothing);
}
