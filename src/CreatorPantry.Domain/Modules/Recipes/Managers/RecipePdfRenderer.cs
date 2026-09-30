using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Renders one approved recipe version as an accessible, printable PDF.
/// </summary>
/// <remarks>
/// <para>
/// A projection: it reads, formats and writes nothing, never fetches an image, and never describes one. The
/// page is real text in an embedded font, tagged for PDF/UA-1 — headings, lists, a figure with the supplied
/// alternative text (or marked decorative when there is none), a document title and language — with the page
/// footer kept out of the reading order. Colours are dark on white and nothing relies on colour alone.
/// </para>
/// <para>
/// Content follows <see cref="RecipeMarkdownExporter"/>: the same gate, the same required facts, the same
/// alternate-unit text (<see cref="RecipeUnitAlternates"/>), and editorial copy labelled "(editorial)".
/// </para>
/// <para>
/// QuestPDF's Community licence applies (organisations under USD 1M annual revenue); a larger organisation
/// needs a paid licence. PDF/UA output is not byte-reproducible, so tests assert text and structure rather than
/// comparing files.
/// </para>
/// </remarks>
public static class RecipePdfRenderer
{
    private const string Font = "Lato";
    private const string Ink = "#1A1A1A";
    private const string Muted = "#4A4A4A";
    private const string Accent = "#1F5130";

    static RecipePdfRenderer()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static RecipePdfReport Render(RecipePdfInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var doc = input.Snapshot;
        var header = doc.Recipe;
        var missing = new List<RecipePdfIssue>();
        var warnings = new List<RecipePdfIssue>();

        if (header.Status != RecipeStatus.Approved && input.Readiness != RecipeVersionReadiness.Ready)
        {
            missing.Add(new(RecipePdfCodes.RecipeNotApproved,
                "A PDF is only produced for an approved or ready recipe version."));
            return new(null, missing, warnings);
        }

        var title = Clean(header.Title);
        if (title is null) missing.Add(new(RecipePdfCodes.NameMissing, "The recipe has no title."));

        var ingredientGroups = doc.IngredientGroups
            .OrderBy(g => g.SortOrder)
            .Select(g => (Title: Clean(g.Title), Lines: g.Ingredients
                .OrderBy(i => i.SortOrder)
                .Where(i => Clean(i.DisplayText) is not null)
                .ToList()))
            .Where(g => g.Lines.Count > 0)
            .ToList();
        if (ingredientGroups.Count == 0)
        {
            missing.Add(new(RecipePdfCodes.IngredientsMissing, "The recipe has no ingredient lines."));
        }

        var instructionGroups = doc.InstructionGroups
            .OrderBy(g => g.SortOrder)
            .Select(g => (Title: Clean(g.Title), Steps: g.Steps
                .OrderBy(s => s.SortOrder)
                .Where(s => Clean(s.Text) is not null)
                .ToList()))
            .Where(g => g.Steps.Count > 0)
            .ToList();
        if (instructionGroups.Count == 0)
        {
            missing.Add(new(RecipePdfCodes.InstructionsMissing, "The recipe has no instruction steps."));
        }

        if (missing.Count > 0) return new(null, missing, warnings);

        var standard = input.Template == RecipeExportTemplate.Standard;
        var editorial = input.Editorial;
        if (editorial is { IsCurrent: false })
        {
            warnings.Add(new(RecipePdfCodes.EditorialNotCurrent,
                "The accepted editorial revision no longer matches this recipe version and was not used."));
            editorial = null;
        }

        if (!standard) editorial = null;

        var image = input.Image;
        if (image is not null && !RecipePdfImageInspector.IsEmbeddable(image.Bytes))
        {
            warnings.Add(new(RecipePdfCodes.ImageInvalid,
                "The image is not a PNG or JPEG within the size limits and was left out."));
            image = null;
        }

        if (image is not null && Clean(image.AltText) is null)
        {
            warnings.Add(new(RecipePdfCodes.ImageAltMissing,
                "The image has no alternative text, so it is marked decorative."));
        }

        var content = BuildContent(input, title!, ingredientGroups, instructionGroups, editorial, warnings);

        byte[] Generate(RecipePdfImage? embedded) =>
            Document.Create(container => Compose(container, input, title!, content, embedded))
                .WithMetadata(new DocumentMetadata
                {
                    Title = title!,
                    Author = Clean(header.AttributionText) ?? string.Empty,
                    Creator = "CreatorPantry",
                    Language = input.Language,
                    CreationDate = input.CreatedAt,
                    ModifiedDate = input.CreatedAt,
                })
                .WithSettings(new DocumentSettings { PDFUA_Conformance = PDFUA_Conformance.PDFUA_1 })
                .GeneratePdf();

        try
        {
            return new(Generate(image), missing, warnings);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (image is not null)
            {
                // Header checks passed but the decoder refused the pixels; the recipe is still exportable.
                warnings.Add(new(RecipePdfCodes.ImageInvalid, "The image could not be decoded and was left out."));
                try
                {
                    return new(Generate(null), missing, warnings);
                }
                catch (Exception retry) when (retry is not OutOfMemoryException)
                {
                    // Fall through: the text itself cannot be drawn.
                }
            }

            missing.Add(new(RecipePdfCodes.UnsupportedCharacters,
                "The recipe contains characters the export font cannot draw, so no PDF was produced."));
            return new(null, missing, warnings);
        }
    }

