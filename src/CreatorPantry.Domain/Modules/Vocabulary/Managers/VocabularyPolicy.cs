using CreatorPantry.Domain.Managers.Reference;
namespace CreatorPantry.Domain.Modules.Vocabulary.Managers;

/// <summary>
/// Limits and lookup rules shared by the controlled vocabularies a recipe is described and filtered by —
/// cuisine, course, cooking technique, and equipment type. Global reference data: no <c>WorkspaceId</c>,
/// never duplicated per workspace (tenancy.md).
/// </summary>
/// <remarks>
/// <para>
/// These vocabularies name shared facts. A creator's own tags and their particular equipment are
/// workspace-owned and belong nowhere near these tables: "my copper jam pan" is not a platform fact, and
/// putting it here would make one creator's naming visible to every other workspace (tenancy.md).
/// </para>
/// <para>
/// Resolution precedence, when free text has to be matched to one of these entries, is: exact
/// <c>Code</c> first, then a <see cref="CreatorPantry.Domain.Modules.Vocabulary.Data.Entities.VocabularyAlias"/>, then a <see cref="VocabularyPolicy.NormalizeAlias"/>
/// comparison against the display name. The last step is deliberately not a stored column — these
/// vocabularies hold tens of rows, not the hundreds of thousands <see cref="CreatorPantry.Domain.Modules.Ingredients.Data.Entities.Ingredient"/> anticipates,
/// so a caller resolves them from an already-cached list rather than paying for an index that would then
/// have to be kept in step with the display name.
/// </para>
/// </remarks>
public static class VocabularyPolicy
{
    /// <summary>Stable lowercase machine key, e.g. <c>tex-mex</c>, <c>main-course</c>, <c>dutch-oven</c>.</summary>

    /// <inheritdoc cref="CodeFormat.CodePattern"/>


    /// <summary>Matches <see cref="CodeFormat.DisplayNameMaxLength"/>: an alias is another way of writing the same name.</summary>
    public const int AliasMaxLength = CodeFormat.DisplayNameMaxLength;

    /// <summary>
    /// The lookup form of an alias or a display name: diacritics folded, lowercased, punctuation reduced to
    /// single spaces. <c>Sauté</c> becomes <c>saute</c>, and <c>Main Course</c> becomes <c>main course</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately the same rule as <see cref="NameNormalization.NormalizeName"/> and deliberately
    /// <em>not</em> <see cref="CreatorPantry.Domain.Modules.Measurement.Managers.MeasurementPolicy.NormalizeAlias"/>. Unit codes are short and closed, so
    /// collapsing their separators is safe; these names are multi-word and open-ended, where
    /// <c>main course</c> collapsing to <c>maincourse</c> would destroy the word boundaries a later
    /// prefix match depends on — exactly the reasoning recorded on the ingredient rule.
    /// </para>
    /// <para>
    /// Delegating rather than copying keeps one implementation of that rule. If the phrase rule ever
    /// changes, these vocabularies should change with it; the stored <c>NormalizedAlias</c> columns would
    /// then need rebuilding, the same way <see cref="CreatorPantry.Domain.Modules.Ingredients.Data.Entities.Ingredient.SearchText"/> would.
    /// </para>
    /// </remarks>
    public static string NormalizeAlias(string value) => NameNormalization.NormalizeName(value);
}
