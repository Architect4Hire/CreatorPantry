namespace CreatorPantry.Domain.Managers.Paging;

/// <summary>
/// A row that can be the last one on a page, and therefore the position a cursor resumes from.
/// </summary>
/// <remarks>
/// Implemented by every repository record the paging helpers handle. It lives in the shared kernel rather
/// than in a module because paging is not any module's domain — Measurement, Vocabulary and Ingredients all
/// page the same way, and the recipe modules will too.
/// </remarks>
public interface IReferenceRow
{
    /// <summary>The value this row was ordered by.</summary>
    string SortValue { get; }

    /// <summary>This row's unique natural key, which breaks ties on <see cref="SortValue"/>.</summary>
    string TieBreaker { get; }
}
