using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;

/// <summary>
/// A surface form that resolves to one <see cref="ControlledVocabulary"/> entry — "entrée" for the main
/// course, "aubergine" for eggplant's cuisine-side equivalents, "crock pot" for a slow cooker. Global
/// reference data with no <c>WorkspaceId</c>, like the entry it points at.
/// </summary>
/// <remarks>
/// <para>
/// Aliases exist so imported recipes, and text a creator or a model wrote in its own words, can be
/// <em>recognised</em> as an existing vocabulary entry instead of silently inventing a new one. A match
/// records a reference; it never rewrites what the creator wrote (recipes.md).
/// </para>
/// <para>
/// A base class for the shared rules, not a mapped entity — see <see cref="ControlledVocabulary"/> for why
/// each vocabulary keeps its own table.
/// </para>
/// </remarks>
public abstract class VocabularyAlias
{
    public Guid Id { get; set; }

    /// <summary>
    /// The entry this alias resolves to. Stored in a column named for that vocabulary — <c>CuisineId</c>,
    /// <c>CourseId</c> — so the table reads plainly in the database; the property is named generically
    /// because the rule it carries is the same for all four.
    /// </summary>
    public Guid VocabularyId { get; set; }

    /// <summary>The alias as written, casing and punctuation intact, for display and provenance.</summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>
    /// The lookup key from <see cref="VocabularyPolicy.NormalizeAlias"/>. Unique across the whole
    /// vocabulary, not merely within one entry: an alias matching two cuisines would make a recipe's
    /// cuisine ambiguous, and there is no context at this layer to break the tie, so the database refuses
    /// to store the ambiguity.
    /// </summary>
    /// <remarks>
    /// One collision this cannot catch, exactly as on <see cref="IngredientAlias.NormalizedAlias"/>: an
    /// alias here equal to the normalized <em>display name</em> of another entry in the same vocabulary. No
    /// unique index spans a column that is not stored. Resolution precedence is therefore a documented rule
    /// — see <see cref="VocabularyPolicy"/> — verified by tests rather than by the schema.
    /// </remarks>
    public string NormalizedAlias { get; set; } = string.Empty;
}
