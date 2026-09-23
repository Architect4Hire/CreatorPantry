using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Auth.Managers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Auth.Data;

/// <summary>Ensures each <see cref="PlatformRoles"/> role exists, creating only what is missing.</summary>
internal sealed class PlatformRoleSeeder(RoleManager<IdentityRole> roles, ILogger<PlatformRoleSeeder> logger) : IDataSeeder
{
    public string Name => "Platform roles";

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (var roleName in PlatformRoles.All)
        {
            // RoleManager does not accept a cancellation token; stop between roles instead.
            cancellationToken.ThrowIfCancellationRequested();

            if (await roles.RoleExistsAsync(roleName))
            {
                continue;
            }

            IdentityResult result;
            try
            {
                result = await roles.CreateAsync(new IdentityRole(roleName));
            }
            catch (DbUpdateException)
            {
                // A concurrent run may have created the role first (unique index on the normalized name).
                if (!await roles.RoleExistsAsync(roleName))
                {
                    throw;
                }

                continue;
            }

            if (result.Succeeded)
            {
                logger.LogInformation("Seeded platform role {RoleName}.", roleName);
            }
            else if (!await roles.RoleExistsAsync(roleName))
            {
                throw new InvalidOperationException(
                    $"Could not create role {roleName}: {string.Join(",", result.Errors.Select(error => error.Code))}.");
            }
        }
    }
}
