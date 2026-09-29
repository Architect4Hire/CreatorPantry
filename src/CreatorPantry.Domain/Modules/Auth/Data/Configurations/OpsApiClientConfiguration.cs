using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Auth.Data.Configurations;

internal sealed class OpsApiClientConfiguration : IEntityTypeConfiguration<OpsApiClient>
{
    public void Configure(EntityTypeBuilder<OpsApiClient> builder)
    {
        builder.ToTable("OpsApiClients");
        builder.HasKey(client => client.Id);

        builder.Property(client => client.Name).IsRequired().HasMaxLength(OpsApiKeyPolicy.NameMaxLength);

        builder.Property(client => client.KeyPrefix)
            .IsRequired()
            .HasMaxLength(OpsApiKeyPolicy.PrefixMaxLength);

        builder.Property(client => client.KeyHash).IsRequired().HasMaxLength(OpsApiKeyPolicy.HashLength);
        builder.Property(client => client.KeySalt).IsRequired().HasMaxLength(OpsApiKeyPolicy.SaltLength);
        builder.Property(client => client.Scopes).IsRequired().HasMaxLength(OpsApiKeyPolicy.ScopesMaxLength);
        builder.Property(client => client.CreatedAt).IsRequired();

        // One client per prefix: the prefix is what a presented key is looked up by, so two clients sharing
        // one would make verification ambiguous.
        builder.HasIndex(client => client.KeyPrefix)
            .IsUnique()
            .HasDatabaseName("UX_OpsApiClients_KeyPrefix");

        // The seeder finds a client by name to decide between insert and rotate.
        builder.HasIndex(client => client.Name)
            .IsUnique()
            .HasDatabaseName("UX_OpsApiClients_Name");
    }
}
