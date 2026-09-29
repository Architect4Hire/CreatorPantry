using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
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
/// The shape of <see cref="AccountAiUsageEntry"/> in the built EF model.
/// </summary>
/// <remarks>
/// Borrows <c>RecipeAggregateFixture</c> for the same reason <c>AiOperationModelShapeTests</c> does: it builds
/// the whole <c>CreatorPantryDbContext</c> model, which is all these assertions read. This entity has no
/// foreign keys of its own — proving that is one of the assertions.
/// </remarks>
public sealed class AccountAiUsageEntryModelShapeTests : IDisposable
{
    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>
    /// The decision this whole module rests on (USAGE-001/002). Asserted rather than trusted, because the
    /// obvious "fix" for an unfiltered table carrying a <c>WorkspaceId</c> is to make it workspace-owned,
    /// which would silently destroy the one question the ledger exists to answer.
    /// </summary>
    [Fact]
    public void Is_deliberately_not_workspace_owned_and_carries_no_query_filter()
    {
        Assert.False(typeof(IWorkspaceOwned).IsAssignableFrom(typeof(AccountAiUsageEntry)));
        Assert.Empty(Entries().GetDeclaredQueryFilters());
    }

    /// <summary>A posted attempt cost what it cost.</summary>
    [Fact]
    public void Is_write_once()
    {
        Assert.True(typeof(IImmutableRecord).IsAssignableFrom(typeof(AccountAiUsageEntry)));
    }

    /// <summary>
    /// No relationship to <c>Workspace</c>, <c>AiOperation</c> or <c>ApplicationUser</c>: every one of them
    /// would cascade an account's usage history away, and deleting a workspace does not unspend the tokens.
    /// The references are ids the recording seam validates, not relationships the database enforces.
    /// </summary>
    [Fact]
    public void Declares_no_foreign_keys_so_no_cascade_can_erase_an_accounts_history()
    {
        var entries = Entries();

        Assert.Empty(entries.GetForeignKeys());

        // Stated by name as well as by count, so that adding one of these later has to argue with this test
        // rather than merely change a number.
        Assert.DoesNotContain(
            entries.GetForeignKeys(),
            key => key.PrincipalEntityType.ClrType == typeof(Workspace)
                || key.PrincipalEntityType.ClrType == typeof(AiOperation)
                || key.PrincipalEntityType.ClrType == typeof(ApplicationUser));
    }

    /// <summary>
    /// One attempt posts at most one entry, so a duplicate delivery posts once. Deliberately not
    /// workspace-leading: operation ids are globally unique, and a dedup lookup that needed a workspace would
    /// reintroduce the scoping this table exists to escape.
    /// </summary>
    [Fact]
    public void One_attempt_can_post_at_most_one_entry()
    {
        var dedup = Entries().GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(AccountAiUsageEntry.AiOperationId), nameof(AccountAiUsageEntry.AttemptNumber)]));

        Assert.True(dedup.IsUnique);
    }

    /// <summary>
    /// The account/period seek. Leads with the account because that is the only thing the read seam
    /// authorizes by.
    /// </summary>
    [Fact]
    public void The_period_index_leads_with_the_account()
    {
        Assert.Contains(Entries().GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(AccountAiUsageEntry.AccountId), nameof(AccountAiUsageEntry.OccurredAt)]));
    }

    /// <summary>
    /// The per-workspace breakdown (USAGE-008), with <c>OccurredAt</c> trailing so one period's slice is a
    /// range seek rather than a scan of the account's whole history.
    /// </summary>
    [Fact]
    public void The_workspace_breakdown_index_leads_with_the_account_not_the_workspace()
    {
        Assert.Contains(Entries().GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(AccountAiUsageEntry.AccountId),
                nameof(AccountAiUsageEntry.WorkspaceId),
                nameof(AccountAiUsageEntry.OccurredAt),
            ]));
    }

    /// <remarks>
    /// Check constraints are read from the design-time model. The runtime model is read-optimized and drops
    /// them — it throws rather than returning an empty list.
    /// </remarks>
    [Theory]
    [InlineData("CK_AccountAiUsageEntries_Attempt_Positive")]
    [InlineData("CK_AccountAiUsageEntries_TaskType_Declared")]
    [InlineData("CK_AccountAiUsageEntries_Outcome_Declared")]
    [InlineData("CK_AccountAiUsageEntries_Tokens_NotNegative")]
    [InlineData("CK_AccountAiUsageEntries_UsageReported_Matches_Tokens")]
    [InlineData("CK_AccountAiUsageEntries_Cost_NotNegative")]
    public void Declares_its_check_constraints(string name)
    {
        Assert.Contains(DesignTimeEntries().GetCheckConstraints(), constraint => constraint.Name == name);
    }

    /// <summary>
    /// The reason this table is allowed to have no workspace filter: it holds counts and never content. The
    /// only strings on it are the account id and the three bounded provider identifiers — no title, no prompt
    /// body, no proposal text, and no free-text column at all, not even a sanitized failure summary.
    /// </summary>
    /// <remarks>
    /// The cheapest way to break that is to add a "Notes" or "LastError" string when something needs
    /// debugging. <c>AiExecutionMetadata.FailureSummary</c> already carries the diagnostic, inside the
    /// workspace filter, which is where it belongs. Widening this list is not a fix for a failure here.
    /// </remarks>
    [Fact]
    public void Has_no_free_text_column_creator_content_could_land_in()
    {
        var textProperties = Entries().GetProperties()
            .Where(property => property.ClrType == typeof(string))
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(
            [
                nameof(AccountAiUsageEntry.AccountId),
                nameof(AccountAiUsageEntry.ModelDeployment),
                nameof(AccountAiUsageEntry.ModelName),
                nameof(AccountAiUsageEntry.ProviderName),
            ],
            textProperties.Order());

        Assert.All(textProperties, name => Assert.NotNull(Entries().FindProperty(name)!.GetMaxLength()));
    }

    /// <summary>
    /// Null is "the provider did not report", never zero (USAGE-005), so the three count columns have to be
    /// able to hold null — and <see cref="AccountAiUsageEntry.UsageReported"/> is what makes that storable
    /// exactly one way.
    /// </summary>
    [Fact]
    public void Token_counts_are_nullable_because_unreported_is_not_zero()
    {
        var entries = Entries();

        Assert.True(entries.FindProperty(nameof(AccountAiUsageEntry.InputTokens))!.IsNullable);
        Assert.True(entries.FindProperty(nameof(AccountAiUsageEntry.OutputTokens))!.IsNullable);
        Assert.True(entries.FindProperty(nameof(AccountAiUsageEntry.TotalTokens))!.IsNullable);
        Assert.False(entries.FindProperty(nameof(AccountAiUsageEntry.UsageReported))!.IsNullable);
    }

    [Fact]
    public void The_account_id_is_required_and_matches_the_identity_key_width()
    {
        var accountId = Entries().FindProperty(nameof(AccountAiUsageEntry.AccountId))!;

        Assert.False(accountId.IsNullable);
        Assert.Equal(AiUsagePolicy.AccountIdMaxLength, accountId.GetMaxLength());
    }

    private IEntityType Entries()
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        return RecipeAggregateFixture.Db(scope).Model.FindEntityType(typeof(AccountAiUsageEntry))!;
    }

    private IEntityType DesignTimeEntries()
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var model = RecipeAggregateFixture.Db(scope).GetService<IDesignTimeModel>().Model;

        return model.FindEntityType(typeof(AccountAiUsageEntry))!;
    }
}
