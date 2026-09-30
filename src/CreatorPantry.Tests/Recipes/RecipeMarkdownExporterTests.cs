using System.Runtime.CompilerServices;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// Golden-file and negative tests for <see cref="RecipeMarkdownExporter"/>. Regenerate the goldens with
/// <c>UPDATE_MARKDOWN_GOLDEN=1</c> and read the diff before committing it.
/// </summary>
public sealed class RecipeMarkdownExporterTests
{
    private const string GoldenDirectory = "MarkdownGolden";

    private static readonly MeasurementUnitServiceModel Gram =
        new(Guid.NewGuid(), "g", "gram", "grams", "g", MeasurementDimension.Mass, MeasurementSystem.Metric, 1m, 0);

    private static readonly MeasurementUnitServiceModel Ounce =
        new(Guid.NewGuid(), "oz", "ounce", "ounces", "oz", MeasurementDimension.Mass, MeasurementSystem.UsCustomary, 28.349523125m, 1);

    private static readonly MeasurementUnitServiceModel Millilitre =
        new(Guid.NewGuid(), "ml", "millilitre", "millilitres", "ml", MeasurementDimension.Volume, MeasurementSystem.Metric, 1m, 0);

    private static readonly MeasurementUnitServiceModel Cup =
        new(Guid.NewGuid(), "cup", "cup", "cups", "cup", MeasurementDimension.Volume, MeasurementSystem.UsCustomary, 236.588236m, 2);

    private static readonly MeasurementUnitServiceModel Piece =
        new(Guid.NewGuid(), "piece", "piece", "pieces", "pc", MeasurementDimension.Count, MeasurementSystem.Neutral, null, 0);

    private static readonly IReadOnlyDictionary<Guid, MeasurementUnitServiceModel> Units =
        new[] { Gram, Ounce, Millilitre, Cup, Piece }.ToDictionary(u => u.Id);

    private static IReadOnlyDictionary<MeasurementDimension, MeasurementUnitServiceModel> MetricTargets =>
        new Dictionary<MeasurementDimension, MeasurementUnitServiceModel>
        {
            [MeasurementDimension.Mass] = Gram,
            [MeasurementDimension.Volume] = Millilitre,
        };

    private static IReadOnlyDictionary<MeasurementDimension, MeasurementUnitServiceModel> UsTargets =>
        new Dictionary<MeasurementDimension, MeasurementUnitServiceModel>
        {
            [MeasurementDimension.Mass] = Ounce,
            [MeasurementDimension.Volume] = Cup,
        };

    private static RecipeSnapshotIngredient Line(
        string text, int order = 0, decimal? quantity = null, decimal? upper = null,
        MeasurementUnitServiceModel? unit = null, bool optional = false, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(), SortOrder = order, DisplayText = text, Quantity = quantity,
        QuantityUpper = upper, MeasurementUnitId = unit?.Id, MeasurementUnitDimension = unit?.Dimension,
        IsOptional = optional,
    };

    private static RecipeSnapshotIngredientGroup Group(string? title, params RecipeSnapshotIngredient[] lines) =>
        new() { Id = Guid.NewGuid(), Title = title, Ingredients = lines };

    private static RecipeSnapshotInstructionGroup Steps(string? title, params string[] steps) => new()
    {
        Id = Guid.NewGuid(), Title = title,
        Steps = steps.Select((t, i) => new RecipeSnapshotInstructionStep { Id = Guid.NewGuid(), SortOrder = i, Text = t }).ToList(),
    };

    private static RecipeSnapshotDocument Doc(
        RecipeSnapshotHeader? header = null,
        IReadOnlyList<RecipeSnapshotIngredientGroup>? ingredients = null,
        IReadOnlyList<RecipeSnapshotInstructionGroup>? instructions = null,
        IReadOnlyList<RecipeSnapshotEquipment>? equipment = null) => new()
    {
        SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
        Recipe = header ?? new RecipeSnapshotHeader { Title = "Soda Bread", Status = RecipeStatus.Approved },
        IngredientGroups = ingredients ?? [Group(null, Line("500 g flour"), Line("1 tsp salt", 1))],
        InstructionGroups = instructions ?? [Steps(null, "Mix.", "Bake.")],
        Equipment = equipment ?? [],
    };

