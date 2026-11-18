namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The JSON Schema a model is shown for IMG-002, generated from <see cref="AiImagePromptOutputDocument"/>
/// rather than hand-written, so the shape a model is asked for and the shape its answer is judged against
/// cannot drift apart.
/// </summary>
public static class AiImagePromptOutputSchema
{
    private static readonly Lazy<string> Exported = new(() =>
        AiOutputSchemaExporter.Export(typeof(AiImagePromptOutputDocument)));

    /// <summary>The schema, as indented JSON suitable for embedding in a prompt.</summary>
    public static string Json => Exported.Value;
}
