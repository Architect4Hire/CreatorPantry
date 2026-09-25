using System.Reflection;
using System.Text;

namespace CreatorPantry.Domain.Managers.Prompts;

/// <summary>
/// The templates compiled into one or more assemblies as embedded resources, parsed and validated once.
/// </summary>
/// <remarks>
/// <para>
/// Embedded rather than read from a directory, so "templates cannot access secrets or arbitrary files" is a
/// property of the design rather than a rule to enforce: there is no path to traverse and no ambient read. It
/// also means a prompt cannot drift from the code whose output schema it promises, because the two ship in the
/// same assembly.
/// </para>
/// <para>
/// Loading happens during service registration, not on first use. A malformed template is then a startup
/// failure — the host refuses to come up — rather than an exception a creator discovers mid-generation, which
/// is what "loading a missing or invalid template fails before provider invocation" has to mean in practice.
/// </para>
/// <para>
/// A template's folder is free: it is found by the <see cref="PromptTemplateFile.Extension"/> suffix, so
/// prompts live beside the module that owns them and this loader never learns about modules.
/// </para>
/// </remarks>
public sealed class EmbeddedPromptTemplateStore : IPromptTemplateStore
{
    /// <summary>Versions of one id, ordered highest first.</summary>
    private readonly Dictionary<string, PromptTemplate[]> _byId;

    private EmbeddedPromptTemplateStore(Dictionary<string, PromptTemplate[]> byId)
    {
        _byId = byId;
        All = byId.Values.SelectMany(versions => versions).ToArray();
    }

    public IReadOnlyCollection<PromptTemplate> All { get; }

    /// <summary>Parses and validates every prompt resource in <paramref name="assemblies"/>.</summary>
    /// <exception cref="PromptTemplateException">
    /// A template is malformed or mis-declared, or two files claim the same id and version.
    /// </exception>
    public static EmbeddedPromptTemplateStore Load(params Assembly[] assemblies) =>
        Load(static _ => true, assemblies);

    /// <summary>Parses and validates the prompt resources <paramref name="resourceFilter"/> accepts.</summary>
    /// <param name="resourceFilter">
    /// Which resource names to consider, on top of the <see cref="PromptTemplateFile.Extension"/> suffix. An
    /// assembly that holds several independent sets of templates — a test assembly holding deliberately broken
    /// ones beside sound ones, most obviously — needs to load one set without the others.
    /// </param>
    /// <exception cref="PromptTemplateException">
    /// A template is malformed or mis-declared, or two files claim the same id and version.
    /// </exception>
    public static EmbeddedPromptTemplateStore Load(
        Func<string, bool> resourceFilter,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(resourceFilter);
        ArgumentNullException.ThrowIfNull(assemblies);

        var loaded = new List<PromptTemplate>();
        var origins = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var assembly in assemblies)
        {
            foreach (var resourceName in assembly.GetManifestResourceNames())
            {
                if (!resourceName.EndsWith(PromptTemplateFile.Extension, StringComparison.Ordinal)
                    || !resourceFilter(resourceName))
                {
                    continue;
                }

                var template = PromptTemplateFile.Parse(resourceName, Read(assembly, resourceName));

                if (!origins.TryAdd(template.Identity, resourceName))
                {
                    throw new PromptTemplateException(
                        template.Identity,
                        $"declared by two resources: '{origins[template.Identity]}' and '{resourceName}'. "
                        + "A version is published once; a changed body needs a new version.");
                }

                loaded.Add(template);
            }
        }

        var byId = loaded
            .GroupBy(template => template.Id, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(template => template.Version).ToArray(),
                StringComparer.Ordinal);

        return new EmbeddedPromptTemplateStore(byId);
    }

    public PromptTemplate Get(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return _byId.TryGetValue(id, out var versions)
            ? versions[0]
            : throw new PromptTemplateException(id, "no template with that id is loaded.");
    }

    public PromptTemplate Get(string id, PromptTemplateVersion version)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        if (!_byId.TryGetValue(id, out var versions))
        {
            throw new PromptTemplateException(id, "no template with that id is loaded.");
        }

        return Array.Find(versions, template => template.Version == version)
            ?? throw new PromptTemplateException(
                $"{id}-{version}",
                "that version is not loaded. Available: "
                + string.Join(", ", versions.Select(template => template.Version)) + ".");
    }

    private static string Read(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new PromptTemplateException(resourceName, "the embedded resource could not be opened.");

        // detectEncodingFromByteOrderMarks strips a BOM, which would otherwise become the body's first
        // character and change its checksum depending on which editor last saved the file.
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return reader.ReadToEnd();
    }
}
