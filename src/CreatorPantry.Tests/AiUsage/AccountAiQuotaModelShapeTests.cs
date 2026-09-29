using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// The shape of <see cref="AccountAiQuota"/> and <see cref="AccountAiQuotaPeriod"/> in the built EF model.
/// </summary>
public sealed class AccountAiQuotaModelShapeTests : IDisposable
{
    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>
    /// USAGE-003: a quota belongs to an account, never to a workspace and never to a membership. Neither table
    /// is workspace-owned and neither carries a filter, because one person's allowance is spent from whichever
    /// workspace they happen to be working in.
    /// </summary>
    [Fact]
    public void Neither_table_is_workspace_owned_or_filtered()
    {
        Assert.False(typeof(IWorkspaceOwned).IsAssignableFrom(typeof(AccountAiQuota)));
        Assert.False(typeof(IWorkspaceOwned).IsAssignableFrom(typeof(AccountAiQuotaPeriod)));

        Assert.Empty(Quotas().GetDeclaredQueryFilters());
        Assert.Empty(Periods().GetDeclaredQueryFilters());
    }

    /// <summary>
    /// Neither table has a <c>WorkspaceId</c> at all — not even the reporting dimension the usage ledger
    /// carries. An allowance that could be named per workspace is one a creator could earn twice.
    /// </summary>
    [Fact]
    public void Neither_table_has_a_workspace_column_at_all()
    {
        Assert.Null(Quotas().FindProperty("WorkspaceId"));
        Assert.Null(Periods().FindProperty("WorkspaceId"));
    }

    [Fact]
    public void Neither_table_declares_a_foreign_key()
    {
        Assert.Empty(Quotas().GetForeignKeys());
        Assert.Empty(Periods().GetForeignKeys());

        // A period deliberately does not reference the quota that opened it: it copied the terms, and it has
        // to survive that quota being superseded.
        Assert.DoesNotContain(
            Periods().GetForeignKeys(),
            key => key.PrincipalEntityType.ClrType == typeof(AccountAiQuota));

        Assert.DoesNotContain(
            Quotas().GetForeignKeys(),
            key => key.PrincipalEntityType.ClrType == typeof(ApplicationUser)
                || key.PrincipalEntityType.ClrType == typeof(Workspace));
    }

