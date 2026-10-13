using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Publishing.Business;
using CreatorPantry.Domain.Modules.Publishing.Managers;

namespace CreatorPantry.Domain.Modules.Publishing.Facade;

/// <summary>
/// What controllers, workers and plugins call to read a workspace's publishing profiles (PUB-001).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every member may read</strong>, a Viewer included: the workspace route policy and the resolved
/// context are the authorization, and there is nothing here to validate — the one input is a flag.
/// </para>
/// <para>
/// <strong>Only a Contributor and above may ask for a refresh.</strong> A refresh spends the provider's quota,
/// which is small and is everything scheduling will have to work with. A Viewer's request for one is not an
/// error — they are simply served the last read, as they would be without asking.
/// </para>
/// <para>
/// <strong>It cannot fail.</strong> A provider that is missing, refusing, throttling or down is a status on a
/// successful answer, so a board that shows profiles degrades to "from a moment ago" rather than to an error.
/// </para>
/// <para>
/// Not cached here: the staleness that matters is the provider's, and the data layer's sync record already
/// holds it durably, where every instance of the host reads the same answer.
/// </para>
/// </remarks>
public interface IPublishingProfileFacade
{
    /// <inheritdoc cref="IPublishingProfileBusiness.GetAsync"/>
    Task<PublishingProfilesServiceModel> GetAsync(bool refresh, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPublishingProfileFacade"/>
internal sealed class PublishingProfileFacade(
    IPublishingProfileBusiness business, IWorkspaceContext workspace) : IPublishingProfileFacade
{
    public Task<PublishingProfilesServiceModel> GetAsync(bool refresh, CancellationToken cancellationToken) =>
        business.GetAsync(refresh && workspace.Role >= WorkspaceRole.Contributor, cancellationToken);
}
