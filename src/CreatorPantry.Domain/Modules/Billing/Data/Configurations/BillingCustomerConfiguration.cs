using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Billing.Data.Configurations;

/// <remarks>
/// Two unique indexes do the work. One customer per account per mode means two requests racing to give an account
/// its first customer cannot both store one; the loser re-reads the winner (<c>BillingCustomerDataLayer</c>). A
/// customer id unique outright means no webhook about one payer can resolve to two accounts.
/// </remarks>
internal sealed class BillingCustomerConfiguration : IEntityTypeConfiguration<BillingCustomer>
{
    public void Configure(EntityTypeBuilder<BillingCustomer> builder)
    {
        builder.ToTable("BillingCustomers", table =>
        {
            // Unquoted and trim(), so the same expression is valid on SQL Server and on the SQLite the model tests
            // run against.
            table.HasCheckConstraint("CK_BillingCustomers_AccountId_NotBlank", "trim(AccountId) <> ''");
            table.HasCheckConstraint("CK_BillingCustomers_ExternalCustomerId_NotBlank", "trim(ExternalCustomerId) <> ''");
        });

        builder.HasKey(customer => customer.Id);

        builder.Property(customer => customer.AccountId)
            .IsRequired()
            .HasMaxLength(BillingPolicy.AccountIdMaxLength);

        builder.Property(customer => customer.ExternalCustomerId)
            .IsRequired()
            .HasMaxLength(BillingPolicy.ExternalReferenceMaxLength);

        builder.Property(customer => customer.IsLiveMode).IsRequired();
        builder.Property(customer => customer.CreatedAt).IsRequired();

        // The lookup every purchase starts with, and the guard against a second customer for one account.
        builder.HasIndex(customer => new { customer.AccountId, customer.IsLiveMode })
            .IsUnique()
            .HasDatabaseName("UX_BillingCustomers_Account_Mode");

        // The reverse lookup a webhook makes, and the guard against one payer being two accounts.
        builder.HasIndex(customer => customer.ExternalCustomerId)
            .IsUnique()
            .HasDatabaseName("UX_BillingCustomers_ExternalCustomerId");
    }
}
