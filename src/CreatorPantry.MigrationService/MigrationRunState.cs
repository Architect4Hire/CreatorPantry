namespace CreatorPantry.MigrationService;

/// <summary>The outcome of the migration run, read by Program to choose the process exit code.</summary>
public sealed class MigrationRunState
{
    public MigrationOutcome Outcome { get; internal set; } = MigrationOutcome.NotRun;

    public int ExitCode => Outcome == MigrationOutcome.Succeeded ? 0 : 1;
}

public enum MigrationOutcome
{
    NotRun,
    Succeeded,
    Failed,
}
