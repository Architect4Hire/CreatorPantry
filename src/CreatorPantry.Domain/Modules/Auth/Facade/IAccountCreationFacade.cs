using CreatorPantry.Domain.Modules.Auth.Business;
using CreatorPantry.Domain.Modules.Auth.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Domain.Modules.Auth.Facade;

/// <summary>
/// When an Identity account was created, for the one rule that needs it: a trial runs from the account's creation
/// for its plan's <c>TrialDays</c> (AP.7.1, <c>ai-pricing.md</c> B-48, B-54).
/// </summary>
/// <remarks>
/// <para>
/// <strong>An instant and nothing else.</strong> Not an email, a name or whether the address is confirmed: Billing
/// asks when the account's trial ends, and an instant answers it. Like <see cref="IPlatformAccountFacade"/>, this is
/// the narrowest question another module can ask about an account, and the only way it asks.
/// </para>
/// <para>
/// <strong>No <c>UserManager</c> beneath it.</strong> The Worker resolves plans when it admits text work and hosts no
/// Identity, so this reads the one column through <c>CreatorPantryDbContext</c> and needs nothing else registered.
/// </para>
/// </remarks>
public interface IAccountCreationFacade
{
    /// <summary>When the account was created; null when Identity has no such account.</summary>
    Task<DateTimeOffset?> FindCreatedAtAsync(string accountId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAccountCreationFacade"/>
internal sealed class AccountCreationFacade(IAccountCreationBusiness business) : IAccountCreationFacade
{
    public Task<DateTimeOffset?> FindCreatedAtAsync(string accountId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        return business.FindCreatedAtAsync(accountId, cancellationToken);
    }
}

/// <summary>Registers <see cref="IAccountCreationFacade"/> and the layers beneath it.</summary>
/// <remarks>
/// <para>
/// <strong>In the facade's own namespace</strong>, so a module that needs the facade registers it by naming only
/// what it may name across the boundary (<c>ModuleBoundaryTests</c>). Billing does, from <c>AddBillingModule</c>,
/// so every host and test that can resolve a plan can resolve a trial's end.
/// </para>
/// <para>TryAdd-based and safe to call repeatedly. Requires <c>CreatorPantryDbContext</c>.</para>
/// </remarks>
public static class AccountCreationServiceCollectionExtensions
{
    public static IServiceCollection AddAccountCreationFacade(this IServiceCollection services)
    {
        services.TryAddScoped<IAccountCreationFacade, AccountCreationFacade>();
        services.TryAddScoped<IAccountCreationBusiness, AccountCreationBusiness>();
        services.TryAddScoped<IAccountCreationDataLayer, AccountCreationDataLayer>();
        services.TryAddScoped<IAccountCreationRepository, AccountCreationRepository>();

        return services;
    }
}
