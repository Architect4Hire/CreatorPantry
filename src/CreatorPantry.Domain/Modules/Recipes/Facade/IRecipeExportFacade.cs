using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Measurement.Facade;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Facade;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Facade;

/// <summary>
/// Read-only exports of one recipe version. Nothing here writes, and the workspace is the resolved request
/// context's: no caller can name one.
/// </summary>
public interface IRecipeExportFacade
{
    /// <summary>
    /// Generates schema.org Recipe JSON-LD for an approved version from the creator's own stored fields and
    /// the accepted, current SEO revision.
    /// </summary>
    /// <remarks>
    /// Fails with <c>recipes.recipe.not_found</c> for an unknown or foreign recipe,
    /// <c>recipes.version.not_found</c> or <c>content.revision.not_found</c> naming the parameter at fault,
    /// <c>recipes.jsonLdExport.notApproved.conflict</c> for a version that is neither approved nor ready, and
    /// <c>recipes.jsonLdExport.incomplete.unprocessable</c> carrying the missing facts.
    /// </remarks>
    Task<OperationResult<RecipeJsonLdExportServiceModel>> GetJsonLdAsync(
        Guid recipeId, RecipeJsonLdExportViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Renders an approved version as Markdown from the creator's own stored fields, in a chosen template and
    /// unit presentation, with the accepted and current editorial revision when the template carries it.
    /// </summary>
    /// <remarks>
    /// Fails with <c>recipes.recipe.not_found</c> for an unknown or foreign recipe,
    /// <c>recipes.version.not_found</c> or <c>content.revision.not_found</c> naming the parameter at fault,
    /// <c>recipes.markdownExport.notApproved.conflict</c> for a version that is neither approved nor ready, and
    /// <c>recipes.markdownExport.incomplete.unprocessable</c> carrying the missing facts.
    /// </remarks>
    Task<OperationResult<RecipeMarkdownExportServiceModel>> GetMarkdownAsync(
        Guid recipeId, RecipeMarkdownExportViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Renders an approved version as a PDF from the creator's own stored fields, in a chosen template, unit
    /// presentation and page size, with the accepted and current editorial revision when the template carries it.
    /// </summary>
    /// <remarks>
    /// Fails with <c>recipes.recipe.not_found</c> for an unknown or foreign recipe,
    /// <c>recipes.version.not_found</c> or <c>content.revision.not_found</c> naming the parameter at fault,
    /// <c>recipes.pdfExport.notApproved.conflict</c> for a version that is neither approved nor ready,
    /// <c>recipes.pdfExport.incomplete.unprocessable</c> carrying what blocks it, and
    /// <c>recipes.pdfExport.render.failed</c> when the renderer itself fails.
    /// </remarks>
    Task<OperationResult<RecipePdfExportServiceModel>> GetPdfAsync(
        Guid recipeId, RecipePdfExportViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Says what an export of the current version would be built from: whether it may be exported, and which
    /// editorial and SEO revisions are accepted and current for it. Nothing in it is content.
    /// </summary>
    /// <remarks>Fails with <c>recipes.recipe.not_found</c> for an unknown or foreign recipe.</remarks>
    Task<OperationResult<RecipeExportSummaryServiceModel>> GetSummaryAsync(
        Guid recipeId, CancellationToken cancellationToken);
}

internal sealed class RecipeExportFacade(
    IValidator<RecipeJsonLdExportViewModel> validator,
    IValidator<RecipeMarkdownExportViewModel> markdownValidator,
    IValidator<RecipePdfExportViewModel> pdfValidator,
    IClock clock,
    IRecipeExportBusiness business,
    IVocabularyFacade vocabulary,
    IMeasurementFacade measurement,
    IContentSeoFacade seoRevisions,
    IContentEditorialFacade editorialRevisions) : IRecipeExportFacade
{
    public async Task<OperationResult<RecipeJsonLdExportServiceModel>> GetJsonLdAsync(
        Guid recipeId, RecipeJsonLdExportViewModel model, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<RecipeJsonLdExportServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.JsonLdExportInvalidRequest,
                "That export cannot be produced as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // The recipe comes from the route and the workspace from the resolved context; the model has a field
        // for neither.
        var source = await business.GetJsonLdSourceAsync(recipeId, model.VersionNumber, cancellationToken);
        if (!source.Succeeded)
        {
            return OperationResult<RecipeJsonLdExportServiceModel>.Failure(source.Error!);
        }

        var header = source.Value!.Document.Recipe;
        var cuisine = header.CuisineId is { } cuisineId
            ? await vocabulary.GetDisplayNameAsync(VocabularyCatalog.Cuisine, cuisineId, cancellationToken)
            : null;
        var course = header.CourseId is { } courseId
            ? await vocabulary.GetDisplayNameAsync(VocabularyCatalog.Course, courseId, cancellationToken)
            : null;

        var seo = await seoRevisions.GetAcceptedSeoAsync(
            recipeId, source.Value.VersionId, model.SeoRevision, cancellationToken);
        if (!seo.Succeeded)
        {
            return OperationResult<RecipeJsonLdExportServiceModel>.Failure(seo.Error!);
        }

        // No media URL exists until the media module is built, so the image is reported missing rather than
        // invented, and never derived from an asset id or a storage path.
        return business.BuildJsonLd(source.Value, cuisine, course, seo.Value, imageUrl: null);
    }

    public async Task<OperationResult<RecipeMarkdownExportServiceModel>> GetMarkdownAsync(
        Guid recipeId, RecipeMarkdownExportViewModel model, CancellationToken cancellationToken)
    {
        var validation = await markdownValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<RecipeMarkdownExportServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.MarkdownExportInvalidRequest,
                "That export cannot be produced as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // Valid, so both parse: an omitted value is the default.
        RecipeMarkdownExportViewModel.TryParseTemplate(model.Template, out var template);
        RecipeMarkdownExportViewModel.TryParseUnits(model.Units, out var units);

        var source = await business.GetMarkdownSourceAsync(recipeId, model.VersionNumber, cancellationToken);
        if (!source.Succeeded)
        {
            return OperationResult<RecipeMarkdownExportServiceModel>.Failure(source.Error!);
        }

        var (unitsById, targetUnits) = await ResolveUnitsAsync(source.Value!, units, cancellationToken);

        // Only the standard template carries editorial copy, so the compact one neither reads it nor is
        // refused for naming a revision it would not use.
        AcceptedEditorialServiceModel? editorial = null;
        if (template == RecipeExportTemplate.Standard)
        {
            var accepted = await editorialRevisions.GetAcceptedEditorialAsync(
                recipeId, source.Value!.VersionId, model.EditorialRevision, cancellationToken);
            if (!accepted.Succeeded)
            {
                return OperationResult<RecipeMarkdownExportServiceModel>.Failure(accepted.Error!);
            }

            editorial = accepted.Value;
        }

        return business.BuildMarkdown(source.Value!, template, units, unitsById, targetUnits, editorial);
    }

    public async Task<OperationResult<RecipePdfExportServiceModel>> GetPdfAsync(
        Guid recipeId, RecipePdfExportViewModel model, CancellationToken cancellationToken)
    {
        var validation = await pdfValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<RecipePdfExportServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.PdfExportInvalidRequest,
                "That export cannot be produced as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // Valid, so all three parse: an omitted value is the default.
        RecipeMarkdownExportViewModel.TryParseTemplate(model.Template, out var template);
        RecipeMarkdownExportViewModel.TryParseUnits(model.Units, out var units);
        RecipePdfExportViewModel.TryParsePageSize(model.PageSize, out var pageSize);

        var source = await business.GetPdfSourceAsync(recipeId, model.VersionNumber, cancellationToken);
        if (!source.Succeeded)
        {
            return OperationResult<RecipePdfExportServiceModel>.Failure(source.Error!);
        }

        var (unitsById, targetUnits) = await ResolveUnitsAsync(source.Value!, units, cancellationToken);

        AcceptedEditorialServiceModel? editorial = null;
        if (template == RecipeExportTemplate.Standard)
        {
            var accepted = await editorialRevisions.GetAcceptedEditorialAsync(
                recipeId, source.Value!.VersionId, model.EditorialRevision, cancellationToken);
            if (!accepted.Succeeded)
            {
                return OperationResult<RecipePdfExportServiceModel>.Failure(accepted.Error!);
            }

            editorial = accepted.Value;
        }

        // No media module exists yet, so there is no authorized image to read: the PDF is produced without a
        // figure rather than with one fetched, guessed or derived from an asset id or a storage path. When
        // media lands, the authorized bytes are read here, through its facade, and passed in.
        return business.BuildPdf(
            source.Value!, template, units, pageSize, unitsById, targetUnits, editorial, image: null, clock.UtcNow);
    }

    public async Task<OperationResult<RecipeExportSummaryServiceModel>> GetSummaryAsync(
        Guid recipeId, CancellationToken cancellationToken)
    {
        var source = await business.GetSummarySourceAsync(recipeId, cancellationToken);
        if (!source.Succeeded)
        {
            return OperationResult<RecipeExportSummaryServiceModel>.Failure(source.Error!);
        }

        // No revision is named, so neither lookup can refuse: each answers the accepted revision or null.
        var editorial = await editorialRevisions.GetAcceptedEditorialAsync(
            recipeId, source.Value!.VersionId, null, cancellationToken);
        if (!editorial.Succeeded)
        {
            return OperationResult<RecipeExportSummaryServiceModel>.Failure(editorial.Error!);
        }

        var seo = await seoRevisions.GetAcceptedSeoAsync(recipeId, source.Value.VersionId, null, cancellationToken);
        if (!seo.Succeeded)
        {
            return OperationResult<RecipeExportSummaryServiceModel>.Failure(seo.Error!);
        }

        return OperationResult<RecipeExportSummaryServiceModel>.Success(
            business.BuildSummary(source.Value, editorial.Value, seo.Value));
    }

    private async Task<(
        IReadOnlyDictionary<Guid, MeasurementUnitServiceModel> UnitsById,
        IReadOnlyDictionary<CreatorPantry.Domain.Managers.Reference.MeasurementDimension, MeasurementUnitServiceModel> Targets)>
        ResolveUnitsAsync(RecipeExportSource source, RecipeUnitPresentation presentation, CancellationToken cancellationToken)
    {
        if (presentation == RecipeUnitPresentation.AsWritten)
        {
            return (
                new Dictionary<Guid, MeasurementUnitServiceModel>(),
                new Dictionary<CreatorPantry.Domain.Managers.Reference.MeasurementDimension, MeasurementUnitServiceModel>());
        }

        var lineUnitIds = source.Document.IngredientGroups
            .SelectMany(group => group.Ingredients)
            .Select(line => line.MeasurementUnitId)
            .OfType<Guid>()
            .Distinct()
            .ToList();

        var lineUnits = await measurement.FindUnitsByIdsAsync(lineUnitIds, cancellationToken);
        var system = presentation == RecipeUnitPresentation.Metric
            ? MeasurementSystem.Metric
            : MeasurementSystem.UsCustomary;

        // The catalogue's own codes, fixed here so every export of a presentation converts to the same
        // units. One whose code is missing or retired is simply absent, and its lines stay as written.
        var targets = await measurement.FindUnitsByCodesAsync(RecipeExportTargetUnits.CodesFor(presentation), cancellationToken);

        return (
            lineUnits.ToDictionary(unit => unit.Id),
            targets.Where(unit => unit.System == system).ToDictionary(unit => unit.Dimension));
    }
}
