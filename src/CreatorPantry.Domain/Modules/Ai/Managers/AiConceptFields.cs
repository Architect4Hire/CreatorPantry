namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The field names AIREC-001's concepts are stored under, as <c>AiStructuredChange.FieldName</c>.
/// </summary>
/// <remarks>
/// A concept is not stored as a concept. <see cref="RecipeConceptsAiTaskHandler"/> flattens each one into an
/// <see cref="AiChangeKind.Add"/> row carrying the title plus an <see cref="AiChangeKind.Set"/> row per other
/// field, so these strings are the only thing tying a written field to a read one. Anything that reads a
/// concept back — <see cref="AiConceptReader"/> on the server, the concept studio in the browser — is doing
/// that flattening in reverse and has to agree on the names.
/// </remarks>
public static class AiConceptFields
{
    public const string Summary = "summary";

    public const string DistinctnessRationale = "distinctnessRationale";

    public const string Assumptions = "assumptions";

    public const string SuggestedIngredients = "suggestedIngredients";

    public const string DietaryNotes = "dietaryNotes";

    public const string TimeBudgetNote = "timeBudgetNote";

    public const string SkillLevelFit = "skillLevelFit";
}

/// <summary>One stored change row of a concept, as read back for projection.</summary>
/// <remarks>
/// Deliberately not <c>AiStructuredChange</c>. This is the three columns reading a concept needs, which keeps
/// the entity — and its disposition, its ids, its ordering — out of a projection that has no business with
/// any of it.
/// </remarks>
public sealed record AiConceptChangeRow(AiChangeKind ChangeKind, string? FieldName, string? AfterValue);

/// <summary>
/// One proposed recipe concept, read back out of the change rows it was translated into.
/// </summary>
/// <param name="Title">The concept's <see cref="AiChangeKind.Add"/> row value.</param>
/// <param name="Summary">Its <c>summary</c> row, or null when the model supplied none.</param>
/// <remarks>
/// Title and summary only, which is what <see cref="AiPolicy.SelectedConceptMaxLength"/> is sized for. The
/// other stored fields are on the concept for a creator to read; see that constant's remarks for why they are
/// not carried into a draft request.
/// </remarks>
public sealed record AiConceptReference(string Title, string? Summary);

/// <summary>
/// The inverse of <see cref="RecipeConceptsAiTaskHandler"/>'s flattening: stored change rows back into the
/// concept they describe.
/// </summary>
/// <remarks>
/// A projection rather than a repository concern. Deciding that the <see cref="AiChangeKind.Add"/> row's
/// value <em>is</em> the title, and that a concept without one is not a concept, is a translation rule about
/// how this module writes concepts — backend.md scopes a repository to queries and persistence, and a rule
/// that lives next to the query it happens to be used by is a rule nothing else can find.
/// </remarks>
public static class AiConceptReader
{
    /// <summary>The concept those rows describe, or null when they do not describe one.</summary>
    /// <remarks>
    /// Null covers an empty list and a list with no <see cref="AiChangeKind.Add"/> row alike. Callers turn
    /// both into the same absence they turn a missing request into, so none of them is distinguishable.
    /// </remarks>
    public static AiConceptReference? Read(IReadOnlyList<AiConceptChangeRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var title = rows.FirstOrDefault(row => row.ChangeKind is AiChangeKind.Add)?.AfterValue;

        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var summary = rows
            .FirstOrDefault(row =>
                row.ChangeKind is AiChangeKind.Set
                && string.Equals(row.FieldName, AiConceptFields.Summary, StringComparison.Ordinal))
            ?.AfterValue;

        return new AiConceptReference(title, summary);
    }
}
