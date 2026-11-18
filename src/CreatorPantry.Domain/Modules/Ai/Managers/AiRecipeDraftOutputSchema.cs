namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The JSON Schema a model is shown for AIREC-002, generated from <see cref="AiRecipeDraftOutputDocument"/> the
/// same way <see cref="AiOutputSchema"/> is generated from the recipe-diff document — exported rather than
/// hand-written, so the shape a model is asked for and the shape its answer is judged against cannot drift
/// apart.
/// </summary>
public static class AiRecipeDraftOutputSchema
{
    private static readonly Lazy<string> Exported = new(() =>
        AiOutputSchemaExporter.Export(typeof(AiRecipeDraftOutputDocument)));

    /// <summary>The schema, as indented JSON suitable for embedding in a prompt.</summary>
    public static string Json => Exported.Value;
}
