using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// A source guard for the one place a recipe gains versions: every path that adds a version after the first
/// must stage the recipe-version-changed event in the same unit (NFR-005).
/// </summary>
/// <remarks>
/// The behavioural tests prove the three paths that exist today. This is what notices a fourth. A version
/// committed without its event would leave accepted derivatives looking current, and nothing would fail until a
/// creator published stale copy — so the rule is enforced where the version is added rather than remembered.
/// Located from this file's own path rather than the build output, so it keeps working when the output
/// directory is redirected.
/// </remarks>
public sealed class RecipeVersionEventStagingTests
{
    [Fact]
    public void Every_version_added_after_the_first_stages_its_event()
    {
        var modules = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(SourceFile())!, "..", "..", "CreatorPantry.Domain", "Modules", "Recipes"));

        Assert.True(Directory.Exists(modules), "cannot find the Recipes module sources");

        var additions = 0;
        var stagings = 0;

        foreach (var file in Directory.EnumerateFiles(modules, "*.cs", SearchOption.AllDirectories))
        {
            var code = StripComments(File.ReadAllText(file));

            additions += Regex.Matches(code, @"\bversions\.Add\(").Count;
            stagings += Regex.Matches(code, @"(?<!void )\bStageVersionChanged\(").Count;
        }

        // Non-vacuous: three writers exist today (create, approval, edit/restore).
        Assert.Equal(3, additions);

        // Create is exempt: nothing can be derived from a recipe that did not exist a moment ago.
        Assert.Equal(additions - 1, stagings);
    }

    private static string StripComments(string source) =>
        Regex.Replace(Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline), @"//[^\n]*", string.Empty);

    private static string SourceFile([CallerFilePath] string path = "") => path;
}