    private static RecipeMarkdownInput Input(
        RecipeSnapshotDocument doc,
        RecipeExportTemplate template = RecipeExportTemplate.Standard,
        RecipeUnitPresentation units = RecipeUnitPresentation.AsWritten,
        RecipeExportEditorial? editorial = null,
        RecipeVersionReadiness readiness = RecipeVersionReadiness.Draft,
        IReadOnlyDictionary<MeasurementDimension, MeasurementUnitServiceModel>? targets = null) => new()
    {
        Snapshot = doc, VersionNumber = 3, Template = template, UnitPresentation = units, Editorial = editorial,
        Readiness = readiness, UnitsById = Units,
        TargetUnits = targets ?? (units == RecipeUnitPresentation.UsCustomary ? UsTargets : MetricTargets),
    };

    // ---- Golden files ----

    [Fact]
    public void Complete_standard_export_matches_golden()
    {
        var flourId = Guid.NewGuid();
        var doc = Doc(
            new RecipeSnapshotHeader
            {
                Title = "Buttermilk Soda Bread", Status = RecipeStatus.Approved,
                Description = "A dense, tangy loaf.", Headnote = "Adapted from my grandmother's table.",
                AttributionText = "Maeve Byrne", SourceUrl = "https://example.com/soda-bread",
                PrepTimeMinutes = 15, CookTimeMinutes = 45, TotalTimeMinutes = 90,
                YieldText = "Makes 2 loaves", Notes = "Do not overmix.", StorageNotes = "Keeps 2 days.",
            },
            [
                Group("Dry", Line("500 g plain flour", 0, id: flourId), Line("1 tsp salt", 1)),
                Group("Wet", Line("400 ml buttermilk", 0), Line("1 egg", 1, optional: true)),
            ],
            [Steps("Dough", "Mix the dry ingredients.", "Add the buttermilk."), Steps("Bake", "Bake until hollow-sounding.")],
            [
                new RecipeSnapshotEquipment { Id = Guid.NewGuid(), SortOrder = 0, DisplayText = "Dutch oven" },
                new RecipeSnapshotEquipment { Id = Guid.NewGuid(), SortOrder = 1, DisplayText = "Pastry brush", IsOptional = true },
            ]);

        var report = RecipeMarkdownExporter.Export(Input(doc, editorial: new RecipeExportEditorial
        {
            IsCurrent = true,
            Headnote = "Simple and forgiving.",
            Introduction = "No yeast needed.",
            Tips = ["Use cold buttermilk."],
            Substitutions = [new(flourId, "Use wholemeal flour", "Denser crumb.")],
            StorageReheating = "Reheat in a hot oven.",
            Faq = [new("Can I freeze it?", "Yes, sliced.")],
            Cta = "Tag us when you bake it!",
        }));

        Assert.True(report.IsComplete);
        Assert.Empty(report.Warnings);
        AssertGolden("complete", report.Markdown!);
    }

    [Fact]
    public void Minimal_export_matches_golden_and_warns_about_omissions()
    {
        var report = RecipeMarkdownExporter.Export(Input(Doc()));

        Assert.True(report.IsComplete);
        AssertGolden("minimal", report.Markdown!);
        Assert.Equal(
            [RecipeMarkdownCodes.TimesMissing, RecipeMarkdownCodes.YieldMissing],
            report.Warnings.Select(w => w.Code));
    }

    [Fact]
    public void Compact_template_drops_description_equipment_and_editorial()
    {
        var doc = Doc(
            new RecipeSnapshotHeader
            {
                Title = "Soda Bread", Status = RecipeStatus.Approved, Description = "Hidden.", CookTimeMinutes = 45,
                ServingCount = 12,
            },
            equipment: [new RecipeSnapshotEquipment { Id = Guid.NewGuid(), DisplayText = "Dutch oven" }]);

        var report = RecipeMarkdownExporter.Export(Input(
            doc, RecipeExportTemplate.Compact,
            editorial: new RecipeExportEditorial { IsCurrent = true, Introduction = "Hidden intro." }));

        Assert.DoesNotContain("Hidden", report.Markdown);
        Assert.DoesNotContain("Equipment", report.Markdown);
        AssertGolden("compact", report.Markdown!);
    }

