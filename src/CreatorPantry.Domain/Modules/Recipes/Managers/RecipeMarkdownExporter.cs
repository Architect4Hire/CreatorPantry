using System.Globalization;
using System.Text;
using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Projects one approved recipe version onto Markdown.
/// </summary>
/// <remarks>
/// A projection, not a stored competing recipe: it reads, formats and writes nothing, and the same input is
/// byte-identical output (LF newlines, no timestamps). The creator's wording is never replaced — an alternate
/// unit is appended to the line as <c>(≈ …)</c>, computed by <see cref="UnitConversionCalculator"/>, and a line
/// that cannot be converted safely stays as written with a warning. Every creator string is escaped by
/// <see cref="RecipeMarkdownEscaper"/>; editorial copy sits under headings marked "(editorial)".
/// </remarks>
public static class RecipeMarkdownExporter
{
    public static RecipeMarkdownReport Export(RecipeMarkdownInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var doc = input.Snapshot;
        var header = doc.Recipe;
        var missing = new List<RecipeMarkdownIssue>();
        var warnings = new List<RecipeMarkdownIssue>();

        if (header.Status != RecipeStatus.Approved && input.Readiness != RecipeVersionReadiness.Ready)
        {
            missing.Add(new(RecipeMarkdownCodes.RecipeNotApproved,
                "A Markdown export is only produced for an approved or ready recipe version."));
            return new(null, missing, warnings);
        }

        var title = RecipeMarkdownEscaper.Line(header.Title);
        if (title.Length == 0)
        {
            missing.Add(new(RecipeMarkdownCodes.NameMissing, "The recipe has no title."));
        }

        var ingredientGroups = doc.IngredientGroups
            .OrderBy(g => g.SortOrder)
            .Select(g => (g.Title, Lines: g.Ingredients
                .OrderBy(i => i.SortOrder)
                .Where(i => !string.IsNullOrWhiteSpace(i.DisplayText))
                .ToList()))
            .Where(g => g.Lines.Count > 0)
            .ToList();
        if (ingredientGroups.Count == 0)
        {
            missing.Add(new(RecipeMarkdownCodes.IngredientsMissing, "The recipe has no ingredient lines."));
        }

        var instructionGroups = doc.InstructionGroups
            .OrderBy(g => g.SortOrder)
            .Select(g => (g.Title, Steps: g.Steps
                .OrderBy(s => s.SortOrder)
                .Where(s => !string.IsNullOrWhiteSpace(s.Text))
                .ToList()))
            .Where(g => g.Steps.Count > 0)
            .ToList();
        if (instructionGroups.Count == 0)
        {
            missing.Add(new(RecipeMarkdownCodes.InstructionsMissing, "The recipe has no instruction steps."));
        }

        if (missing.Count > 0)
        {
            return new(null, missing, warnings);
        }

        var standard = input.Template == RecipeExportTemplate.Standard;
        var editorial = input.Editorial;
        if (editorial is { IsCurrent: false })
        {
            warnings.Add(new(RecipeMarkdownCodes.EditorialNotCurrent,
                "The accepted editorial revision no longer matches this recipe version and was not used."));
            editorial = null;
        }

        if (!standard) editorial = null;

        var sb = new StringBuilder();
        sb.Append("# ").Append(title).Append("\n\n");

        if (standard && RecipeMarkdownEscaper.Block(header.Description) is { Length: > 0 } description)
        {
            sb.Append("> ").Append(description.Replace("\n", "\n> ")).Append("\n\n");
        }

        if (Attribution(header) is { } attribution)
        {
            sb.Append(attribution).Append("\n\n");
        }

        AppendTimesAndYield(sb, header, warnings);

        if (standard && RecipeMarkdownEscaper.Block(header.Headnote) is { Length: > 0 } headnote)
        {
            sb.Append(headnote).Append("\n\n");
        }

        AppendEditorial(sb, "Headnote (editorial)", editorial?.Headnote);
        AppendEditorial(sb, "Introduction (editorial)", editorial?.Introduction);

        if (standard && doc.Equipment.Count > 0)
        {
            var equipment = doc.Equipment
                .Where(e => !string.IsNullOrWhiteSpace(e.DisplayText))
                .OrderBy(e => e.IsOptional).ThenBy(e => e.SortOrder)
                .ToList();
            if (equipment.Count > 0)
            {
                sb.Append("## Equipment\n\n");
                foreach (var item in equipment)
                {
                    sb.Append("- ").Append(RecipeMarkdownEscaper.Block(item.DisplayText, "  "))
                        .Append(item.IsOptional ? " (optional)" : string.Empty).Append('\n');
                }

                sb.Append('\n');
            }
        }

        AppendIngredients(sb, ingredientGroups, input, warnings);
        AppendInstructions(sb, instructionGroups);

        if (RecipeMarkdownEscaper.Block(header.Notes) is { Length: > 0 } notes)
        {
            sb.Append("## Notes\n\n").Append(notes).Append("\n\n");
        }

        if (RecipeMarkdownEscaper.Block(header.StorageNotes) is { Length: > 0 } storage)
        {
            sb.Append("## Storage\n\n").Append(storage).Append("\n\n");
        }

        if (editorial is not null)
        {
            AppendEditorialTail(sb, editorial, doc, warnings);
        }

        sb.Append("---\n\n");
        sb.Append("Exported from CreatorPantry · Version ")
            .Append(input.VersionNumber.ToString(CultureInfo.InvariantCulture))
            .Append(" · Units: ")
            .Append(input.UnitPresentation switch
            {
                RecipeUnitPresentation.Metric => "metric",
                RecipeUnitPresentation.UsCustomary => "US customary",
                _ => "as written",
            })
            .Append('\n');

        return new(sb.ToString(), missing, warnings);
    }

