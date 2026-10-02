namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The "Create my voice" wizard's step slugs, in order, and the ordering rules a saved session must obey.
/// </summary>
/// <remarks>
/// A domain constant rather than client data: the server is the authority on which slugs exist, so a draft
/// can never claim to be on a step the wizard does not have.
/// </remarks>
public static class BrandSetupSteps
{
    public const string Goals = "goals";

    public const string Style = "style";

    public const string Examples = "examples";

    public const string ReviewText = "review-text";

    public const string Create = "create";

    public const string Edit = "edit";

    public const string Finish = "finish";

    /// <summary>The slugs in wizard order.</summary>
    public static IReadOnlyList<string> All { get; } = [Goals, Style, Examples, ReviewText, Create, Edit, Finish];

    /// <summary>Zero-based position of a slug, or -1 when it is not a step.</summary>
    public static int IndexOf(string? slug)
    {
        for (var index = 0; index < All.Count; index++)
        {
            if (string.Equals(All[index], slug, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// The ordering rules, as field/message pairs: known slugs; furthest at or beyond current; completed and
    /// skipped sets of known, distinct, disjoint steps at or before furthest.
    /// </summary>
    public static IEnumerable<(string Field, string Message)> Check(
        string? currentStep,
        string? furthestStep,
        IReadOnlyList<string?>? completedSteps,
        IReadOnlyList<string?>? skippedSteps)
    {
        var current = IndexOf(currentStep);
        var furthest = IndexOf(furthestStep);

        if (current < 0)
        {
            yield return ("CurrentStep", "currentStep must be one of the wizard's step slugs.");
        }

        if (furthest < 0)
        {
            yield return ("FurthestStep", "furthestStep must be one of the wizard's step slugs.");
        }

        if (current >= 0 && furthest >= 0 && furthest < current)
        {
            yield return ("FurthestStep", "furthestStep cannot come before currentStep.");
        }

        foreach (var failure in CheckSet("CompletedSteps", completedSteps, furthest))
        {
            yield return failure;
        }

        foreach (var failure in CheckSet("SkippedSteps", skippedSteps, furthest))
        {
            yield return failure;
        }

        if (completedSteps is not null && skippedSteps is not null
            && completedSteps.Intersect(skippedSteps, StringComparer.Ordinal).Any())
        {
            yield return ("SkippedSteps", "A step cannot be both completed and skipped.");
        }
    }

    private static IEnumerable<(string, string)> CheckSet(string field, IReadOnlyList<string?>? steps, int furthest)
    {
        if (steps is null)
        {
            yield return (field, $"{ToCamel(field)} is required; send an empty list for none.");
            yield break;
        }

        if (steps.Any(step => IndexOf(step) < 0))
        {
            yield return (field, $"{ToCamel(field)} may contain only the wizard's step slugs.");
            yield break;
        }

        if (steps.Distinct(StringComparer.Ordinal).Count() != steps.Count)
        {
            yield return (field, $"{ToCamel(field)} cannot repeat a step.");
        }

        if (furthest >= 0 && steps.Any(step => IndexOf(step) > furthest))
        {
            yield return (field, $"{ToCamel(field)} cannot include a step beyond furthestStep.");
        }
    }

    private static string ToCamel(string field) => char.ToLowerInvariant(field[0]) + field[1..];
}
