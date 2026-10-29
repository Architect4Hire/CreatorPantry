using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.AiUsage.Data;

/// <summary>
/// EF access to an account's notice choices. Reads and writes no other module's tables.
/// </summary>
/// <remarks>
/// No <c>IgnoreQueryFilters()</c> and nothing to ignore: <see cref="AccountAiNoticeSetting"/> is not
/// <see cref="IWorkspaceOwned"/>, so every query here is account-scoped by construction. The account is always
/// a parameter from a validated session and never anything a request could carry.
/// </remarks>
internal interface IAiNoticeSettingRepository
{
    /// <summary>This account's row, or null when it has never chosen and so takes the defaults.</summary>
    Task<AccountAiNoticeSetting?> FindAsync(string accountId, CancellationToken cancellationToken);

    void Add(AccountAiNoticeSetting setting);

    Task SaveAsync(CancellationToken cancellationToken);

    /// <summary>Forgets everything tracked, so a lost race can start again from the database.</summary>
    void Forget();
}

/// <inheritdoc cref="IAiNoticeSettingRepository"/>
internal sealed class AiNoticeSettingRepository(CreatorPantryDbContext context) : IAiNoticeSettingRepository
{
    /// <remarks>
    /// Tracked rather than <c>AsNoTracking</c>: the only caller that asks this question is about to write the
    /// row it gets back, and re-reading it to write would be two round trips and a second chance to race.
    /// Seeks <c>UX_AccountAiNoticeSettings_Account</c>, the index that is the one-row-per-account invariant.
    /// </remarks>
    public Task<AccountAiNoticeSetting?> FindAsync(string accountId, CancellationToken cancellationToken) =>
        context.AccountAiNoticeSettings.SingleOrDefaultAsync(
            setting => setting.AccountId == accountId, cancellationToken);

    public void Add(AccountAiNoticeSetting setting) => context.AccountAiNoticeSettings.Add(setting);

    public Task SaveAsync(CancellationToken cancellationToken) => context.SaveChangesAsync(cancellationToken);

    public void Forget() => context.ChangeTracker.Clear();
}
