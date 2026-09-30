using CreatorPantry.Domain.Managers.Results;
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
}

internal sealed class RecipeExportFacade(
    IValidator<RecipeJsonLdExportViewModel> validator,
    IValidator<RecipeMarkdownExportViewModel> markdownValidator,
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
