using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.MigrationService;

internal sealed class DbContextMigrationTarget<TContext>(TContext context) : IMigrationTarget
    where TContext : DbContext
{
    public string Name => typeof(TContext).Name;

    // The execution strategy retries transient SQL failures around the whole migration run.
    public Task MigrateAsync(CancellationToken cancellationToken) =>
        context.Database.CreateExecutionStrategy()
            .ExecuteAsync(context, static (ctx, token) => ctx.Database.MigrateAsync(token), cancellationToken);
}
