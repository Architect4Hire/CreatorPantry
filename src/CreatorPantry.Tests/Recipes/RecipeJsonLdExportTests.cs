using System.Text.Json;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Measurement.Facade;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Facade;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using FluentValidation;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The JSON-LD export's Business and Facade, over stubs of the layer below each: what is exportable, what is
/// asked of the neighbouring modules, and which failure wins.
/// </summary>
public sealed class RecipeJsonLdExportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid RecipeId = Guid.NewGuid();
    private static readonly Guid VersionId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    // ---- Fixtures ----

    private static RecipeSnapshotDocument Document(
        RecipeStatus status = RecipeStatus.Approved,
        Guid? cuisineId = null,
        Guid? courseId = null,
        bool withIngredients = true) => new()
    {
        SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
        Recipe = new RecipeSnapshotHeader
        {
            Title = "Soda Bread", Status = status, Description = "Stored description.", CuisineId = cuisineId,
            CourseId = courseId, CookTimeMinutes = 45, YieldText = "Makes 2 loaves",
        },
        IngredientGroups = withIngredients
            ? [new RecipeSnapshotIngredientGroup
                {
                    Id = Guid.NewGuid(),
                    Ingredients = [new RecipeSnapshotIngredient { Id = Guid.NewGuid(), DisplayText = "500 g flour" }],
                }]
            : [],
        InstructionGroups =
        [
            new RecipeSnapshotInstructionGroup
            {
                Id = Guid.NewGuid(),
                Steps = [new RecipeSnapshotInstructionStep { Id = Guid.NewGuid(), Text = "Bake." }],
            },
        ],
    };

    private static RecipeVersionSnapshotRecord Row(
        RecipeSnapshotDocument document,
        RecipeVersionReadiness readiness = RecipeVersionReadiness.Draft,
        int number = 3) =>
        new(VersionId, number, RecipeVersionSource.CreatorEdit, readiness, Now, RecipeSnapshotSerializer.Serialize(document));

    private sealed class StubDataLayer(bool visible, RecipeVersionSnapshotRecord? row) : IRecipeExportDataLayer
    {
        public List<(Guid RecipeId, int? VersionNumber)> Calls { get; } = [];

        public Task<(bool RecipeVisible, RecipeVersionSnapshotRecord? Source)> FindSourceAsync(
            Guid recipeId, int? versionNumber, CancellationToken cancellationToken)
        {
            Calls.Add((recipeId, versionNumber));
            return Task.FromResult((visible, row));
        }
    }

    private static RecipeExportBusiness BusinessOver(StubDataLayer dataLayer) => new(dataLayer);

    private static RecipeExportSource Source(RecipeSnapshotDocument? document = null) =>
        new(VersionId, 3, RecipeVersionReadiness.Draft, document ?? Document());

    // ---- Business: which version, and whether it may be exported ----

    [Fact]
    public async Task An_unknown_or_foreign_recipe_is_not_found_without_reading_a_version()
    {
        var result = await BusinessOver(new StubDataLayer(visible: false, row: null))
            .GetJsonLdSourceAsync(RecipeId, null, Ct);

        Assert.Equal(RecipeErrorCodes.RecipeNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task A_version_the_recipe_lacks_is_not_found_naming_the_parameter()
    {
        var result = await BusinessOver(new StubDataLayer(visible: true, row: null))
            .GetJsonLdSourceAsync(RecipeId, 9, Ct);

        Assert.Equal(RecipeErrorCodes.VersionNotFound, result.Error!.Code);
        Assert.Contains("versionNumber", result.Error.FieldErrors.Keys);
    }

    [Theory]
    [InlineData(RecipeStatus.Draft)]
    [InlineData(RecipeStatus.InDevelopment)]
    [InlineData(RecipeStatus.Testing)]
    [InlineData(RecipeStatus.ReadyForReview)]
    [InlineData(RecipeStatus.Archived)]
    public async Task A_version_that_is_not_approved_is_refused_with_a_conflict(RecipeStatus status)
    {
        var result = await BusinessOver(new StubDataLayer(true, Row(Document(status))))
            .GetJsonLdSourceAsync(RecipeId, null, Ct);

        Assert.Equal(RecipeErrorCodes.JsonLdExportNotApprovedConflict, result.Error!.Code);
    }

    [Fact]
    public async Task An_approved_version_is_the_source()
    {
        var result = await BusinessOver(new StubDataLayer(true, Row(Document())))
            .GetJsonLdSourceAsync(RecipeId, null, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(VersionId, result.Value!.VersionId);
        Assert.Equal(3, result.Value.VersionNumber);
        Assert.Equal("Soda Bread", result.Value.Document.Recipe.Title);
    }

    [Fact]
    public async Task A_version_marked_ready_is_exportable_although_the_recipe_moved_on()
    {
        var result = await BusinessOver(new StubDataLayer(true, Row(Document(RecipeStatus.Draft), RecipeVersionReadiness.Ready)))
            .GetJsonLdSourceAsync(RecipeId, null, Ct);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task The_requested_version_number_is_passed_down_and_nothing_else_is_substituted()
    {
        var dataLayer = new StubDataLayer(true, Row(Document()));

        await BusinessOver(dataLayer).GetJsonLdSourceAsync(RecipeId, 2, Ct);
        await BusinessOver(dataLayer).GetJsonLdSourceAsync(RecipeId, null, Ct);

        Assert.Equal([(RecipeId, (int?)2), (RecipeId, null)], dataLayer.Calls);
    }

    // ---- Business: generation ----

    private static OperationResult<RecipeJsonLdExportServiceModel> Build(
        RecipeExportSource source,
        string? cuisine = null,
        string? course = null,
        AcceptedSeoServiceModel? seo = null,
        string? imageUrl = "https://cdn.example.com/soda-bread.jpg") =>
        BusinessOver(new StubDataLayer(true, null)).BuildJsonLd(source, cuisine, course, seo, imageUrl);

    [Fact]
    public void A_complete_recipe_yields_the_document_and_its_version()
    {
        var result = Build(Source(Document(cuisineId: Guid.NewGuid())), cuisine: "Irish", course: "Bread");

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Value!.VersionNumber);

        using var json = JsonDocument.Parse(result.Value.Json);
        Assert.Equal("Recipe", json.RootElement.GetProperty("@type").GetString());
        Assert.Equal("Soda Bread", json.RootElement.GetProperty("name").GetString());
        Assert.Equal("Irish", json.RootElement.GetProperty("recipeCuisine").GetString());
        Assert.Equal("Bread", json.RootElement.GetProperty("recipeCategory").GetString());
    }

    [Fact]
    public void A_current_accepted_revision_supplies_the_description_and_keywords()
    {
        var result = Build(Source(), seo: new AcceptedSeoServiceModel(1, "Accepted meta.", ["soda bread"], IsCurrent: true));

        using var json = JsonDocument.Parse(result.Value!.Json);
        Assert.Equal("Accepted meta.", json.RootElement.GetProperty("description").GetString());
        Assert.Equal("soda bread", json.RootElement.GetProperty("keywords").GetString());
    }

    [Fact]
    public void A_stale_accepted_revision_is_left_out_and_reported_as_a_warning()
    {
        var result = Build(Source(), seo: new AcceptedSeoServiceModel(1, "Stale meta.", ["stale"], IsCurrent: false));

        using var json = JsonDocument.Parse(result.Value!.Json);
        Assert.Equal("Stored description.", json.RootElement.GetProperty("description").GetString());
        Assert.False(json.RootElement.TryGetProperty("keywords", out _));
        Assert.Contains(result.Value.Warnings, warning => warning.Code == RecipeJsonLdCodes.EditorialNotCurrent);
    }

    [Fact]
    public void Without_an_image_the_export_is_unprocessable_and_lists_what_is_missing()
    {
        var result = Build(Source(), imageUrl: null);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.JsonLdExportIncompleteUnprocessable, result.Error!.Code);

        var missing = Assert.IsAssignableFrom<IReadOnlyList<RecipeJsonLdIssue>>(result.Error.Extensions!["missingRequired"]);
        Assert.Equal(RecipeJsonLdCodes.ImageMissing, Assert.Single(missing).Code);
    }

    [Fact]
    public void A_recipe_missing_several_facts_lists_each_and_produces_no_document()
    {
        var result = Build(Source(Document(withIngredients: false)), imageUrl: null);

        var missing = (IReadOnlyList<RecipeJsonLdIssue>)result.Error!.Extensions!["missingRequired"]!;
        Assert.Equal(
            [RecipeJsonLdCodes.IngredientsMissing, RecipeJsonLdCodes.ImageMissing],
            missing.Select(issue => issue.Code));
        Assert.Null(result.Value);
    }

    // ---- Facade ----

    private sealed class StubBusiness(
        OperationResult<RecipeExportSource> source,
        OperationResult<RecipeJsonLdExportServiceModel>? built = null) : IRecipeExportBusiness
    {
        public int SourceCalls { get; private set; }

        public int? SourceVersionNumber { get; private set; }

        public List<(string? Cuisine, string? Course, AcceptedSeoServiceModel? Seo, string? Image)> Builds { get; } = [];

        public Task<OperationResult<RecipeExportSource>> GetJsonLdSourceAsync(
            Guid recipeId, int? versionNumber, CancellationToken cancellationToken)
        {
            SourceCalls++;
            SourceVersionNumber = versionNumber;
            return Task.FromResult(source);
        }

        public Task<OperationResult<RecipeExportSource>> GetMarkdownSourceAsync(
            Guid recipeId, int? versionNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public OperationResult<RecipeMarkdownExportServiceModel> BuildMarkdown(
            RecipeExportSource source,
            RecipeExportTemplate template,
            RecipeUnitPresentation units,
            IReadOnlyDictionary<Guid, Domain.Modules.Measurement.Managers.MeasurementUnitServiceModel> unitsById,
            IReadOnlyDictionary<Domain.Managers.Reference.MeasurementDimension, Domain.Modules.Measurement.Managers.MeasurementUnitServiceModel> targetUnits,
            Domain.Modules.Content.Managers.AcceptedEditorialServiceModel? editorial) =>
            throw new NotSupportedException();

        public Task<OperationResult<RecipeExportSummarySource>> GetSummarySourceAsync(
            Guid recipeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public RecipeExportSummaryServiceModel BuildSummary(
            RecipeExportSummarySource source,
            Domain.Modules.Content.Managers.AcceptedEditorialServiceModel? editorial,
            AcceptedSeoServiceModel? seo) =>
            throw new NotSupportedException();

        public Task<OperationResult<RecipeExportSource>> GetPdfSourceAsync(
            Guid recipeId, int? versionNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public OperationResult<RecipePdfExportServiceModel> BuildPdf(
            RecipeExportSource source,
            RecipeExportTemplate template,
            RecipeUnitPresentation units,
            RecipePdfPageSize pageSize,
            IReadOnlyDictionary<Guid, Domain.Modules.Measurement.Managers.MeasurementUnitServiceModel> unitsById,
            IReadOnlyDictionary<Domain.Managers.Reference.MeasurementDimension, Domain.Modules.Measurement.Managers.MeasurementUnitServiceModel> targetUnits,
            Domain.Modules.Content.Managers.AcceptedEditorialServiceModel? editorial,
            RecipePdfImage? image,
            DateTimeOffset createdAt) =>
            throw new NotSupportedException();

        public OperationResult<RecipeJsonLdExportServiceModel> BuildJsonLd(
            RecipeExportSource source, string? cuisineName, string? courseName, AcceptedSeoServiceModel? seo, string? imageUrl)
        {
            Builds.Add((cuisineName, courseName, seo, imageUrl));
            return built ?? OperationResult<RecipeJsonLdExportServiceModel>.Success(
                new RecipeJsonLdExportServiceModel("{}", source.VersionNumber, []));
        }
    }

    private sealed class StubVocabulary(string? cuisine, string? course) : IVocabularyFacade
    {
        public List<(VocabularyCatalog Catalog, Guid Id)> Lookups { get; } = [];

        public Task<string?> GetDisplayNameAsync(VocabularyCatalog catalog, Guid id, CancellationToken cancellationToken)
        {
            Lookups.Add((catalog, id));
            return Task.FromResult(catalog == VocabularyCatalog.Cuisine ? cuisine : course);
        }

        public Task<bool> IsUsableAsync(VocabularyCatalog catalog, Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult<CursorPageServiceModel<ReferenceEntryServiceModel>>> ListFoodCategoriesAsync(ReferenceQueryViewModel model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult<CursorPageServiceModel<ReferenceEntryServiceModel>>> ListCuisinesAsync(ReferenceQueryViewModel model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult<CursorPageServiceModel<ReferenceEntryServiceModel>>> ListCoursesAsync(ReferenceQueryViewModel model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult<CursorPageServiceModel<CookingTechniqueServiceModel>>> ListTechniquesAsync(ReferenceQueryViewModel model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult<CursorPageServiceModel<ReferenceEntryServiceModel>>> ListEquipmentTypesAsync(ReferenceQueryViewModel model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult<CursorPageServiceModel<DescribedReferenceEntryServiceModel>>> ListDietaryProfilesAsync(ReferenceQueryViewModel model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult<CursorPageServiceModel<DescribedReferenceEntryServiceModel>>> ListAllergensAsync(ReferenceQueryViewModel model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ReferenceEntryServiceModel>> ListActiveCuisinesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ReferenceEntryServiceModel>> ListActiveCoursesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CookingTechniqueServiceModel>> ListActiveTechniquesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubSeoFacade(OperationResult<AcceptedSeoServiceModel?> result) : IContentSeoFacade
    {
        public List<(Guid RecipeId, Guid VersionId, int? Revision)> Calls { get; } = [];

        public Task<OperationResult<AcceptedSeoServiceModel?>> GetAcceptedSeoAsync(
            Guid recipeId, Guid recipeVersionId, int? revisionNumber, CancellationToken cancellationToken)
        {
            Calls.Add((recipeId, recipeVersionId, revisionNumber));
            return Task.FromResult(result);
        }
    }

    private sealed class NoMeasurement : IMeasurementFacade
    {
        public Task<Domain.Managers.Reference.MeasurementDimension?> FindUsableUnitDimensionAsync(Guid unitId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult<CursorPageServiceModel<MeasurementUnitServiceModel>>> ListUnitsAsync(MeasurementUnitQueryViewModel model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<MeasurementUnitServiceModel>> FindUnitsByIdsAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<MeasurementUnitServiceModel>> FindUnitsByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult<IReadOnlyList<UnitMatchResult>>> ResolveCandidatesAsync(IReadOnlyList<string> candidateTexts, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class NoEditorial : IContentEditorialFacade
    {
        public Task<OperationResult<AcceptedEditorialServiceModel?>> GetAcceptedEditorialAsync(
            Guid recipeId, Guid recipeVersionId, int? revisionNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static OperationResult<AcceptedSeoServiceModel?> NoSeo() =>
        OperationResult<AcceptedSeoServiceModel?>.Success(null);

    private sealed class FixedClock : Domain.Managers.Time.IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private static RecipeExportFacade FacadeOver(StubBusiness business, StubVocabulary vocabulary, StubSeoFacade seo) =>
        new(
            new RecipeJsonLdExportViewModelValidator(),
            new RecipeMarkdownExportViewModelValidator(),
            new RecipePdfExportViewModelValidator(),
            new FixedClock(),
            business,
            vocabulary,
            new NoMeasurement(),
            seo,
            new NoEditorial());

    [Fact]
    public async Task An_invalid_query_is_refused_before_anything_is_read()
    {
        var business = new StubBusiness(OperationResult<RecipeExportSource>.Success(Source()));
        var seo = new StubSeoFacade(NoSeo());
        var facade = FacadeOver(business, new StubVocabulary(null, null), seo);

        var result = await facade.GetJsonLdAsync(RecipeId, new RecipeJsonLdExportViewModel(0, -1), Ct);

        Assert.Equal(RecipeErrorCodes.JsonLdExportInvalidRequest, result.Error!.Code);
        Assert.Equal(["versionNumber", "seoRevision"], result.Error.FieldErrors.Keys);
        Assert.Equal(0, business.SourceCalls);
        Assert.Empty(seo.Calls);
    }

    [Fact]
    public async Task A_version_that_cannot_be_exported_asks_nothing_of_vocabulary_or_content()
    {
        var refusal = OperationResult<RecipeExportSource>.Failure(new OperationError(
            RecipeErrorCodes.JsonLdExportNotApprovedConflict, "Not approved.", new Dictionary<string, string[]>()));
        var business = new StubBusiness(refusal);
        var vocabulary = new StubVocabulary("Irish", "Bread");
        var seo = new StubSeoFacade(NoSeo());

        var result = await FacadeOver(business, vocabulary, seo)
            .GetJsonLdAsync(RecipeId, new RecipeJsonLdExportViewModel(), Ct);

        Assert.Equal(RecipeErrorCodes.JsonLdExportNotApprovedConflict, result.Error!.Code);
        Assert.Empty(vocabulary.Lookups);
        Assert.Empty(seo.Calls);
        Assert.Empty(business.Builds);
    }

    [Fact]
    public async Task Names_the_seo_revision_and_the_exact_version_are_resolved_and_handed_to_generation()
    {
        var cuisineId = Guid.NewGuid();
        var courseId = Guid.NewGuid();
        var accepted = new AcceptedSeoServiceModel(2, "Meta.", ["a"], IsCurrent: true);
        var business = new StubBusiness(OperationResult<RecipeExportSource>.Success(Source(Document(cuisineId: cuisineId, courseId: courseId))));
        var vocabulary = new StubVocabulary("Irish", "Bread");
        var seo = new StubSeoFacade(OperationResult<AcceptedSeoServiceModel?>.Success(accepted));

        var result = await FacadeOver(business, vocabulary, seo)
            .GetJsonLdAsync(RecipeId, new RecipeJsonLdExportViewModel(VersionNumber: 3, SeoRevision: 2), Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(3, business.SourceVersionNumber);
        Assert.Equal([(VocabularyCatalog.Cuisine, cuisineId), (VocabularyCatalog.Course, courseId)], vocabulary.Lookups);
        Assert.Equal([(RecipeId, VersionId, (int?)2)], seo.Calls);
        Assert.Equal(("Irish", "Bread", accepted, (string?)null), Assert.Single(business.Builds));
    }

    [Fact]
    public async Task A_recipe_with_no_cuisine_or_course_asks_vocabulary_for_nothing()
    {
        var vocabulary = new StubVocabulary("Irish", "Bread");

        await FacadeOver(
                new StubBusiness(OperationResult<RecipeExportSource>.Success(Source())),
                vocabulary,
                new StubSeoFacade(NoSeo()))
            .GetJsonLdAsync(RecipeId, new RecipeJsonLdExportViewModel(), Ct);

        Assert.Empty(vocabulary.Lookups);
    }

    [Fact]
    public async Task A_revision_that_is_not_found_stops_generation()
    {
        var missing = OperationResult<AcceptedSeoServiceModel?>.Failure(new OperationError(
            ContentErrorCodes.RevisionNotFound, "No such revision.", new Dictionary<string, string[]>()));
        var business = new StubBusiness(OperationResult<RecipeExportSource>.Success(Source()));

        var result = await FacadeOver(business, new StubVocabulary(null, null), new StubSeoFacade(missing))
            .GetJsonLdAsync(RecipeId, new RecipeJsonLdExportViewModel(SeoRevision: 5), Ct);

        Assert.Equal(ContentErrorCodes.RevisionNotFound, result.Error!.Code);
        Assert.Empty(business.Builds);
    }

    [Fact]
    public async Task The_facade_never_supplies_an_image_url_until_media_exists()
    {
        var business = new StubBusiness(OperationResult<RecipeExportSource>.Success(Source()));

        await FacadeOver(business, new StubVocabulary(null, null), new StubSeoFacade(NoSeo()))
            .GetJsonLdAsync(RecipeId, new RecipeJsonLdExportViewModel(), Ct);

        Assert.Null(Assert.Single(business.Builds).Image);
    }

    [Fact]
    public void The_validator_accepts_omitted_and_positive_numbers_only()
    {
        var validator = new RecipeJsonLdExportViewModelValidator();

        Assert.True(validator.Validate(new RecipeJsonLdExportViewModel()).IsValid);
        Assert.True(validator.Validate(new RecipeJsonLdExportViewModel(1, 1)).IsValid);
        Assert.False(validator.Validate(new RecipeJsonLdExportViewModel(VersionNumber: 0)).IsValid);
        Assert.False(validator.Validate(new RecipeJsonLdExportViewModel(SeoRevision: 0)).IsValid);
        Assert.False(validator.Validate(new RecipeJsonLdExportViewModel(VersionNumber: -3)).IsValid);
    }
}
