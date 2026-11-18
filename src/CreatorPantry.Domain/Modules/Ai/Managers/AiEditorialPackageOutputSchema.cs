namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>Exports <see cref="AiEditorialPackageOutputDocument"/> as the JSON Schema the prompt carries.</summary>
public static class AiEditorialPackageOutputSchema
{
    private static readonly Lazy<string> Exported = new(() =>
        AiOutputSchemaExporter.Export(typeof(AiEditorialPackageOutputDocument)));

    public static string Json => Exported.Value;
}
