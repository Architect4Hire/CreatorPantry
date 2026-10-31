using CreatorPantry.Domain.Managers.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Auth.Data;

internal interface IAccountCreationRepository
{
    /// <summary>The account's <c>CreatedAt</c>, by primary key; null when there is no such account.</summary>
    Task<DateTimeOffset?> FindCreatedAtAsync(string accountId, CancellationToken cancellationToken);
}

internal sealed class AccountCreationRepository(CreatorPantryDbContext context) : IAccountCreationRepository
{
    public Task<DateTimeOffset?> FindCreatedAtAsync(string accountId, CancellationToken cancellationToken) =>
        context.Users
            .AsNoTracking()
            .Where(user => user.Id == accountId)
            .Select(user => (DateTimeOffset?)user.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
}
