namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>Exports <see cref="AiBrandGuideOutputDocument"/> as the JSON Schema the prompt carries.</summary>
/// <remarks>
/// The document is the schema, so the shape a model is asked for and the shape it is judged against are one
/// definition. Editing the document changes this export, which means bumping the template's
/// <c>outputSchemaVersion</c>.
/// </remarks>
public static class AiBrandGuideOutputSchema
{
    private static readonly Lazy<string> Exported = new(() =>
        AiOutputSchemaExporter.Export(typeof(AiBrandGuideOutputDocument)));

    public static string Json => Exported.Value;
}
