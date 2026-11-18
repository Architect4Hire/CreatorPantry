namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>Exports <see cref="AiBrandStyleSamplesOutputDocument"/> as the JSON Schema the prompt carries.</summary>
/// <remarks>
/// One schema for both calls, because both calls are asked for the same three pieces. What differs between them
/// is the material in the prompt, never the shape of the answer — which is what makes the two halves comparable
/// at all.
/// </remarks>
public static class AiBrandStyleSamplesOutputSchema
{
    private static readonly Lazy<string> Exported = new(() =>
        AiOutputSchemaExporter.Export(typeof(AiBrandStyleSamplesOutputDocument)));

    public static string Json => Exported.Value;
}
