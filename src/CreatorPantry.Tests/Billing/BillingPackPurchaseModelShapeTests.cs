using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// The shape of <see cref="BillingPackPurchase"/> in the built EF model (AP.6.3): the account's rather than a
/// workspace's, one row per checkout and one per payment, and a record that is kept however the payment ends.
/// </summary>
public sealed class BillingPackPurchaseModelShapeTests : IDisposable
{
    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>B-47: a person pays once for the account they work in every workspace with.</summary>
    [Fact]
    public void A_purchase_belongs_to_an_account_and_never_to_a_workspace()
    {
        Assert.False(typeof(IWorkspaceOwned).IsAssignableFrom(typeof(BillingPackPurchase)));
        Assert.Empty(Purchases().GetDeclaredQueryFilters());
        Assert.Null(Purchases().FindProperty("WorkspaceId"));
    }

    /// <summary>
    /// The payment's record and nothing more: no price, no card, no email, no invoice. The whole column list, so a
    /// field added later is a decision.
    /// </summary>
    [Fact]
    public void A_row_records_one_payment_and_carries_nothing_else()
    {
        Assert.Equal(
            [
                nameof(BillingPackPurchase.AccountId),
                nameof(BillingPackPurchase.CreditsClawedBack),
                nameof(BillingPackPurchase.CreditsGranted),
                nameof(BillingPackPurchase.ExternalPaymentReference),
                nameof(BillingPackPurchase.ExternalReversalReference),
                nameof(BillingPackPurchase.ExternalSessionId),
                nameof(BillingPackPurchase.GrantId),
                nameof(BillingPackPurchase.Id),
                nameof(BillingPackPurchase.IsLiveMode),
                nameof(BillingPackPurchase.PackKey),
                nameof(BillingPackPurchase.PurchasedAt),
                nameof(BillingPackPurchase.Reversal),
                nameof(BillingPackPurchase.ReversedAt),
                nameof(BillingPackPurchase.RowVersion),
            ],
            Purchases().GetProperties().Select(property => property.Name).Order());
    }

    /// <summary>One purchase per checkout, and one per payment where the payment is known: what a reversal seeks by.</summary>
    [Fact]
    public void One_purchase_per_checkout_and_one_per_payment()
    {
        var perSession = Index(nameof(BillingPackPurchase.ExternalSessionId));
        Assert.True(perSession.IsUnique);
        Assert.Null(perSession.GetFilter());

        var perPayment = Index(nameof(BillingPackPurchase.ExternalPaymentReference));
        Assert.True(perPayment.IsUnique);
        Assert.Equal("ExternalPaymentReference IS NOT NULL", perPayment.GetFilter());
    }

    [Theory]
    [InlineData("CK_BillingPackPurchases_AccountId_NotBlank")]
    [InlineData("CK_BillingPackPurchases_SessionId_NotBlank")]
    [InlineData("CK_BillingPackPurchases_PackKey_NotBlank")]
    [InlineData("CK_BillingPackPurchases_Payment_NotBlank")]
    [InlineData("CK_BillingPackPurchases_Credits_Positive")]
    [InlineData("CK_BillingPackPurchases_ClawedBack_Within_Granted")]
    [InlineData("CK_BillingPackPurchases_Reversal_Recorded")]
    public void The_table_declares_its_check_constraints(string name)
    {
        Assert.Contains(name, DesignTimePurchases().GetCheckConstraints().Select(constraint => constraint.Name));
    }

    /// <summary>
    /// Kept, because what a payment bought and what became of it is the answer a refund or a creator's question is
    /// read from; and a contended row, because two reversals of one payment can be processed at once.
    /// </summary>
    [Fact]
    public void A_row_is_retained_and_carries_a_row_version()
    {
        Assert.True(typeof(IRetainedRecord).IsAssignableFrom(typeof(BillingPackPurchase)));
        Assert.Equal(
            nameof(BillingPackPurchase.RowVersion),
            Assert.Single(Purchases().GetProperties(), property => property.IsConcurrencyToken).Name);
    }

    /// <summary>The grant it made, and nothing that could take the purchase away with it.</summary>
    [Fact]
    public void A_purchase_names_the_grant_it_made_and_never_cascades()
    {
        var grant = Assert.Single(Purchases().GetForeignKeys());

        Assert.Equal(typeof(AccountCreditGrant), grant.PrincipalEntityType.ClrType);
        Assert.Equal(nameof(BillingPackPurchase.GrantId), Assert.Single(grant.Properties).Name);
        Assert.Equal(DeleteBehavior.Restrict, grant.DeleteBehavior);
    }

