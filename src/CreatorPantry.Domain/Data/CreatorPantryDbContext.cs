using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Data;

/// <summary>The single DbContext for the modular monolith.</summary>
public class CreatorPantryDbContext(DbContextOptions<CreatorPantryDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole, string>(options)
{
    /// <summary>The Aspire connection name; matches the database resource in the AppHost.</summary>
    public const string ConnectionName = "creatorpantrydb";

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(CreatorPantryDbContext).Assembly);
    }
}
