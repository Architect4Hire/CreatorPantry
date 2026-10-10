using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Turns a <see cref="CreativeContextPackage"/> into prompt text, in one place (AF.1.5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Everything it renders is data, and is added to an envelope as untrusted reference material.</strong>
/// A working title, a picture brief, a theme's description, alt text, recipe lines, an earlier model's concept
/// and a saved prompt are all text somebody other than this system wrote, and any of them can carry an
/// instruction. <see cref="AddTo"/> puts them in <c>REFERENCES</c> segments, which the envelope fences and
/// places in the user message at <see cref="PromptSegmentTrust.Untrusted"/> — never in the system message.
/// </para>
/// <para>
/// <strong>It renders no instruction of its own</strong>, for the reason <see cref="BrandContextPromptRenderer"/>
/// gives: an instruction inside a data segment is indistinguishable from one a source forged there. What a
/// capability should do with this material belongs in its task template.
/// </para>
/// <para>
/// <strong>No identifier, version or checksum reaches the prompt.</strong> A model cannot use one legitimately
/// and could only echo it back as content. Those stay on the package, for provenance.
/// </para>
/// <para>
/// <strong>Deterministic.</strong> The same package renders to the same bytes: property order is fixed by the
/// code below, lists keep the package's order, and nothing here reads a clock or a culture.
/// </para>
/// </remarks>
public static class CreativeContextPromptRenderer
{
    /// <summary>Who wrote a concept's words, stated beside them. A fact about provenance, not an instruction.</summary>
    public const string ConceptAuthor = "an earlier AI suggestion, chosen by the creator";

    /// <summary>Whose words a picture's description is, where the creator wrote it.</summary>
    public const string AltTextAuthor = "the creator's alt text";

    /// <summary>
    /// Whose words a picture's observations are, where a model read the picture (AF.6.6).
    /// </summary>
    /// <remarks>
    /// It says both halves on purpose: that a model wrote this, and that the creator did not. A reading
    /// labelled only "a reading of the picture" would be taken for something the creator checked, and nothing
    /// downstream may present it as their words.
    /// </remarks>
    public const string ReadingAuthor = "a model's reading of the picture, not the creator's words";

    private static readonly JsonSerializerOptions Json = new()
    {
        // So an apostrophe in a title reads as the creator typed it rather than as an escape sequence. This
        // encoder still escapes quotes, backslashes and every control character, line breaks included.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // One line, and it has to stay one: the envelope's fences are whole lines, so text that cannot contain
        // a physical line break cannot begin a line that looks like one. An indented serializer would put
        // creator text at the start of lines of its own.
        WriteIndented = false,
    };

    /// <summary>
    /// What the piece is: the creator's words, the channels and the day. Null when the package carries none.
    /// </summary>
    public static string? Brief(CreativeContextPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (package.Words?.WorkingTitle is { } title)
        {
            payload["workingTitle"] = title;
        }

        if (package.Words?.PictureBrief is { } brief)
        {
            payload["pictureTheCreatorHasInMind"] = brief;
        }

        if (package.Channels.Count > 0)
        {
            payload["channels"] = package.Channels.Select(channel => channel.DisplayName).ToArray();
        }

        if (package.Day?.Day is { } day)
        {
            payload["dayOfWeek"] = day.ToString();
        }

        if (package.Day?.ThemeName is { } themeName)
        {
            payload["theme"] = package.Day.ThemeDescription is { } description
                ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = themeName, ["about"] = description }
                : new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = themeName };
        }

        return payload.Count == 0 ? null : JsonSerializer.Serialize(payload, Json);
    }

    /// <summary>
    /// The sources the piece draws on: recipes, concepts, pictures and saved prompts. Null when there are none.
    /// </summary>
    public static string? Sources(CreativeContextPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (package.Recipes.Count > 0)
        {
            payload["recipes"] = package.Recipes.Select(Recipe).ToArray();
        }

        if (package.Concepts.Count > 0)
        {
            // Said outright who wrote these. A creator choosing a concept does not make its words theirs: a
            // summary that says "high-protein" is an earlier suggestion's phrase, and without this it would
            // read to the next model as something the creator asserted.
            payload["chosenConcepts"] = package.Concepts
                .Select(concept =>
                {
                    var rendered = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["writtenBy"] = ConceptAuthor,
                        ["title"] = concept.Title,
                    };

                    if (concept.Summary is { } summary)
                    {
                        rendered["summary"] = summary;
                    }

                    return rendered;
                })
                .ToArray();
        }

        if (package.Pictures.Count > 0)
        {
            // Three shapes, and which one a picture gets is the package's decision rather than this method's.
            // A described picture carries the creator's words and says they are the creator's. A read one
            // carries the observations and says they are a model's, with each confidence beside its text. An
            // undescribed one carries the fixed sentence and nothing else: no id, no kind, no file name —
            // nothing a model could spin into a description of pixels nobody has looked at.
            payload["pictures"] = package.Pictures.Select(Picture).ToArray();
        }

        if (package.Prompts.Count > 0)
        {
            payload["savedImagePrompts"] = package.Prompts.Select(prompt => prompt.Text).ToArray();
        }

        // Counts of what the piece draws on and this prompt does not show, so what is here is never taken for
        // all there is. Numbers only: not which, and not why.
        var notShown = CreativeContextPackageSelection.NotShown(package.Dropped);

        if (notShown.Count > 0)
        {
            var counts = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var (section, count) in notShown)
            {
                counts[section] = count;
            }

            payload["sourcesNotShown"] = counts;
        }

        return payload.Count == 0 ? null : JsonSerializer.Serialize(payload, Json);
    }

    /// <summary>
    /// Adds the package to an envelope as untrusted reference material, stamped with the workspace it was read
    /// in.
    /// </summary>
    /// <param name="builder">The envelope under construction.</param>
    /// <param name="package">The package to render.</param>
    /// <remarks>
    /// There is deliberately no workspace parameter. The segments are stamped with
    /// <see cref="CreativeContextPackage.WorkspaceId"/> — the workspace the context row was read from — and the
    /// builder refuses a segment stamped for any workspace but its own. If a caller passed the id, it would be
    /// supplying both sides of that comparison and the check would only ever catch a typo.
    /// </remarks>
    public static PromptEnvelopeBuilder AddTo(PromptEnvelopeBuilder builder, CreativeContextPackage package)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(package);

        if (Brief(package) is { } brief)
        {
            builder.AddReference(package.WorkspaceId, brief);
        }

        if (Sources(package) is { } sources)
        {
            builder.AddReference(package.WorkspaceId, sources);
        }

        return builder;
    }

    /// <summary>
    /// One picture, as the task may be told about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A reading is never called the creator's words.</strong> It is unreviewed model output about
    /// pixels, so it says whose it is, and each observation keeps the confidence the reading gave it — an
    /// observation whose label is dropped reads as a fact (<c>IMediaPictureAnalysisFacade</c>).
    /// </para>
    /// <para>
    /// <strong>An unread picture's entry is one fixed sentence.</strong> Everything that could be said about
    /// it and is not the picture — which library it is in, what it is called, what was asked for when it was
    /// made — is absent, because every one of those is something a model could write a description out of.
    /// </para>
    /// </remarks>
    private static Dictionary<string, object?> Picture(CreativeContextPictureEntry picture)
    {
        if (picture.Reading is { } reading)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["describedBy"] = ReadingAuthor,
                ["readAt"] = reading.ReadAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["observations"] = reading.Observations
                    .Select(observation => new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["aspect"] = observation.Aspect,
                        ["confidence"] = observation.Confidence,
                        ["text"] = observation.Text,
                    })
                    .ToArray(),
            };
        }

        return picture.Description is { } description
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["describedBy"] = AltTextAuthor,
                ["description"] = description,
            }
            : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["note"] = CreativeContextPackage.UndescribedPicture,
            };
    }

    private static Dictionary<string, object?> Recipe(CreativeContextRecipeEntry recipe)
    {
        var rendered = new Dictionary<string, object?>(StringComparer.Ordinal) { ["title"] = recipe.Title };

        if (recipe.YieldText is { } yield)
        {
            rendered["yield"] = yield;
        }

        // Minutes as the recipe records them. No sum, no conversion: arithmetic on recipe facts is domain
        // code's, and a total is stored independently of its parts (recipes.md).
        if (recipe.PrepTimeMinutes is { } prep)
        {
            rendered["prepMinutes"] = prep;
        }

        if (recipe.CookTimeMinutes is { } cook)
        {
            rendered["cookMinutes"] = cook;
        }

        if (recipe.RestTimeMinutes is { } rest)
        {
            rendered["restMinutes"] = rest;
        }

        if (recipe.TotalTimeMinutes is { } total)
        {
            rendered["totalMinutes"] = total;
        }

        rendered["ingredients"] = recipe.Ingredients;
        rendered["steps"] = recipe.Steps;

        // Said outright, so a shortened list is never mistaken for the whole recipe.
        if (recipe.OmittedIngredientCount > 0)
        {
            rendered["ingredientLinesNotShown"] = recipe.OmittedIngredientCount;
        }

        if (recipe.OmittedStepCount > 0)
        {
            rendered["stepsNotShown"] = recipe.OmittedStepCount;
        }

        return rendered;
    }
}
