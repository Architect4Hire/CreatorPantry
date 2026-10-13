using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace CreatorPantry.Tests;

/// <summary>
/// A real SQL Server database of a fixture's own, already migrated, on a server the whole test run shares.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why one server.</strong> Every fixture that needs SQL Server used to start a container of its own
/// and run every migration against it: some thirty servers in a full run, a dozen or more of them booting at
/// once. That was most of a full run's time, and it is what made a class go red only when everything ran
/// together. One server holds thirty small databases without noticing.
/// </para>
/// <para>
/// <strong>Why isolation is unchanged.</strong> Each fixture still gets a database nothing else can see, under
/// a name nothing else knows. Two fixtures share a process and a port, and no row, lock or transaction.
/// </para>
/// <para>
/// <strong>What is still proved about migrations.</strong> The first fixture to start runs
/// <c>Database.MigrateAsync()</c> against a fresh database, exactly as each used to — so a migration SQL Server
/// rejects still fails the run, for every fixture, with SQL Server's own message. That database is then backed
/// up and each fixture's is restored from it, which is the same schema and the same migration history byte
/// for byte. A fixture that calls <c>MigrateAsync()</c> itself finds nothing left to apply.
/// </para>
/// <para>
/// The shape — <see cref="StartAsync"/>, <see cref="GetConnectionString"/>, <see cref="DisposeAsync"/> — is the
/// container's own, so a fixture holds this where it held one.
/// </para>
/// </remarks>
internal sealed class SqlServerTestDatabase : IAsyncDisposable
{
    private const string Image = "mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-22.04";

    private const string Template = "cp_template";

    private const string BackupPath = "/var/opt/mssql/data/cp_template.bak";

    /// <summary>The server, started once, with the migrated template backed up on it. Reaped with the process.</summary>
    private static readonly Lazy<Task<MsSqlContainer>> Server = new(StartServerAsync);

    private readonly string _name = $"cp_{Guid.NewGuid():N}";

    private string? _connectionString;

    public async Task StartAsync()
    {
        var server = await Server.Value;

        await ExecuteAsync(
            server.GetConnectionString(),
            $"""
            RESTORE DATABASE [{_name}] FROM DISK = '{BackupPath}'
            WITH MOVE '{Template}' TO '/var/opt/mssql/data/{_name}.mdf',
                 MOVE '{Template}_log' TO '/var/opt/mssql/data/{_name}_log.ldf';
            """);

        _connectionString = For(server, _name);
    }

    public string GetConnectionString() =>
        _connectionString ?? throw new InvalidOperationException("The database has not been started.");

    public async ValueTask DisposeAsync()
    {
        if (_connectionString is null)
        {
            return;
        }

        // Pooled connections would hold the database open against the drop.
        SqlConnection.ClearPool(new SqlConnection(_connectionString));

        var server = await Server.Value;
        await ExecuteAsync(
            server.GetConnectionString(),
            $"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}];");
    }

    private static async Task<MsSqlContainer> StartServerAsync()
    {
        var server = new MsSqlBuilder(Image).Build();
        await server.StartAsync();

        await ExecuteAsync(server.GetConnectionString(), $"CREATE DATABASE [{Template}];");

        // The context alone, with what its constructor asks for and nothing of any module.
        await using (var provider = new ServiceCollection()
            .AddTenancy()
            .AddApplicationTime()
            .AddAudit()
            .AddLogging()
            .AddDbContext<CreatorPantryDbContext>(options => options.UseSqlServer(For(server, Template)))
            .BuildServiceProvider(validateScopes: true))
        {
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.MigrateAsync();
        }

        SqlConnection.ClearAllPools();
        await ExecuteAsync(server.GetConnectionString(), $"BACKUP DATABASE [{Template}] TO DISK = '{BackupPath}' WITH INIT;");

        return server;
    }

    private static string For(MsSqlContainer server, string database) =>
        new SqlConnectionStringBuilder(server.GetConnectionString()) { InitialCatalog = database }.ConnectionString;

    /// <summary>
    /// One restore, backup or drop at a time. Each takes a fraction of a second, so nothing is lost by it, and
    /// a dozen fixtures restoring from one backup in the same instant has taken the server down.
    /// </summary>
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await OneAtATime.WaitAsync();

        try
        {
            await RunAsync(connectionString, sql);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private static async Task RunAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }
}
