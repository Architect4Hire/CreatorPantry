using System.IO.Compression;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// Text and structure assertions for <see cref="RecipePdfRenderer"/>. PDF/UA output is not byte-reproducible,
/// so nothing here compares files. Set <c>CP_PDF_FIXTURE_DIR</c> to also write each rendered fixture there for
/// visual inspection.
/// </summary>
public sealed class RecipePdfRendererTests
{
    private static readonly DateTimeOffset Created = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static readonly MeasurementUnitServiceModel Gram =
        new(Guid.NewGuid(), "g", "gram", "grams", "g", MeasurementDimension.Mass, MeasurementSystem.Metric, 1m, 0);

    private static readonly MeasurementUnitServiceModel Ounce =
        new(Guid.NewGuid(), "oz", "ounce", "ounces", "oz", MeasurementDimension.Mass, MeasurementSystem.UsCustomary, 28.349523125m, 1);

    private static readonly MeasurementUnitServiceModel Millilitre =
        new(Guid.NewGuid(), "ml", "millilitre", "millilitres", "ml", MeasurementDimension.Volume, MeasurementSystem.Metric, 1m, 0);

    private static readonly MeasurementUnitServiceModel Cup =
        new(Guid.NewGuid(), "cup", "cup", "cups", "cup", MeasurementDimension.Volume, MeasurementSystem.UsCustomary, 236.588236m, 2);

    private static readonly IReadOnlyDictionary<Guid, MeasurementUnitServiceModel> Units =
        new[] { Gram, Ounce, Millilitre, Cup }.ToDictionary(u => u.Id);

    private static readonly IReadOnlyDictionary<MeasurementDimension, MeasurementUnitServiceModel> MetricTargets =
        new Dictionary<MeasurementDimension, MeasurementUnitServiceModel>
        {
            [MeasurementDimension.Mass] = Gram,
            [MeasurementDimension.Volume] = Millilitre,
        };

    // ---- Fixtures ----

    private static RecipeSnapshotIngredient Line(
        string text, int order, decimal? quantity = null, MeasurementUnitServiceModel? unit = null, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(), SortOrder = order, DisplayText = text, Quantity = quantity,
        MeasurementUnitId = unit?.Id, MeasurementUnitDimension = unit?.Dimension,
    };

    private static RecipeSnapshotIngredientGroup Group(string? title, params RecipeSnapshotIngredient[] lines) =>
        new() { Id = Guid.NewGuid(), Title = title, Ingredients = lines };

    private static RecipeSnapshotInstructionGroup Steps(string? title, params string[] steps) => new()
    {
        Id = Guid.NewGuid(), Title = title,
        Steps = steps.Select((t, i) => new RecipeSnapshotInstructionStep
            { Id = Guid.NewGuid(), SortOrder = i, Text = t, Note = i == 0 ? "Work quickly." : null }).ToList(),
    };

    private static RecipeSnapshotDocument Short() => new()
    {
        SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
        Recipe = new RecipeSnapshotHeader { Title = "Soda Bread", Status = RecipeStatus.Approved },
        IngredientGroups = [Group(null, Line("500 g flour", 0), Line("1 tsp salt", 1))],
        InstructionGroups = [Steps(null, "Mix.", "Bake.")],
    };

    private static RecipeSnapshotDocument Long(Guid flourId)
    {
        var dry = Enumerable.Range(0, 28)
            .Select(i => Line($"{100 + i} g ingredient number {i + 1}, finely chopped and lightly toasted", i, 100 + i, Gram))
            .Append(Line("500 g plain flour", 99, 500, Gram, flourId))
            .ToArray();
        var steps = Enumerable.Range(1, 24)
            .Select(i => $"Step {i}: combine the mixture carefully and rest it for {i} minutes before continuing, "
                + "making sure nothing sticks to the sides of the bowl or the work surface.")
            .ToArray();
        return new RecipeSnapshotDocument
        {
            SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
            Recipe = new RecipeSnapshotHeader
            {
                Title = "Buttermilk Soda Bread With Toasted Seeds And Honey",
                Status = RecipeStatus.Approved,
                Description = "A dense, tangy loaf with a crisp crust and a soft crumb.",
                Headnote = "Adapted from my grandmother's table.",
                AttributionText = "Maeve Byrne", SourceUrl = "https://example.com/soda-bread",
                PrepTimeMinutes = 15, CookTimeMinutes = 45, TotalTimeMinutes = 90,
                YieldText = "Makes 2 loaves", Notes = "Do not overmix the dough.", StorageNotes = "Keeps for two days.",
            },
            IngredientGroups = [Group("Dry", dry), Group("Wet", Line("400 ml buttermilk", 0, 400, Millilitre), Line("1 cup milk", 1, 1, Cup))],
            InstructionGroups = [Steps("Dough", steps.Take(12).ToArray()), Steps("Bake", steps.Skip(12).ToArray())],
            Equipment = [new RecipeSnapshotEquipment { Id = Guid.NewGuid(), DisplayText = "Dutch oven" }],
        };
    }

