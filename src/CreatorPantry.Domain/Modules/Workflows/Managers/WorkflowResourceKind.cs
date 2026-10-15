namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>A kind of domain resource a run can link to by stable id. Grows as the catalogue needs it.</summary>
/// <remarks>
/// <para>
/// Each member names a record another module owns; a link is a pointer to it, never a copy (13A.2).
/// </para>
/// <para>
/// <strong>Append only; never renumber.</strong> Stored values are these numbers, and so is the copy an idempotent
/// replay keeps for a day — renumbering a member would change what both mean.
/// </para>
/// <para>
/// <strong>Adding a member is a migration.</strong> The run tables' kind checks are written from
/// <see cref="WorkflowResourceKinds.Lowest"/> and <see cref="WorkflowResourceKinds.Highest"/>, so a new kind
/// changes the model, and the model check fails until a migration widens them (13A.5).
/// </para>
/// </remarks>
public enum WorkflowResourceKind
{
    Recipe = 1,

    /// <summary>A piece of work a picture, posts or a long-form piece are made for.</summary>
    CreativeContext = 2,

    LongFormDraft = 3,

    BrandStyleGuide = 4,

    /// <summary>A picture in the library.</summary>
    MediaAsset = 5,

    /// <summary>A card on the content board.</summary>
    ContentCard = 6,

    PublishingRecord = 7,

    /// <summary>A personal work task (13A.23). Named before it exists, for the planner that is not built yet.</summary>
    WorkTask = 8,

    /// <summary>
    /// An AI request a step is waiting on — recipe ideas, or a first draft made from one (13A.14). A draft pointer
    /// only: it is what a step resumes from, never a result, because the request makes nothing of the creator's
    /// until they keep it, and keeping it makes a recipe.
    /// </summary>
    AiRequest = 9,
}

public static class WorkflowResourceKinds
{
    /// <summary>The lowest and highest stored values, which the run tables' kind checks admit between.</summary>
    public static readonly int Lowest = Enum.GetValues<WorkflowResourceKind>().Min(kind => (int)kind);

    public static readonly int Highest = Enum.GetValues<WorkflowResourceKind>().Max(kind => (int)kind);

    /// <summary>
    /// The placeholder a capability's route template uses for a linked resource of each kind, e.g.
    /// <c>{recipeId}</c>. A template may use these and nothing else.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, WorkflowResourceKind> RoutePlaceholders =
        new Dictionary<string, WorkflowResourceKind>(StringComparer.Ordinal)
        {
            ["recipeId"] = WorkflowResourceKind.Recipe,
            ["contextId"] = WorkflowResourceKind.CreativeContext,
            ["draftId"] = WorkflowResourceKind.LongFormDraft,
            ["guideId"] = WorkflowResourceKind.BrandStyleGuide,
            ["assetId"] = WorkflowResourceKind.MediaAsset,
            ["cardId"] = WorkflowResourceKind.ContentCard,
        };
}
