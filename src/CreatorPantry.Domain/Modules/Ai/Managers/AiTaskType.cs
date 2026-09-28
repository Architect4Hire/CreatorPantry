namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>Which AI capability an operation is running.</summary>
/// <remarks>
/// <para>
/// Deliberately almost empty. The recipe capabilities — concepts, rewrites, substitutions, scaling review —
/// arrive with the prompts that define them, and inventing their names here before a single one exists would
/// fix a vocabulary nothing has had to live with yet.
/// </para>
/// <para>
/// A task type is chosen by the server from an allow-list, never taken from a request field: the client picks
/// a discriminator the API recognises, and the API maps it here.
/// </para>
/// </remarks>
public enum AiTaskType
{
    /// <summary>
    /// Not declared. Never valid on a stored row — the check constraint refuses it — so that a row which
    /// forgot to say what it was doing cannot pass for the first real member of this enum.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// An inert task used to exercise the operation lifecycle end to end without calling a model. It is what
    /// the first generic proposal endpoint is wired to before any real capability is enabled.
    /// </summary>
    Diagnostic = 1,

    /// <summary>
    /// AIREC-001: proposes several distinct recipe concepts from a creator's structured brief — audience,
    /// course, cuisine, dietary goals, available ingredients, exclusions, equipment, skill, season, time
    /// budget, and creator style. Names no recipe: the brief is the source, not a pinned version.
    /// </summary>
    RecipeConcepts = 2,

    /// <summary>
    /// AIREC-002: proposes one complete structured first draft — title, description, yield, timing,
    /// ingredient groups, ordered instructions, equipment, notes, and unresolved questions — from a selected
    /// concept or declared brief fields. Names no recipe: like <see cref="RecipeConcepts"/>, there is nothing
    /// yet to pin a version against.
    /// </summary>
    RecipeFirstDraft = 3,

    /// <summary>
    /// AIREC-003: proposes scoped changes to one pinned version of an existing recipe, in the sections the
    /// creator selected and towards the goal they stated, with a rationale for each.
    /// </summary>
    /// <remarks>
    /// The first task to <em>revise</em> rather than originate: it names a recipe and a version, so it is the
    /// recipe-bound lifecycle's own shape — a server-computed diff against the pinned source, reviewed through
    /// the proposal panel and applied through the recipe module's facade.
    /// </remarks>
    RecipeRevision = 4,

    /// <summary>
    /// AIREC-004: ranked alternatives for one ingredient the creator selected in a pinned version, with
    /// quantity guidance, technique/flavour/texture impact, dietary and allergen consequences, confidence,
    /// evidence, and what to test before trusting any of it.
    /// </summary>
    /// <remarks>
    /// <strong>Names a recipe and proposes no change to it</strong>, which no earlier task does.
    /// <see cref="RecipeConcepts"/> and <see cref="RecipeFirstDraft"/> propose content with no recipe to
    /// change; <see cref="RecipeRevision"/> proposes changes to one. This reads a recipe to understand what an
    /// ingredient is doing in it, and answers with advice a creator acts on themselves — hence
    /// <see cref="AiOperationScope.Advisory"/>, which permits no change to reach the recipe at all.
    /// </remarks>
    IngredientSubstitution = 5,
}
