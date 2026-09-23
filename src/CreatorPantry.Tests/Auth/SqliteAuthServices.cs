using System.Collections.Concurrent;
using CreatorPantry.Domain.Modules.Auth;
using CreatorPantry.Domain.Modules.Auth.Managers;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Gateways;
using CreatorPantry.Domain.Managers.Time;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace CreatorPantry.Tests.Auth;

/// <summary>
/// The real auth seam and UserManager over an in-memory SQLite database, with a fake clock, the
/// development message sink, and captured log output. SQLite enforces the Identity unique indexes;
/// SQL Server-specific behavior is verified once migrations exist.
/// </summary>
internal sealed class SqliteAuthServices : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    /// <param name="configure">Applied after the production registrations, to override options.</param>
    public SqliteAuthServices(Action<IServiceCollection>? configure = null)
    {
        _connection.Open();

        var services = new ServiceCollection()
            .AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs))
            .AddSingleton<TimeProvider>(Time)
            .AddApplicationTime()
            .AddDbContext<CreatorPantryDbContext>(options => options.UseSqlite(_connection))
            .AddDevelopmentAccountMessageSink()
            .AddAuthDomain();
        configure?.Invoke(services);

        _provider = services.BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.EnsureCreated();
    }

    public FakeTimeProvider Time { get; } = new(Now);

    public CapturingLoggerProvider Logs { get; } = new();

    public InMemoryAccountMessageSink Messages => _provider.GetRequiredService<InMemoryAccountMessageSink>();

    /// <summary>A fresh scope per call, like a request.</summary>
    public AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

    /// <summary>Creates a user whose email is confirmed, returning its id.</summary>
    public async Task<string> CreateConfirmedUserAsync(string email, string password)
    {
        await using var scope = CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Sam", CreatedAt = Now, EmailConfirmed = true };

        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Code)));
        return user.Id;
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }
}

/// <summary>Captures every formatted log message and structured value, for leak assertions.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(" ", pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                : string.Empty;
            entries.Enqueue($"{formatter(state, exception)} {values} {exception}");
        }
    }
}
