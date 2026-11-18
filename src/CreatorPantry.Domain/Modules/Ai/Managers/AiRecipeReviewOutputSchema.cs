namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The JSON Schema a model is shown for AIREC-006, generated from
/// <see cref="AiRecipeReviewOutputDocument"/> the same way <see cref="AiSubstitutionOutputSchema"/> is
/// generated from AIREC-004's document — exported rather than hand-written, so the shape a model is asked for
/// and the shape its answer is judged against cannot drift apart.
/// </summary>
public static class AiRecipeReviewOutputSchema
{
    private static readonly Lazy<string> Exported = new(() =>
        AiOutputSchemaExporter.Export(typeof(AiRecipeReviewOutputDocument)));

    /// <summary>The schema, as indented JSON suitable for embedding in a prompt.</summary>
    public static string Json => Exported.Value;
}
