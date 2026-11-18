namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The JSON Schema a model is shown for <see cref="AiTaskType.DishFacetSuggestion"/>, generated from
/// <see cref="AiDishFacetsOutputDocument"/> rather than hand-written, so the shape a model is asked for and
/// the shape its answer is judged against cannot drift apart.
/// </summary>
/// <remarks>
/// The schema states the three facet names and the two confidence bands, because they are enums on the
/// document. It cannot state which <em>codes</em> are allowed — those are catalogue rows, read per request —
/// so the candidate lists travel as a prompt segment and are enforced by
/// <see cref="DishFacetSuggestionAiTaskHandler"/> against the same read. A schema that tried to carry them
/// would be a schema that went stale the first time a cuisine was added.
/// </remarks>
public static class AiDishFacetsOutputSchema
{
    private static readonly Lazy<string> Exported = new(() =>
        AiOutputSchemaExporter.Export(typeof(AiDishFacetsOutputDocument)));

    /// <summary>The schema, as indented JSON suitable for embedding in a prompt.</summary>
    public static string Json => Exported.Value;
}
