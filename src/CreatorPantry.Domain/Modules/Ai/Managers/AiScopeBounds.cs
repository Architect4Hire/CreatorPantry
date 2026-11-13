namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The bound a recipe-diff task works inside, rendered for the model from the same tables
/// <see cref="AiOutputValidator"/> enforces it with.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Shared by every recipe-diff handler</strong> (AIREC-003 revision, AIREC-005 adaptation), so the
/// instructions cannot describe a bound the validator does not apply, and two handlers cannot describe the same
/// bound two ways.
/// </para>
/// <para>
/// <strong>Field names are rendered exactly as <c>fieldName</c> must carry them.</strong> They were once listed as
/// <c>Recipe.title</c>, <c>Ingredient.displayText</c> — and a model told those were "the only fields you may set"
/// reasonably answered with <c>"fieldName": "Recipe.title"</c>, which the validator refuses as outside the
/// scope, non-correctably. Live runs failed that way on every revision observed. The target is the
/// <c>targetKind</c> and the name is the <c>fieldName</c>, so each is now shown where it goes and the rendering
/// says so in as many words.
/// </para>
/// </remarks>
internal static class AiScopeBounds
{
    /// <summary>
    /// The settable fields, grouped by target, with the bare field name the answer must use.
    /// </summary>
    /// <returns>"none" for an empty allow-list: a blank reads as absent, the opposite of what it means.</returns>
    public static string AllowedFields(AiOperationScope scope)
    {
        var groups = Targets(scope)
            .Select(target => (
                Target: target,
                Fields: AiPolicy.AllowedFields(scope, target).OrderBy(field => field, StringComparer.Ordinal).ToList()))
            .Where(group => group.Fields.Count > 0)
            .Select(group => $"targetKind {group.Target} with fieldName {string.Join(" or ", group.Fields.Select(Quote))}")
            .ToList();

        return groups.Count == 0
            ? "none"
            : string.Join("; ", groups)
                + ". Write fieldName as the bare name shown, such as \"title\" — never prefixed with its target, "
                + "as in \"Recipe.title\"";
    }

    /// <summary>The kinds of change the server can apply to each target the scope permits.</summary>
    public static string AllowedChanges(AiOperationScope scope)
    {
        var kinds = Targets(scope)
            .SelectMany(target => AiChangeApplicability.For(target).Select(kind => $"{kind} on {target}"))
            .OrderBy(kind => kind, StringComparer.Ordinal)
            .ToList();

        return kinds.Count == 0 ? "none" : string.Join(", ", kinds);
    }

    /// <summary>Every target the scope permits that some change can actually be applied to.</summary>
    private static List<AiChangeTargetKind> Targets(AiOperationScope scope) =>
        AiPolicy.AllowedTargets(scope)
            .Where(target => AiChangeApplicability.For(target).Count > 0)
            .OrderBy(target => target.ToString(), StringComparer.Ordinal)
            .ToList();

    private static string Quote(string field) => $"\"{field}\"";
}