    private static string? Attribution(RecipeSnapshotHeader header)
    {
        var parts = new List<string>();
        if (RecipeMarkdownEscaper.Line(header.AttributionText) is { Length: > 0 } text) parts.Add($"*{text}*");
        if (!string.IsNullOrWhiteSpace(header.SourceUrl))
        {
            var url = header.SourceUrl.Trim();
            parts.Add(Uri.TryCreate(url, UriKind.Absolute, out var uri)
                      && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                ? $"<{uri.AbsoluteUri}>"
                : RecipeMarkdownEscaper.Line(url));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static void AppendTimesAndYield(StringBuilder sb, RecipeSnapshotHeader header, List<RecipeMarkdownIssue> warnings)
    {
        var parts = RecipeExportFacts.Times(header).Select(t => $"**{t.Label}** {t.Text}").ToList();
        if (parts.Count == 0)
        {
            warnings.Add(new(RecipeMarkdownCodes.TimesMissing, "No prep, cook or total time is recorded."));
        }

        if (RecipeExportFacts.Yield(header) is { } yield)
        {
            parts.Add("**Yield** " + RecipeMarkdownEscaper.Line(yield));
        }
        else
        {
            warnings.Add(new(RecipeMarkdownCodes.YieldMissing, "No yield or serving count is recorded."));
        }

        if (parts.Count > 0) sb.Append(string.Join(" · ", parts)).Append("\n\n");
    }

    private static void AppendIngredients(
        StringBuilder sb,
        List<(string? Title, List<RecipeSnapshotIngredient> Lines)> groups,
        RecipeMarkdownInput input,
        List<RecipeMarkdownIssue> warnings)
    {
        sb.Append("## Ingredients\n\n");
        var lineNumber = 0;
        foreach (var (title, lines) in groups)
        {
            if (RecipeMarkdownEscaper.Line(title) is { Length: > 0 } heading)
            {
                sb.Append("### ").Append(heading).Append("\n\n");
            }

            foreach (var line in lines)
            {
                lineNumber++;
                sb.Append("- ").Append(RecipeMarkdownEscaper.Block(line.DisplayText, "  "));
                if (Convert(line, lineNumber, input, warnings) is { } converted)
                {
                    sb.Append(" (≈ ").Append(converted).Append(')');
                }

                if (line.IsOptional) sb.Append(" (optional)");
                sb.Append('\n');
            }

            sb.Append('\n');
        }
    }

    private static void AppendInstructions(
        StringBuilder sb, List<(string? Title, List<RecipeSnapshotInstructionStep> Steps)> groups)
    {
        sb.Append("## Instructions\n\n");
        foreach (var (title, steps) in groups)
        {
            if (RecipeMarkdownEscaper.Line(title) is { Length: > 0 } heading)
            {
                sb.Append("### ").Append(heading).Append("\n\n");
            }

            var number = 0;
            foreach (var step in steps)
            {
                number++;
                var marker = $"{number}. ";
                var indent = new string(' ', marker.Length);
                sb.Append(marker).Append(RecipeMarkdownEscaper.Block(step.Text, indent)).Append('\n');
                if (RecipeMarkdownEscaper.Block(step.Note, indent) is { Length: > 0 } note)
                {
                    sb.Append(indent).Append("*Note: ").Append(note).Append("*\n");
                }
            }

            sb.Append('\n');
        }
    }

    private static void AppendEditorial(StringBuilder sb, string heading, string? text)
    {
        if (RecipeMarkdownEscaper.Block(text) is not { Length: > 0 } body) return;
        sb.Append("## ").Append(heading).Append("\n\n").Append(body).Append("\n\n");
    }

    private static void AppendEditorialTail(
        StringBuilder sb, RecipeExportEditorial editorial, RecipeSnapshotDocument doc, List<RecipeMarkdownIssue> warnings)
    {
        var tips = editorial.Tips.Select(t => RecipeMarkdownEscaper.Block(t, "  ")).Where(t => t.Length > 0).ToList();
        if (tips.Count > 0)
        {
            sb.Append("## Tips (editorial)\n\n");
            foreach (var tip in tips) sb.Append("- ").Append(tip).Append('\n');
            sb.Append('\n');
        }

        var lineText = doc.IngredientGroups.SelectMany(g => g.Ingredients).ToDictionary(i => i.Id, i => i.DisplayText);
        var substitutions = new List<string>();
        foreach (var s in editorial.Substitutions)
        {
            if (!lineText.TryGetValue(s.LineId, out var original))
            {
                warnings.Add(new(RecipeMarkdownCodes.SubstitutionUnmatched,
                    "An editorial substitution refers to an ingredient line not in this version and was skipped."));
                continue;
            }

            substitutions.Add(
                $"**{RecipeMarkdownEscaper.Line(original)}** — {RecipeMarkdownEscaper.Line(s.Suggestion)}. "
                + RecipeMarkdownEscaper.Line(s.CulinaryNote));
        }

        if (substitutions.Count > 0)
        {
            sb.Append("## Substitutions (editorial)\n\n");
            foreach (var s in substitutions) sb.Append("- ").Append(s).Append('\n');
            sb.Append('\n');
        }

        AppendEditorial(sb, "Storage and reheating (editorial)", editorial.StorageReheating);

        var faq = editorial.Faq
            .Where(f => RecipeMarkdownEscaper.Line(f.Question).Length > 0 && RecipeMarkdownEscaper.Line(f.Answer).Length > 0)
            .ToList();
        if (faq.Count > 0)
        {
            sb.Append("## FAQ (editorial)\n\n");
            foreach (var f in faq)
            {
                sb.Append("**").Append(RecipeMarkdownEscaper.Line(f.Question)).Append("**\n\n")
                    .Append(RecipeMarkdownEscaper.Block(f.Answer)).Append("\n\n");
            }
        }

        AppendEditorial(sb, "Call to action (editorial)", editorial.Cta);
    }

    private static string? Convert(
        RecipeSnapshotIngredient line, int lineNumber, RecipeMarkdownInput input, List<RecipeMarkdownIssue> warnings)
    {
        var alternate = RecipeUnitAlternates.For(line, input.UnitPresentation, input.UnitsById, input.TargetUnits);
        if (alternate.Refusal is { } reason)
        {
            warnings.Add(new(RecipeMarkdownCodes.UnitNotConverted,
                $"Ingredient line {lineNumber} was left as written: {reason}."));
        }

        return alternate.Text;
    }
}
