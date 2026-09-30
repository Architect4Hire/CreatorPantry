using System.Runtime.CompilerServices;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// Golden-file and negative tests for <see cref="RecipeJsonLdGenerator"/>. Regenerate the goldens with
/// <c>UPDATE_JSONLD_GOLDEN=1</c> and read the diff before committing it.
/// </summary>
public sealed class RecipeJsonLdGeneratorTests
{
    private const string GoldenDirectory = "JsonLdGolden";

    private static RecipeSnapshotDocument Doc(
        string? title = "Soda Bread",
        RecipeStatus status = RecipeStatus.Approved,
        Action<RecipeSnapshotHeaderBuilder>? header = null,
        IReadOnlyList<RecipeSnapshotIngredientGroup>? ingredients = null,
        IReadOnlyList<RecipeSnapshotInstructionGroup>? instructions = null,
        IReadOnlyList<RecipeSnapshotEquipment>? equipment = null)
    {
        var b = new RecipeSnapshotHeaderBuilder { Title = title!, Status = status };
        header?.Invoke(b);
        return new RecipeSnapshotDocument
        {
            SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
            Recipe = b.Build(),
            IngredientGroups = ingredients ?? [IngredientGroup(null, "500 g flour", "1 tsp salt")],
            InstructionGroups = instructions ?? [InstructionGroup(null, "Mix.", "Bake.")],
            Equipment = equipment ?? [],
        };
    }

    private sealed class RecipeSnapshotHeaderBuilder
    {
        public string Title { get; set; } = "";
        public RecipeStatus Status { get; set; }
        public string? Description { get; set; }
        public string? AttributionText { get; set; }
        public Guid? CuisineId { get; set; }
        public Guid? CourseId { get; set; }
        public int? PrepTimeMinutes { get; set; }
        public int? CookTimeMinutes { get; set; }
        public int? RestTimeMinutes { get; set; }
        public int? TotalTimeMinutes { get; set; }
        public string? YieldText { get; set; }
        public decimal? ServingCount { get; set; }
        public string? Headnote { get; set; }

        public RecipeSnapshotHeader Build() => new()
        {
            Title = Title, Status = Status, Description = Description, AttributionText = AttributionText,
            CuisineId = CuisineId, CourseId = CourseId, PrepTimeMinutes = PrepTimeMinutes,
            CookTimeMinutes = CookTimeMinutes, RestTimeMinutes = RestTimeMinutes,
            TotalTimeMinutes = TotalTimeMinutes, YieldText = YieldText, ServingCount = ServingCount,
            Headnote = Headnote,
        };
    }

    private static RecipeSnapshotIngredientGroup IngredientGroup(string? title, params string[] lines) => new()
    {
        Id = Guid.NewGuid(), Title = title,
        Ingredients = lines.Select((t, i) => new RecipeSnapshotIngredient
            { Id = Guid.NewGuid(), SortOrder = i, DisplayText = t }).ToList(),
    };

    private static RecipeSnapshotInstructionGroup InstructionGroup(string? title, params string[] steps) => new()
    {
        Id = Guid.NewGuid(), Title = title,
        Steps = steps.Select((t, i) => new RecipeSnapshotInstructionStep
            { Id = Guid.NewGuid(), SortOrder = i, Text = t }).ToList(),
    };

    private static RecipeJsonLdInput Input(
        RecipeSnapshotDocument doc,
        string? image = "https://cdn.example.com/soda-bread.jpg",
        RecipeJsonLdEditorial? editorial = null,
        string? cuisine = null,
        string? course = null,
        RecipeVersionReadiness readiness = RecipeVersionReadiness.Draft) =>
        new() { Snapshot = doc, ImageUrl = image, Editorial = editorial, CuisineName = cuisine, CourseName = course, Readiness = readiness };

    // ---- Golden files ----

    [Fact]
    public void Complete_recipe_matches_golden()
    {
        var doc = Doc(
            header: h =>
            {
                h.Description = "Stored description.";
                h.AttributionText = "Maeve Byrne";
                h.CuisineId = Guid.NewGuid();
                h.CourseId = Guid.NewGuid();
                h.PrepTimeMinutes = 15;
                h.CookTimeMinutes = 45;
                h.TotalTimeMinutes = 90;
                h.YieldText = "Makes 2 loaves";
                h.ServingCount = 12;
                h.Headnote = "A headnote that must never appear.";
            },
            ingredients:
            [
                IngredientGroup(null, "500 g plain flour"),
            ],
            instructions:
            [
                InstructionGroup("Dough", "Mix the dry ingredients.", "Add the buttermilk."),
                InstructionGroup("Bake", "Bake until hollow-sounding."),
            ],
            equipment:
            [
                new RecipeSnapshotEquipment { Id = Guid.NewGuid(), SortOrder = 0, DisplayText = "Dutch oven" },
                new RecipeSnapshotEquipment { Id = Guid.NewGuid(), SortOrder = 1, DisplayText = "Pastry brush", IsOptional = true },
            ]);

        var report = RecipeJsonLdGenerator.Generate(Input(
            doc,
            editorial: new("A dense, tangy loaf.", ["soda bread", "Soda Bread", "no yeast"], IsCurrent: true),
            cuisine: "Irish",
            course: "Bread"));

        Assert.True(report.IsComplete);
        AssertGolden("complete", report.Json!);
    }

