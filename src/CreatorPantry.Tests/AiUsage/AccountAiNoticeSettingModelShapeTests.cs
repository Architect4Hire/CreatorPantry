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
/// The shape of <see cref="AccountAiNoticeSetting"/>, and of the two notice columns on
/// <see cref="AccountAiQuotaPeriod"/>, in the built EF model (B-59).
/// </summary>
public sealed class AccountAiNoticeSettingModelShapeTests : IDisposable
{
    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>
    /// A creator's choice about their own account, not a workspace's: no <c>WorkspaceId</c>, no filter, and
    /// nothing to ignore (USAGE-003).
    /// </summary>
    [Fact]
    public void The_setting_is_not_workspace_owned_or_filtered()
    {
        Assert.False(typeof(IWorkspaceOwned).IsAssignableFrom(typeof(AccountAiNoticeSetting)));
        Assert.Empty(Settings().GetDeclaredQueryFilters());
        Assert.Null(Settings().FindProperty("WorkspaceId"));
    }

    /// <summary>
    /// No foreign key to the account, on the same reasoning as the quota tables: erasure is a documented path
    /// (9A.3), not a cascade default.
    /// </summary>
    [Fact]
    public void The_setting_declares_no_foreign_key()
    {
        Assert.Empty(Settings().GetForeignKeys());

        Assert.DoesNotContain(
            Settings().GetForeignKeys(),
            key => key.PrincipalEntityType.ClrType == typeof(ApplicationUser)
                || key.PrincipalEntityType.ClrType == typeof(Workspace));
    }

    /// <summary>
    /// One row per account, held by a unique index rather than by a write path anyone has to remember: two
    /// rows would be two answers to "is this notice on", and the reader would have to pick.
    /// </summary>
    [Fact]
    public void One_row_per_account_is_an_index_rather_than_a_convention()
    {
        var index = Assert.Single(Settings().GetIndexes());

        Assert.True(index.IsUnique);
        Assert.Equal("UX_AccountAiNoticeSettings_Account", index.GetDatabaseName());
        Assert.Equal([nameof(AccountAiNoticeSetting.AccountId)], index.Properties.Select(p => p.Name));
    }

    [Fact]
    public void The_account_id_is_stored_at_the_identity_key_width()
    {
        var accountId = Settings().FindProperty(nameof(AccountAiNoticeSetting.AccountId))!;

        Assert.False(accountId.IsNullable);
        Assert.Equal(AiUsagePolicy.AccountIdMaxLength, accountId.GetMaxLength());
    }

    /// <summary>
    /// The only string on the table is the account it belongs to. A preference row carries no creator text,
    /// which is what makes an account-scoped read of it safe rather than careful.
    /// </summary>
    [Fact]
    public void The_setting_stores_no_free_text()
    {
        Assert.Equal(
            [nameof(AccountAiNoticeSetting.AccountId)],
            Settings().GetProperties().Where(property => property.ClrType == typeof(string))
                .Select(property => property.Name).Order());
    }

    /// <summary>
    /// Both notice columns are nullable, and null is the meaning rather than missing data: not told, and not
    /// dismissed.
    /// </summary>
    [Fact]
    public void A_period_records_the_notice_and_its_dismissal_as_instants_that_may_be_absent()
    {
        var raised = Periods().FindProperty(nameof(AccountAiQuotaPeriod.LowCreditNoticedAt))!;
        var dismissed = Periods().FindProperty(nameof(AccountAiQuotaPeriod.LowCreditNoticeDismissedAt))!;

        Assert.True(raised.IsNullable);
        Assert.True(dismissed.IsNullable);
        Assert.Equal(typeof(DateTimeOffset?), raised.ClrType);
        Assert.Equal(typeof(DateTimeOffset?), dismissed.ClrType);
    }

    /// <summary>
    /// A dismissal is a dismissal of something: the database refuses one recorded before the notice it is
    /// about, or without one at all.
    /// </summary>
    [Fact]
    public void A_dismissal_without_a_notice_is_unstorable()
    {
        var constraints = DesignTime<AccountAiQuotaPeriod>().GetCheckConstraints()
            .Select(constraint => constraint.Name);

        Assert.Contains("CK_AccountAiQuotaPeriods_LowCreditNotice_Dismissed_After_Raised", constraints);
    }

    private IEntityType Settings() => Runtime<AccountAiNoticeSetting>();

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
