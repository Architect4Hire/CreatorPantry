namespace CreatorPantry.MigrationService;

/// <summary>A database whose schema the migration service brings up to date before dependents start.</summary>
public interface IMigrationTarget
{
    string Name { get; }

    Task MigrateAsync(CancellationToken cancellationToken);
}