    private static RecipeSnapshotDocument Special() => new()
    {
        SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
        Recipe = new RecipeSnapshotHeader
        {
            Title = "Crème brûlée « Classic » — #1 <b>", Status = RecipeStatus.Approved,
            AttributionText = "Zoë Œuvre", YieldText = "2 | 3 & <cups>",
        },
        IngredientGroups = [Group(null, Line("2 × œufs & ½ tsp vanilla", 0), Line("- 1 cup <cream> [fresh]", 1))],
        InstructionGroups = [Steps(null, "Whisk.\nThen chill — overnight.", "Serve at 180 °C (356 °F).")],
    };

    private static byte[] SolidPng(int width, int height)
    {
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 3);
            for (var x = 0; x < width; x++)
            {
                raw[row + 1 + x * 3] = (byte)(200 - 100 * y / height);
                raw[row + 2 + x * 3] = (byte)(120 + 100 * x / width);
                raw[row + 3 + x * 3] = 60;
            }
        }

        using var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);

        var ihdr = new byte[13];
        WriteInt(ihdr, 0, width);
        WriteInt(ihdr, 4, height);
        ihdr[8] = 8;
        ihdr[9] = 2;

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", idat.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteInt(byte[] b, int offset, int value)
    {
        b[offset] = (byte)(value >> 24);
        b[offset + 1] = (byte)(value >> 16);
        b[offset + 2] = (byte)(value >> 8);
        b[offset + 3] = (byte)value;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var length = new byte[4];
        WriteInt(length, 0, data.Length);
        s.Write(length);
        var typeAndData = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(typeAndData);
        var crc = new byte[4];
        WriteInt(crc, 0, unchecked((int)Crc32(typeAndData)));
        s.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return ~crc;
    }

    private static RecipePdfInput Input(
        RecipeSnapshotDocument doc,
        RecipePdfPageSize size = RecipePdfPageSize.A4,
        RecipeExportTemplate template = RecipeExportTemplate.Standard,
        RecipeUnitPresentation units = RecipeUnitPresentation.AsWritten,
        RecipeExportEditorial? editorial = null,
        RecipePdfImage? image = null,
        RecipeVersionReadiness readiness = RecipeVersionReadiness.Draft) => new()
    {
        Snapshot = doc, VersionNumber = 3, CreatedAt = Created, PageSize = size, Template = template,
        UnitPresentation = units, Editorial = editorial, Image = image, Readiness = readiness,
        UnitsById = Units, TargetUnits = MetricTargets,
    };

    private static byte[] Render(string name, RecipePdfInput input)
    {
        var report = RecipePdfRenderer.Render(input);
        Assert.True(report.IsComplete, string.Join("; ", report.MissingRequired.Select(i => i.Code)));

        if (Environment.GetEnvironmentVariable("CP_PDF_FIXTURE_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name + ".pdf"), report.Pdf!);
        }

        return report.Pdf!;
    }

    private static string AllText(PdfDocument pdf) =>
        string.Join("\n", pdf.GetPages().Select(p => p.Text));

    // ---- Content ----

    [Fact]
    public void A_short_recipe_is_one_page_of_real_text()
    {
        using var pdf = PdfDocument.Open(Render("short", Input(Short())));

        Assert.Equal(1, pdf.NumberOfPages);
        var text = pdf.GetPage(1).Text;
        Assert.Contains("Soda Bread", text);
        Assert.Contains("500 g flour", text);
        Assert.Contains("Ingredients", text);
        Assert.Contains("Instructions", text);
        Assert.Contains("Mix.", text);
        Assert.Contains("Version 3", text);
        Assert.Contains("Page 1 of 1", text);
    }

    [Fact]
    public void A_long_recipe_flows_across_pages_with_a_footer_on_each_and_keeps_every_line()
    {
        var flourId = Guid.NewGuid();
        var editorial = new RecipeExportEditorial
        {
            IsCurrent = true,
            Introduction = "No yeast needed.",
            Tips = ["Use cold buttermilk."],
            Substitutions = [new(flourId, "Use wholemeal flour", "Denser crumb.")],
            Faq = [new("Can I freeze it?", "Yes, sliced.")],
        };
        var image = new RecipePdfImage(SolidPng(600, 400), "A golden loaf of soda bread on a wooden board.");

        using var pdf = PdfDocument.Open(Render("long", Input(
            Long(flourId), units: RecipeUnitPresentation.Metric, editorial: editorial, image: image)));

        Assert.True(pdf.NumberOfPages >= 3, $"expected a multi-page document, got {pdf.NumberOfPages}");
        var pages = pdf.GetPages().ToList();
        foreach (var page in pages)
        {
            Assert.Contains($"Page {page.Number} of {pages.Count}", page.Text);
            Assert.Contains("Version 3", page.Text);
            Assert.True(page.Text.Length > 80, $"page {page.Number} is nearly empty");
        }

        var text = AllText(pdf);
        Assert.Contains("ingredient number 28", text);
        Assert.Contains("Step 24:", text);
        Assert.Contains("Introduction (editorial)", text);
        Assert.Contains("Substitutions (editorial)", text);
        Assert.Contains("Adapted from my grandmother", text);
        Assert.Contains("Maeve Byrne", text);
        Assert.Contains("Makes 2 loaves", text);
        Assert.Contains("Total: 1 h 30 min", text);
        Assert.Contains("Units: metric", text);
        Assert.Contains("≈ 237 ml", text);
        Assert.Contains("Note: Work quickly.", text);
    }

    [Fact]
    public void Page_count_is_stable_for_the_long_fixture()
    {
        var flourId = Guid.NewGuid();
        var count = PageCount(Input(Long(flourId)));

        Assert.Equal(count, PageCount(Input(Long(flourId))));
        Assert.Equal(count, PageCount(Input(Long(flourId), units: RecipeUnitPresentation.AsWritten)));
    }

    private static int PageCount(RecipePdfInput input)
    {
        using var pdf = PdfDocument.Open(Render("stability", input));
        return pdf.NumberOfPages;
    }

    [Fact]
    public void Special_characters_render_as_text_without_being_escaped()
    {
        using var pdf = PdfDocument.Open(Render("special", Input(Special())));

        var text = AllText(pdf);
        Assert.Contains("Crème brûlée", text);
        Assert.Contains("#1 <b>", text);
        Assert.Contains("œufs & ½ tsp vanilla", text);
        Assert.Contains("- 1 cup <cream> [fresh]", text);
        Assert.Contains("180 °C", text);
    }

    [Fact]
    public void Letter_and_a4_use_their_own_page_sizes()
    {
        using var a4 = PdfDocument.Open(Render("a4", Input(Short())));
        using var letter = PdfDocument.Open(Render("letter", Input(Short(), RecipePdfPageSize.Letter)));

        Assert.Equal(595, a4.GetPage(1).Width, 1);
        Assert.Equal(842, a4.GetPage(1).Height, 1);
        Assert.Equal(612, letter.GetPage(1).Width, 1);
        Assert.Equal(792, letter.GetPage(1).Height, 1);
    }

    [Fact]
    public void The_compact_template_drops_description_and_editorial()
    {
        var doc = Short() with
        {
            Recipe = Short().Recipe with { Description = "Hidden description." },
        };

        using var pdf = PdfDocument.Open(Render("compact", Input(
            doc, template: RecipeExportTemplate.Compact,
            editorial: new RecipeExportEditorial { IsCurrent = true, Introduction = "Hidden intro." })));

        var text = AllText(pdf);
        Assert.DoesNotContain("Hidden", text);
    }

    // ---- Accessibility and structure ----

    [Fact]
    public void The_document_declares_a_title_a_language_and_a_tag_tree()
    {
        using var pdf = PdfDocument.Open(Render("tags", Input(Short())));

        Assert.Equal("Soda Bread", pdf.Information.Title);

        var catalog = pdf.Structure.Catalog.CatalogDictionary;
        Assert.True(catalog.TryGet(NameToken.Lang, out var lang));
        Assert.Equal("en-US", ((StringToken)lang).Data);
        Assert.True(catalog.ContainsKey(NameToken.Create("StructTreeRoot")), "no structure tree");

        var marked = (DictionaryToken)Resolve(pdf, catalog.Data[NameToken.Create("MarkInfo")]);
        Assert.True(((BooleanToken)marked.Data[NameToken.Create("Marked")]).Data);
    }

    [Fact]
    public void Headings_lists_and_the_figure_alt_text_are_tagged()
    {
        var image = new RecipePdfImage(SolidPng(200, 120), "A golden loaf of soda bread.");
        var bytes = Render("structure", Input(Short(), image: image));

        var types = StructureTypes(bytes, out var figureAlts);

        Assert.Contains("H1", types);
        Assert.Contains("H2", types);
        Assert.Contains("L", types);
        Assert.Contains("LI", types);
        Assert.Contains("Lbl", types);
        Assert.Contains("LBody", types);
        Assert.Contains("Figure", types);
        Assert.Contains("A golden loaf of soda bread.", figureAlts);
    }

    [Fact]
    public void An_image_without_alt_text_is_decorative_not_described()
    {
        var report = RecipePdfRenderer.Render(Input(Short(), image: new RecipePdfImage(SolidPng(200, 120), "  ")));

        Assert.True(report.IsComplete);
        Assert.Contains(report.Warnings, w => w.Code == RecipePdfCodes.ImageAltMissing);

        var types = StructureTypes(report.Pdf!, out var figureAlts);
        Assert.DoesNotContain("Figure", types);
        Assert.Empty(figureAlts);
    }

    private static List<string> StructureTypes(byte[] bytes, out List<string> figureAlts)
    {
        using var pdf = PdfDocument.Open(bytes);
        var types = new List<string>();
        var alts = new List<string>();
        var root = pdf.Structure.Catalog.CatalogDictionary.Data[NameToken.Create("StructTreeRoot")];
        Walk(pdf, root, types, alts, 0);
        figureAlts = alts;
        return types;
    }

    private static IToken Resolve(PdfDocument pdf, IToken token) =>
        token is IndirectReferenceToken reference ? pdf.Structure.GetObject(reference.Data).Data : token;

    private static void Walk(PdfDocument pdf, IToken token, List<string> types, List<string> alts, int depth)
    {
        if (depth > 40) return;
        token = Resolve(pdf, token);

        switch (token)
        {
            case ArrayToken array:
                foreach (var item in array.Data) Walk(pdf, item, types, alts, depth + 1);
                break;
            case DictionaryToken dictionary:
                if (dictionary.Data.TryGetValue(NameToken.S, out var s) && s is NameToken type)
                {
                    types.Add(type.Data);
                    if (type.Data == "Figure" && dictionary.Data.TryGetValue(NameToken.Create("Alt"), out var alt)
                        && alt is StringToken altText)
                    {
                        alts.Add(altText.Data);
                    }
                }

                if (dictionary.Data.TryGetValue(NameToken.K, out var kids)) Walk(pdf, kids, types, alts, depth + 1);
                break;
        }
    }

    // ---- Refusals, warnings, and images ----

    [Theory]
    [InlineData(RecipeStatus.Draft)]
    [InlineData(RecipeStatus.InDevelopment)]
    [InlineData(RecipeStatus.Testing)]
    [InlineData(RecipeStatus.ReadyForReview)]
    [InlineData(RecipeStatus.Archived)]
    public void An_unapproved_recipe_is_refused(RecipeStatus status)
    {
        var doc = Short() with { Recipe = Short().Recipe with { Status = status } };

        var report = RecipePdfRenderer.Render(Input(doc));

        Assert.Null(report.Pdf);
        Assert.Equal(RecipePdfCodes.RecipeNotApproved, Assert.Single(report.MissingRequired).Code);
    }

    [Fact]
    public void A_ready_version_of_an_unapproved_recipe_is_accepted()
    {
        var doc = Short() with { Recipe = Short().Recipe with { Status = RecipeStatus.Draft } };

        Assert.True(RecipePdfRenderer.Render(Input(doc, readiness: RecipeVersionReadiness.Ready)).IsComplete);
    }

    [Fact]
    public void Each_required_fact_missing_is_reported_and_no_pdf_is_produced()
    {
        var doc = new RecipeSnapshotDocument
        {
            SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
            Recipe = new RecipeSnapshotHeader { Title = " ", Status = RecipeStatus.Approved },
            IngredientGroups = [Group(null, Line(" ", 0))],
            InstructionGroups = [Steps(null, "")],
        };

        var report = RecipePdfRenderer.Render(Input(doc));

        Assert.Null(report.Pdf);
        Assert.Equal(
            [RecipePdfCodes.NameMissing, RecipePdfCodes.IngredientsMissing, RecipePdfCodes.InstructionsMissing],
            report.MissingRequired.Select(i => i.Code));
    }

    [Theory]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1, 0, 1, 0 })] // GIF
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67, 0x3E })] // SVG text
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 })] // truncated PNG
    [InlineData(new byte[0])]
    public void An_image_that_is_not_a_png_or_jpeg_is_left_out_with_a_warning(byte[] bytes)
    {
        var report = RecipePdfRenderer.Render(Input(Short(), image: new RecipePdfImage(bytes, "Alt.")));

        Assert.True(report.IsComplete);
        Assert.Contains(report.Warnings, w => w.Code == RecipePdfCodes.ImageInvalid);
        var types = StructureTypes(report.Pdf!, out _);
        Assert.DoesNotContain("Figure", types);
    }

    [Fact]
    public void An_oversized_image_is_left_out()
    {
        var report = RecipePdfRenderer.Render(Input(Short(), image: new RecipePdfImage(SolidPng(8001, 2), "Alt.")));

        Assert.True(report.IsComplete);
        Assert.Contains(report.Warnings, w => w.Code == RecipePdfCodes.ImageInvalid);
    }

    [Fact]
    public void A_valid_header_with_undecodable_pixels_is_left_out_not_fatal()
    {
        var bad = SolidPng(10, 10);
        Array.Fill(bad, (byte)0xAB, 40, bad.Length - 60); // corrupt the compressed data, keep the header

        var report = RecipePdfRenderer.Render(Input(Short(), image: new RecipePdfImage(bad, "Alt.")));

        Assert.True(report.IsComplete);
        Assert.Contains(report.Warnings, w => w.Code == RecipePdfCodes.ImageInvalid);
    }

    [Fact]
    public void Text_the_font_cannot_draw_blocks_the_export_instead_of_printing_boxes()
    {
        var doc = Short() with { Recipe = Short().Recipe with { Title = "麻婆豆腐" } };

        var report = RecipePdfRenderer.Render(Input(doc));

        Assert.Null(report.Pdf);
        Assert.Equal(RecipePdfCodes.UnsupportedCharacters, Assert.Single(report.MissingRequired).Code);
    }

    [Fact]
    public void A_stale_editorial_revision_is_ignored_with_a_warning()
    {
        var report = RecipePdfRenderer.Render(Input(
            Short(), editorial: new RecipeExportEditorial { IsCurrent = false, Introduction = "Stale intro." }));

        using var pdf = PdfDocument.Open(report.Pdf!);
        Assert.DoesNotContain("Stale intro.", AllText(pdf));
        Assert.Contains(report.Warnings, w => w.Code == RecipePdfCodes.EditorialNotCurrent);
    }

    [Fact]
    public void Rendering_does_not_mutate_its_input()
    {
        var doc = Short();
        var before = System.Text.Json.JsonSerializer.Serialize(doc);

        RecipePdfRenderer.Render(Input(doc, image: new RecipePdfImage(SolidPng(50, 50), "Alt.")));

        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(doc));
    }
}