    private sealed record Block(string Kind, string? Text = null, IReadOnlyList<ListEntry>? Entries = null, bool Ordered = false);

    private sealed record ListEntry(string Text, string? Note = null);

    private sealed record Content(IReadOnlyList<string> Meta, IReadOnlyList<Block> Blocks, string Attribution, string Footer);

    private static Content BuildContent(
        RecipePdfInput input,
        string title,
        List<(string? Title, List<RecipeSnapshotIngredient> Lines)> ingredientGroups,
        List<(string? Title, List<RecipeSnapshotInstructionStep> Steps)> instructionGroups,
        RecipeExportEditorial? editorial,
        List<RecipePdfIssue> warnings)
    {
        var header = input.Snapshot.Recipe;
        var standard = input.Template == RecipeExportTemplate.Standard;
        var blocks = new List<Block>();

        var meta = RecipeExportFacts.Times(header).Select(t => $"{t.Label}: {t.Text}").ToList();
        if (RecipeExportFacts.Yield(header) is { } yield) meta.Add($"Yield: {yield}");

        if (standard && Clean(header.Description) is { } description) blocks.Add(new("quote", description));
        if (standard && Clean(header.Headnote) is { } headnote) blocks.Add(new("text", headnote));
        AddEditorial(blocks, "Headnote (editorial)", editorial?.Headnote);
        AddEditorial(blocks, "Introduction (editorial)", editorial?.Introduction);

        if (standard)
        {
            var equipment = input.Snapshot.Equipment
                .Where(e => Clean(e.DisplayText) is not null)
                .OrderBy(e => e.IsOptional).ThenBy(e => e.SortOrder)
                .Select(e => new ListEntry(Clean(e.DisplayText)! + (e.IsOptional ? " (optional)" : string.Empty)))
                .ToList();
            if (equipment.Count > 0)
            {
                blocks.Add(new("h2", "Equipment"));
                blocks.Add(new("list", Entries: equipment));
            }
        }

        blocks.Add(new("h2", "Ingredients"));
        var lineNumber = 0;
        foreach (var (groupTitle, lines) in ingredientGroups)
        {
            if (groupTitle is not null) blocks.Add(new("h3", groupTitle));
            var entries = new List<ListEntry>();
            foreach (var line in lines)
            {
                lineNumber++;
                var text = Clean(line.DisplayText)!;
                var alternate = RecipeUnitAlternates.For(line, input.UnitPresentation, input.UnitsById, input.TargetUnits);
                if (alternate.Refusal is { } reason)
                {
                    warnings.Add(new(RecipePdfCodes.UnitNotConverted,
                        $"Ingredient line {lineNumber} was left as written: {reason}."));
                }

                if (alternate.Text is not null) text += $" (≈ {alternate.Text})";
                if (line.IsOptional) text += " (optional)";
                entries.Add(new ListEntry(text));
            }

            blocks.Add(new("list", Entries: entries));
        }

        blocks.Add(new("h2", "Instructions"));
        foreach (var (groupTitle, steps) in instructionGroups)
        {
            if (groupTitle is not null) blocks.Add(new("h3", groupTitle));
            blocks.Add(new("list", Entries: steps.Select(s => new ListEntry(Clean(s.Text)!, Clean(s.Note))).ToList(), Ordered: true));
        }

        if (Clean(header.Notes) is { } notes)
        {
            blocks.Add(new("h2", "Notes"));
            blocks.Add(new("text", notes));
        }

        if (Clean(header.StorageNotes) is { } storage)
        {
            blocks.Add(new("h2", "Storage"));
            blocks.Add(new("text", storage));
        }

        if (editorial is not null) AddEditorialTail(blocks, editorial, input.Snapshot, warnings);

        var attribution = string.Join(" · ", new[] { Clean(header.AttributionText), Clean(header.SourceUrl) }.OfType<string>());
        var units = input.UnitPresentation switch
        {
            RecipeUnitPresentation.Metric => "metric",
            RecipeUnitPresentation.UsCustomary => "US customary",
            _ => "as written",
        };

        return new(meta, blocks, attribution, $"Exported from CreatorPantry · Version {input.VersionNumber} · Units: {units}");
    }

    private static void AddEditorial(List<Block> blocks, string heading, string? text)
    {
        if (Clean(text) is not { } body) return;
        blocks.Add(new("h2", heading));
        blocks.Add(new("text", body));
    }

