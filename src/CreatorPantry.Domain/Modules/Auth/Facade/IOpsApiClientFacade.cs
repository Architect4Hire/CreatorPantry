using CreatorPantry.Domain.Modules.Auth.Business;
using CreatorPantry.Domain.Modules.Auth.Managers;

namespace CreatorPantry.Domain.Modules.Auth.Facade;

/// <summary>
/// Verifies an ops API key (baseline B-14). Used by the API's <c>OpsApiKey</c> authentication handler, which
/// injects a facade like any other caller rather than reaching for a repository.
/// </summary>
public interface IOpsApiClientFacade
{
    /// <summary>
    /// The client a presented credential belongs to, or null when it is malformed, unknown, revoked, or the
    /// secret does not match. The four are deliberately indistinguishable to the caller.
    /// </summary>
    Task<OpsApiClientServiceModel?> AuthenticateAsync(string? credential, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IOpsApiClientFacade"/>
internal sealed class OpsApiClientFacade(IOpsApiClientBusiness business) : IOpsApiClientFacade
{
    public Task<OpsApiClientServiceModel?> AuthenticateAsync(string? credential, CancellationToken cancellationToken) =>
        business.AuthenticateAsync(credential, cancellationToken);
}
