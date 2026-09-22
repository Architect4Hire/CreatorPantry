using System.Diagnostics;
using CreatorPantry.Domain.Data.Seeding;

namespace CreatorPantry.MigrationService;

internal sealed class MigrationWorker(
    IServiceProvider services,
    IHostEnvironment environment,
    IHostApplicationLifetime lifetime,
    MigrationRunState state,
    ILogger<MigrationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ServiceDefaults traces the ActivitySource named after the application.
        using var source = new ActivitySource(environment.ApplicationName);
        using var activity = source.StartActivity("Migrating database", ActivityKind.Client);

        try
        {
            await using var scope = services.CreateAsyncScope();
            var targets = scope.ServiceProvider.GetServices<IMigrationTarget>().ToList();

            if (targets.Count == 0)
            {
                logger.LogInformation("No migration targets registered; nothing to apply.");
            }

            foreach (var target in targets)
            {
                logger.LogInformation("Applying migrations for {MigrationTarget}.", target.Name);
                await target.MigrateAsync(stoppingToken);
                logger.LogInformation("Migrations applied for {MigrationTarget}.", target.Name);
            }

            // Seeders run only after every schema is current, and must be idempotent.
            foreach (var seeder in scope.ServiceProvider.GetServices<IDataSeeder>())
            {
                logger.LogInformation("Seeding {Seeder}.", seeder.Name);
                await seeder.SeedAsync(stoppingToken);
            }

            state.Outcome = MigrationOutcome.Succeeded;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("Migration run was cancelled before completion.");
            activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");
            state.Outcome = MigrationOutcome.Failed;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Migration run failed.");
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error);
            state.Outcome = MigrationOutcome.Failed;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }
}
