using System.Reflection;
using System.Text;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// Every evaluation fixture compiled into this assembly as an embedded resource, parsed and validated once.
/// </summary>
/// <remarks>
/// Mirrors <c>EmbeddedPromptTemplateStore</c>: loading happens once, up front, so a malformed fixture is an
/// authoring mistake caught immediately rather than a silently-skipped test case.
/// </remarks>
public sealed class AiEvaluationFixtureStore
{
    private AiEvaluationFixtureStore(IReadOnlyList<AiEvaluationFixture> all) => All = all;

    public IReadOnlyList<AiEvaluationFixture> All { get; }

    /// <exception cref="AiEvaluationException">
    /// A fixture is malformed or mis-declared, or two files claim the same id and version.
    /// </exception>
    public static AiEvaluationFixtureStore Load(Func<string, bool> resourceFilter, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(resourceFilter);
        ArgumentNullException.ThrowIfNull(assemblies);

        var loaded = new List<AiEvaluationFixture>();
        var origins = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var assembly in assemblies)
        {
            foreach (var resourceName in assembly.GetManifestResourceNames())
            {
                if (!resourceName.EndsWith(AiEvaluationFixtureFile.Extension, StringComparison.Ordinal)
                    || !resourceFilter(resourceName))
                {
                    continue;
                }

                var fixture = AiEvaluationFixtureFile.Parse(resourceName, Read(assembly, resourceName));

                if (!origins.TryAdd(fixture.Identity, resourceName))
                {
                    throw new AiEvaluationException(
                        fixture.Identity,
                        $"declared by two resources: '{origins[fixture.Identity]}' and '{resourceName}'. "
                        + "A version is published once; a changed scenario needs a new version.");
                }

                loaded.Add(fixture);
            }
        }

        return new AiEvaluationFixtureStore(loaded);
    }

    private static string Read(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new AiEvaluationException(resourceName, "the embedded resource could not be opened.");

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return reader.ReadToEnd();
    }
}
