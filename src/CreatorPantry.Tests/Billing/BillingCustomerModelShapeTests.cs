using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// The shape of <see cref="BillingCustomer"/> in the built EF model (AP.6.1): the account's, not a workspace's, and
/// held to one customer per account per mode, and one account per customer, by its indexes.
/// </summary>
public sealed class BillingCustomerModelShapeTests : IDisposable
{
    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>B-47: a person pays once for the account they work in every workspace with.</summary>
    [Fact]
    public void A_billing_customer_belongs_to_an_account_and_never_to_a_workspace()
    {
        Assert.False(typeof(IWorkspaceOwned).IsAssignableFrom(typeof(BillingCustomer)));
        Assert.Empty(Customers().GetDeclaredQueryFilters());
        Assert.Null(Customers().FindProperty("WorkspaceId"));
        Assert.Empty(Customers().GetForeignKeys());
    }

    /// <summary>
    /// The mapping and nothing else: no plan, no price, no email, no subscription. The whole column list, so a field
    /// added later is a decision, and a provider's subscription id lands in AP.6.2's records rather than here.
    /// </summary>
    [Fact]
    public void A_row_maps_an_account_to_a_customer_and_carries_nothing_else()
    {
        Assert.Equal(
            [
                nameof(BillingCustomer.AccountId),
                nameof(BillingCustomer.CreatedAt),
                nameof(BillingCustomer.ExternalCustomerId),
                nameof(BillingCustomer.Id),
                nameof(BillingCustomer.IsLiveMode),
            ],
            Customers().GetProperties().Select(property => property.Name).Order());
    }

    [Fact]
    public void One_customer_per_account_per_mode_and_one_account_per_customer()
    {
        var perAccount = Index(nameof(BillingCustomer.AccountId), nameof(BillingCustomer.IsLiveMode));
        Assert.True(perAccount.IsUnique);
        Assert.Null(perAccount.GetFilter());

        var perCustomer = Index(nameof(BillingCustomer.ExternalCustomerId));
        Assert.True(perCustomer.IsUnique);
        Assert.Null(perCustomer.GetFilter());
    }

    [Theory]
    [InlineData("CK_BillingCustomers_AccountId_NotBlank")]
    [InlineData("CK_BillingCustomers_ExternalCustomerId_NotBlank")]
    public void The_table_declares_its_check_constraints(string name)
    {
        Assert.Contains(name, DesignTimeCustomers().GetCheckConstraints().Select(constraint => constraint.Name));
    }

    /// <summary>Made once and never edited: a customer deleted at the provider is a new row, not this one rewritten.</summary>
    [Fact]
    public void A_row_is_write_once()
    {
        Assert.True(typeof(IImmutableRecord).IsAssignableFrom(typeof(BillingCustomer)));
        Assert.DoesNotContain(Customers().GetProperties(), property => property.IsConcurrencyToken);
    }

    [Fact]
    public void Column_widths_match_the_account_id_and_the_providers_id()
    {
        Assert.Equal(BillingPolicy.AccountIdMaxLength, Customers().FindProperty(nameof(BillingCustomer.AccountId))!.GetMaxLength());
        Assert.Equal(BillingPolicy.ExternalReferenceMaxLength, Customers().FindProperty(nameof(BillingCustomer.ExternalCustomerId))!.GetMaxLength());
        Assert.False(Customers().FindProperty(nameof(BillingCustomer.ExternalCustomerId))!.IsNullable);
    }

    private IIndex Index(params string[] properties) =>
        Customers().GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name).SequenceEqual(properties));

    /// <summary>Check constraints are kept in the design-time model only; the runtime one does not carry them.</summary>
    private IEntityType DesignTimeCustomers()
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        return RecipeAggregateFixture.Db(scope).GetService<IDesignTimeModel>().Model.FindEntityType(typeof(BillingCustomer))!;
    }

    private IEntityType Customers()
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        return RecipeAggregateFixture.Db(scope).Model.FindEntityType(typeof(BillingCustomer))!;
    }
}
