namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>Exports <see cref="AiSeoPackageOutputDocument"/> as the JSON Schema the prompt carries.</summary>
public static class AiSeoPackageOutputSchema
{
    private static readonly Lazy<string> Exported = new(() =>
        AiOutputSchemaExporter.Export(typeof(AiSeoPackageOutputDocument)));

    public static string Json => Exported.Value;
}
