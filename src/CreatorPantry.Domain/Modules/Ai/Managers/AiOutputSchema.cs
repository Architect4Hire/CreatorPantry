namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The JSON Schema a model is shown, generated from <see cref="AiOutputDocument"/>.
/// </summary>
/// <remarks>
/// <para>
/// Exported rather than hand-written, so the schema the model is asked to satisfy and the schema its answer is
/// held to are the same definition. A hand-maintained schema file beside the type would be two sources of
/// truth with nothing to keep them in step — the drift the prompt templates' body checksum exists to prevent
/// elsewhere.
/// </para>
/// <para>
/// The exporter covers shape: properties, types, required members, and the enum members each field accepts.
/// It does not express the rules stage 5 of the validator enforces, and that asymmetry is deliberate — a
/// model that is told a rule still has to be checked against it.
/// </para>
/// </remarks>
public static class AiOutputSchema
{
    private static readonly Lazy<string> Exported = new(() =>
        AiOutputSchemaExporter.Export(typeof(AiOutputDocument)));

    /// <summary>The schema, as indented JSON suitable for embedding in a prompt.</summary>
    public static string Json => Exported.Value;
}