    [Fact]
    public void Minimal_recipe_matches_golden_and_warns_about_omissions()
    {
        var report = RecipeJsonLdGenerator.Generate(Input(Doc()));

        Assert.True(report.IsComplete);
        AssertGolden("minimal", report.Json!);
        Assert.Equal(
            [RecipeJsonLdCodes.TimesMissing, RecipeJsonLdCodes.YieldMissing],
            report.Warnings.Select(w => w.Code));
    }

    [Fact]
    public void Special_characters_are_escaped_and_preserved_verbatim()
    {
        var doc = Doc(
            title: "Crème brûlée \"Classic\" </script>",
            ingredients: [IngredientGroup(null, "2 × œufs & 1 cup <cream>", "  ½ tsp vanilla  ")],
            instructions: [InstructionGroup(null, "Whisk.\nThen chill — overnight.")]);

        var report = RecipeJsonLdGenerator.Generate(Input(doc));

        Assert.True(report.IsComplete);
        Assert.DoesNotContain("</script>", report.Json);
        using var parsed = JsonDocument.Parse(report.Json!);
        Assert.Equal("Crème brûlée \"Classic\" </script>", parsed.RootElement.GetProperty("name").GetString());
        Assert.Equal("½ tsp vanilla", parsed.RootElement.GetProperty("recipeIngredient")[1].GetString());
        AssertGolden("special-characters", report.Json!);
    }

    [Fact]
    public void Output_is_deterministic()
    {
        var input = Input(Doc(header: h => h.CookTimeMinutes = 30));

        Assert.Equal(RecipeJsonLdGenerator.Generate(input).Json, RecipeJsonLdGenerator.Generate(input).Json);
    }

    // ---- Missing and invalid data ----

