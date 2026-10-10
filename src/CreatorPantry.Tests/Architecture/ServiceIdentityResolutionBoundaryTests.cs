using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace CreatorPantry.Tests.Architecture;

/// <summary>
/// <c>ResolveForServiceAsync</c> resolves a workspace from nothing but its id and runs as the system identity, so
/// it skips the membership check every other resolver makes. That is only safe while every caller is durable
/// background work whose workspace id came from a committed row this application wrote — never from a route, body,
/// query, header, AI tool argument or job payload a client could shape (tenancy.md, auth.md).
/// </summary>
/// <remarks>
/// <para>
/// A list of permitted callers rather than a rule about types: adding one is a reviewable line here, which is the
/// point. Located from this file's own path so it keeps working when the build output is redirected.
/// </para>
/// <para>
/// Every caller today takes its workspace id from a row committed in the same transaction as the thing it is
/// about — an outbox message beside a recipe version, an extraction operation beside a document version, an
/// embedding operation beside the extraction it names, a generation operation beside the request a creator
/// confirmed. The second is the reason the summary above says "a committed row" rather than "a committed outbox row": the
/// property that matters is that no client chose the value, not which table it was read from. All also deserve
/// the <em>service</em> resolver rather than the operation one, because none may stop working when the member
/// who caused it leaves the workspace.
/// </para>
/// </remarks>
public sealed class ServiceIdentityResolutionBoundaryTests
{
    /// <summary>Files outside Tenancy that may call the service-identity resolver, by path below <c>src/</c>.</summary>
    private static readonly string[] PermittedCallers =
    [
        "CreatorPantry.Domain/Modules/Brand/Managers/BrandSourceEmbeddingWorker.cs",
        "CreatorPantry.Domain/Modules/Brand/Managers/BrandSourceExtractionWorker.cs",
        "CreatorPantry.Domain/Modules/Media/Managers/GeneratedImageWorker.cs",
        "CreatorPantry.Domain/Modules/Media/Managers/MediaRenditionRequestedOutboxHandler.cs",
        "CreatorPantry.Domain/Modules/Media/Managers/StagedImageRetentionWorker.cs",
        "CreatorPantry.Domain/Modules/Recipes/Managers/RecipeVersionChangedOutboxHandler.cs",
    ];

    [Fact]
    public void Only_permitted_background_handlers_resolve_a_workspace_as_the_service_identity()
    {
        var src = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, "..", ".."));

        var callers = new[] { "CreatorPantry.Domain", "CreatorPantry.ApiService", "CreatorPantry.Worker", "CreatorPantry.Gateway" }
            .SelectMany(project => Directory.Exists(Path.Combine(src, project))
                ? Directory.EnumerateFiles(Path.Combine(src, project), "*.cs", SearchOption.AllDirectories)
                : [])
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !file.Contains($"{Path.DirectorySeparatorChar}Tenancy{Path.DirectorySeparatorChar}"))
            .Where(file => Regex.IsMatch(StripComments(File.ReadAllText(file)), @"\.ResolveForServiceAsync\s*\("))
            .Select(file => Path.GetRelativePath(src, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Order()
            .ToList();

        // Non-vacuous: the callers that exist today are found.
        Assert.NotEmpty(callers);
        Assert.Equal(PermittedCallers.Order(), callers);
    }

    private static string StripComments(string source) =>
        Regex.Replace(Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline), @"//[^\n]*", string.Empty);

    private static string SourceFile([CallerFilePath] string path = "") => path;
}
