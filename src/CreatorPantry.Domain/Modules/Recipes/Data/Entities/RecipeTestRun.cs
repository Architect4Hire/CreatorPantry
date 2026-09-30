using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Entities;

/// <summary>
/// One cook of one exact recipe version: when it was made, by whom, under what conditions, what came out, and
/// what the tester thought of it. Workspace-owned creator intellectual property, and the root of the test-run
/// aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A test run is always against an exact version.</strong> <see cref="RecipeVersionId"/> is required
/// and there is no "latest" to fall back on, because the whole value of a test is that it says what was true
/// of a specific set of words. A run that floated against whatever the recipe says today would report a result
/// for a recipe nobody cooked.
/// </para>
/// <para>
/// <strong>A run never changes the recipe.</strong> Every "actual" field here records what happened in a
/// kitchen, beside — never over — what the version claims. Reconciling the two is the creator's decision, and
/// it happens by editing the recipe, which produces a new version of its own.
/// </para>
/// <para>
/// <strong>Mutable, unlike <see cref="RecipeVersion"/>.</strong> A test is entered while it is happening and
/// finished afterwards, and the tester's own words about their own cook are theirs to correct. The one part
/// of this aggregate that is frozen is <see cref="TestIssueResolution"/>, which records a decision somebody
/// made rather than an observation somebody is still making. <see cref="RowVersion"/> is what makes two people
/// writing up the same bake a recoverable conflict rather than a lost one.
/// </para>
/// </remarks>
public class RecipeTestRun : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>
    /// The recipe this tested — the same recipe <see cref="RecipeVersionId"/> belongs to, guaranteed by the
    /// database rather than by whoever wrote the row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This looks like the duplicated fact <see cref="Recipe.DuplicatedFromVersionId"/>'s remarks forbid, and
    /// it is not, because it is <em>constrained</em> rather than merely copied: the composite foreign key
    /// <c>(WorkspaceId, RecipeId, RecipeVersionId) -> RecipeVersions (WorkspaceId, RecipeId, Id)</c> makes a
    /// row whose recipe disagrees with its version's recipe unrepresentable. Redundant by design, exactly as
    /// <see cref="Recipe.YieldUnitDimension"/> is.
    /// </para>
    /// <para>
    /// It earns that column twice over. A recipe's test history — the common read, and a recipe-scoped route —
    /// becomes one index seek with no join into <c>RecipeVersions</c>. And carrying it down the aggregate is
    /// what lets <see cref="TestIssueResolution.ResolutionRecipeVersionId"/> be constrained to a version
    /// <em>of this same recipe</em> by the database, instead of by a validator that a later caller can forget
    /// to run.
    /// </para>
    /// </remarks>
    public Guid RecipeId { get; set; }

    /// <summary>The exact version that was cooked. Required; see the type's remarks.</summary>
    public Guid RecipeVersionId { get; set; }

    /// <summary>
    /// When the cooking happened, which is not when the row was written.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="CreatedAt"/> because testers write their notes up afterwards, sometimes days
    /// afterwards. A history ordered by <see cref="CreatedAt"/> would order the tests by when somebody got
    /// round to typing them.
    /// </remarks>
    public DateTimeOffset TestedAt { get; set; }

    /// <summary>
    /// The <c>WorkspaceMembership</c> of whoever cooked it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Membership rather than Identity user id, and not a foreign key, for the reasons
    /// <see cref="Recipe.CreatedByMembershipId"/> gives: membership is the workspace-scoped identity
    /// (auth.md), and authorship has to survive a member leaving.
    /// </para>
    /// <para>
    /// Distinct from <see cref="CreatedByMembershipId"/>, which is whoever entered the record. One person
    /// writing up another's bake is ordinary, and collapsing the two would credit the wrong kitchen.
    /// </para>
    /// <para>
    /// This does mean a test cooked by somebody with no membership — a recipe tester outside the workspace —
    /// has no place to be named yet. That is a real limit of this model and not an oversight; adding an
    /// external tester is a decision about who may appear in a workspace's records, not a column.
    /// </para>
    /// </remarks>
    public Guid TestedByMembershipId { get; set; }

    /// <summary>The tester's verdict. Never derived from the issues or the rating; see <see cref="TestRunOutcome"/>.</summary>
    public TestRunOutcome Outcome { get; set; }

    /// <summary>
    /// One to five, or null when the tester did not score it. Bounded by
    /// <c>CK_RecipeTestRuns_Rating_Range</c>.
    /// </summary>
    public int? Rating { get; set; }

    /// <summary>The conditions the test ran under, in the tester's words. See <see cref="TestRunPolicy.EnvironmentNotesMaxLength"/>.</summary>
    public string? EnvironmentNotes { get; set; }

    /// <summary>What was actually used, where it differed from the recipe. See <see cref="TestRunPolicy.EquipmentNotesMaxLength"/>.</summary>
    public string? EquipmentNotes { get; set; }

    /// <summary>
    /// What it actually made, exactly as the tester phrased it: "got 10, not 12". The canonical actual yield,
    /// on the same terms <see cref="Recipe.YieldText"/> is canonical for the recipe — the structured trio
    /// below enriches it and never replaces it.
    /// </summary>
    public string? ActualYieldText { get; set; }

    /// <summary>The numeric actual yield when one was measured. Additive.</summary>
    public decimal? ActualYieldQuantity { get; set; }

    /// <summary>The unit it was measured in. Null unless <see cref="ActualYieldQuantity"/> is set.</summary>
    public Guid? ActualYieldUnitId { get; set; }

    /// <summary>
    /// Mirrors the referenced unit's own dimension, so the composite foreign key
    /// <c>(ActualYieldUnitId, ActualYieldUnitDimension)</c> can point at <c>MeasurementUnits (Id, Dimension)</c>
    /// and <c>CK_RecipeTestRuns_ActualYieldUnit_Dimension</c> can rule out
    /// <see cref="MeasurementDimension.Temperature"/>. The same pairing
    /// <see cref="Recipe.YieldUnitDimension"/> uses, and for the same reason: a yield measured in degrees is
    /// not a yield.
    /// </summary>
    public MeasurementDimension? ActualYieldUnitDimension { get; set; }

    /// <summary>What the prep actually took, in minutes.</summary>
    public int? ActualPrepTimeMinutes { get; set; }

    /// <summary>What the cooking actually took, in minutes.</summary>
    public int? ActualCookTimeMinutes { get; set; }

    /// <summary>What the resting actually took, in minutes.</summary>
    public int? ActualRestTimeMinutes { get; set; }

    /// <summary>
    /// What the whole thing actually took, stored independently of the other three.
    /// </summary>
    /// <remarks>
    /// Nothing here sums the parts, for the reason <see cref="Recipe.TotalTimeMinutes"/> gives on the recipe
    /// side and one more besides: a tester reporting "three hours, mostly waiting" is reporting an elapsed
    /// wall-clock time, and overlapping prep with cooking is exactly what makes a real kitchen's total
    /// smaller than the sum. Deriving it would replace a measurement with an arithmetic guess.
    /// </remarks>
    public int? ActualTotalTimeMinutes { get; set; }

    /// <summary>
    /// How the cook went overall. A <see cref="TestObservation"/> is about one aspect of the result; this is
    /// about the bake.
    /// </summary>
    public string? SummaryNotes { get; set; }

    /// <inheritdoc cref="Recipe.CreatedByMembershipId"/>
    public Guid CreatedByMembershipId { get; set; }

    /// <inheritdoc cref="Recipe.CreatedByMembershipId"/>
    public Guid UpdatedByMembershipId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Optimistic concurrency token. Two people writing up one bake — the cook and whoever is keeping the
    /// notes — is the ordinary case, and the second writer must get a recoverable conflict rather than
    /// silently erasing the first one's work.
    /// </summary>
    public byte[] RowVersion { get; set; } = [];

    public ICollection<TestObservation> Observations { get; set; } = [];

    public ICollection<TestIssue> Issues { get; set; } = [];

    public ICollection<TestAttachmentLink> Attachments { get; set; } = [];
}
