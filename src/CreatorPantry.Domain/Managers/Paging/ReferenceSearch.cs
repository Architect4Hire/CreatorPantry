namespace CreatorPantry.Domain.Managers.Paging;

/// <summary>
/// A search term in the two forms the reference tables are searchable by.
/// </summary>
/// <remarks>
/// <para>
/// Both are needed because the catalogue stores names and aliases differently on purpose. An alias is stored
/// only in its normalized form, where <c>Tex-Mex</c> is <c>tex mex</c> and <c>Jalapeño</c> is <c>jalapeno</c>;
/// a display name is stored exactly as written. Searching one form against the other quietly fails to match —
/// <c>tex mex</c> is not a substring of <c>Tex-Mex</c> — so each surface is searched in its own form.
/// </para>
/// <para>
/// <see cref="Raw"/> is lowercased here rather than left to the database, because the two databases disagree:
/// SQL Server's default collation is case-insensitive and SQLite's is not. Comparing lowercase to lowercase
/// makes a search mean the same thing in a test as it does in production.
/// </para>
/// </remarks>
/// <param name="Raw">Trimmed and lowercased, for matching display names, plural forms, and codes.</param>
/// <param name="Normalized">
/// From <see cref="IngredientPolicy.NormalizeName"/>, for matching the stored alias keys and
/// <see cref="Data.Ingredient.SearchText"/>.
/// </param>
public sealed record ReferenceSearch(string Raw, string Normalized);