    [Fact]
    public void Each_required_fact_missing_is_reported_and_no_json_is_produced()
    {
        var doc = Doc(title: "  ", ingredients: [IngredientGroup(null, " ")], instructions: [InstructionGroup(null, "")]);

        var report = RecipeJsonLdGenerator.Generate(Input(doc, image: null));

        Assert.Null(report.Json);
        Assert.False(report.IsComplete);
        Assert.Equal(
            [
                RecipeJsonLdCodes.NameMissing, RecipeJsonLdCodes.IngredientsMissing,
                RecipeJsonLdCodes.InstructionsMissing, RecipeJsonLdCodes.ImageMissing,
            ],
            report.MissingRequired.Select(i => i.Code));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path.jpg")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/a.jpg")]
    public void A_non_http_image_is_invalid(string image)
    {
        var report = RecipeJsonLdGenerator.Generate(Input(Doc(), image));

        Assert.Null(report.Json);
        Assert.Equal(RecipeJsonLdCodes.ImageInvalid, Assert.Single(report.MissingRequired).Code);
    }

    [Theory]
    [InlineData(RecipeStatus.Draft)]
    [InlineData(RecipeStatus.InDevelopment)]
    [InlineData(RecipeStatus.Testing)]
    [InlineData(RecipeStatus.ReadyForReview)]
    [InlineData(RecipeStatus.Archived)]
    public void An_unapproved_recipe_is_refused(RecipeStatus status)
    {
        var report = RecipeJsonLdGenerator.Generate(Input(Doc(status: status)));

        Assert.Null(report.Json);
        Assert.Equal(RecipeJsonLdCodes.RecipeNotApproved, Assert.Single(report.MissingRequired).Code);
    }

    [Fact]
    public void A_ready_version_of_an_unapproved_recipe_is_accepted()
    {
        var report = RecipeJsonLdGenerator.Generate(
            Input(Doc(status: RecipeStatus.Draft), readiness: RecipeVersionReadiness.Ready));

        Assert.True(report.IsComplete);
    }

    [Fact]
    public void A_stale_editorial_revision_is_ignored_with_a_warning()
    {
        var doc = Doc(header: h => h.Description = "Stored description.");

        var report = RecipeJsonLdGenerator.Generate(Input(
            doc, editorial: new("Stale meta.", ["stale"], IsCurrent: false)));

        using var parsed = JsonDocument.Parse(report.Json!);
        Assert.Equal("Stored description.", parsed.RootElement.GetProperty("description").GetString());
        Assert.False(parsed.RootElement.TryGetProperty("keywords", out _));
        Assert.Contains(report.Warnings, w => w.Code == RecipeJsonLdCodes.EditorialNotCurrent);
    }

    [Fact]
    public void An_unresolved_cuisine_is_omitted_with_a_warning()
    {
        var report = RecipeJsonLdGenerator.Generate(Input(Doc(header: h => h.CuisineId = Guid.NewGuid())));

        using var parsed = JsonDocument.Parse(report.Json!);
        Assert.False(parsed.RootElement.TryGetProperty("recipeCuisine", out _));
        Assert.Contains(report.Warnings, w => w.Code == RecipeJsonLdCodes.CuisineUnresolved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_times_are_omitted(int minutes)
    {
        var report = RecipeJsonLdGenerator.Generate(Input(Doc(header: h =>
        {
            h.PrepTimeMinutes = minutes;
            h.CookTimeMinutes = minutes;
            h.TotalTimeMinutes = minutes;
        })));

        using var parsed = JsonDocument.Parse(report.Json!);
        Assert.False(parsed.RootElement.TryGetProperty("prepTime", out _));
        Assert.False(parsed.RootElement.TryGetProperty("cookTime", out _));
        Assert.False(parsed.RootElement.TryGetProperty("totalTime", out _));
    }

    [Theory]
    [InlineData(45, "PT45M")]
    [InlineData(60, "PT1H")]
    [InlineData(90, "PT1H30M")]
    [InlineData(1500, "PT25H")]
    public void Times_are_iso_8601_durations(int minutes, string expected)
    {
        var report = RecipeJsonLdGenerator.Generate(Input(Doc(header: h => h.CookTimeMinutes = minutes)));

        using var parsed = JsonDocument.Parse(report.Json!);
        Assert.Equal(expected, parsed.RootElement.GetProperty("cookTime").GetString());
    }

    [Fact]
    public void Total_time_is_never_summed_and_rest_time_is_not_mapped()
    {
        var report = RecipeJsonLdGenerator.Generate(Input(Doc(header: h =>
        {
            h.PrepTimeMinutes = 10;
            h.CookTimeMinutes = 20;
            h.RestTimeMinutes = 30;
        })));

        using var parsed = JsonDocument.Parse(report.Json!);
        Assert.False(parsed.RootElement.TryGetProperty("totalTime", out _));
    }

    [Theory]
    [InlineData(1, "1 serving")]
    [InlineData(12, "12 servings")]
    [InlineData(2.5, "2.5 servings")]
    public void Servings_alone_produce_a_serving_yield(double servings, string expected)
    {
        var report = RecipeJsonLdGenerator.Generate(Input(Doc(header: h => h.ServingCount = (decimal)servings)));

        using var parsed = JsonDocument.Parse(report.Json!);
        Assert.Equal(expected, parsed.RootElement.GetProperty("recipeYield").GetString());
    }

    [Fact]
    public void The_creators_yield_wording_wins_over_the_serving_count()
    {
        var report = RecipeJsonLdGenerator.Generate(Input(Doc(header: h =>
        {
            h.YieldText = "Makes 2 loaves";
            h.ServingCount = 12;
        })));

        using var parsed = JsonDocument.Parse(report.Json!);
        Assert.Equal("Makes 2 loaves", parsed.RootElement.GetProperty("recipeYield").GetString());
    }

    [Fact]
    public void Facts_the_model_cannot_vouch_for_are_never_emitted()
    {
        var report = RecipeJsonLdGenerator.Generate(Input(
            Doc(header: h => h.Headnote = "Headnote"),
            editorial: new("Meta.", ["a"], IsCurrent: true)));

        using var parsed = JsonDocument.Parse(report.Json!);
        string[] forbidden =
            ["nutrition", "aggregateRating", "review", "suitableForDiet", "video", "url", "datePublished"];
        foreach (var property in forbidden)
        {
            Assert.False(parsed.RootElement.TryGetProperty(property, out _), property);
        }
    }

    // ---- Golden plumbing ----

    private static void AssertGolden(string name, string actual, [CallerFilePath] string sourceFile = "")
    {
        var normalized = actual.ReplaceLineEndings("\n") + "\n";

        if (Environment.GetEnvironmentVariable("UPDATE_JSONLD_GOLDEN") == "1")
        {
            var path = Path.Combine(Path.GetDirectoryName(sourceFile)!, GoldenDirectory, $"{name}.golden.json");
            File.WriteAllText(path, normalized);
            return;
        }

        var assembly = typeof(RecipeJsonLdGeneratorTests).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith($"{GoldenDirectory}.{name}.golden.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        Assert.Equal(reader.ReadToEnd().ReplaceLineEndings("\n"), normalized);
    }
}
