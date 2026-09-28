namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The creator's structured brief, as keys in an operation's <c>TaskInputsJson</c>.
/// </summary>
/// <remarks>
/// <strong>Shared by both capabilities that take a brief, and that is the point.</strong> AIREC-001 and
/// AIREC-002 ask for the same eleven things, each writes them in one place and reads them back in another,
/// and until these were constants there were four copies of every name with nothing tying them together.
/// Renaming one now moves all four.
/// </remarks>
public static class AiBriefInputs
{
    public const string Audience = "audience";

    public const string Course = "course";

    public const string Cuisine = "cuisine";

    public const string DietaryGoals = "dietaryGoals";

    public const string AvailableIngredients = "availableIngredients";

    public const string Exclusions = "exclusions";

    public const string Equipment = "equipment";

    public const string Skill = "skill";

    public const string Season = "season";

    public const string TimeBudget = "timeBudget";

    public const string CreatorStyle = "creatorStyle";

    /// <summary>Every brief key, in the order a brief is rendered in.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Audience,
        Course,
        Cuisine,
        DietaryGoals,
        AvailableIngredients,
        Exclusions,
        Equipment,
        Skill,
        Season,
        TimeBudget,
        CreatorStyle,
    ];
}

/// <summary>
/// The keys AIREC-002's request writes into <c>AiOperation.TaskInputsJson</c>, and which of them the task
/// handler renders into a prompt.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The one place the request seam and the handler have to agree.</strong> The request writes this JSON
/// and <see cref="RecipeFirstDraftAiTaskHandler"/> reads it back, in a different process, some time later —
/// there is no type between them, so a key renamed on one side and not the other would not fail to compile.
/// It would produce drafts quietly missing the field, which is the failure this type exists to make
/// impossible: both sides name the key from here, and a test asserts the two sets agree.
/// </para>
/// <para>
/// <strong><see cref="Provenance"/> is stored and never rendered.</strong> The concept's ids record which
/// concept a draft was made from — and are what makes a repeated idempotency key naming a <em>different</em>
/// concept a conflict rather than a replay — but the model has no use for an identifier and ai.md says it
/// never receives one. Only <see cref="Rendered"/> reaches a prompt.
/// </para>
/// </remarks>
public static class AiFirstDraftInputs
{
    /// <summary>The chosen concept's title and summary, composed into one line by the request seam.</summary>
    /// <remarks>The one key this capability adds to <see cref="AiBriefInputs"/>'s shared eleven.</remarks>
    public const string SelectedConcept = "selectedConcept";

    /// <summary>The concept request the chosen concept came from. Stored, never rendered.</summary>
    public const string SourceConceptRequestId = "sourceConceptRequestId";

    /// <summary>The chosen concept's server-minted id. Stored, never rendered.</summary>
    public const string SourceConceptId = "sourceConceptId";

    /// <summary>Every key that reaches a prompt, in the order the brief is rendered in.</summary>
    public static IReadOnlyList<string> Rendered { get; } = [SelectedConcept, .. AiBriefInputs.All];

    /// <summary>Keys recorded on the operation for provenance and idempotency, never rendered.</summary>
    public static IReadOnlyList<string> Provenance { get; } = [SourceConceptRequestId, SourceConceptId];

    /// <summary>Every key the request seam may write. Nothing outside this reaches the stored inputs.</summary>
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>([.. Rendered, .. Provenance], StringComparer.Ordinal);
}
