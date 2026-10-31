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
/// The shape of <see cref="AccountPlan"/> in the built EF model (AP.4.1): platform-scoped like the quota tables,
/// and held to one chain per account by its keys rather than by the code that writes it.
/// </summary>
public sealed class AccountPlanModelShapeTests : IDisposable
{
    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>
    /// B-47: a plan is the account's across every workspace it works in. Not workspace-owned, not filtered, and
    /// with no workspace column to name one by — a plan that could be set per workspace is one an account could
    /// hold twice.
    /// </summary>
    [Fact]
    public void A_plan_belongs_to_an_account_and_never_to_a_workspace()
    {
        Assert.False(typeof(IWorkspaceOwned).IsAssignableFrom(typeof(AccountPlan)));
        Assert.Empty(Plans().GetDeclaredQueryFilters());
        Assert.Null(Plans().FindProperty("WorkspaceId"));
    }

    /// <summary>
    /// The one foreign key is the chain's own. None to <c>ApplicationUser</c>, because account erasure is a
    /// documented path rather than a cascade, and none to a workspace.
    /// </summary>
    [Fact]
    public void The_only_foreign_key_is_the_link_to_the_row_superseded()
    {
        var key = Assert.Single(Plans().GetForeignKeys());

        Assert.Equal(typeof(AccountPlan), key.PrincipalEntityType.ClrType);
        Assert.Equal(
            [nameof(AccountPlan.AccountId), nameof(AccountPlan.SupersedesFrom)],
            key.Properties.Select(property => property.Name));
        Assert.Equal(
            [nameof(AccountPlan.AccountId), nameof(AccountPlan.EffectiveFrom)],
            key.PrincipalKey.Properties.Select(property => property.Name));

        // A cascade would turn deleting one row into deleting the chain after it.
        Assert.Equal(DeleteBehavior.NoAction, key.DeleteBehavior);
    }

    /// <summary>Two rows for one account cannot start together; also the seek for the row in force.</summary>
    [Fact]
    public void No_two_rows_for_an_account_start_at_one_instant()
    {
        var key = Plans().GetKeys().Single(candidate => !candidate.IsPrimaryKey());

        Assert.Equal(
            [nameof(AccountPlan.AccountId), nameof(AccountPlan.EffectiveFrom)],
            key.Properties.Select(property => property.Name));
    }

    [Fact]
    public void A_row_is_superseded_at_most_once_and_an_account_has_one_first_row()
    {
        var successor = Index(nameof(AccountPlan.AccountId), nameof(AccountPlan.SupersedesFrom));
        Assert.True(successor.IsUnique);
        Assert.Equal("SupersedesFrom IS NOT NULL", successor.GetFilter());

        var root = Index(nameof(AccountPlan.AccountId));
        Assert.True(root.IsUnique);
        Assert.Equal("SupersedesFrom IS NULL", root.GetFilter());
    }

    [Theory]
    [InlineData("CK_AccountPlans_Source_Declared")]
    [InlineData("CK_AccountPlans_PlanKey_NotBlank")]
    [InlineData("CK_AccountPlans_After_Superseded")]
    public void The_table_declares_its_check_constraints(string name)
    {
        Assert.Contains(name, DesignTimePlans().GetCheckConstraints().Select(constraint => constraint.Name));
    }

    /// <summary>
    /// No price, no figure and no provider reference: the catalogue owns what a plan grants, and Phase 6's
    /// integration records own a Stripe id. The whole column list, so a field added later is a decision.
    /// </summary>
    [Fact]
    public void A_row_names_a_plan_and_carries_none_of_its_figures()
    {
        Assert.Equal(
            [
                nameof(AccountPlan.AccountId),
                nameof(AccountPlan.EffectiveFrom),
                nameof(AccountPlan.Id),
                nameof(AccountPlan.PlanKey),
                nameof(AccountPlan.Source),
                nameof(AccountPlan.SupersedesFrom),
            ],
            Plans().GetProperties().Select(property => property.Name).Order());
    }

    /// <summary>
    /// Immutable in the sense that matters — a row is never edited, so there is no row version — but not an
    /// <see cref="IImmutableRecord"/>, because cancelling a scheduled change deletes the end of the chain (AP.6.2).
    /// </summary>
    [Fact]
    public void A_row_carries_no_row_version_and_is_not_marked_write_once()
    {
        Assert.DoesNotContain(Plans().GetProperties(), property => property.IsConcurrencyToken);
        Assert.False(typeof(IImmutableRecord).IsAssignableFrom(typeof(AccountPlan)));
    }

    /// <summary>
    /// Billing restates the account id's width rather than reaching into the AiUsage module for it; this is what
    /// keeps the two from drifting, and a plan key the column cannot hold is refused at startup.
    /// </summary>
    [Fact]
    public void Column_widths_match_the_account_id_and_the_catalogue_key()
    {
        Assert.Equal(AiUsagePolicy.AccountIdMaxLength, BillingPolicy.AccountIdMaxLength);
        Assert.Equal(BillingPolicy.AccountIdMaxLength, Plans().FindProperty(nameof(AccountPlan.AccountId))!.GetMaxLength());
        Assert.Equal(BillingPolicy.PlanKeyMaxLength, Plans().FindProperty(nameof(AccountPlan.PlanKey))!.GetMaxLength());
        Assert.True(Plans().FindProperty(nameof(AccountPlan.PlanKey))!.IsNullable);
    }

    private IIndex Index(params string[] properties) =>
        Plans().GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name).SequenceEqual(properties));

    /// <summary>Check constraints are kept in the design-time model only; the runtime one does not carry them.</summary>
    private IEntityType DesignTimePlans()
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        return RecipeAggregateFixture.Db(scope).GetService<IDesignTimeModel>().Model.FindEntityType(typeof(AccountPlan))!;
    }

    private IEntityType Plans()
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        return RecipeAggregateFixture.Db(scope).Model.FindEntityType(typeof(AccountPlan))!;
    }
}
