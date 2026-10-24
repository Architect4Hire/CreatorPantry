using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CreatorPantry.Domain.Modules.Tenancy.Managers;

namespace CreatorPantry.Tests.Tenancy;

/// <summary>
/// Every literal top-level route in the SPA is an address no workspace may be given.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a real defect class, not a tidiness rule.</strong> The router matches each literal first
/// and only then falls through to <c>:workspaceSlug</c>, so a workspace whose slug equalled one would be
/// permanently unreachable — every link into it would render the public landing page, the sign-in page or the
/// shell's own index instead, with nothing raised anywhere. The workspace would look created and be lost.
/// </para>
/// <para>
/// The route file is read rather than executed, in the manner of <c>WireEnumMirrorTests</c>: the question is
/// which first segments the browser claims, and the answer is in the text. The file is found through
/// <see cref="CallerFilePathAttribute"/> so a redirected build output cannot turn a missing file into a
/// failure that reads like a regression.
/// </para>
/// </remarks>
public sealed class ReservedSlugMirrorTests
{
    private const string RoutesPath = "src/web/src/app/app.routes.ts";

    /// <summary>Addresses the parse has to find, so a regex that matches nothing cannot pass silently.</summary>
    private static readonly string[] KnownRoutes = ["app", "pricing", "sign-in"];

    [Fact]
    public void Every_literal_top_level_route_is_a_reserved_workspace_address()
    {
        var literals = TopLevelRouteLiterals();
        var unreserved = literals.Where(literal => !WorkspacePolicy.IsReservedSlug(literal)).ToArray();

        Assert.All(KnownRoutes, known => Assert.Contains(known, literals));
        Assert.True(
            unreserved.Length == 0,
            $"{RoutesPath} serves {string.Join(", ", unreserved)} itself. Add each to WorkspacePolicy.ReservedSlugs, "
            + "or a workspace given that address becomes unreachable.");
    }

    /// <summary>
    /// A reserved address is only worth reserving if a workspace could otherwise have been given it, so every
    /// one has to be a slug in the first place.
    /// </summary>
    [Fact]
    public void Every_reserved_address_is_a_wellformed_slug()
    {
        Assert.All(
            WorkspacePolicy.ReservedSlugs,
            slug => Assert.Matches(WorkspacePolicy.SlugPattern, slug));
    }

    /// <summary>
    /// The first path segments the SPA serves itself: the <c>path</c> of every route object sitting directly in
    /// a route array rather than inside another route's <c>children</c>, which is what a brace depth of one
    /// distinguishes. The empty path and <c>:workspaceSlug</c> are not addresses anyone could be given.
    /// </summary>
    private static IReadOnlyList<string> TopLevelRouteLiterals()
    {
        var source = WithoutComments(ReadRoutes());
        var start = source.IndexOf("export const routes: Routes = [", StringComparison.Ordinal);

        Assert.True(start >= 0, $"no exported `routes` array in {RoutesPath}");

        var literals = new List<string>();
        var arrayDepth = 0;
        var braceDepth = 0;

        for (var index = start; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '[': arrayDepth++; break;
                case '{': braceDepth++; break;
                case '}': braceDepth--; break;
                case ']':
                    arrayDepth--;
                    if (arrayDepth == 0)
                    {
                        return Finish(literals);
                    }

                    break;
                default:
                    // A route object directly in an array, whether the outer one or the conditional array the
                    // development-only showcase route is spread from.
                    if (braceDepth == 1 && source[index] == 'p' && Match(source, index) is { } path)
                    {
                        literals.Add(path);
                    }

                    break;
            }
        }

        Assert.Fail($"the exported `routes` array in {RoutesPath} is never closed");
        return [];
    }

    private static string? Match(string source, int index)
    {
        var match = Regex.Match(source[index..], @"^path:\s*'([^']*)'");

        return match.Success ? match.Groups[1].Value : null;
    }

    private static IReadOnlyList<string> Finish(List<string> literals) =>
        [.. literals.Where(path => path.Length > 0 && !path.StartsWith(':'))];

    private static string ReadRoutes([CallerFilePath] string callerPath = "") =>
        File.ReadAllText(Path.Combine(RepositoryRoot(callerPath), RoutesPath));

    /// <summary>
    /// Comments go before anything is located, because this file's own comments name routes in prose — and a
    /// brace or bracket inside one would move every depth counted after it.
    /// </summary>
    private static string WithoutComments(string source) =>
        Regex.Replace(source, @"/\*.*?\*/|//[^\n]*", string.Empty, RegexOptions.Singleline);

    /// <summary>The repository root, from this file's compile-time location: <c>src/CreatorPantry.Tests/Tenancy/</c>.</summary>
    private static string RepositoryRoot(string callerPath) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "..", ".."));
}