    private static void AddEditorialTail(
        List<Block> blocks, RecipeExportEditorial editorial, RecipeSnapshotDocument doc, List<RecipePdfIssue> warnings)
    {
        var tips = editorial.Tips.Select(Clean).OfType<string>().Select(t => new ListEntry(t)).ToList();
        if (tips.Count > 0)
        {
            blocks.Add(new("h2", "Tips (editorial)"));
            blocks.Add(new("list", Entries: tips));
        }

        var lineText = doc.IngredientGroups.SelectMany(g => g.Ingredients).ToDictionary(i => i.Id, i => i.DisplayText);
        var substitutions = new List<ListEntry>();
        foreach (var s in editorial.Substitutions)
        {
            if (!lineText.TryGetValue(s.LineId, out var original))
            {
                warnings.Add(new(RecipePdfCodes.SubstitutionUnmatched,
                    "An editorial substitution refers to an ingredient line not in this version and was skipped."));
                continue;
            }

            substitutions.Add(new ListEntry($"{Clean(original)} — {Clean(s.Suggestion)}. {Clean(s.CulinaryNote)}"));
        }

        if (substitutions.Count > 0)
        {
            blocks.Add(new("h2", "Substitutions (editorial)"));
            blocks.Add(new("list", Entries: substitutions));
        }

        AddEditorial(blocks, "Storage and reheating (editorial)", editorial.StorageReheating);

        var faq = editorial.Faq.Where(f => Clean(f.Question) is not null && Clean(f.Answer) is not null).ToList();
        if (faq.Count > 0)
        {
            blocks.Add(new("h2", "FAQ (editorial)"));
            foreach (var f in faq)
            {
                blocks.Add(new("h3", Clean(f.Question)));
                blocks.Add(new("text", Clean(f.Answer)));
            }
        }

        AddEditorial(blocks, "Call to action (editorial)", editorial.Cta);
    }

    private static void Compose(
        IDocumentContainer container, RecipePdfInput input, string title, Content content, RecipePdfImage? image)
    {
        container.Page(page =>
        {
            page.Size(input.PageSize == RecipePdfPageSize.Letter ? PageSizes.Letter : PageSizes.A4);
            page.Margin(54);
            page.DefaultTextStyle(t => t.FontFamily(Font).FontSize(11).FontColor(Ink).LineHeight(1.35f));

            page.Content().PaddingBottom(14).Column(column =>
            {
                column.Spacing(6);

                column.Item().SemanticHeading1().Text(title).FontSize(24).Bold().FontColor(Accent);

                if (content.Attribution.Length > 0)
                {
                    column.Item().SemanticParagraph().Text(content.Attribution).Italic().FontColor(Muted);
                }

                if (content.Meta.Count > 0)
                {
                    column.Item().SemanticParagraph().Text(string.Join("   ·   ", content.Meta)).SemiBold();
                }

                if (image is not null)
                {
                    var alt = Clean(image.AltText);
                    var frame = column.Item().PaddingVertical(6).AlignCenter().MaxHeight(230);
                    if (alt is not null)
                    {
                        frame.SemanticImage(alt).Image(image.Bytes).FitArea();
                    }
                    else
                    {
                        frame.SemanticIgnore().Image(image.Bytes).FitArea();
                    }
                }

                foreach (var block in content.Blocks) Render(column, block);

                column.Item().PaddingTop(14).SemanticParagraph().Text(content.Footer).FontSize(9).FontColor(Muted);
            });

            page.Footer().SemanticIgnore().AlignCenter().Text(text =>
            {
                text.DefaultTextStyle(t => t.FontSize(9).FontColor(Muted));
                text.Span($"{title} · Version {input.VersionNumber} · Page ");
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPages();
            });
        });
    }

    private static void Render(ColumnDescriptor column, Block block)
    {
        switch (block.Kind)
        {
            case "h2":
                column.Item().PaddingTop(10).EnsureSpace(90).SemanticHeading2()
                    .Text(block.Text).FontSize(15).Bold().FontColor(Accent);
                break;
            case "h3":
                column.Item().PaddingTop(4).EnsureSpace(70).SemanticHeading3()
                    .Text(block.Text).FontSize(12).Bold();
                break;
            case "quote":
                column.Item().SemanticBlockQuotation().BorderLeft(2).BorderColor(Accent).PaddingLeft(8)
                    .Text(block.Text).Italic();
                break;
            case "text":
                column.Item().SemanticParagraph().Text(block.Text);
                break;
            case "list":
                column.Item().SemanticList().Column(list =>
                {
                    list.Spacing(3);
                    var number = 0;
                    foreach (var entry in block.Entries!)
                    {
                        number++;
                        var label = block.Ordered ? $"{number}." : "•";
                        list.Item().ShowEntire().SemanticListItem().Row(row =>
                        {
                            row.ConstantItem(block.Ordered ? 26 : 16).SemanticListLabel().Text(label).SemiBold();
                            row.RelativeItem().SemanticListItemBody().Column(body =>
                            {
                                body.Item().Text(entry.Text);
                                if (entry.Note is not null)
                                {
                                    body.Item().Text($"Note: {entry.Note}").Italic().FontColor(Muted);
                                }
                            });
                        });
                    }
                });
                break;
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
