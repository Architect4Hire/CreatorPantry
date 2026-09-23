using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Seeding;
using CreatorPantry.Domain.Modules.Vocabulary.Seeding;
using CreatorPantry.Domain.Modules.Ingredients.Seeding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CreatorPantry.Tests.Reference;

/// <summary>
/// An in-memory SQLite database holding the real seeded reference catalogue.
/// </summary>
/// <remarks>
/// Seeded by the production <see cref="ReferenceDataSeeder"/> rather than by hand-built rows, so these tests
/// page and search over the same catalogue a deployment gets. A repository test that invented its own three
/// cuisines would prove the query compiles and nothing about whether it orders the real data correctly.
/// </remarks>
internal sealed class ReferenceCatalogFixture : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    private ReferenceCatalogFixture()
    {
    }

    public static async Task<ReferenceCatalogFixture> StartAsync()
    {
        var fixture = new ReferenceCatalogFixture();
        await fixture._connection.OpenAsync();

        await using var context = fixture.CreateContext();
        await context.Database.EnsureCreatedAsync();

        await new ReferenceDataSeeder(
                context,
                new ReferenceSeedOptions(IncludeDevelopmentSampleData: true),
                NullLogger<ReferenceDataSeeder>.Instance)
            .SeedAsync(TestContext.Current.CancellationToken);

        return fixture;
    }

    public CreatorPantryDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CreatorPantryDbContext>().UseSqlite(_connection).Options);

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
