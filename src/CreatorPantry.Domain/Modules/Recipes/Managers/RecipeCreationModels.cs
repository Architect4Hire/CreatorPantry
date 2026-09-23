namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// One tag a write applies, in both the forms the vocabulary needs: the creator's own wording to display,
/// and the normalized form that decides whether this is a new tag or an existing one.
/// </summary>
/// <remarks>
/// Business produces both — normalizing is a rule, and rules are its business. The DataLayer only looks up
/// <see cref="NormalizedName"/> and stages a row carrying <see cref="Name"/> when nothing matches.
/// </remarks>
public sealed record RecipeTagName(string Name, string NormalizedName);

/// <summary>
/// What a create produced: the recipe, and the identity of the version that recorded its first state.
/// </summary>
/// <remarks>
/// Identities rather than entities. The caller needs to name what was written — a location header, an audit
/// row, a response — and handing back tracked entities would invite a second layer to read fields that were
/// only ever loaded for the write.
/// </remarks>
public sealed record CreatedRecipe(Guid RecipeId, Guid VersionId, int VersionNumber);

/// <summary>
/// What a caller is told about a recipe that was just created.
/// </summary>
/// <remarks>
/// A ServiceModel, not <see cref="CreatedRecipe"/>: that record is what the DataLayer hands back, and
/// publishing it directly would tie the wire format to a persistence result — anything added there for an
/// internal caller would silently widen the API. This shape is chosen for what a client needs after a
/// create: the id to navigate to, and enough to render the new recipe without an immediate second request.
/// </remarks>
public sealed record CreatedRecipeServiceModel(
    Guid RecipeId,
    string Title,
    RecipeStatus Status,
    Guid VersionId,
    int VersionNumber,
    DateTimeOffset CreatedAt);