    [Fact]
    public void Special_characters_are_escaped_and_cannot_become_structure()
    {
        var doc = Doc(
            new RecipeSnapshotHeader
            {
                Title = "# Crème *brûlée* <b>hi</b>", Status = RecipeStatus.Approved,
                Description = "- not a list\n> not a quote", AttributionText = "[click](http://evil.example)",
                SourceUrl = "javascript:alert(1)", YieldText = "2 | 3 & <cups>",
            },
            [Group(null, Line("- 2 × œufs & `1` cup <cream>"), Line("1. first"), Line("---", 1), Line("![x](http://e/x.png)", 2))],
            [Steps(null, "Whisk.\nThen chill — overnight.\n\n## Not a heading", "<script>alert(1)</script>")]);

        var report = RecipeMarkdownExporter.Export(Input(doc));

        Assert.True(report.IsComplete);
        var markdown = report.Markdown!;
        Assert.DoesNotContain("<script>", markdown);
        Assert.DoesNotContain("<b>", markdown);
        Assert.DoesNotContain("](http", markdown.Replace("\\](http", string.Empty));
        Assert.DoesNotContain("\n## Not a heading", markdown);
        Assert.DoesNotContain("<javascript", markdown);
        AssertGolden("special-characters", markdown);
    }

    [Fact]
    public void Metric_presentation_appends_converted_quantities_matches_golden()
    {
        var doc = Doc(ingredients:
        [
            Group(null,
                Line("4 oz butter", 0, 4m, unit: Ounce),
                Line("1 cup milk", 1, 1m, unit: Cup),
                Line("1 to 2 cups water", 2, 1m, 2m, unit: Cup),
                Line("500 g flour", 3, 500m, unit: Gram),
                Line("2 eggs", 4, 2m, unit: Piece),
                Line("salt to taste", 5)),
        ]);

        var report = RecipeMarkdownExporter.Export(Input(doc, units: RecipeUnitPresentation.Metric));

        Assert.True(report.IsComplete);
        Assert.Empty(report.Warnings.Where(w => w.Code == RecipeMarkdownCodes.UnitNotConverted));
        AssertGolden("alternate-unit-metric", report.Markdown!);
    }

    [Fact]
    public void Us_presentation_appends_converted_quantities_matches_golden()
    {
        var doc = Doc(ingredients:
        [
            Group(null,
                Line("250 g butter", 0, 250m, unit: Gram),
                Line("350 ml milk", 1, 350m, unit: Millilitre),
                Line("1 cup milk", 2, 1m, unit: Cup)),
        ]);

        var report = RecipeMarkdownExporter.Export(Input(doc, units: RecipeUnitPresentation.UsCustomary));

        Assert.True(report.IsComplete);
        AssertGolden("alternate-unit-us", report.Markdown!);
    }

    [Fact]
    public void A_line_that_cannot_be_converted_stays_as_written_with_a_warning()
    {
        var unknown = Guid.NewGuid();
        var doc = Doc(ingredients:
        [
            Group(null,
                new RecipeSnapshotIngredient
                {
                    Id = Guid.NewGuid(), DisplayText = "3 mystery", Quantity = 3m, MeasurementUnitId = unknown,
                    MeasurementUnitDimension = MeasurementDimension.Mass,
                },
                Line("100 g sugar", 1, 100m, unit: Gram)),
        ]);

        var report = RecipeMarkdownExporter.Export(Input(
            doc, units: RecipeUnitPresentation.UsCustomary,
            targets: new Dictionary<MeasurementDimension, MeasurementUnitServiceModel>()));

        Assert.Contains("- 3 mystery\n", report.Markdown);
        Assert.Contains("- 100 g sugar\n", report.Markdown);
        Assert.Equal(2, report.Warnings.Count(w => w.Code == RecipeMarkdownCodes.UnitNotConverted));
    }

    [Fact]
    public void As_written_never_converts()
    {
        var doc = Doc(ingredients: [Group(null, Line("4 oz butter", 0, 4m, unit: Ounce))]);

        var report = RecipeMarkdownExporter.Export(Input(doc));

        Assert.DoesNotContain("≈", report.Markdown);
    }

    // ---- Gates and report ----

    [Fact]
    public void Output_is_deterministic()
    {
        var input = Input(Doc(), units: RecipeUnitPresentation.Metric);

        Assert.Equal(RecipeMarkdownExporter.Export(input).Markdown, RecipeMarkdownExporter.Export(input).Markdown);
    }

