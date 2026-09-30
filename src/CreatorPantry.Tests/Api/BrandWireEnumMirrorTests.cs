using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Tests.Api;

/// <summary>
/// The brand enums the browser decodes (<c>BrandLinkKind</c>, <c>BrandAssetRole</c>) have TypeScript unions and
/// runtime value lists that must name exactly the members the server sends.
/// </summary>
/// <remarks>
/// The same defect class <see cref="WireEnumMirrorTests"/> guards: the browser decodes an unknown member as
/// <c>null</c>, which fails the whole profile payload, not one field. Kept separate because that test is written
/// around the AI models file's naming scheme; this one reads <c>brand-profile.models.ts</c>.
/// </remarks>
public sealed class BrandWireEnumMirrorTests
{
    private const string ModelsPath = "src/web/src/app/models/brand-profile.models.ts";

    public static TheoryData<string> Enums => ["BrandLinkKind", "BrandAssetRole"];

    private static string[] Members(string typeName) => typeName switch
    {
        "BrandLinkKind" => [.. Enum.GetNames<BrandLinkKind>().Order(StringComparer.Ordinal)],
        "BrandAssetRole" => [.. Enum.GetNames<BrandAssetRole>().Order(StringComparer.Ordinal)],
        _ => throw new ArgumentOutOfRangeException(nameof(typeName)),
    };

    [Theory]
    [MemberData(nameof(Enums))]
    public void The_TypeScript_union_names_every_member(string typeName)
    {
        var body = Between(ReadModels(), $"export type {typeName} =", ";");

        Assert.Equal(Members(typeName), Quoted(body).Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [MemberData(nameof(Enums))]
    public void The_runtime_list_names_every_member(string typeName)
    {
        var opening = typeName == "BrandLinkKind"
            ? "BRAND_LINK_KINDS: readonly BrandLinkKind[] = ["
            : "new Set<BrandAssetRole>([";
        var closing = typeName == "BrandLinkKind" ? "];" : "]);";

        var body = Between(ReadModels(), opening, closing);

        Assert.Equal(Members(typeName), Quoted(body).Order(StringComparer.Ordinal).ToArray());
    }

    private static string Between(string source, string opening, string closing)
    {
        var start = source.IndexOf(opening, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{opening}' not found in {ModelsPath}");

        var from = start + opening.Length;
        var end = source.IndexOf(closing, from, StringComparison.Ordinal);
        Assert.True(end > from, $"'{opening}' is never closed in {ModelsPath}");

        return source[from..end];
    }

    private static IEnumerable<string> Quoted(string body) =>
        Regex.Matches(body, @"'([A-Za-z]+)'").Select(match => match.Groups[1].Value);

    // Comments are stripped first: doc comments name members in prose and can contain semicolons.
    private static string ReadModels([CallerFilePath] string callerPath = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(root, ModelsPath));

        return Regex.Replace(source, @"/\*.*?\*/|//[^\n]*", string.Empty, RegexOptions.Singleline);
    }
}
