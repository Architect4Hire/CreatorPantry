namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Limits and invariants for the test-run aggregate, shared by EF configuration and by the validation that
/// arrives with the write seam. Test runs are workspace-owned creator intellectual property (tenancy.md):
/// every entity in the aggregate carries a <c>WorkspaceId</c> and is covered by the global query filter.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="RecipePolicy"/> rather than folded into it, because that type is about what a
/// recipe may say and this is about what a test of one may record. Where the two genuinely mean the same
/// thing — a duration in minutes, a caption on a linked asset, a creator's yield phrasing — this type reuses
/// <see cref="RecipePolicy"/>'s constant rather than restating the number, so the pair cannot drift into
/// disagreeing about what a plausible time is.
/// </para>
/// </remarks>
public static class TestRunPolicy
{
    /// <summary>
    /// The rating scale, inclusive at both ends. Five points because a tester is judging one cook of one
    /// version, not scoring it; a finer scale would invite precision the judgement does not have.
    /// </summary>
    public const int MinRating = 1;

    /// <inheritdoc cref="MinRating"/>
    public const int MaxRating = 5;

    /// <summary>
    /// The conditions the test was run under: oven, altitude, weather, whatever the tester thought mattered.
    /// </summary>
    /// <remarks>
    /// Free text on purpose. The things that change a bake — a convection oven, a humid day, a gas hob that
    /// runs hot — have no shared vocabulary to normalise against, and inventing one would force a tester to
    /// pick the nearest wrong option instead of writing the sentence they meant.
    /// </remarks>
    public const int EnvironmentNotesMaxLength = 1000;

    /// <summary>
    /// What was actually used, where it differed from what the recipe calls for: a dark metal tin in place of
    /// glass, a hand whisk in place of the mixer.
    /// </summary>
    /// <remarks>
    /// Free text rather than links into the <c>EquipmentType</c> vocabulary or a child table of its own. The
    /// recipe's own <c>RecipeEquipment</c> already says what the recipe asks for; this says what the tester
    /// had, which is a sentence about a deviation and not a list.
    /// </remarks>
    public const int EquipmentNotesMaxLength = 1000;

    /// <summary>How the cook went overall, as opposed to a <c>TestObservation</c> about one aspect of it.</summary>
    public const int SummaryNotesMaxLength = RecipePolicy.LongTextMaxLength;

    /// <summary>One observation. Prose, and occasionally a long paragraph of it.</summary>
    public const int ObservationTextMaxLength = RecipePolicy.LongTextMaxLength;

    /// <summary>An issue's one-line summary: "crumb too dense", "12 minutes was not enough".</summary>
    public const int IssueTitleMaxLength = 200;

    /// <summary>What the issue was, at length, when the title is not enough.</summary>
    public const int IssueDescriptionMaxLength = RecipePolicy.LongTextMaxLength;

    /// <summary>What was done about an issue, in the words of whoever dealt with it.</summary>
    public const int ResolutionNotesMaxLength = RecipePolicy.LongTextMaxLength;

    /// <summary>
    /// How many observations one test run may carry.
    /// </summary>
    /// <remarks>
    /// A cap rather than a judgement about how much a tester may notice: without one, a single request can
    /// stage unbounded rows, which is the same risk <see cref="RecipePolicy.MaxInstructionStepsPerRecipe"/>
    /// bounds. Generous — a long bake watched closely produces a lot of notes.
    /// </remarks>
    public const int MaxObservationsPerRun = 100;

    /// <inheritdoc cref="MaxObservationsPerRun"/>
    public const int MaxIssuesPerRun = 100;

    /// <summary>
    /// How far ahead of the server's clock a <c>testedAt</c> may sit before it is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not zero, because a client's clock is not the server's and refusing a test recorded seconds "in the
    /// future" would reject correct requests for no reason. Not generous either: the error this catches is a
    /// mistyped year, and a test dated 2099 would sit at the top of a recipe's history permanently.
    /// </para>
    /// <para>
    /// There is deliberately no floor. A creator recording a bake from last year is entering their own
    /// history, and a product that refused it would be telling them their records are wrong.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Why a correction version older than the issue it resolves was accepted anyway.
    /// </summary>
    /// <remarks>
    /// A reason rather than a flag. A boolean would record that somebody overrode the check and leave the
    /// next reader with no way to tell a considered decision from a mis-click.
    /// </remarks>
    public const int PredatingVersionOverrideReasonMaxLength = 1000;
}
