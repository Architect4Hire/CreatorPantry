using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Business;

public interface IRecipeExportBusiness
{
    /// <summary>
    /// Finds the version to export and decides whether it may be: only an approved recipe, or a version
    /// marked ready, is exported.
    /// </summary>
    Task<OperationResult<RecipeExportSource>> GetJsonLdSourceAsync(
        Guid recipeId, int? versionNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Generates the document from a source this class already cleared, plus the names and accepted SEO
    /// revision the facade resolved. Deterministic, and it writes nothing.
    /// </summary>
    /// <param name="imageUrl">
    /// An absolute URL the media seam has authorized for the hero image, or <c>null</c>. Nothing supplies one
    /// until media is built, so an export is reported incomplete rather than given an invented or derived URL.
    /// </param>
    OperationResult<RecipeJsonLdExportServiceModel> BuildJsonLd(
        RecipeExportSource source,
        string? cuisineName,
        string? courseName,
        AcceptedSeoServiceModel? seo,
        string? imageUrl);

    /// <summary>
    /// Finds the version to export as Markdown and decides whether it may be, by the same rule as
    /// <see cref="GetJsonLdSourceAsync"/> but refusing with this export's own code.
    /// </summary>
    Task<OperationResult<RecipeExportSource>> GetMarkdownSourceAsync(
        Guid recipeId, int? versionNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Renders the Markdown from a source this class already cleared, plus the unit lookups and accepted
    /// editorial revision the facade resolved. Deterministic, and it writes nothing.
    /// </summary>
    OperationResult<RecipeMarkdownExportServiceModel> BuildMarkdown(
        RecipeExportSource source,
        RecipeExportTemplate template,
        RecipeUnitPresentation units,
        IReadOnlyDictionary<Guid, MeasurementUnitServiceModel> unitsById,
        IReadOnlyDictionary<MeasurementDimension, MeasurementUnitServiceModel> targetUnits,
        AcceptedEditorialServiceModel? editorial);

    /// <summary>
    /// Finds the version to export as a PDF and decides whether it may be, by the same rule as
    /// <see cref="GetJsonLdSourceAsync"/> but refusing with this export's own code.
    /// </summary>
    Task<OperationResult<RecipeExportSource>> GetPdfSourceAsync(
        Guid recipeId, int? versionNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Renders the PDF from a source this class already cleared. Writes nothing and fetches nothing: the image
    /// is whatever the caller has already authorized and read, or <c>null</c>.
    /// </summary>
    OperationResult<RecipePdfExportServiceModel> BuildPdf(
        RecipeExportSource source,
        RecipeExportTemplate template,
        RecipeUnitPresentation units,
        RecipePdfPageSize pageSize,
        IReadOnlyDictionary<Guid, MeasurementUnitServiceModel> unitsById,
        IReadOnlyDictionary<MeasurementDimension, MeasurementUnitServiceModel> targetUnits,
        AcceptedEditorialServiceModel? editorial,
        RecipePdfImage? image,
        DateTimeOffset createdAt);

    /// <summary>
    /// Finds the current version and says whether it may be exported, without refusing when it may not:
    /// the summary exists to say why.
    /// </summary>
    Task<OperationResult<RecipeExportSummarySource>> GetSummarySourceAsync(
        Guid recipeId, CancellationToken cancellationToken);

    /// <summary>Combines the version with the accepted copy the facade looked up. Reads and writes nothing.</summary>
    RecipeExportSummaryServiceModel BuildSummary(
        RecipeExportSummarySource source, AcceptedEditorialServiceModel? editorial, AcceptedSeoServiceModel? seo);
}

internal sealed class RecipeExportBusiness(IRecipeExportDataLayer dataLayer) : IRecipeExportBusiness
{
    public Task<OperationResult<RecipeExportSource>> GetJsonLdSourceAsync(
        Guid recipeId, int? versionNumber, CancellationToken cancellationToken) =>
        GetSourceAsync(recipeId, versionNumber, RecipeErrorCodes.JsonLdExportNotApprovedConflict, cancellationToken);

    public Task<OperationResult<RecipeExportSource>> GetMarkdownSourceAsync(
        Guid recipeId, int? versionNumber, CancellationToken cancellationToken) =>
        GetSourceAsync(recipeId, versionNumber, RecipeErrorCodes.MarkdownExportNotApprovedConflict, cancellationToken);

    public Task<OperationResult<RecipeExportSource>> GetPdfSourceAsync(
        Guid recipeId, int? versionNumber, CancellationToken cancellationToken) =>
        GetSourceAsync(recipeId, versionNumber, RecipeErrorCodes.PdfExportNotApprovedConflict, cancellationToken);

    public async Task<OperationResult<RecipeExportSummarySource>> GetSummarySourceAsync(
        Guid recipeId, CancellationToken cancellationToken)
    {
        var (visible, row) = await dataLayer.FindSourceAsync(recipeId, null, cancellationToken);

        // The same answer for an unknown recipe and another workspace's, so existence is not disclosed.
        if (!visible)
        {
            return OperationResult<RecipeExportSummarySource>.Failure(new OperationError(
                RecipeErrorCodes.RecipeNotFound,
                "That recipe could not be found.",
                new Dictionary<string, string[]>()));
        }

        // A recipe that exists but has no readable version has nothing to export and nothing to summarise.
        if (row is null)
        {
            return OperationResult<RecipeExportSummarySource>.Failure(OperationError.Validation(
                RecipeErrorCodes.VersionNotFound,
                "That version could not be read.",
                [("versionNumber", "This recipe has no such version.")]));
        }

        var document = RecipeSnapshotSerializer.Deserialize(row.Document);
        var exportable = document.Recipe.Status == RecipeStatus.Approved || row.Readiness == RecipeVersionReadiness.Ready;

        return OperationResult<RecipeExportSummarySource>.Success(
            new RecipeExportSummarySource(row.Id, row.VersionNumber, exportable));
    }

    public RecipeExportSummaryServiceModel BuildSummary(
        RecipeExportSummarySource source, AcceptedEditorialServiceModel? editorial, AcceptedSeoServiceModel? seo) =>
        new(
            source.VersionNumber,
            source.Exportable,
            source.Exportable ? null : RecipeExportSummaryReasons.NotApproved,
            editorial is null ? null : new RecipeExportAcceptedCopyServiceModel(editorial.RevisionNumber, editorial.IsCurrent),
            seo is null ? null : new RecipeExportAcceptedCopyServiceModel(seo.RevisionNumber, seo.IsCurrent));

    private async Task<OperationResult<RecipeExportSource>> GetSourceAsync(
        Guid recipeId, int? versionNumber, string notApprovedCode, CancellationToken cancellationToken)
    {
        var (visible, row) = await dataLayer.FindSourceAsync(recipeId, versionNumber, cancellationToken);

        // The same answer for an unknown recipe and another workspace's, so existence is not disclosed.
        if (!visible)
        {
            return OperationResult<RecipeExportSource>.Failure(new OperationError(
                RecipeErrorCodes.RecipeNotFound,
                "That recipe could not be found.",
                new Dictionary<string, string[]>()));
        }

        if (row is null)
        {
            return OperationResult<RecipeExportSource>.Failure(OperationError.Validation(
                RecipeErrorCodes.VersionNotFound,
                "That version could not be read.",
                [("versionNumber", "This recipe has no such version.")]));
        }

        var document = RecipeSnapshotSerializer.Deserialize(row.Document);

        // No draft override: no approved-automation policy exists, and falling back to an older approved
        // version would publish content the creator did not ask about.
        if (document.Recipe.Status != RecipeStatus.Approved && row.Readiness != RecipeVersionReadiness.Ready)
        {
            return OperationResult<RecipeExportSource>.Failure(new OperationError(
                notApprovedCode,
                "Only an approved recipe version, or one marked ready, can be exported.",
                new Dictionary<string, string[]>()));
        }

        return OperationResult<RecipeExportSource>.Success(
            new RecipeExportSource(row.Id, row.VersionNumber, row.Readiness, document));
    }

    public OperationResult<RecipeJsonLdExportServiceModel> BuildJsonLd(
        RecipeExportSource source,
        string? cuisineName,
        string? courseName,
        AcceptedSeoServiceModel? seo,
        string? imageUrl)
    {
        var report = RecipeJsonLdGenerator.Generate(new RecipeJsonLdInput
        {
            Snapshot = source.Document,
            Readiness = source.Readiness,
            CuisineName = cuisineName,
            CourseName = courseName,
            ImageUrl = imageUrl,
            Editorial = seo is null ? null : new RecipeJsonLdEditorial(seo.MetaDescription, seo.KeyPhrases, seo.IsCurrent),
        });

        if (report.Json is null)
        {
            return OperationResult<RecipeJsonLdExportServiceModel>.Failure(new OperationError(
                RecipeErrorCodes.JsonLdExportIncompleteUnprocessable,
                "This recipe is missing facts its structured data cannot be published without.",
                new Dictionary<string, string[]>(),
                new Dictionary<string, object?> { ["missingRequired"] = report.MissingRequired }));
        }

        return OperationResult<RecipeJsonLdExportServiceModel>.Success(
            new RecipeJsonLdExportServiceModel(report.Json, source.VersionNumber, report.Warnings));
    }

    public OperationResult<RecipeMarkdownExportServiceModel> BuildMarkdown(
        RecipeExportSource source,
        RecipeExportTemplate template,
        RecipeUnitPresentation units,
        IReadOnlyDictionary<Guid, MeasurementUnitServiceModel> unitsById,
        IReadOnlyDictionary<MeasurementDimension, MeasurementUnitServiceModel> targetUnits,
        AcceptedEditorialServiceModel? editorial)
    {
        var report = RecipeMarkdownExporter.Export(new RecipeMarkdownInput
        {
            Snapshot = source.Document,
            VersionNumber = source.VersionNumber,
            Readiness = source.Readiness,
            Template = template,
            UnitPresentation = units,
            UnitsById = unitsById,
            TargetUnits = targetUnits,
            Editorial = editorial is null ? null : ToExportEditorial(editorial),
        });

        if (report.Markdown is null)
        {
            return OperationResult<RecipeMarkdownExportServiceModel>.Failure(new OperationError(
                RecipeErrorCodes.MarkdownExportIncompleteUnprocessable,
                "This recipe is missing facts its Markdown export cannot be produced without.",
                new Dictionary<string, string[]>(),
                new Dictionary<string, object?> { ["missingRequired"] = report.MissingRequired }));
        }

        return OperationResult<RecipeMarkdownExportServiceModel>.Success(new RecipeMarkdownExportServiceModel(
            report.Markdown,
            source.VersionNumber,
            RecipeExportFileName.For(source.Document.Recipe.Title, source.VersionNumber, "md"),
            report.Warnings));
    }

    public OperationResult<RecipePdfExportServiceModel> BuildPdf(
        RecipeExportSource source,
        RecipeExportTemplate template,
        RecipeUnitPresentation units,
        RecipePdfPageSize pageSize,
        IReadOnlyDictionary<Guid, MeasurementUnitServiceModel> unitsById,
        IReadOnlyDictionary<MeasurementDimension, MeasurementUnitServiceModel> targetUnits,
        AcceptedEditorialServiceModel? editorial,
        RecipePdfImage? image,
        DateTimeOffset createdAt)
    {
        var exportEditorial = editorial is null ? null : ToExportEditorial(editorial);

        RecipePdfReport report;
        try
        {
            report = RecipePdfRenderer.Render(new RecipePdfInput
            {
                Snapshot = source.Document,
                VersionNumber = source.VersionNumber,
                CreatedAt = createdAt,
                Readiness = source.Readiness,
                Template = template,
                PageSize = pageSize,
                UnitPresentation = units,
                UnitsById = unitsById,
                TargetUnits = targetUnits,
                Editorial = exportEditorial,
                Image = image,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // The renderer reports what it can name; anything else is a fault of ours. Nothing of the
            // exception, and none of the creator's text it may mention, reaches the response.
            return OperationResult<RecipePdfExportServiceModel>.Failure(new OperationError(
                RecipeErrorCodes.PdfExportRenderFailed,
                "The PDF could not be produced.",
                new Dictionary<string, string[]>()));
        }

        if (report.Pdf is null)
        {
            return OperationResult<RecipePdfExportServiceModel>.Failure(new OperationError(
                RecipeErrorCodes.PdfExportIncompleteUnprocessable,
                "This recipe cannot be produced as a PDF.",
                new Dictionary<string, string[]>(),
                new Dictionary<string, object?> { ["missingRequired"] = report.MissingRequired }));
        }

        return OperationResult<RecipePdfExportServiceModel>.Success(new RecipePdfExportServiceModel(
            report.Pdf,
            source.VersionNumber,
            RecipeExportFileName.For(source.Document.Recipe.Title, source.VersionNumber, "pdf"),
            ContentTagOf(source, template, units, pageSize, unitsById, targetUnits, exportEditorial, image),
            report.Warnings));
    }

    /// <summary>
    /// A digest of every input the renderer reads (including the unit catalogue and the layout version), so the same inputs always carry the same validator even
    /// though the PDF's bytes differ from one render to the next.
    /// </summary>
    private static string ContentTagOf(
        RecipeExportSource source,
        RecipeExportTemplate template,
        RecipeUnitPresentation units,
        RecipePdfPageSize pageSize,
        IReadOnlyDictionary<Guid, MeasurementUnitServiceModel> unitsById,
        IReadOnlyDictionary<MeasurementDimension, MeasurementUnitServiceModel> targetUnits,
        RecipeExportEditorial? editorial,
        RecipePdfImage? image)
    {
        // The unit catalogue is reference data that can be corrected, and the converted amounts on the page
        // come from it, so what it says is an input like any other. Ordered, so the same catalogue always
        // hashes the same.
        var catalogue = System.Text.Json.JsonSerializer.Serialize(new
        {
            Lines = unitsById.OrderBy(pair => pair.Key).Select(pair => pair.Value),
            Targets = targetUnits.OrderBy(pair => pair.Key).Select(pair => pair.Value),
        });

        var material = string.Join(
            "\n",
            RecipePdfRenderer.LayoutVersion,
            catalogue,
            source.VersionId,
            template,
            units,
            pageSize,
            RecipeSnapshotSerializer.Serialize(source.Document),
            editorial is null ? "-" : System.Text.Json.JsonSerializer.Serialize(editorial),
            image is null ? "-" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.Bytes)));

        return Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)))[..32];
    }

    private static RecipeExportEditorial ToExportEditorial(AcceptedEditorialServiceModel editorial) => new()
    {
        IsCurrent = editorial.IsCurrent,
        Headnote = editorial.Headnote,
        Introduction = editorial.Introduction,
        Tips = editorial.Tips,
        Substitutions = [.. editorial.Substitutions.Select(s => new RecipeExportSubstitution(s.LineId, s.Suggestion, s.CulinaryNote))],
        StorageReheating = editorial.StorageReheating,
        Faq = [.. editorial.Faq.Select(f => new RecipeExportFaq(f.Question, f.Answer))],
        Cta = editorial.Cta,
    };
}