    /// <summary>
    /// The invariant the whole effective-dating rests on: at most one set of terms in force per account at any
    /// instant, held by a filtered unique index rather than by a write path anyone has to remember.
    /// </summary>
    [Fact]
    public void At_most_one_quota_is_in_force_per_account()
    {
        var current = Quotas().GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(AccountAiQuota.AccountId)]));

        Assert.True(current.IsUnique);
        Assert.Equal("EffectiveTo IS NULL", current.GetFilter());
    }

    [Fact]
    public void Quota_history_rows_cannot_collide_on_a_start_instant()
    {
        var history = Quotas().GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(AccountAiQuota.AccountId), nameof(AccountAiQuota.EffectiveFrom)]));

        Assert.True(history.IsUnique);
        Assert.Null(history.GetFilter());
    }

    /// <summary>
    /// What makes the period roll idempotent: two workers both opening the next period race here and one
    /// loses, rather than both succeeding and splitting the account's allowance across two rows.
    /// </summary>
    [Fact]
    public void One_period_per_account_per_start_instant()
    {
        var roll = Periods().GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(AccountAiQuotaPeriod.AccountId), nameof(AccountAiQuotaPeriod.StartsAt)]));

        Assert.True(roll.IsUnique);
    }

    /// <summary>
    /// The finalization sweep, and the one index here that deliberately does not lead with the account: it
    /// looks for ended-but-unsettled periods before it knows whose they are, exactly as
    /// <c>AiOperation</c>'s claim index looks for queued work before it knows the workspace. Asserted
    /// explicitly so "not account-leading" stays a decision rather than an oversight someone later corrects.
    /// </summary>
    [Fact]
    public void The_finalization_sweep_leads_with_the_end_not_the_account()
    {
        var sweep = Periods().GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(AccountAiQuotaPeriod.EndsAt)]));

        Assert.False(sweep.IsUnique);
        Assert.Equal("SettledAt IS NULL", sweep.GetFilter());
    }

    /// <summary>
    /// USAGE-003 forbids inventing a default allowance in domain code. A nullable column is what makes that
    /// structural: there is nothing to read and fall back from, because the row says the number is not here.
    /// The period's copy is required, because by then the default has been resolved to a fact.
    /// </summary>
    [Fact]
    public void A_quota_allowance_may_be_absent_but_a_periods_may_not()
    {
        Assert.True(Quotas().FindProperty(nameof(AccountAiQuota.Allowance))!.IsNullable);
        Assert.False(Periods().FindProperty(nameof(AccountAiQuotaPeriod.Allowance))!.IsNullable);
    }

    /// <summary>
    /// The period freezes the terms it opened under, so an administrator changing a quota mid-period cannot
    /// retroactively re-price what a creator has already spent against.
    /// </summary>
    [Theory]
    [InlineData(nameof(AccountAiQuotaPeriod.Unit))]
    [InlineData(nameof(AccountAiQuotaPeriod.Allowance))]
    [InlineData(nameof(AccountAiQuotaPeriod.TimeZoneId))]
    [InlineData(nameof(AccountAiQuotaPeriod.LocalStartDate))]
    public void A_period_carries_its_own_copy_of_the_terms(string property)
    {
        Assert.NotNull(Periods().FindProperty(property));
    }

    /// <summary>
    /// Contended counters, not collaborative editing (USAGE-004). The quota has no rowversion because its
    /// terms are replaced by a new effective-dated row rather than incremented.
    /// </summary>
    [Fact]
    public void Only_the_period_carries_a_concurrency_token()
    {
        Assert.True(Periods().FindProperty(nameof(AccountAiQuotaPeriod.RowVersion))!.IsConcurrencyToken);
        Assert.Null(Quotas().FindProperty("RowVersion"));
    }

    /// <summary>
    /// Both tables are written to after insert — the quota when it is closed, the period on every reservation
    /// — so neither may claim the write-once guarantee the ledger makes.
    /// </summary>
    [Fact]
    public void Neither_table_claims_to_be_write_once()
    {
        Assert.False(typeof(IImmutableRecord).IsAssignableFrom(typeof(AccountAiQuota)));
        Assert.False(typeof(IImmutableRecord).IsAssignableFrom(typeof(AccountAiQuotaPeriod)));
    }

    /// <remarks>
    /// Check constraints are read from the design-time model. The runtime model is read-optimized and drops
    /// them — it throws rather than returning an empty list.
    /// </remarks>
    [Theory]
    [InlineData("CK_AccountAiQuotas_Unit_Declared")]
    [InlineData("CK_AccountAiQuotas_PeriodLength_Declared")]
    [InlineData("CK_AccountAiQuotas_CarryOver_Declared")]
    [InlineData("CK_AccountAiQuotas_Allowance_NotNegative")]
    [InlineData("CK_AccountAiQuotas_Anchor_Matches_Length")]
    [InlineData("CK_AccountAiQuotas_CarryOverCap_Matches_Rule")]
    [InlineData("CK_AccountAiQuotas_Effective_Range")]
    public void The_quota_declares_its_check_constraints(string name)
    {
        Assert.Contains(DesignTime<AccountAiQuota>().GetCheckConstraints(), constraint => constraint.Name == name);
    }

    /// <inheritdoc cref="The_quota_declares_its_check_constraints"/>
    [Theory]
    [InlineData("CK_AccountAiQuotaPeriods_Unit_Declared")]
    [InlineData("CK_AccountAiQuotaPeriods_Ends_After_Starts")]
    [InlineData("CK_AccountAiQuotaPeriods_Totals_NotNegative")]
    [InlineData("CK_AccountAiQuotaPeriods_Settled_After_End")]
    public void The_period_declares_its_check_constraints(string name)
    {
        Assert.Contains(DesignTime<AccountAiQuotaPeriod>().GetCheckConstraints(), constraint => constraint.Name == name);
    }

    /// <summary>
    /// A monthly anchor above <see cref="AiUsagePolicy.MonthlyAnchorMax"/> is refused, so every anchor lands
    /// in every month and a boundary never moves depending on which month it is.
    /// </summary>
    [Fact]
    public void The_anchor_constraint_pairs_each_length_with_its_own_range()
    {
        var constraint = DesignTime<AccountAiQuota>().GetCheckConstraints()
            .Single(check => check.Name == "CK_AccountAiQuotas_Anchor_Matches_Length");

        Assert.Contains(
            $"PeriodLength = {(int)AiQuotaPeriodLength.Monthly} AND PeriodAnchor BETWEEN 1 AND {AiUsagePolicy.MonthlyAnchorMax}",
            constraint.Sql,
            StringComparison.Ordinal);

        Assert.Contains(
            $"PeriodLength = {(int)AiQuotaPeriodLength.Weekly} AND PeriodAnchor BETWEEN 0 AND 6",
            constraint.Sql,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A provider can use more than a reservation estimated, and refusing to record the overshoot would lose
    /// the attempt entirely. Asserted as an absence so that "add a sanity constraint" has to argue with this
    /// test rather than quietly break settlement.
    /// </summary>
    [Fact]
    public void A_periods_totals_are_not_capped_by_its_allowance()
    {
        Assert.DoesNotContain(
            DesignTime<AccountAiQuotaPeriod>().GetCheckConstraints(),
            constraint => constraint.Sql.Contains("Consumed + Reserved", StringComparison.Ordinal));
    }

    /// <summary>
    /// Both tables hold terms and counts. The only strings are the account id, the actor id and the IANA zone
    /// — no reason text, no administrator note, and nowhere for creator content to land.
    /// </summary>
    [Fact]
    public void Neither_table_has_a_free_text_column()
    {
        Assert.Equal(
            [
                nameof(AccountAiQuota.AccountId),
                nameof(AccountAiQuota.LastChangedByUserId),
                nameof(AccountAiQuota.TimeZoneId),
            ],
            Quotas().GetProperties().Where(property => property.ClrType == typeof(string))
                .Select(property => property.Name).Order());

        Assert.Equal(
            [
                nameof(AccountAiQuotaPeriod.AccountId),
                nameof(AccountAiQuotaPeriod.TimeZoneId),
            ],
            Periods().GetProperties().Where(property => property.ClrType == typeof(string))
                .Select(property => property.Name).Order());
    }

    private IEntityType Quotas() => Runtime<AccountAiQuota>();

    private IEntityType Periods() => Runtime<AccountAiQuotaPeriod>();

    private IEntityType Runtime<TEntity>()
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        return RecipeAggregateFixture.Db(scope).Model.FindEntityType(typeof(TEntity))!;
    }

    private IEntityType DesignTime<TEntity>()
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var model = RecipeAggregateFixture.Db(scope).GetService<IDesignTimeModel>().Model;

        return model.FindEntityType(typeof(TEntity))!;
    }
}
