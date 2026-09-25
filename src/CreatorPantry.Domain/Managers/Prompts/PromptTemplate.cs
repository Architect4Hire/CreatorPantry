using System.Text;

namespace CreatorPantry.Domain.Managers.Prompts;

/// <summary>A validated prompt template: its declared contract plus the body it renders.</summary>
/// <remarks>
/// Every instance came through <see cref="PromptTemplateFile.Parse"/>, so the invariants that file states are
/// already true here: the body's placeholders and the declared inputs agree exactly, and
/// <see cref="BodyChecksum"/> matches the body.
/// </remarks>
public sealed class PromptTemplate
{
    internal PromptTemplate(
        string id,
        PromptTemplateVersion version,
        string outputSchemaVersion,
        PromptSafetyClass safetyClass,
        IReadOnlyList<PromptTemplateInput> inputs,
        string body,
        string bodyChecksum)
    {
        Id = id;
        Version = version;
        OutputSchemaVersion = outputSchemaVersion;
        SafetyClass = safetyClass;
        Inputs = inputs;
        Body = body;
        BodyChecksum = bodyChecksum;
    }

    /// <summary>Stable identity, lowercase dot-separated, e.g. <c>recipe.concepts</c>.</summary>
    public string Id { get; }

    public PromptTemplateVersion Version { get; }

    /// <summary>
    /// The structured-output schema this template's body promises to produce. Declared here so the validator
    /// that eventually reads model output learns which schema to hold it to from the template, not from the
    /// call site.
    /// </summary>
    public string OutputSchemaVersion { get; }

    public PromptSafetyClass SafetyClass { get; }

    public IReadOnlyList<PromptTemplateInput> Inputs { get; }

    /// <summary>The task instructions, LF-normalized with trailing whitespace removed.</summary>
    /// <remarks>
    /// Task instructions only. System policy, retrieved references, and untrusted creator or imported text are
    /// assembled around this by the context envelope, which owns the delimiting — a template body never
    /// carries those segments itself.
    /// </remarks>
    public string Body { get; }

    /// <summary><c>sha256:</c> and lowercase hex over <see cref="Body"/>, as the manifest declared it.</summary>
    /// <remarks>
    /// Provenance, once verified: a stored generation can record which exact body produced it, and a body that
    /// changed without its version changing is visible rather than inferred.
    /// </remarks>
    public string BodyChecksum { get; }

    /// <summary>Substitutes the supplied values into the body.</summary>
    /// <exception cref="PromptTemplateException">
    /// A required input was not supplied, or a value was supplied that this template does not declare — the
    /// latter because it almost always means a renamed input and a caller that was not updated.
    /// </exception>
    /// <remarks>
    /// <para>
    /// A single pass over the body, not successive <see cref="string.Replace(string,string)"/> calls. Replacing
    /// in sequence would re-scan text that substitution had just inserted, so a value containing
    /// <c>{{other}}</c> could have that placeholder filled in too — a small injection vector that costs
    /// nothing to close. Here a value is copied out verbatim and never re-examined.
    /// </para>
    /// <para>
    /// Verbatim is the whole contract: this does not escape, delimit, or otherwise mark a value as untrusted.
    /// That belongs to the context envelope, which knows which segments are creator-controlled.
    /// </para>
    /// </remarks>
    public string Render(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var missing = Inputs
            .Where(input => input.Required && !values.ContainsKey(input.Name))
            .Select(input => input.Name)
            .ToList();

        if (missing.Count > 0)
        {
            throw new PromptTemplateException(
                Identity, $"required input(s) not supplied: {string.Join(", ", missing)}.");
        }

        var declared = Inputs.Select(input => input.Name).ToHashSet(StringComparer.Ordinal);
        var undeclared = values.Keys.Where(name => !declared.Contains(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();

        if (undeclared.Count > 0)
        {
            throw new PromptTemplateException(
                Identity, $"value(s) supplied that this template does not declare: {string.Join(", ", undeclared)}.");
        }

        var rendered = new StringBuilder(Body.Length);
        var position = 0;

        while (true)
        {
            var open = Body.IndexOf(PromptTemplateFile.PlaceholderOpen, position, StringComparison.Ordinal);

            if (open < 0)
            {
                rendered.Append(Body, position, Body.Length - position);
                return rendered.ToString();
            }

            var nameStart = open + PromptTemplateFile.PlaceholderOpen.Length;
            var close = Body.IndexOf(PromptTemplateFile.PlaceholderClose, nameStart, StringComparison.Ordinal);
            var name = Body[nameStart..close];

            rendered.Append(Body, position, open - position);

            // An optional input with no value supplied renders as nothing. The body keeps whatever wording
            // surrounds the placeholder, which is why an optional value belongs on its own line or clause.
            if (values.TryGetValue(name, out var value))
            {
                rendered.Append(value);
            }

            position = close + PromptTemplateFile.PlaceholderClose.Length;
        }
    }

    /// <summary>How this template names itself in a failure message.</summary>
    internal string Identity => $"{Id}-{Version}";
}
