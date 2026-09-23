using CreatorPantry.Domain.Managers.Reference;
namespace CreatorPantry.Domain.Modules.Ingredients.Managers;

/// <summary>
/// What kind of authority a <see cref="CreatorPantry.Domain.Modules.Ingredients.Data.Entities.ReferenceSource"/> is. This is the difference between a vetted
/// measurement and a guess, so it is recorded rather than inferred: reference facts must surface their
/// provenance and uncertainty instead of presenting every value as equally settled (recipes.md, ai.md).
/// </summary>
/// <remarks>
/// These numeric values are persisted <em>and</em> named literally by
/// <c>CK_IngredientDensityReferences_AiEstimate_NotApproved</c>. Do not renumber or reorder them; append new
/// kinds at the end and update that constraint in the same migration.
/// </remarks>
public enum ReferenceSourceKind
{
    /// <summary>An official or institutional dataset, e.g. USDA FoodData Central.</summary>
    OfficialDatabase = 0,

    /// <summary>A cookbook, test kitchen, or established publisher.</summary>
    Publisher = 1,

    /// <summary>A manufacturer's published figures for their own product.</summary>
    Manufacturer = 2,

    /// <summary>Measured directly under recorded conditions.</summary>
    LabMeasured = 3,

    /// <summary>Contributed by a person without institutional backing; useful, but not vetted.</summary>
    CommunityContributed = 4,

    /// <summary>
    /// Produced by a model. Always an estimate and never approvable on its own: a value from here cannot be
    /// promoted to <see cref="DensityReviewStatus.Approved"/>, because doing so would let an invented number
    /// reach a creator as a settled fact. Re-sourcing it to a real authority is the way forward, not approval.
    /// </summary>
    AiEstimated = 5,
}
