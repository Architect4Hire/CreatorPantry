using CreatorPantry.Domain.Modules.Auth.Business;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Data;
using CreatorPantry.Domain.Modules.Auth.Facade;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Auth.Managers;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Domain.Modules.Auth;

public static class AuthServiceCollectionExtensions
{
    /// <summary>
    /// Registers Identity user management (no sign-in, cookies, or bearer tokens) and the auth seam.
    /// Requires <see cref="CreatorPantryDbContext"/>, <c>AddApplicationTime</c>, and an
    /// <see cref="CreatorPantry.Domain.Modules.Auth.Gateways.IAccountMessageSender"/> to be registered.
    /// </summary>
    public static IServiceCollection AddAuthDomain(this IServiceCollection services)
    {
        services.AddIdentityStores();

        services.AddScoped<IValidator<RegisterUserViewModel>, RegisterUserViewModelValidator>();
        services.AddScoped<IValidator<RequestPasswordResetViewModel>, RequestPasswordResetViewModelValidator>();
        services.AddScoped<IValidator<CompletePasswordResetViewModel>, CompletePasswordResetViewModelValidator>();
        services.AddScoped<IValidator<ChangePasswordViewModel>, ChangePasswordViewModelValidator>();
        services.AddScoped<IValidator<ConfirmEmailViewModel>, ConfirmEmailViewModelValidator>();
        services.AddScoped<IValidator<VerifyCredentialsViewModel>, VerifyCredentialsViewModelValidator>();
        services.AddScoped<IValidator<ValidateSessionViewModel>, ValidateSessionViewModelValidator>();
        services.AddScoped<IAuthFacade, AuthFacade>();
        services.AddScoped<IAuthBusiness, AuthBusiness>();
        services.AddScoped<IAuthDataLayer, AuthDataLayer>();
        services.AddScoped<IUserRepository, UserRepository>();

        services.AddScoped<IPlatformAccountFacade, PlatformAccountFacade>();
        services.AddScoped<IPlatformAccountBusiness, PlatformAccountBusiness>();

        services.AddOpsApiClients();

        return services;
    }

    /// <summary>
    /// Registers the ops API key seam (baseline B-14): the facade the API's authentication handler calls, and
    /// the layers beneath it. Requires <see cref="CreatorPantryDbContext"/> and <c>AddApplicationTime</c>.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AddAuthDomain"/> so the migration service, which seeds clients but hosts no
    /// Identity endpoints, can register this alone. Safe to call repeatedly.
    /// </remarks>
    public static IServiceCollection AddOpsApiClients(this IServiceCollection services)
    {
        // The data layer stamps CreatedAt, RotatedAt and LastUsedAt, so this seam owns its clock dependency
        // rather than assuming a caller registered one. TryAdd-based, so a host or test that supplied its own
        // TimeProvider first keeps it.
        services.AddApplicationTime();

        services.TryAddScoped<IOpsApiClientFacade, OpsApiClientFacade>();
        services.TryAddScoped<IOpsApiClientBusiness, OpsApiClientBusiness>();
        services.TryAddScoped<IOpsApiClientDataLayer, OpsApiClientDataLayer>();
        services.TryAddScoped<IOpsApiClientRepository, OpsApiClientRepository>();

        return services;
    }

    /// <summary>
    /// Registers the PlatformAdmin role seeder and the ops API client seeder. Run them only where the schema
    /// exists (the migration service, after migrations).
    /// </summary>
    public static IServiceCollection AddPlatformRoleSeeding(this IServiceCollection services, IConfiguration? configuration = null)
    {
        services.AddIdentityStores();
        services.AddScoped<IDataSeeder, PlatformRoleSeeder>();

        services.AddOpsApiClients();
        var ops = services.AddOptions<OpsApiClientOptions>();
        if (configuration is not null)
        {
            ops.Bind(configuration.GetSection(OpsApiClientOptions.SectionName));
        }

        services.AddScoped<IDataSeeder, OpsApiClientSeeder>();

        return services;
    }

    /// <summary>Identity user and role stores with CreatorPantry's account options. Safe to call repeatedly.</summary>
    public static IServiceCollection AddIdentityStores(this IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IdentityStoresMarker)))
        {
            return services;
        }

        services.AddSingleton<IdentityStoresMarker>();

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.User.AllowedUserNameCharacters = string.Empty; // UserName is the email; EmailAddress rules apply.
                options.SignIn.RequireConfirmedEmail = true;
                options.SignIn.RequireConfirmedAccount = true;

                options.Password.RequiredLength = AccountPolicy.PasswordMinLength;
                options.Password.RequiredUniqueChars = 1;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;

                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = AccountPolicy.MaxFailedSignInAttempts;
                options.Lockout.DefaultLockoutTimeSpan = AccountPolicy.LockoutDuration;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<CreatorPantryDbContext>()
            .AddDefaultTokenProviders();

        // Reset tokens are protected with Data Protection. Deployed environments must persist the key
        // ring (shared across instances) or outstanding tokens stop validating after a restart.
        services.AddDataProtection();
        services.Configure<DataProtectionTokenProviderOptions>(options =>
            options.TokenLifespan = AccountPolicy.PasswordResetTokenLifetime);

        return services;
    }

    private sealed class IdentityStoresMarker;
}