    [Fact]
    public void Column_widths_and_credit_precision_match_the_modules_policy()
    {
        Assert.Equal(BillingPolicy.AccountIdMaxLength, Width(nameof(BillingPackPurchase.AccountId)));
        Assert.Equal(BillingPolicy.ExternalReferenceMaxLength, Width(nameof(BillingPackPurchase.ExternalSessionId)));
        Assert.Equal(BillingPolicy.ExternalReferenceMaxLength, Width(nameof(BillingPackPurchase.ExternalPaymentReference)));
        Assert.Equal(BillingPolicy.ExternalReferenceMaxLength, Width(nameof(BillingPackPurchase.ExternalReversalReference)));
        Assert.Equal(BillingPolicy.PackKeyMaxLength, Width(nameof(BillingPackPurchase.PackKey)));

        foreach (var credits in new[] { nameof(BillingPackPurchase.CreditsGranted), nameof(BillingPackPurchase.CreditsClawedBack) })
        {
            var property = Purchases().FindProperty(credits)!;
            Assert.Equal(BillingPolicy.CreditPrecision, property.GetPrecision());
            Assert.Equal(BillingPolicy.CreditScale, property.GetScale());
        }
    }

    /// <summary>What a pack's purchase is for is fixed; only the reversal may be written, and only once.</summary>
    [Fact]
    public void Only_the_reversal_may_change_and_only_once()
    {
        var purchase = new BillingPackPurchase
        {
            Id = Guid.NewGuid(),
            AccountId = "user-sam-8f2e",
            ExternalSessionId = "cs_test_SamPack1",
            ExternalPaymentReference = "pi_SamPayment1",
            PackKey = "small",
            CreditsGranted = 2500m,
            GrantId = Guid.NewGuid(),
            PurchasedAt = DateTimeOffset.UnixEpoch,
            Reversal = BillingPackReversal.Refund,
            CreditsClawedBack = 2100m,
            ReversedAt = DateTimeOffset.UnixEpoch.AddDays(1),
        };

        // Nothing was reversed when the row was read: recording one is the change the row allows.
        Assert.Null(purchase.RefuseChange(name => name switch
        {
            nameof(BillingPackPurchase.AccountId) => purchase.AccountId,
            nameof(BillingPackPurchase.ExternalSessionId) => purchase.ExternalSessionId,
            nameof(BillingPackPurchase.ExternalPaymentReference) => purchase.ExternalPaymentReference,
            nameof(BillingPackPurchase.IsLiveMode) => purchase.IsLiveMode,
            nameof(BillingPackPurchase.PackKey) => purchase.PackKey,
            nameof(BillingPackPurchase.CreditsGranted) => purchase.CreditsGranted,
            nameof(BillingPackPurchase.GrantId) => purchase.GrantId,
            nameof(BillingPackPurchase.PurchasedAt) => purchase.PurchasedAt,
            _ => null,
        }));

        // A second reversal, and a repriced pack, are both refused.
        Assert.NotNull(purchase.RefuseChange(name => name switch
        {
            nameof(BillingPackPurchase.AccountId) => purchase.AccountId,
            nameof(BillingPackPurchase.ExternalSessionId) => purchase.ExternalSessionId,
            nameof(BillingPackPurchase.ExternalPaymentReference) => purchase.ExternalPaymentReference,
            nameof(BillingPackPurchase.IsLiveMode) => purchase.IsLiveMode,
            nameof(BillingPackPurchase.PackKey) => purchase.PackKey,
            nameof(BillingPackPurchase.CreditsGranted) => purchase.CreditsGranted,
            nameof(BillingPackPurchase.GrantId) => purchase.GrantId,
            nameof(BillingPackPurchase.PurchasedAt) => purchase.PurchasedAt,
            nameof(BillingPackPurchase.ReversedAt) => DateTimeOffset.UnixEpoch.AddHours(1),
            _ => null,
        }));

        Assert.NotNull(purchase.RefuseChange(name => name switch
        {
            nameof(BillingPackPurchase.CreditsGranted) => 7500m,
            _ => null,
        }));
    }

    /// <summary>The stored event says what it was about, so processing never reads the provider's type string.</summary>
    [Fact]
    public void A_provider_event_records_what_it_was_about()
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var events = RecipeAggregateFixture.Db(scope).Model.FindEntityType(typeof(BillingProviderEvent))!;
        var kind = events.FindProperty(nameof(BillingProviderEvent.Kind))!;

        Assert.False(kind.IsNullable);
        Assert.Equal(typeof(BillingEventKind), kind.ClrType);
    }

    private int? Width(string property) => Purchases().FindProperty(property)!.GetMaxLength();

    private IIndex Index(params string[] properties) =>
        Purchases().GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name).SequenceEqual(properties));

    /// <summary>Check constraints are kept in the design-time model only; the runtime one does not carry them.</summary>
    private IEntityType DesignTimePurchases()
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        return RecipeAggregateFixture.Db(scope).GetService<IDesignTimeModel>().Model.FindEntityType(typeof(BillingPackPurchase))!;
    }

    private IEntityType Purchases()
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        return RecipeAggregateFixture.Db(scope).Model.FindEntityType(typeof(BillingPackPurchase))!;
    }
}
