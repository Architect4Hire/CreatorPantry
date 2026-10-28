using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// The shape of <see cref="AccountCreditGrant"/> in the built EF model (AP.5.1): platform-scoped like the plan,
/// kept until spent with no expiry (B-50), and never deleted.
/// </summary>
/// <remarks>
/// On the SQLite fixture, which also proves every check constraint on the table parses there: the whole model is
/// created by <c>EnsureCreated</c>, so one that did not would red this class at initialisation.
/// </remarks>
public sealed class AccountCreditGrantModelShapeTests : IDisposable
{
    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>B-47: credits are the account's across every workspace it works in.</summary>
    [Fact]
    public void A_grant_belongs_to_an_account_and_never_to_a_workspace()
    {
        Assert.False(typeof(IWorkspaceOwned).IsAssignableFrom(typeof(AccountCreditGrant)));
        Assert.Empty(Grants().GetDeclaredQueryFilters());
        Assert.Null(Grants().FindProperty("WorkspaceId"));
        Assert.Empty(Grants().GetForeignKeys());
    }

    /// <summary>
    /// The whole column list, so a field added later is a decision. No expiry (B-50), no price, no pack key and no
    /// provider id beyond the one external reference a purchase answers to.
    /// </summary>
    [Fact]
    public void A_grant_carries_its_credits_and_no_expiry()
    {
        Assert.Equal(
            [
                nameof(AccountCreditGrant.AccountId),
                nameof(AccountCreditGrant.ClawedBackAt),
                nameof(AccountCreditGrant.CreditsClawedBack),
                nameof(AccountCreditGrant.CreditsGranted),
                nameof(AccountCreditGrant.CreditsRemaining),
                nameof(AccountCreditGrant.ExternalReferenceId),
                nameof(AccountCreditGrant.GrantedAt),
                nameof(AccountCreditGrant.Id),
                nameof(AccountCreditGrant.RowVersion),
                nameof(AccountCreditGrant.Source),
            ],
            Grants().GetProperties().Select(property => property.Name).Order());
    }

    /// <summary>
    /// Kept, not frozen: a grant is spent down and may be clawed back, so it is not write-once, and it is never
    /// deleted. The row version is what a spend and a claw-back meet on.
    /// </summary>
    [Fact]
    public void A_grant_is_retained_and_contended_on_its_row_version()
    {
        Assert.True(typeof(IRetainedRecord).IsAssignableFrom(typeof(AccountCreditGrant)));
        Assert.False(typeof(IImmutableRecord).IsAssignableFrom(typeof(AccountCreditGrant)));

        var version = Grants().FindProperty(nameof(AccountCreditGrant.RowVersion))!;
        Assert.True(version.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, version.ValueGenerated);
    }

    [Theory]
    [InlineData("CK_AccountCreditGrants_Source_Declared")]
    [InlineData("CK_AccountCreditGrants_Granted_Positive")]
    [InlineData("CK_AccountCreditGrants_Remaining_Within_Granted")]
    [InlineData("CK_AccountCreditGrants_ClawedBack_Within_Granted")]
    [InlineData("CK_AccountCreditGrants_ClawBack_Recorded")]
    [InlineData("CK_AccountCreditGrants_Purchase_Referenced")]
    [InlineData("CK_AccountCreditGrants_Reference_NotBlank")]
    public void The_table_declares_its_check_constraints(string name)
    {
        Assert.Contains(name, DesignTimeGrants().GetCheckConstraints().Select(constraint => constraint.Name));
    }

    /// <summary>One grant per reference within a source — the guarantee AP.6.3's replay rests on.</summary>
    [Fact]
    public void A_reference_grants_once_within_its_source()
    {
        var reference = Index(nameof(AccountCreditGrant.Source), nameof(AccountCreditGrant.ExternalReferenceId));

        Assert.True(reference.IsUnique);
        Assert.Equal("ExternalReferenceId IS NOT NULL", reference.GetFilter());

        var order = Index(nameof(AccountCreditGrant.AccountId), nameof(AccountCreditGrant.GrantedAt), nameof(AccountCreditGrant.Id));
        Assert.False(order.IsUnique);
    }

    /// <summary>
    /// Billing restates the AiUsage widths rather than reaching across the module boundary for them; this keeps
    /// a grant's credits the precision a period's allowance is kept to, since the two are spent side by side.
    /// </summary>
    [Fact]
    public void Column_widths_match_the_account_id_and_the_allowance()
    {
        Assert.Equal(AiUsagePolicy.AccountIdMaxLength, BillingPolicy.AccountIdMaxLength);
        Assert.Equal(AiUsagePolicy.AllowancePrecision, BillingPolicy.CreditPrecision);
        Assert.Equal(AiUsagePolicy.AllowanceScale, BillingPolicy.CreditScale);
        Assert.Equal(IdempotencyPolicy.KeyMaxLength, BillingPolicy.ExternalReferenceMaxLength);

        Assert.Equal(BillingPolicy.AccountIdMaxLength, Grants().FindProperty(nameof(AccountCreditGrant.AccountId))!.GetMaxLength());
        Assert.Equal(
            BillingPolicy.ExternalReferenceMaxLength,
            Grants().FindProperty(nameof(AccountCreditGrant.ExternalReferenceId))!.GetMaxLength());

        foreach (var name in new[]
        {
            nameof(AccountCreditGrant.CreditsGranted),
            nameof(AccountCreditGrant.CreditsRemaining),
            nameof(AccountCreditGrant.CreditsClawedBack),
        })
        {
            var property = Grants().FindProperty(name)!;
            Assert.Equal(BillingPolicy.CreditPrecision, property.GetPrecision());
            Assert.Equal(BillingPolicy.CreditScale, property.GetScale());
        }
    }

    private IIndex Index(params string[] properties) =>
        Grants().GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name).SequenceEqual(properties));

    /// <summary>Check constraints are kept in the design-time model only; the runtime one does not carry them.</summary>
    private IEntityType DesignTimeGrants()
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        return RecipeAggregateFixture.Db(scope).GetService<IDesignTimeModel>().Model.FindEntityType(typeof(AccountCreditGrant))!;
    }

    private IEntityType Grants()
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        return RecipeAggregateFixture.Db(scope).Model.FindEntityType(typeof(AccountCreditGrant))!;
    }
}
