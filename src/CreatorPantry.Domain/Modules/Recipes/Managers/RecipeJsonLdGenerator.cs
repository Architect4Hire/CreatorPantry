using System.Globalization;
using System.Text;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Projects one approved recipe version onto a schema.org <c>Recipe</c> JSON-LD document.
/// </summary>
/// <remarks>
/// <para>
/// A projection, not a source of truth: it reads, formats and writes nothing. Every value comes from the
/// creator's own stored fields or from an accepted, current SEO revision. It never emits <c>nutrition</c>,
/// <c>aggregateRating</c>, <c>review</c>, <c>suitableForDiet</c>, <c>video</c>, <c>url</c> or
/// <c>datePublished</c>: the model has no vetted source for the first four, and the last two belong to
/// Publication records (content.md, recipes.md).
/// </para>
/// <para>
/// Deterministic: the same input is byte-identical output. Property order is fixed, and the default encoder
/// escapes <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c> so the document is safe inside a script element.
/// </para>
/// </remarks>
public static class RecipeJsonLdGenerator
{
    public static RecipeJsonLdReport Generate(RecipeJsonLdInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var doc = input.Snapshot;
        var header = doc.Recipe;
        var missing = new List<RecipeJsonLdIssue>();
        var warnings = new List<RecipeJsonLdIssue>();

        if (header.Status != RecipeStatus.Approved && input.Readiness != RecipeVersionReadiness.Ready)
        {
            missing.Add(new(RecipeJsonLdCodes.RecipeNotApproved,
                "Structured data is only produced for an approved or ready recipe version."));
            return new(null, missing, warnings);
        }

        var name = Clean(header.Title);
        if (name is null)
        {
            missing.Add(new(RecipeJsonLdCodes.NameMissing, "The recipe has no title."));
        }

        var ingredients = doc.IngredientGroups
            .OrderBy(g => g.SortOrder)
            .SelectMany(g => g.Ingredients.OrderBy(i => i.SortOrder))
            .Select(i => Clean(i.DisplayText))
            .OfType<string>()
            .ToList();
        if (ingredients.Count == 0)
        {
            missing.Add(new(RecipeJsonLdCodes.IngredientsMissing, "The recipe has no ingredient lines."));
        }

        var instructionGroups = doc.InstructionGroups
            .OrderBy(g => g.SortOrder)
            .Select(g => (Title: Clean(g.Title),
                Steps: g.Steps.OrderBy(s => s.SortOrder).Select(s => Clean(s.Text)).OfType<string>().ToList()))
            .Where(g => g.Steps.Count > 0)
            .ToList();
        if (instructionGroups.Count == 0)
        {
            missing.Add(new(RecipeJsonLdCodes.InstructionsMissing, "The recipe has no instruction steps."));
        }

        string? image = null;
        if (string.IsNullOrWhiteSpace(input.ImageUrl))
        {
            missing.Add(new(RecipeJsonLdCodes.ImageMissing, "No authorized image URL was supplied."));
        }
        else if (Uri.TryCreate(input.ImageUrl.Trim(), UriKind.Absolute, out var uri)
                 && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            image = uri.AbsoluteUri;
        }
        else
        {
            missing.Add(new(RecipeJsonLdCodes.ImageInvalid, "The image URL is not an absolute http(s) URL."));
        }

        var editorial = input.Editorial;
        if (editorial is { IsCurrent: false })
        {
            warnings.Add(new(RecipeJsonLdCodes.EditorialNotCurrent,
                "The accepted SEO revision no longer matches this recipe version and was not used."));
            editorial = null;
        }

        var seoDescription = Clean(editorial?.MetaDescription);
        var description = seoDescription ?? Clean(header.Description);

        if (seoDescription is not null)
        {
            // Generated copy replaces the creator's own description here, and structured data carries no
            // "(editorial)" label the way Markdown and PDF do, so the provenance is reported instead.
            warnings.Add(new(RecipeJsonLdCodes.DescriptionFromSeo,
                "The description comes from the accepted SEO revision, not from the recipe's own description."));

            // The scan that ran when the copy was generated is not the last word: the creator may have edited
            // it since, and the recipe may have changed. Reported, never rewritten or dropped.
            var unsupported = AiEditorialClaimScanner.ScanText(seoDescription, AiEditorialSourceFacts.From(doc));
            if (unsupported.Count > 0)
            {
                warnings.Add(new(RecipeJsonLdCodes.DescriptionUnsupportedClaim,
                    $"The SEO description makes {unsupported.Count} claim(s) the recipe does not support: {unsupported[0].Message}"));
            }
        }

        var prep = Minutes(header.PrepTimeMinutes);
        var cook = Minutes(header.CookTimeMinutes);
        var total = Minutes(header.TotalTimeMinutes);
        if (prep is null && cook is null && total is null)
        {
            warnings.Add(new(RecipeJsonLdCodes.TimesMissing, "No prep, cook or total time is recorded."));
        }

        var recipeYield = Yield(header);
        if (recipeYield is null)
        {
            warnings.Add(new(RecipeJsonLdCodes.YieldMissing, "No yield or serving count is recorded."));
        }

        var cuisine = Clean(input.CuisineName);
        if (header.CuisineId is not null && cuisine is null)
        {
            warnings.Add(new(RecipeJsonLdCodes.CuisineUnresolved, "The cuisine could not be resolved to a name."));
        }

        var course = Clean(input.CourseName);
        if (header.CourseId is not null && course is null)
        {
            warnings.Add(new(RecipeJsonLdCodes.CourseUnresolved, "The course could not be resolved to a name."));
        }

        if (missing.Count > 0)
        {
            return new(null, missing, warnings);
        }

        var keywords = (editorial?.KeyPhrases ?? [])
            .Select(Clean).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var tools = doc.Equipment
            .Where(e => !e.IsOptional)
            .OrderBy(e => e.SortOrder)
            .Select(e => Clean(e.DisplayText)).OfType<string>()
            .ToList();
        var author = Clean(header.AttributionText);

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            w.WriteStartObject();
            w.WriteString("@context", "https://schema.org");
            w.WriteString("@type", "Recipe");
            w.WriteString("name", name);
            if (description is not null) w.WriteString("description", description);
            w.WriteStartArray("image");
            w.WriteStringValue(image);
            w.WriteEndArray();
            if (author is not null)
            {
                w.WriteStartObject("author");
                w.WriteString("@type", "Person");
                w.WriteString("name", author);
                w.WriteEndObject();
            }
            if (prep is not null) w.WriteString("prepTime", prep);
            if (cook is not null) w.WriteString("cookTime", cook);
            if (total is not null) w.WriteString("totalTime", total);
            if (recipeYield is not null) w.WriteString("recipeYield", recipeYield);
            if (course is not null) w.WriteString("recipeCategory", course);
            if (cuisine is not null) w.WriteString("recipeCuisine", cuisine);
            if (keywords.Count > 0) w.WriteString("keywords", string.Join(", ", keywords));
            if (tools.Count > 0)
            {
                w.WriteStartArray("tool");
                foreach (var tool in tools)
                {
                    w.WriteStartObject();
                    w.WriteString("@type", "HowToTool");
                    w.WriteString("name", tool);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteStartArray("recipeIngredient");
            foreach (var line in ingredients) w.WriteStringValue(line);
            w.WriteEndArray();
            w.WriteStartArray("recipeInstructions");
            foreach (var (title, steps) in instructionGroups)
            {
                if (title is null)
                {
                    foreach (var step in steps) WriteStep(w, step);
                    continue;
                }
                w.WriteStartObject();
                w.WriteString("@type", "HowToSection");
                w.WriteString("name", title);
                w.WriteStartArray("itemListElement");
                foreach (var step in steps) WriteStep(w, step);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return new(Encoding.UTF8.GetString(stream.ToArray()), missing, warnings);
    }

    private static void WriteStep(Utf8JsonWriter w, string text)
    {
        w.WriteStartObject();
        w.WriteString("@type", "HowToStep");
        w.WriteString("text", text);
        w.WriteEndObject();
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>ISO 8601 duration for a positive whole-minute count; <c>null</c> for anything else.</summary>
    private static string? Minutes(int? minutes)
    {
        if (minutes is not > 0) return null;
        var hours = minutes.Value / 60;
        var rest = minutes.Value % 60;
        return (hours, rest) switch
        {
            (0, _) => $"PT{rest}M",
            (_, 0) => $"PT{hours}H",
            _ => $"PT{hours}H{rest}M",
        };
    }

    /// <summary>
    /// The creator's own wording when there is any; otherwise only the serving count. The measured batch is
    /// not composed in, because a rebuilt phrase could contradict what the creator wrote.
    /// </summary>
    private static string? Yield(RecipeSnapshotHeader header)
    {
        if (Clean(header.YieldText) is { } text) return text;
        if (header.ServingCount is not > 0) return null;
        var count = header.ServingCount.Value.ToString("0.##", CultureInfo.InvariantCulture);
        return header.ServingCount == 1 ? "1 serving" : $"{count} servings";
    }
}
