using CreatorPantry.Domain.Managers.Reference;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// What the creator says about a source document they are uploading. Carries no workspace, owner, media type,
/// size or storage field: the workspace comes from the route and membership, and everything about the file is
/// established from its bytes.
/// </summary>
public sealed record UploadBrandSourceDocumentViewModel
{
    public string? Title { get; init; }

    public BrandSourceDocumentType? DocumentType { get; init; }

    public BrandSourcePurpose? Purpose { get; init; }

    public string? ChannelKey { get; init; }

    public string? Audience { get; init; }

    public IReadOnlyList<string?>? Tags { get; init; }
}

/// <summary>The file part of an upload, as the controller hands it over.</summary>
/// <param name="Content">Seekable and positioned anywhere: it is read more than once. The caller disposes it.</param>
/// <param name="FileName">The name the client sent. Display only, and cleaned before it is kept.</param>
public sealed record BrandSourceUploadFile(Stream Content, string? FileName);

/// <summary>
/// An upload that has passed inspection and the malware scan and has not been stored. Its identifiers are
/// fixed here, once per request, so a re-run of the storing step names the same object.
/// </summary>
/// <param name="MediaType">Established from the bytes.</param>
public sealed record BrandSourcePreparedUpload(
    Guid DocumentId,
    Guid VersionId,
    string MediaType,
    long SizeBytes,
    string ContentChecksum,
    string OriginalFileName,
    Stream Content);

/// <summary>A brand source document's metadata and its current version. Never an object key or a URL.</summary>
public sealed record BrandSourceDocumentServiceModel(
    Guid Id,
    string Title,
    BrandSourceDocumentType DocumentType,
    BrandSourcePurpose Purpose,
    string? ChannelKey,
    string? Audience,
    IReadOnlyList<string> Tags,
    BrandSourceDocumentStatus Status,
    BrandSourceDocumentVersionServiceModel CurrentVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ConcurrencyToken);

/// <param name="MediaType">Established from the file's bytes, not from what the client declared.</param>
/// <param name="OriginalFileName">The creator's own filename, for display.</param>
public sealed record BrandSourceDocumentVersionServiceModel(
    Guid Id,
    int VersionNumber,
    string MediaType,
    long SizeBytes,
    string OriginalFileName,
    DateTimeOffset CreatedAt);

/// <summary>The name a file arrived with, reduced to something safe to keep and show.</summary>
public static class BrandSourceFileName
{
    /// <summary>
    /// The last path segment with control characters removed, trimmed; null when nothing is left. Never part
    /// of an object key, so this is about what is displayed, not where anything is written.
    /// </summary>
    public static string? Clean(string? fileName)
    {
        if (fileName is null)
        {
            return null;
        }

        var name = fileName[(fileName.LastIndexOfAny(['/', '\\']) + 1)..];
        var cleaned = string.Concat(name.Where(character => !char.IsControl(character))).Trim();

        return cleaned.Length == 0 ? null : cleaned;
    }
}

/// <summary>The shape rules for an upload, as <c>(Field, Message)</c> pairs.</summary>
internal static class BrandSourceInputChecks
{
    public const string FileField = "File";

    public static IEnumerable<(string, string)> Title(string? value)
    {
        var title = BrandProfileInputChecks.Normalize(value);

        if (title is null)
        {
            yield return (nameof(UploadBrandSourceDocumentViewModel.Title), "A source document needs a title.");
        }
        else if (title.Length > BrandPolicy.SourceDocumentTitleMaxLength)
        {
            yield return (nameof(UploadBrandSourceDocumentViewModel.Title),
                $"A title can be at most {BrandPolicy.SourceDocumentTitleMaxLength} characters.");
        }
    }

    public static IEnumerable<(string, string)> Classification(BrandSourceDocumentType? type, BrandSourcePurpose? purpose)
    {
        if (type is not { } documentType || !Enum.IsDefined(documentType))
        {
            yield return (nameof(UploadBrandSourceDocumentViewModel.DocumentType), "Choose what kind of document this is.");
        }

        if (purpose is not { } sourcePurpose || !Enum.IsDefined(sourcePurpose))
        {
            yield return (nameof(UploadBrandSourceDocumentViewModel.Purpose), "Choose what this document is evidence of.");
        }
    }

    /// <summary>Whether the channel exists. Whether it is still offered is Business's to decide.</summary>
    public static IEnumerable<(string, string)> Channel(string? value, IContentChannelCatalog catalog)
    {
        if (BrandProfileInputChecks.Normalize(value) is { } key
            && (key.Length > BrandPolicy.ChannelKeyMaxLength || catalog.Find(key) is null))
        {
            yield return (nameof(UploadBrandSourceDocumentViewModel.ChannelKey), "That is not a channel CreatorPantry knows.");
        }
    }

    public static IEnumerable<(string, string)> Tags(IReadOnlyList<string?>? tags)
    {
        if (tags is null)
        {
            yield break;
        }

        const string field = nameof(UploadBrandSourceDocumentViewModel.Tags);

        if (tags.Count > BrandPolicy.MaxSourceDocumentTags)
        {
            yield return (field, $"A source document can have at most {BrandPolicy.MaxSourceDocumentTags} tags.");
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < tags.Count; index++)
        {
            var name = BrandProfileInputChecks.Normalize(tags[index]);
            var normalized = name is null ? string.Empty : NameNormalization.NormalizeName(name);

            if (name is null || normalized.Length == 0 || name.Length > BrandPolicy.SourceTagNameMaxLength)
            {
                yield return ($"{field}[{index}]", $"A tag needs a name of at most {BrandPolicy.SourceTagNameMaxLength} characters.");
            }
            else if (!seen.Add(normalized))
            {
                yield return ($"{field}[{index}]", "Each tag can be listed once.");
            }
        }
    }

    /// <summary>That there is a file, with bytes and a usable name. What the bytes are is Business's to decide.</summary>
    public static IEnumerable<(string, string)> File(BrandSourceUploadFile? file)
    {
        if (file is null || file.Content.Length == 0)
        {
            yield return (FileField, "Choose a file to upload.");
            yield break;
        }

        var name = BrandSourceFileName.Clean(file.FileName);

        if (name is null)
        {
            yield return (FileField, "The file needs a name.");
        }
        else if (name.Length > BrandPolicy.OriginalFileNameMaxLength)
        {
            yield return (FileField, $"A filename can be at most {BrandPolicy.OriginalFileNameMaxLength} characters.");
        }
    }
}

public sealed class UploadBrandSourceDocumentViewModelValidator : AbstractValidator<UploadBrandSourceDocumentViewModel>
{
    public UploadBrandSourceDocumentViewModelValidator(IContentChannelCatalog channels)
    {
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in new[]
            {
                BrandSourceInputChecks.Title(model.Title),
                BrandSourceInputChecks.Classification(model.DocumentType, model.Purpose),
                BrandSourceInputChecks.Channel(model.ChannelKey, channels),
                BrandProfileInputChecks.Text(nameof(model.Audience), model.Audience, BrandPolicy.SourceDocumentAudienceMaxLength),
                BrandSourceInputChecks.Tags(model.Tags),
            }.SelectMany(failures => failures))
            {
                context.AddFailure(field, message);
            }
        });
    }
}
