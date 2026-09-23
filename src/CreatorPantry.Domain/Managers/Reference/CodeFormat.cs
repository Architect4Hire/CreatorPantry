namespace CreatorPantry.Domain.Managers.Reference;

/// <summary>
/// The shape every controlled-vocabulary entry's stable key and display name share, across every reference
/// module.
/// </summary>
/// <remarks>
/// <para>
/// These four values were previously declared four times — <c>CodeFormat.CodePattern</c> with
/// <c>TraitPolicy</c>, <c>VocabularyPolicy</c> and <c>FoodCategoryPolicy</c> each aliasing it. A genuinely
/// cross-cutting constant was parked inside a single module's policy and re-exported three ways, which meant
/// changing it looked like changing units.
/// </para>
/// <para>
/// The lowercase-kebab form is held here rather than by collation, which differs between SQL Server
/// (case-insensitive by default) and the SQLite database the constraint tests run against. Nothing enforces
/// the pattern on write today — the seeder is the only writer — so the seed-set test is the enforcement.
/// </para>
/// </remarks>
public static class CodeFormat
{
    /// <summary>Stable lowercase machine key: permanent, because seed data and stored rows resolve by it.</summary>
    public const int CodeMaxLength = 32;

    /// <summary>Lowercase alphanumeric segments joined by single hyphens; no leading, trailing or doubled hyphen.</summary>
    public const string CodePattern = "^[a-z0-9]+(-[a-z0-9]+)*$";

    public const int DisplayNameMaxLength = 64;

    /// <summary>
    /// Required on the entries whose meaning depends on it — an allergen or a dietary profile says what it
    /// covers, and every trait recorded against it inherits that scope.
    /// </summary>
    public const int DescriptionMaxLength = 512;
}
