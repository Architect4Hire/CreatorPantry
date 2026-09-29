using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// The shape of <see cref="AccountAiQuotaReservation"/> in the built EF model.
/// </summary>
public sealed class AccountAiQuotaReservationModelShapeTests : IDisposable
{
    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>
    /// USAGE-003/004: a hold belongs to an account, never to a workspace. Not workspace-owned, no filter, and
    /// no <c>WorkspaceId</c> column at all — an allowance that could be named per workspace is one a creator
    /// could earn twice.
    /// </summary>
    [Fact]
    public void A_reservation_is_not_workspace_owned_filtered_or_scoped()
    {
        Assert.False(typeof(IWorkspaceOwned).IsAssignableFrom(typeof(AccountAiQuotaReservation)));
        Assert.Empty(Reservations().GetDeclaredQueryFilters());
        Assert.Null(Reservations().FindProperty("WorkspaceId"));
    }

    /// <summary>
    /// The absence of a query filter is defensible for exactly one reason, and it is the same reason the usage
    /// ledger beside it gives: the row holds counts and never content. Every column is an id, an instant, an
    /// amount, a flag, or an enum — and a free-text column here would make the missing filter indefensible.
    /// </summary>
    [Fact]
    public void A_reservation_has_no_free_text_column_of_any_kind()
    {
        var text = Reservations().GetProperties()
            .Where(property => property.ClrType == typeof(string)
                && property.Name != nameof(AccountAiQuotaReservation.AccountId))
            .Select(property => property.Name)
            .ToList();

        Assert.True(
            text.Count == 0,
            "a content-bearing column was added to an unfiltered table: " + string.Join(", ", text));
    }

    /// <summary>
    /// The one real relationship in this module, and the one deliberate absence beside it. A hold without its
    /// period is uninterpretable, so that reference is a foreign key — and Restrict, because a period holding
    /// outstanding reservations is not one anything may remove. Nothing else is: deleting a workspace does not
    /// unspend an account's allowance, and account erasure is a documented path rather than a cascade.
    /// </summary>
    [Fact]
    public void A_reservation_references_its_period_and_nothing_else()
    {
        var foreignKey = Assert.Single(Reservations().GetForeignKeys());

        Assert.Equal(typeof(AccountAiQuotaPeriod), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);

        Assert.DoesNotContain(
            Reservations().GetForeignKeys(),
            key => key.PrincipalEntityType.ClrType == typeof(Workspace)
                || key.PrincipalEntityType.ClrType == typeof(ApplicationUser)
                || key.PrincipalEntityType.ClrType == typeof(AiOperation));
    }

    /// <summary>
    /// One claim, one hold. The operation alone is not the identity: a lease that lapses returns its operation
    /// to the queue, and the next worker to claim it is asking for allowance again.
    /// </summary>
    [Fact]
    public void One_claim_can_hold_allowance_only_once()
    {
        var index = Reservations().GetIndexes().Single(candidate =>
            candidate.Properties.Select(property => property.Name).SequenceEqual(
            [
                nameof(AccountAiQuotaReservation.AiOperationId),
                nameof(AccountAiQuotaReservation.LeaseToken),
            ]));

        Assert.True(index.IsUnique);
    }

    /// <summary>
    /// The two sweep indexes are filtered and do not lead with the account, for the reason the AI queue's claim
    /// index does not lead with the workspace: a sweep is looking for work before it knows whose it is.
    /// </summary>
    [Theory]
    [InlineData(nameof(AccountAiQuotaReservation.ExpiresAt), "ReleasedAt IS NULL")]
    [InlineData(nameof(AccountAiQuotaReservation.SettledAt), "PostedAt IS NULL")]
    public void The_sweep_indexes_are_filtered_and_lead_with_the_instant(string column, string filter)
    {
        var index = Reservations().GetIndexes().Single(candidate =>
            candidate.Properties.Select(property => property.Name).SequenceEqual([column]));

        Assert.False(index.IsUnique);
        Assert.Equal(filter, index.GetFilter());
    }

    /// <summary>
    /// The amounts share the allowance scale rather than the ledger's cost scale: these are units a creator
    /// reads and an administrator sets, and a settlement that rounded per attempt would accumulate the error.
    /// </summary>
    [Theory]
    [InlineData(nameof(AccountAiQuotaReservation.ReservedAmount))]
    [InlineData(nameof(AccountAiQuotaReservation.PerAttemptEstimate))]
    [InlineData(nameof(AccountAiQuotaReservation.SettledAmount))]
    public void The_amounts_are_stored_at_the_allowance_scale(string column)
    {
        var property = Reservations().FindProperty(column);

        Assert.NotNull(property);
        Assert.Equal(AiUsagePolicy.AllowancePrecision, property.GetPrecision());
        Assert.Equal(AiUsagePolicy.AllowanceScale, property.GetScale());
    }

    /// <summary>
    /// The hold carries no row version of its own: every write that moves one of its counters moves the
    /// period's in the same transaction, so the period's token already serializes them.
    /// </summary>
    [Fact]
    public void A_reservation_carries_no_concurrency_token_of_its_own()
    {
        Assert.DoesNotContain(Reservations().GetProperties(), property => property.IsConcurrencyToken);

        // And the period still does, because it is the contended one.
        Assert.Contains(
            Runtime<AccountAiQuotaPeriod>().GetProperties(),
            property => property.IsConcurrencyToken);
    }

    private IEntityType Reservations() => Runtime<AccountAiQuotaReservation>();

    private IEntityType Runtime<TEntity>()
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        return RecipeAggregateFixture.Db(scope).Model.FindEntityType(typeof(TEntity))!;
    }
}
