using CreatorPantry.Domain.Modules.Auth.Business;

namespace CreatorPantry.Domain.Modules.Auth.Facade;

/// <summary>
/// The narrowest cross-module question another context can ask about an Identity account: does it exist?
/// </summary>
/// <remarks>
/// <para>
/// <strong>Existence and nothing else.</strong> USAGE-009 lets a platform administrator see "numbers,
/// accounts, and workspace identifiers" — so an ops route needs to tell an unknown account id from a known
/// one with no usage, and needs nothing further. Returning an email or a display name here would put account
/// holders' personal details behind a machine credential for no stated purpose, so this facade cannot.
/// </para>
/// <para>
/// Facade to facade is the only traffic that crosses a module boundary (backend.md); the AiUsage module
/// reaches this and never <c>UserManager</c>, <c>AspNetUsers</c>, or the Auth module's repositories.
/// </para>
/// </remarks>
public interface IPlatformAccountFacade
{
    Task<bool> AccountExistsAsync(string accountId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPlatformAccountFacade"/>
internal sealed class PlatformAccountFacade(IPlatformAccountBusiness business) : IPlatformAccountFacade
{
    public Task<bool> AccountExistsAsync(string accountId, CancellationToken cancellationToken) =>
        business.AccountExistsAsync(accountId, cancellationToken);
}
