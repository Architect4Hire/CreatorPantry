using System.Text.RegularExpressions;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Reference;
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

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The Markdown export's file name, query validation, Business and Facade, over stubs of the layer below each.
/// </summary>
public sealed partial class RecipeMarkdownExportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid RecipeId = Guid.NewGuid();
    private static readonly Guid VersionId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*-v\d+\.md$")]
    private static partial Regex SafeName();

    // ---- File name ----

    [Theory]
    [InlineData("Soda Bread", 3, "soda-bread-v3.md")]
    [InlineData("  Crème  brûlée  ", 1, "creme-brulee-v1.md")]
    [InlineData("Œufs à la neige & Ærø ß", 2, "oeufs-a-la-neige-aero-ss-v2.md")]
    [InlineData("100% Whole-Wheat (No Knead!)", 12, "100-whole-wheat-no-knead-v12.md")]
    [InlineData("麻婆豆腐", 1, "recipe-v1.md")]
    [InlineData("", 4, "recipe-v4.md")]
    [InlineData("   ", 4, "recipe-v4.md")]
    [InlineData("!!!", 4, "recipe-v4.md")]
    [InlineData("CON", 1, "recipe-con-v1.md")]
    [InlineData("lpt1", 1, "recipe-lpt1-v1.md")]
    public void A_title_becomes_a_predictable_slug(string title, int version, string expected)
    {
        Assert.Equal(expected, RecipeExportFileName.For(title, version, "md"));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\windows\\system32")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j")]
    [InlineData("line\r\nbreak\tand\0nul")]
    [InlineData("\"; filename=\"evil.exe")]
    [InlineData("%2e%2e%2f")]
    [InlineData("😀 Emoji 🍞 bread")]
    [InlineData("a--b___c   d")]
    [InlineData("-leading and trailing-")]
    public void Nothing_in_a_title_can_escape_the_name(string title)
    {
        var name = RecipeExportFileName.For(title, 7, "md");

        Assert.Matches(SafeName(), name);
        Assert.True(name.Length <= RecipeExportFileName.MaxSlugLength + "-v7.md".Length);
        Assert.Equal(name, RecipeExportFileName.For(title, 7, "md"));
    }

    [Fact]
    public void A_long_title_is_cut_without_leaving_a_dangling_hyphen()
    {
        var name = RecipeExportFileName.Slug(new string('a', 58) + " b " + new string('c', 40));

        Assert.True(name.Length <= RecipeExportFileName.MaxSlugLength);
        Assert.DoesNotContain("--", name);
        Assert.False(name.EndsWith('-'));
    }

    // ---- Validator ----

    [Theory]
    [InlineData(null, null, null, null, true)]
    [InlineData(1, "standard", "asWritten", 1, true)]
    [InlineData(2, "Compact", "METRIC", null, true)]
    [InlineData(null, "standard", "usCustomary", null, true)]
    [InlineData(0, null, null, null, false)]
    [InlineData(null, null, null, 0, false)]
    [InlineData(null, "fancy", null, null, false)]
    [InlineData(null, "1", null, null, false)]
    [InlineData(null, null, "imperial", null, false)]
    [InlineData(null, null, "2", null, false)]
    public void The_validator_accepts_only_known_templates_units_and_positive_numbers(
        int? version, string? template, string? units, int? editorial, bool valid)
    {
        var result = new RecipeMarkdownExportViewModelValidator()
            .Validate(new RecipeMarkdownExportViewModel(version, template, units, editorial));

        Assert.Equal(valid, result.IsValid);
    }

    // ---- Fixtures ----

    private static RecipeSnapshotDocument Document(
        RecipeStatus status = RecipeStatus.Approved,
        bool withIngredients = true,
        Guid? ounceId = null) => new()
    {
        SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
        Recipe = new RecipeSnapshotHeader { Title = "Soda Bread", Status = status, CookTimeMinutes = 45 },
        IngredientGroups = withIngredients
            ? [new RecipeSnapshotIngredientGroup
                {
                    Id = Guid.NewGuid(),
                    Ingredients =
                    [
                        new RecipeSnapshotIngredient
                        {
                            Id = Guid.NewGuid(), SortOrder = 0, DisplayText = "4 oz butter", Quantity = 4m,
                            MeasurementUnitId = ounceId, MeasurementUnitDimension = ounceId is null ? null : MeasurementDimension.Mass,
                        },
                    ],
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

    private static RecipeVersionSnapshotRecord Row(RecipeSnapshotDocument document, RecipeVersionReadiness readiness = RecipeVersionReadiness.Draft) =>
        new(VersionId, 3, RecipeVersionSource.CreatorEdit, readiness, Now, RecipeSnapshotSerializer.Serialize(document));

    private sealed class StubDataLayer(bool visible, RecipeVersionSnapshotRecord? row) : IRecipeExportDataLayer
    {
        public Task<(bool RecipeVisible, RecipeVersionSnapshotRecord? Source)> FindSourceAsync(
            Guid recipeId, int? versionNumber, CancellationToken cancellationToken) =>
            Task.FromResult((visible, row));
    }

    private static RecipeExportBusiness BusinessOver(RecipeSnapshotDocument? document = null, RecipeVersionReadiness readiness = RecipeVersionReadiness.Draft) =>
        new(new StubDataLayer(true, Row(document ?? Document(), readiness)));

    private static readonly MeasurementUnitServiceModel Ounce =
        new(Guid.NewGuid(), "oz", "ounce", "ounces", "oz", MeasurementDimension.Mass, MeasurementSystem.UsCustomary, 28.349523125m, 2);

    private static readonly MeasurementUnitServiceModel Gram =
        new(Guid.NewGuid(), "g", "gram", "grams", "g", MeasurementDimension.Mass, MeasurementSystem.Metric, 1m, 0);

    private static readonly MeasurementUnitServiceModel Cup =
        new(Guid.NewGuid(), "cup-us", "US cup", "US cups", "cup", MeasurementDimension.Volume, MeasurementSystem.UsCustomary, 236.5882365m, 2);

    private static readonly MeasurementUnitServiceModel Millilitre =
        new(Guid.NewGuid(), "ml", "milliliter", "milliliters", "ml", MeasurementDimension.Volume, MeasurementSystem.Metric, 1m, 0);

    private static RecipeExportSource Source(RecipeSnapshotDocument? document = null) =>
        new(VersionId, 3, RecipeVersionReadiness.Draft, document ?? Document());

    // ---- Business: gate ----

    [Theory]
    [InlineData(RecipeStatus.Draft)]
    [InlineData(RecipeStatus.Archived)]
    public async Task A_version_that_is_not_approved_is_refused_with_this_exports_own_code(RecipeStatus status)
    {
        var result = await BusinessOver(Document(status)).GetMarkdownSourceAsync(RecipeId, null, Ct);

        Assert.Equal(RecipeErrorCodes.MarkdownExportNotApprovedConflict, result.Error!.Code);
    }

    [Fact]
    public async Task The_json_ld_gate_keeps_its_own_code()
    {
        var result = await BusinessOver(Document(RecipeStatus.Draft)).GetJsonLdSourceAsync(RecipeId, null, Ct);

        Assert.Equal(RecipeErrorCodes.JsonLdExportNotApprovedConflict, result.Error!.Code);
    }

    [Fact]
    public async Task An_approved_or_ready_version_is_the_markdown_source()
    {
        Assert.True((await BusinessOver().GetMarkdownSourceAsync(RecipeId, null, Ct)).Succeeded);
        Assert.True((await BusinessOver(Document(RecipeStatus.Draft), RecipeVersionReadiness.Ready)
            .GetMarkdownSourceAsync(RecipeId, null, Ct)).Succeeded);
    }

    [Fact]
    public async Task An_unknown_recipe_and_a_missing_version_keep_the_shared_codes()
    {
        var unknown = await new RecipeExportBusiness(new StubDataLayer(false, null)).GetMarkdownSourceAsync(RecipeId, null, Ct);
        var missing = await new RecipeExportBusiness(new StubDataLayer(true, null)).GetMarkdownSourceAsync(RecipeId, 9, Ct);

        Assert.Equal(RecipeErrorCodes.RecipeNotFound, unknown.Error!.Code);
        Assert.Equal(RecipeErrorCodes.VersionNotFound, missing.Error!.Code);
        Assert.Contains("versionNumber", missing.Error.FieldErrors.Keys);
    }

    // ---- Business: rendering ----

    private static readonly IReadOnlyDictionary<Guid, MeasurementUnitServiceModel> NoUnits = new Dictionary<Guid, MeasurementUnitServiceModel>();
    private static readonly IReadOnlyDictionary<MeasurementDimension, MeasurementUnitServiceModel> NoTargets = new Dictionary<MeasurementDimension, MeasurementUnitServiceModel>();

    [Fact]
    public void A_complete_recipe_yields_the_markdown_a_version_and_a_safe_file_name()
    {
        var result = BusinessOver().BuildMarkdown(
            Source(), RecipeExportTemplate.Standard, RecipeUnitPresentation.AsWritten, NoUnits, NoTargets, null);

        Assert.True(result.Succeeded);
        Assert.Equal("soda-bread-v3.md", result.Value!.FileName);
        Assert.Equal(3, result.Value.VersionNumber);
        Assert.StartsWith("# Soda Bread\n", result.Value.Markdown);
        Assert.Contains("Version 3", result.Value.Markdown);
    }

    [Fact]
    public void A_recipe_missing_facts_is_unprocessable_and_lists_each_one()
    {
        var result = BusinessOver().BuildMarkdown(
            Source(Document(withIngredients: false)), RecipeExportTemplate.Standard,
            RecipeUnitPresentation.AsWritten, NoUnits, NoTargets, null);

        Assert.Equal(RecipeErrorCodes.MarkdownExportIncompleteUnprocessable, result.Error!.Code);
        var missing = Assert.IsAssignableFrom<IReadOnlyList<RecipeMarkdownIssue>>(result.Error.Extensions!["missingRequired"]);
        Assert.Equal(RecipeMarkdownCodes.IngredientsMissing, Assert.Single(missing).Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Units_are_converted_by_the_canonical_code_and_appended_to_the_creators_line()
    {
        var document = Document(ounceId: Ounce.Id);
        var result = BusinessOver(document).BuildMarkdown(
            Source(document), RecipeExportTemplate.Standard, RecipeUnitPresentation.Metric,
            new Dictionary<Guid, MeasurementUnitServiceModel> { [Ounce.Id] = Ounce },
            new Dictionary<MeasurementDimension, MeasurementUnitServiceModel> { [MeasurementDimension.Mass] = Gram },
            null);

        Assert.Contains("- 4 oz butter (≈ 113 g)", result.Value!.Markdown);
    }

    [Fact]
    public void Current_editorial_is_marked_and_stale_editorial_is_left_out_with_a_warning()
    {
        static AcceptedEditorialServiceModel Editorial(bool current) =>
            new(1, null, "No yeast needed.", ["Use cold buttermilk."], [], null, [], null, current);

        var current = BusinessOver().BuildMarkdown(
            Source(), RecipeExportTemplate.Standard, RecipeUnitPresentation.AsWritten, NoUnits, NoTargets, Editorial(true));
        var stale = BusinessOver().BuildMarkdown(
            Source(), RecipeExportTemplate.Standard, RecipeUnitPresentation.AsWritten, NoUnits, NoTargets, Editorial(false));

        Assert.Contains("## Introduction (editorial)", current.Value!.Markdown);
        Assert.Contains("- Use cold buttermilk.", current.Value.Markdown);
        Assert.DoesNotContain("No yeast needed.", stale.Value!.Markdown);
        Assert.Contains(stale.Value.Warnings, warning => warning.Code == RecipeMarkdownCodes.EditorialNotCurrent);
    }

    // ---- Facade ----

    private sealed class RecordingMeasurement(params MeasurementUnitServiceModel[] known) : IMeasurementFacade
    {
        public List<IReadOnlyCollection<Guid>> IdLookups { get; } = [];

        public List<IReadOnlyCollection<string>> CodeLookups { get; } = [];

        public Task<IReadOnlyList<MeasurementUnitServiceModel>> FindUnitsByIdsAsync(
            IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken)
        {
            IdLookups.Add(unitIds);
            return Task.FromResult<IReadOnlyList<MeasurementUnitServiceModel>>([.. known.Where(unit => unitIds.Contains(unit.Id))]);
        }

        public Task<IReadOnlyList<MeasurementUnitServiceModel>> FindUnitsByCodesAsync(
            IReadOnlyCollection<string> codes, CancellationToken cancellationToken)
        {
            CodeLookups.Add(codes);
            return Task.FromResult<IReadOnlyList<MeasurementUnitServiceModel>>([.. known.Where(unit => codes.Contains(unit.Code))]);
        }

        public Task<MeasurementDimension?> FindUsableUnitDimensionAsync(Guid unitId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult<CursorPageServiceModel<MeasurementUnitServiceModel>>> ListUnitsAsync(MeasurementUnitQueryViewModel model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult<IReadOnlyList<UnitMatchResult>>> ResolveCandidatesAsync(IReadOnlyList<string> candidateTexts, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingEditorial(OperationResult<AcceptedEditorialServiceModel?> result) : IContentEditorialFacade
    {
        public List<(Guid RecipeId, Guid VersionId, int? Revision)> Calls { get; } = [];

        public Task<OperationResult<AcceptedEditorialServiceModel?>> GetAcceptedEditorialAsync(
            Guid recipeId, Guid recipeVersionId, int? revisionNumber, CancellationToken cancellationToken)
        {
            Calls.Add((recipeId, recipeVersionId, revisionNumber));
            return Task.FromResult(result);
        }
    }

    private sealed class UnusedSeo : IContentSeoFacade
    {
        public Task<OperationResult<AcceptedSeoServiceModel?>> GetAcceptedSeoAsync(
            Guid recipeId, Guid recipeVersionId, int? revisionNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedVocabulary : IVocabularyFacade
    {
        public Task<string?> GetDisplayNameAsync(VocabularyCatalog catalog, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> IsUsableAsync(VocabularyCatalog catalog, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

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

    private static OperationResult<AcceptedEditorialServiceModel?> NoEditorial() =>
        OperationResult<AcceptedEditorialServiceModel?>.Success(null);

    private sealed class FixedClock : Domain.Managers.Time.IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private static RecipeExportFacade FacadeOver(
        IRecipeExportBusiness business, RecordingMeasurement measurement, RecordingEditorial editorial) =>
        new(
            new RecipeJsonLdExportViewModelValidator(),
            new RecipeMarkdownExportViewModelValidator(),
            new RecipePdfExportViewModelValidator(),
            new FixedClock(),
            business,
            new UnusedVocabulary(),
            measurement,
            new UnusedSeo(),
            editorial);

    [Fact]
    public async Task An_invalid_query_is_refused_naming_every_field_before_anything_is_read()
    {
        var measurement = new RecordingMeasurement();
        var editorial = new RecordingEditorial(NoEditorial());

        var result = await FacadeOver(BusinessOver(), measurement, editorial).GetMarkdownAsync(
            RecipeId, new RecipeMarkdownExportViewModel(0, "fancy", "imperial", -1), Ct);

        Assert.Equal(RecipeErrorCodes.MarkdownExportInvalidRequest, result.Error!.Code);
        Assert.Equal(["versionNumber", "editorialRevision", "template", "units"], result.Error.FieldErrors.Keys);
        Assert.Empty(measurement.IdLookups);
        Assert.Empty(editorial.Calls);
    }

    [Fact]
    public async Task A_version_that_cannot_be_exported_asks_nothing_of_measurement_or_content()
    {
        var measurement = new RecordingMeasurement();
        var editorial = new RecordingEditorial(NoEditorial());

        var result = await FacadeOver(BusinessOver(Document(RecipeStatus.Draft)), measurement, editorial)
            .GetMarkdownAsync(RecipeId, new RecipeMarkdownExportViewModel(Units: "metric"), Ct);

        Assert.Equal(RecipeErrorCodes.MarkdownExportNotApprovedConflict, result.Error!.Code);
        Assert.Empty(measurement.IdLookups);
        Assert.Empty(measurement.CodeLookups);
        Assert.Empty(editorial.Calls);
    }

    [Fact]
    public async Task As_written_never_consults_measurement()
    {
        var measurement = new RecordingMeasurement(Ounce, Gram);

        var result = await FacadeOver(BusinessOver(Document(ounceId: Ounce.Id)), measurement, new RecordingEditorial(NoEditorial()))
            .GetMarkdownAsync(RecipeId, new RecipeMarkdownExportViewModel(), Ct);

        Assert.True(result.Succeeded);
        Assert.Empty(measurement.IdLookups);
        Assert.Empty(measurement.CodeLookups);
        Assert.DoesNotContain("≈", result.Value!.Markdown);
    }

    [Theory]
    [InlineData("metric", new[] { "g", "ml" }, "(≈ 113 g)")]
    [InlineData("usCustomary", new[] { "oz", "cup-us" }, null)]
    public async Task A_converted_presentation_asks_for_its_fixed_target_units_and_the_lines_own(
        string units, string[] expectedCodes, string? expectedText)
    {
        var measurement = new RecordingMeasurement(Ounce, Gram, Cup, Millilitre);

        var result = await FacadeOver(BusinessOver(Document(ounceId: Ounce.Id)), measurement, new RecordingEditorial(NoEditorial()))
            .GetMarkdownAsync(RecipeId, new RecipeMarkdownExportViewModel(Units: units), Ct);

        Assert.True(result.Succeeded);
        Assert.Equal([Ounce.Id], Assert.Single(measurement.IdLookups));
        Assert.Equal(expectedCodes, Assert.Single(measurement.CodeLookups));
        if (expectedText is not null)
        {
            Assert.Contains(expectedText, result.Value!.Markdown);
        }
        else
        {
            // Already in the requested system, so left exactly as the creator wrote it.
            Assert.DoesNotContain("≈", result.Value!.Markdown);
        }
    }

    [Fact]
    public async Task A_target_unit_the_catalogue_lacks_leaves_the_line_as_written_with_a_warning()
    {
        // Only the source unit exists; no gram to convert to.
        var measurement = new RecordingMeasurement(Ounce);

        var result = await FacadeOver(BusinessOver(Document(ounceId: Ounce.Id)), measurement, new RecordingEditorial(NoEditorial()))
            .GetMarkdownAsync(RecipeId, new RecipeMarkdownExportViewModel(Units: "metric"), Ct);

        Assert.Contains("- 4 oz butter\n", result.Value!.Markdown);
        Assert.Contains(result.Value.Warnings, warning => warning.Code == RecipeMarkdownCodes.UnitNotConverted);
    }

    [Fact]
    public async Task The_standard_template_asks_for_the_accepted_editorial_of_the_exact_version()
    {
        var editorial = new RecordingEditorial(OperationResult<AcceptedEditorialServiceModel?>.Success(
            new AcceptedEditorialServiceModel(2, null, "Intro.", [], [], null, [], null, IsCurrent: true)));

        var result = await FacadeOver(BusinessOver(), new RecordingMeasurement(), editorial)
            .GetMarkdownAsync(RecipeId, new RecipeMarkdownExportViewModel(EditorialRevision: 2), Ct);

        Assert.Equal([(RecipeId, VersionId, (int?)2)], editorial.Calls);
        Assert.Contains("## Introduction (editorial)", result.Value!.Markdown);
    }

    [Fact]
    public async Task The_compact_template_neither_reads_editorial_nor_refuses_a_revision_it_would_not_use()
    {
        var editorial = new RecordingEditorial(NoEditorial());

        var result = await FacadeOver(BusinessOver(), new RecordingMeasurement(), editorial)
            .GetMarkdownAsync(RecipeId, new RecipeMarkdownExportViewModel(Template: "compact", EditorialRevision: 9), Ct);

        Assert.True(result.Succeeded);
        Assert.Empty(editorial.Calls);
    }

    [Fact]
    public async Task An_editorial_revision_that_is_not_found_stops_rendering()
    {
        var notFound = OperationResult<AcceptedEditorialServiceModel?>.Failure(new OperationError(
            ContentErrorCodes.RevisionNotFound, "No such revision.", new Dictionary<string, string[]>()));

        var result = await FacadeOver(BusinessOver(), new RecordingMeasurement(), new RecordingEditorial(notFound))
            .GetMarkdownAsync(RecipeId, new RecipeMarkdownExportViewModel(EditorialRevision: 5), Ct);

        Assert.Equal(ContentErrorCodes.RevisionNotFound, result.Error!.Code);
    }

    [Fact]
    public void The_fixed_target_units_are_documented_by_code()
    {
        Assert.Equal(["g", "ml"], RecipeExportTargetUnits.CodesFor(RecipeUnitPresentation.Metric));
        Assert.Equal(["oz", "cup-us"], RecipeExportTargetUnits.CodesFor(RecipeUnitPresentation.UsCustomary));
        Assert.Empty(RecipeExportTargetUnits.CodesFor(RecipeUnitPresentation.AsWritten));
    }
}
