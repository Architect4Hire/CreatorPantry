using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Entities;

/// <summary>
/// One thing the tester noticed during a test run. Interior to the <see cref="RecipeTestRun"/> aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Mutable, and deliberately so.</strong> These are the tester's own words about their own cook, and
/// recipes.md protects creator-entered text from being rewritten <em>by the system</em>, not from being
/// corrected by the person who wrote it. Freezing them would mean a typo entered mid-bake could never be
/// fixed while the run around it was still being edited.
/// </para>
/// <para>
/// What is guaranteed instead is narrower and is the thing that actually matters: <strong>resolving an issue
/// never touches an observation.</strong> That is a property of which command writes what, and it holds
/// because the resolution seam writes <see cref="TestIssueResolution"/> rows and nothing else. A history
/// interface offers no edit here either — but that is a decision about a screen, not about a row.
/// </para>
/// <para>
/// Not anchored to a particular step or ingredient line. The version this belongs to stores its steps inside
/// a snapshot document, so an identifier pointing into one would be a column no foreign key could constrain
/// and no reader could trust — the kind of unchecked identifier
/// <see cref="RecipeAssetLink.MediaAssetId"/> only tolerates because a real constraint is coming for it.
/// </para>
/// </remarks>
public class TestObservation : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid RecipeTestRunId { get; set; }

    /// <summary>What this note is about. <see cref="TestObservationKind.Unspecified"/> is a legitimate answer.</summary>
    public TestObservationKind Kind { get; set; }

    /// <summary>What the tester noticed, in their words. Required — an observation with nothing in it is not one.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Position within the run's observations, unique there.</summary>
    public int SortOrder { get; set; }
}
