namespace CreatorPantry.Domain.Managers.Persistence;

/// <summary>
/// Idempotent seed data applied by the migration service after migrations. Running a seeder any number
/// of times must leave the same data.
/// </summary>
public interface IDataSeeder
{
    string Name { get; }

    Task SeedAsync(CancellationToken cancellationToken);
}
