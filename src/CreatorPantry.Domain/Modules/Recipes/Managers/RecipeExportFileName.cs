using System.Globalization;
using CreatorPantry.Domain.Managers.Text;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// A file name for an exported recipe that is safe to put in a <c>Content-Disposition</c> header and on any
/// file system, and is the same for the same recipe every time.
/// </summary>
/// <remarks>
/// ASCII only, lower case, <c>a-z0-9</c> and single hyphens, so there is nothing to quote, encode or escape and
/// no way for a title to smuggle in a path separator, a quote, a line break or a dot segment. It carries the
/// version number and nothing that identifies a workspace, a recipe id or any storage location.
/// <para>
/// The folding itself is <see cref="FileNameSlug"/> in the shared kernel, because the brand source library
/// now names downloads the same way. What stays here is what is specific to a recipe export: the
/// <c>-v{n}</c> suffix, the extension, and "recipe" as the word to fall back on.
/// </para>
/// </remarks>
public static class RecipeExportFileName
{
    public const int MaxSlugLength = FileNameSlug.MaxLength;

    /// <param name="title">The recipe's title; any text at all.</param>
    /// <param name="versionNumber">The exported version, appended as <c>-v{n}</c>.</param>
    /// <param name="extension">The extension without its dot, from the caller's own constants.</param>
    public static string For(string? title, int versionNumber, string extension) =>
        $"{Slug(title)}-v{versionNumber.ToString(CultureInfo.InvariantCulture)}.{extension}";

    public static string Slug(string? title) => FileNameSlug.From(title, "recipe", MaxSlugLength);
}
