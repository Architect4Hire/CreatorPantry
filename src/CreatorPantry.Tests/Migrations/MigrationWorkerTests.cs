using CreatorPantry.Domain.Data.Seeding;
using CreatorPantry.MigrationService;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CreatorPantry.Tests.Migrations;

public class MigrationWorkerTests
{
    [Fact]
    public async Task Run_with_no_targets_succeeds_and_stops_the_host()
    {
        var state = await RunHostAsync(_ => { });

        Assert.Equal(MigrationOutcome.Succeeded, state.Outcome);
        Assert.Equal(0, state.ExitCode);
    }

    [Fact]
    public async Task Failing_target_fails_the_run_with_a_nonzero_exit_code()
    {
        var state = await RunHostAsync(services =>
            services.AddSingleton<IMigrationTarget>(new RecordingTarget("broken", [], fail: true)));

        Assert.Equal(MigrationOutcome.Failed, state.Outcome);
        Assert.Equal(1, state.ExitCode);
    }

    [Fact]
    public async Task Targets_run_in_registration_order()
    {
        var calls = new List<string>();

        var state = await RunHostAsync(services => services
            .AddSingleton<IMigrationTarget>(new RecordingTarget("first", calls))
            .AddSingleton<IMigrationTarget>(new RecordingTarget("second", calls)));

        Assert.Equal(MigrationOutcome.Succeeded, state.Outcome);
        Assert.Equal(["first", "second"], calls);
    }

    [Fact]
    public async Task Failure_stops_later_targets()
    {
        var calls = new List<string>();

        var state = await RunHostAsync(services => services
            .AddSingleton<IMigrationTarget>(new RecordingTarget("broken", calls, fail: true))
            .AddSingleton<IMigrationTarget>(new RecordingTarget("after", calls)));

        Assert.Equal(MigrationOutcome.Failed, state.Outcome);
        Assert.Equal(["broken"], calls);
    }

    [Fact]
    public async Task Seeders_run_after_every_migration_target()
    {
        var calls = new List<string>();

        // Registration order deliberately interleaves seeders and targets.
        var state = await RunHostAsync(services => services
            .AddSingleton<IDataSeeder>(new RecordingSeeder("seed", calls))
            .AddSingleton<IMigrationTarget>(new RecordingTarget("schema-a", calls))
            .AddSingleton<IMigrationTarget>(new RecordingTarget("schema-b", calls)));

        Assert.Equal(MigrationOutcome.Succeeded, state.Outcome);
        Assert.Equal(["schema-a", "schema-b", "seed"], calls);
    }

    [Fact]
    public async Task Failing_migration_skips_seeding_and_failing_seeder_fails_the_run()
    {
        var calls = new List<string>();

        var migrationFailure = await RunHostAsync(services => services
            .AddSingleton<IMigrationTarget>(new RecordingTarget("broken", calls, fail: true))
            .AddSingleton<IDataSeeder>(new RecordingSeeder("seed", calls)));
        var seederFailure = await RunHostAsync(services => services
            .AddSingleton<IDataSeeder>(new RecordingSeeder("broken-seed", calls, fail: true)));

        Assert.Equal(MigrationOutcome.Failed, migrationFailure.Outcome);
        Assert.Equal(MigrationOutcome.Failed, seederFailure.Outcome);
        Assert.Equal(["broken", "broken-seed"], calls);
    }

    // Runs the real host to completion; the worker must stop the application itself.
    private static async Task<MigrationRunState> RunHostAsync(Action<IServiceCollection> configure)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddMigrationHost();
        configure(builder.Services);

        using var host = builder.Build();
        var state = host.Services.GetRequiredService<MigrationRunState>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        await host.RunAsync(timeout.Token);

        Assert.False(timeout.IsCancellationRequested, "The migration host did not stop on its own.");
        return state;
    }

    private sealed class RecordingTarget(string name, List<string> calls, bool fail = false) : IMigrationTarget
    {
        public string Name => name;

        public Task MigrateAsync(CancellationToken cancellationToken)
        {
            calls.Add(name);
            return fail ? Task.FromException(new InvalidOperationException("Migration failed.")) : Task.CompletedTask;
        }
    }

    private sealed class RecordingSeeder(string name, List<string> calls, bool fail = false) : IDataSeeder
    {
        public string Name => name;

        public Task SeedAsync(CancellationToken cancellationToken)
        {
            calls.Add(name);
            return fail ? Task.FromException(new InvalidOperationException("Seeding failed.")) : Task.CompletedTask;
        }
    }
}
