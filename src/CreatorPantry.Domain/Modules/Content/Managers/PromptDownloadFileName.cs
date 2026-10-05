using System.Globalization;
using CreatorPantry.Domain.Managers.Text;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// A file name for a downloaded prompt that is safe to put in a <c>Content-Disposition</c> header and on any
/// file system, and is the same for the same prompt every time.
/// </summary>
/// <remarks>
/// <para>
/// ASCII only, lower case, <c>a-z0-9</c> and single hyphens plus one dot, so there is nothing to quote, encode
/// or escape and no way for a label to smuggle in a path separator, a quote, a line break or a dot segment.
/// The folding is <see cref="FileNameSlug"/> in the shared kernel, which the recipe exports and the brand
/// source library already name downloads with; what is specific to a prompt is here.
/// </para>
/// <para>
/// <strong>It carries the moment the prompt was saved, and that needed deciding.</strong> A recipe export
/// disambiguates with <c>-v{n}</c> and a brand document with its version number; an immutable prompt has no
/// version, and <see cref="Data.Entities.PromptRecord.Label"/> is optional — so every unlabelled prompt in a
/// library would otherwise download as <c>prompt.txt</c> and overwrite the last one in the creator's downloads
/// folder. The timestamp is still deterministic, because the row it comes from can never change.
/// </para>
/// <para>
/// <strong>No id, no workspace, no storage location</strong>, the same bar <c>RecipeExportFileName</c> holds
/// to. A prompt's id is in the URL the client asked with, so putting it in the name would disclose nothing —
/// but a file name is something a creator shares, forwards and keeps, and none of those should carry an
/// internal identifier.
/// </para>
/// </remarks>
public static class PromptDownloadFileName
{
    public const int MaxSlugLength = FileNameSlug.MaxLength;

    /// <summary>What an unlabelled prompt is called, and the prefix for a reserved device name.</summary>
    public const string Fallback = "prompt";

    /// <summary>The extension for PRM-004's plain-text download, without its dot.</summary>
    public const string TextExtension = "txt";

    /// <summary>The extension for PRM-005's JSON record download, without its dot.</summary>
    public const string JsonExtension = "json";

    /// <param name="label">The creator's own label for the prompt; any text at all, including null.</param>
    /// <param name="savedAt">When the prompt was saved. Appended as <c>-{yyyyMMdd-HHmmss}</c> in UTC.</param>
    /// <param name="extension">The extension without its dot, from the caller's own constants.</param>
    public static string For(string? label, DateTimeOffset savedAt, string extension) =>
        $"{Slug(label)}-{Stamp(savedAt)}.{extension}";

    public static string Slug(string? label) => FileNameSlug.From(label, Fallback, MaxSlugLength);

    /// <summary>
    /// The saved moment as sortable digits, in UTC so the name does not depend on the offset the row happened
    /// to be read with, and invariant so it does not depend on the server's culture.
    /// </summary>
    private static string Stamp(DateTimeOffset savedAt) =>
        savedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
}
