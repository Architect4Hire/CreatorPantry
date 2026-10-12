using System.Globalization;
using CreatorPantry.Domain.Managers.Text;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>What an accepted long-form draft can be exported as (LF.5.1). Append, never renumber.</summary>
public enum LongFormExportFormat
{
    Markdown = 0,
    Html = 1,
}

/// <summary>One recipe version a revision was written against, as the attribution block names it.</summary>
/// <param name="Title">The recipe's title as that version recorded it.</param>
/// <param name="VersionNumber">That version's own number.</param>
public sealed record LongFormExportRecipe(string Title, int VersionNumber);

/// <summary>Everything <see cref="LongFormExporter"/> may read. The exporter does no I/O.</summary>
/// <param name="Status">Where the draft stands. Anything but accepted, or accepted and since stale, is refused.</param>
/// <param name="RevisionNumber">The accepted revision's number, for the attribution block.</param>
/// <param name="Title">
/// The creator's working title for the piece, or null. An issue's subject line is read from
/// <paramref name="Document"/> and comes first.
/// </param>
/// <param name="Document">The accepted revision's content.</param>
/// <param name="Recipes">
/// The revision's recipe pins, in pin order. A null entry is a pinned version that can no longer be read: it is
/// still named, as unavailable, because the document was still written from it.
/// </param>
public sealed record LongFormExportInput(
    LongFormKind Kind,
    LongFormDraftStatus Status,
    int RevisionNumber,
    string? Title,
    LongFormDocument Document,
    IReadOnlyList<LongFormExportRecipe?> Recipes);

public sealed record LongFormExportIssue(string Code, string Message);

/// <summary>
/// The outcome of an export. <see cref="Content"/> is present only when nothing refused it.
/// </summary>
/// <param name="Title">The title the document carries, as plain text on one line, or null when it has none.</param>
/// <param name="Refusals">Why there is no document. Each code is a <c>ContentErrorCodes</c> value.</param>
/// <param name="Warnings">What a creator should be told beside the document. Never written into it.</param>
public sealed record LongFormExportReport(
    string? Content,
    string? Title,
    IReadOnlyList<LongFormExportIssue> Refusals,
    IReadOnlyList<LongFormExportIssue> Warnings);

/// <summary>Stable codes for a <see cref="LongFormExportReport"/>'s warnings.</summary>
public static class LongFormExportCodes
{
    /// <summary>One or more planned sections have no text and are not in the document.</summary>
    public const string SectionsUnwritten = "content.long_form.export.sections_unwritten";

    /// <summary>A source the accepted revision was pinned to has changed since it was accepted.</summary>
    public const string SourcesMoved = "content.long_form.export.sources_moved";

    /// <summary>A pinned recipe version can no longer be read, so the attribution cannot name it.</summary>
    public const string RecipeUnavailable = "content.long_form.export.recipe_unavailable";
}

/// <summary>One thing a creator should be told about an export.</summary>
public sealed record LongFormExportWarningServiceModel(string Code, string Message);

/// <summary>An accepted long-form draft as one document (LF.5.1). Read, never stored.</summary>
/// <param name="RevisionNumber">The accepted revision the document was made from.</param>
/// <param name="Title">The title the document carries, as plain text on one line, or null when it has none.</param>
/// <param name="Content">The document. The same accepted revision always gives the same text.</param>
/// <param name="FileName">A safe, deterministic ASCII file name for saving it. Never a path.</param>
public sealed record LongFormExportServiceModel(
    Guid DraftId,
    LongFormKind Kind,
    LongFormExportFormat Format,
    int RevisionNumber,
    string? Title,
    string Content,
    string FileName,
    IReadOnlyList<LongFormExportWarningServiceModel> Warnings);

/// <summary>
/// A file name for an exported long-form document (LF.5.2) that is safe in a <c>Content-Disposition</c> header
/// and on any file system, and is the same for the same accepted revision every time.
/// </summary>
/// <remarks>
/// The folding is <see cref="FileNameSlug"/>'s: ASCII <c>a-z0-9</c> and single hyphens, so a title cannot
/// smuggle in a path separator, a quote or a line break. What is added here is what is specific to this
/// export: <c>-r{n}</c> for the revision — a draft has revisions where a recipe has versions — the extension,
/// and the word to fall back on when the piece has no title. It carries no id, workspace or storage location.
/// </remarks>
public static class LongFormExportFileName
{
    public static string For(string? title, LongFormKind kind, int revisionNumber, LongFormExportFormat format) =>
        $"{FileNameSlug.From(title, Fallback(kind))}-r{revisionNumber.ToString(CultureInfo.InvariantCulture)}.{Extension(format)}";

    private static string Fallback(LongFormKind kind) => kind is LongFormKind.NewsletterIssue ? "newsletter" : "article";

    private static string Extension(LongFormExportFormat format) => format is LongFormExportFormat.Html ? "html" : "md";
}
