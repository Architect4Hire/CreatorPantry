using CreatorPantry.Domain.Modules.Auth.Business;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Data;
using CreatorPantry.Domain.Modules.Auth.Facade;
using CreatorPantry.Domain.Modules.Auth.Managers;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Modules.Auth;

public static class AuthServiceCollectionExtensions
{
    /// <summary>
    /// Registers Identity user management (no sign-in, cookies, or bearer tokens) and the auth seam.
    /// Requires <see cref="CreatorPantryDbContext"/>, <c>AddApplicationTime</c>, and an
    /// <see cref="Gateways.AccountMessages.IAccountMessageSender"/> to be registered.
    /// </summary>
    public static IServiceCollection AddAuthDomain(this IServiceCollection services)
    {
        services.AddIdentityStores();

        services.AddScoped<IValidator<RegisterUserViewModel>, RegisterUserViewModelValidator>();
        services.AddScoped<IValidator<RequestPasswordResetViewModel>, RequestPasswordResetViewModelValidator>();
        services.AddScoped<IValidator<CompletePasswordResetViewModel>, CompletePasswordResetViewModelValidator>();
        services.AddScoped<IValidator<ChangePasswordViewModel>, ChangePasswordViewModelValidator>();
        services.AddScoped<IValidator<VerifyCredentialsViewModel>, VerifyCredentialsViewModelValidator>();
        services.AddScoped<IValidator<ValidateSessionViewModel>, ValidateSessionViewModelValidator>();
        services.AddScoped<IAuthFacade, AuthFacade>();
        services.AddScoped<IAuthBusiness, AuthBusiness>();
        services.AddScoped<IAuthDataLayer, AuthDataLayer>();
        services.AddScoped<IUserRepository, UserRepository>();

        return services;
    }

    /// <summary>
    /// Registers the PlatformAdmin role seeder. Run it only where the Identity schema exists (the migration
    /// service, after migrations).
    /// </summary>
    public static IServiceCollection AddPlatformRoleSeeding(this IServiceCollection services)
    {
        services.AddIdentityStores();
        services.AddScoped<IDataSeeder, PlatformRoleSeeder>();

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