    [Fact]
    public void Each_required_fact_missing_is_reported_and_no_markdown_is_produced()
    {
        var doc = Doc(
            new RecipeSnapshotHeader { Title = " ", Status = RecipeStatus.Approved },
            [Group(null, Line(" "))],
            [Steps(null, "")]);

        var report = RecipeMarkdownExporter.Export(Input(doc));

        Assert.Null(report.Markdown);
        Assert.Equal(
            [RecipeMarkdownCodes.NameMissing, RecipeMarkdownCodes.IngredientsMissing, RecipeMarkdownCodes.InstructionsMissing],
            report.MissingRequired.Select(i => i.Code));
    }

    [Theory]
    [InlineData(RecipeStatus.Draft)]
    [InlineData(RecipeStatus.InDevelopment)]
    [InlineData(RecipeStatus.Testing)]
    [InlineData(RecipeStatus.ReadyForReview)]
    [InlineData(RecipeStatus.Archived)]
    public void An_unapproved_recipe_is_refused(RecipeStatus status)
    {
        var doc = Doc(new RecipeSnapshotHeader { Title = "Soda Bread", Status = status });

        var report = RecipeMarkdownExporter.Export(Input(doc));

        Assert.Null(report.Markdown);
        Assert.Equal(RecipeMarkdownCodes.RecipeNotApproved, Assert.Single(report.MissingRequired).Code);
    }

    [Fact]
    public void A_ready_version_of_an_unapproved_recipe_is_accepted()
    {
        var doc = Doc(new RecipeSnapshotHeader { Title = "Soda Bread", Status = RecipeStatus.Draft });

        Assert.True(RecipeMarkdownExporter.Export(Input(doc, readiness: RecipeVersionReadiness.Ready)).IsComplete);
    }

    [Fact]
    public void A_stale_editorial_revision_is_ignored_with_a_warning()
    {
        var report = RecipeMarkdownExporter.Export(Input(
            Doc(), editorial: new RecipeExportEditorial { IsCurrent = false, Introduction = "Stale intro." }));

        Assert.DoesNotContain("Stale intro.", report.Markdown);
        Assert.Contains(report.Warnings, w => w.Code == RecipeMarkdownCodes.EditorialNotCurrent);
    }

    [Fact]
    public void A_substitution_for_a_line_not_in_the_version_is_skipped_with_a_warning()
    {
        var report = RecipeMarkdownExporter.Export(Input(
            Doc(), editorial: new RecipeExportEditorial
            {
                IsCurrent = true,
                Substitutions = [new(Guid.NewGuid(), "Use oat flour", "Nutty.")],
            }));

        Assert.DoesNotContain("oat flour", report.Markdown);
        Assert.Contains(report.Warnings, w => w.Code == RecipeMarkdownCodes.SubstitutionUnmatched);
    }

    [Fact]
    public void Editorial_copy_is_always_marked_and_no_food_claims_are_added()
    {
        var report = RecipeMarkdownExporter.Export(Input(
            Doc(), editorial: new RecipeExportEditorial { IsCurrent = true, Introduction = "Hello.", Tips = ["A tip."] }));

        Assert.Contains("## Introduction (editorial)", report.Markdown);
        Assert.Contains("## Tips (editorial)", report.Markdown);
        foreach (var word in new[] { "allergen", "nutrition", "calorie", "gluten-free", "vegan", "safe" })
        {
            Assert.DoesNotContain(word, report.Markdown, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- Golden plumbing ----

    private static void AssertGolden(string name, string actual, [CallerFilePath] string sourceFile = "")
    {
        var normalized = actual.ReplaceLineEndings("\n");

        if (Environment.GetEnvironmentVariable("UPDATE_MARKDOWN_GOLDEN") == "1")
        {
            var path = Path.Combine(Path.GetDirectoryName(sourceFile)!, GoldenDirectory, $"{name}.golden.md");
            File.WriteAllText(path, normalized);
            return;
        }

        var assembly = typeof(RecipeMarkdownExporterTests).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith($"{GoldenDirectory}.{name}.golden.md", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        Assert.Equal(reader.ReadToEnd().ReplaceLineEndings("\n"), normalized);
    }
}
