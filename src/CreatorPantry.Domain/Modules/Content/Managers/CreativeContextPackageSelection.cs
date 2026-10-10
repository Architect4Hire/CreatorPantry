using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>The parts of a creative context a task may be shown.</summary>
[Flags]
public enum CreativeContextSections
{
    None = 0,
    WorkingTitle = 1,
    PictureBrief = 2,
    Channels = 4,
    Day = 8,
    Recipe = 16,
    Concept = 32,
    Pictures = 64,
    Prompts = 128,
}

/// <summary>
/// Which task is shown what, how much of it, and what the result hashes to — the deterministic half of the
/// creative-context assembler, kept apart from the reads so it can be tested without any.
/// </summary>
public static class CreativeContextPackageSelection
{
    private const CreativeContextSections Everything =
        CreativeContextSections.WorkingTitle
        | CreativeContextSections.PictureBrief
        | CreativeContextSections.Channels
        | CreativeContextSections.Day
        | CreativeContextSections.Recipe
        | CreativeContextSections.Concept
        | CreativeContextSections.Pictures;

    /// <summary>Four characters per token, rounded up: the estimate the brand package uses, for the same reason.</summary>
    /// <remarks>
    /// The domain holds no tokenizer and should not. A real count depends on the provider's vocabulary, which
    /// would make a package's content vary by deployment; the exact figure arrives afterwards in usage
    /// accounting. So the budget below is deliberately generous.
    /// </remarks>
    public const int CharactersPerToken = 4;

    public const int MaxEstimatedTokens = 4_000;

    public const int MaxRecipes = 2;

    public const int MaxConcepts = 3;

    public const int MaxPictures = 6;

    public const int MaxPrompts = 2;

    public const int MaxIngredientLines = 60;

    public const int MaxIngredientLineLength = 300;

    public const int MaxSteps = 40;

    public const int MaxStepLength = 1_000;

    public const int MaxThemeDescriptionLength = 1_000;

    public const int MaxConceptTitleLength = 200;

    public const int MaxConceptSummaryLength = 1_500;

    public const int MaxAltTextLength = 500;

    /// <summary>
    /// How much of one stored reading a package may carry, across every observation in it.
    /// </summary>
    /// <remarks>
    /// A reading may hold sixteen observations of a thousand characters each
    /// (<c>MediaPictureAnalysisPolicy</c>), which is thirty-two times the alt-text cap and, six pictures over,
    /// more than the whole package's budget. This is the concept-summary cap — the largest single entry the
    /// package already carries — so a read picture contributes about as much as a chosen idea does, and
    /// observations are kept whole: the one that would cross the line is left out rather than cut.
    /// </remarks>
    public const int MaxPictureAnalysisLength = 1_500;

    /// <summary>The longest one observation's aspect or confidence label may be carried as.</summary>
    /// <remarks>
    /// <c>MediaPictureAnalysisPolicy.LabelMaxLength</c>'s own number, repeated rather than borrowed for the
    /// reason this file repeats every other bound: a package's shape is this module's to state.
    /// </remarks>
    public const int MaxPictureObservationLabelLength = 64;

    /// <summary>
    /// What a task grounds in. A task that is not listed grounds in nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A table rather than a default of "everything", and deliberately closed: a task type added later gets
    /// nothing until somebody decides what it should see and writes it here.
    /// </para>
    /// <para>
    /// The capabilities that produce or judge recipe facts — revision, substitution, adaptation, review — are
    /// absent on purpose. A working title, a picture brief or somebody's alt text has no business near a
    /// decision about quantities, temperatures or safety. Editorial and SEO packages are absent because each
    /// is already pinned to the recipe version it writes about.
    /// </para>
    /// </remarks>
    public static CreativeContextSections SectionsFor(AiTaskType taskType) => taskType switch
    {
        // Ideas for a day and a set of channels. Not the picture brief: that describes one image, and a
        // concept list is not an image.
        AiTaskType.RecipeConcepts =>
            CreativeContextSections.WorkingTitle | CreativeContextSections.Channels | CreativeContextSections.Day,

        // The chosen concept is the source. Channels do not shape a recipe.
        AiTaskType.RecipeFirstDraft =>
            CreativeContextSections.WorkingTitle | CreativeContextSections.Day | CreativeContextSections.Concept,

        // Recipe steps included, on purpose: a process shot is a picture of a step, and "fold until just
        // combined" is what tells an image task what the bowl looks like. The steps' times and temperatures
        // travel as the creator's text and are not for an image task to restate as facts — that is its
        // template's rule to state.
        AiTaskType.PhotographyConcept => Everything,

        // The one task that may also read a saved prompt: it writes one.
        AiTaskType.ImagePrompt => Everything | CreativeContextSections.Prompts,

        // What the piece is about, so the posts are about it too. Not the context's own channel list: the
        // channels to write are the request's, stated in the task, and a second list would be a second answer
        // to "which channels". Not saved image prompts: a prompt describes a picture to a renderer, and its
        // words are not the creator's about their food.
        AiTaskType.ChannelPosts =>
            CreativeContextSections.WorkingTitle
            | CreativeContextSections.PictureBrief
            | CreativeContextSections.Day
            | CreativeContextSections.Recipe
            | CreativeContextSections.Concept
            | CreativeContextSections.Pictures,

        _ => CreativeContextSections.None,
    };

    public static int EstimateTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + CharactersPerToken - 1) / CharactersPerToken;

    /// <summary>
    /// Keeps whole lines from the front, up to a count and with no line over a length, and says how many were
    /// left out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never cuts a line. Half an ingredient line is a wrong ingredient line, and half a step is an
    /// instruction with its ending missing — both worse than a list that says it is short.
    /// </para>
    /// <para>
    /// <strong>The list stops at the first line that does not fit</strong>, whether that is the line past the
    /// count or one line too long to carry, and everything from there on is counted as omitted. Skipping an
    /// over-long line and carrying on would leave a gap in the middle of a list that looks whole — an
    /// ingredient silently missing between two that are shown — where "the last N are not shown" is something
    /// a reader can act on.
    /// </para>
    /// </remarks>
    public static (IReadOnlyList<string> Kept, int Omitted) KeepWholeLines(
        IEnumerable<string?> lines, int maxLines, int maxLineLength)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var kept = new List<string>();
        var omitted = 0;
        var stopped = false;

        foreach (var raw in lines)
        {
            var line = raw?.Trim();

            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            if (stopped || line.Length > maxLineLength || kept.Count >= maxLines)
            {
                stopped = true;
                omitted++;
                continue;
            }

            kept.Add(line);
        }

        return (kept, omitted);
    }

    public static int Estimate(CreativeContextWords? words) =>
        EstimateTokens(words?.WorkingTitle) + EstimateTokens(words?.PictureBrief);

    public static int Estimate(IReadOnlyList<CreativeContextChannelEntry> channels) =>
        channels.Sum(channel => EstimateTokens(channel.DisplayName));

    public static int Estimate(CreativeContextDayEntry? day) =>
        EstimateTokens(day?.ThemeName) + EstimateTokens(day?.ThemeDescription);

    public static int Estimate(CreativeContextRecipeEntry recipe) =>
        EstimateTokens(recipe.Title)
        + EstimateTokens(recipe.YieldText)
        + recipe.Ingredients.Sum(EstimateTokens)
        + recipe.Steps.Sum(EstimateTokens);

    public static int Estimate(CreativeContextConceptEntry concept) =>
        EstimateTokens(concept.Title) + EstimateTokens(concept.Summary);

    public static int Estimate(CreativeContextPictureEntry picture) =>
        picture.Reading is { } reading
            ? reading.Observations.Sum(
                observation => EstimateTokens(observation.Aspect)
                    + EstimateTokens(observation.Confidence)
                    + EstimateTokens(observation.Text))
            : EstimateTokens(picture.Description ?? CreativeContextPackage.UndescribedPicture);

    /// <summary>
    /// The one confidence a reading's observation may be grounded on.
    /// </summary>
    /// <remarks>
    /// <c>AiReferenceImageConfidence.Clear</c>'s own word. Mirrored rather than referenced for the reason this
    /// file repeats every other bound, and compared without case so a provider's casing cannot decide what a
    /// task is told.
    /// </remarks>
    public const string ClearConfidence = "Clear";

    /// <summary>
    /// The observations of one reading a task may be grounded on: the clear ones, as many as fit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Only what the reading was clear about.</strong> A "probable" or "hard to tell" observation is a
    /// guess about someone's photograph, and a guess that reaches a prompt is a guess that can reach a post —
    /// plausible-sounding and wrong, which is the one shape of error a creator is least likely to catch. The
    /// prompt forbids writing one as a fact; this is the same rule in code, where a model cannot decline it
    /// (ai.md).
    /// </para>
    /// <para>
    /// <strong>A reading with nothing clear in it grounds nothing</strong>, and the picture then reads as one
    /// nobody has described — which is true: it was looked at and nothing definite was seen.
    /// </para>
    /// </remarks>
    public static (IReadOnlyList<CreativeContextPictureObservation> Kept, bool Omitted) GroundableObservations(
        IReadOnlyList<CreativeContextPictureObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        var clear = observations
            .Where(observation => string.Equals(observation.Confidence, ClearConfidence, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var (kept, omitted) = FitReading(clear);

        // "Some were left out" covers both reasons, because a creator and a reviewer need the same one fact:
        // the task was told less than the reading says.
        return (kept, omitted || clear.Count != observations.Count);
    }

    /// <summary>
    /// The observations of one stored reading that fit, and whether any were left out.
    /// </summary>
    /// <remarks>
    /// In the order the reading gives them, and whole: the first observation that would cross
    /// <see cref="MaxPictureAnalysisLength"/> ends the list, and everything after it is left out too rather
    /// than skipped over — a reading read in order is an account of a picture, and one with a hole in the
    /// middle silently reorders what the model thought worth saying first.
    /// </remarks>
    public static (IReadOnlyList<CreativeContextPictureObservation> Kept, bool Omitted) FitReading(
        IReadOnlyList<CreativeContextPictureObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        var kept = new List<CreativeContextPictureObservation>();
        var length = 0;

        foreach (var observation in observations)
        {
            var cost = observation.Aspect.Length + observation.Confidence.Length + observation.Text.Length;

            if (length + cost > MaxPictureAnalysisLength)
            {
                return (kept, true);
            }

            length += cost;
            kept.Add(observation);
        }

        return (kept, false);
    }

    public static int Estimate(CreativeContextPromptEntry prompt) => EstimateTokens(prompt.Text);

    /// <summary>
    /// A stable hash of what the package carries and exactly which records it was read from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Covers the task, every piece of text, every id and version, and every count the rendered prompt states
    /// — how many recipe lines are not shown, and how many sources of each kind could not be. Whatever a model
    /// was told is in here, so two packages that render differently never share a checksum.
    /// </para>
    /// <para>
    /// Not the assembly time, the estimate, the omission list, or which references were left out and why:
    /// those are reporting, and including them would move the checksum when nothing a model saw had changed.
    /// </para>
    /// <para>
    /// Each value is written with its length in front, so creator text containing <c>=</c> or a line break
    /// cannot forge the start of the next field. Taken after the budget, so it names what is carried rather
    /// than what was selected before trimming.
    /// </para>
    /// </remarks>
    public static string Checksum(
        AiTaskType taskType,
        Guid contextId,
        string? contextVersion,
        CreativeContextWords? words,
        IReadOnlyList<CreativeContextChannelEntry> channels,
        CreativeContextDayEntry? day,
        IReadOnlyList<CreativeContextRecipeEntry> recipes,
        IReadOnlyList<CreativeContextConceptEntry> concepts,
        IReadOnlyList<CreativeContextPictureEntry> pictures,
        IReadOnlyList<CreativeContextPromptEntry> prompts,
        IReadOnlyList<CreativeContextDrop> dropped)
    {
        ArgumentNullException.ThrowIfNull(dropped);

        var canonical = new StringBuilder();

        void Field(string name, string? value)
        {
            canonical.Append(name).Append('=');
            canonical.Append(value is null ? "~" : value.Length.ToString(CultureInfo.InvariantCulture));
            canonical.Append(':').Append(value).Append('\n');
        }

        static string? Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);

        Field("task", taskType.ToString());
        Field("context", contextId.ToString("N"));
        Field("contextVersion", contextVersion);
        Field("workingTitle", words?.WorkingTitle);
        Field("pictureBrief", words?.PictureBrief);

        foreach (var channel in channels)
        {
            Field($"channel:{channel.Key}", channel.DisplayName);
        }

        Field("day", day?.Day?.ToString());
        Field("themeKey", day?.ThemeKey);
        Field("themeRevision", Number(day?.ThemeRevision));
        Field("themeName", day?.ThemeName);
        Field("themeDescription", day?.ThemeDescription);

        foreach (var recipe in recipes)
        {
            var id = $"recipe:{recipe.RecipeId:N}:{recipe.RecipeVersionId:N}";

            Field($"{id}:reference", recipe.ReferenceId.ToString("N"));
            Field($"{id}:title", recipe.Title);
            Field($"{id}:yield", recipe.YieldText);
            Field($"{id}:prep", Number(recipe.PrepTimeMinutes));
            Field($"{id}:cook", Number(recipe.CookTimeMinutes));
            Field($"{id}:rest", Number(recipe.RestTimeMinutes));
            Field($"{id}:total", Number(recipe.TotalTimeMinutes));

            foreach (var line in recipe.Ingredients)
            {
                Field($"{id}:ingredient", line);
            }

            foreach (var step in recipe.Steps)
            {
                Field($"{id}:step", step);
            }

            Field($"{id}:ingredientsNotShown", Number(recipe.OmittedIngredientCount));
            Field($"{id}:stepsNotShown", Number(recipe.OmittedStepCount));
        }

        foreach (var concept in concepts)
        {
            var id = $"concept:{concept.ConceptRequestId:N}:{concept.ConceptId:N}";

            Field($"{id}:reference", concept.ReferenceId.ToString("N"));
            Field($"{id}:title", concept.Title);
            Field($"{id}:summary", concept.Summary);
        }

        foreach (var picture in pictures)
        {
            var id = $"picture:{picture.Kind}:{picture.PictureId:N}:{Number(picture.VersionNumber)}";

            Field($"{id}:reference", picture.ReferenceId.ToString("N"));
            Field($"{id}:source", picture.DescriptionSource.ToString());
            Field($"{id}:description", picture.Description);

            // A stored reading is content the task was grounded on, so it has to reach the checksum: without
            // it, the same picture read and unread would hash alike, and a proposal could not say which of
            // the two it was written from. The instant is in as well — the same pixels read again are a
            // different grounding, even where the words happen to match.
            if (picture.Reading is { } reading)
            {
                Field($"{id}:readAt", reading.ReadAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

                foreach (var observation in reading.Observations)
                {
                    Field($"{id}:observation", $"{observation.Aspect}|{observation.Confidence}|{observation.Text}");
                }
            }
        }

        foreach (var prompt in prompts)
        {
            var id = $"prompt:{prompt.PromptRecordId:N}";

            Field($"{id}:reference", prompt.ReferenceId.ToString("N"));
            Field($"{id}:text", prompt.Text);
        }

        foreach (var (section, count) in NotShown(dropped))
        {
            Field($"notShown:{section}", Number(count));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));

        return "sha256:" + Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// How many sources of each kind the task would have been shown and was not, in a fixed order.
    /// </summary>
    /// <remarks>
    /// Counts and nothing else — not which, and not why. A model told "one recipe is not shown" will not
    /// mistake what it has for everything the piece draws on; it has no use for the reason, and a reason would
    /// be the one place a difference between "gone" and "never this workspace's" could show. Sources the task
    /// does not ground in are not counted: they were never going to be shown, so they are not missing.
    /// </remarks>
    public static IReadOnlyList<(string Section, int Count)> NotShown(IReadOnlyList<CreativeContextDrop> dropped)
    {
        ArgumentNullException.ThrowIfNull(dropped);

        int Count(params CreativeContextReferenceKind[] kinds) => dropped.Count(drop =>
            drop.Reason is not CreativeContextDropReason.NotUsedByTask && kinds.Contains(drop.Kind));

        (string Section, int Count)[] counts =
        [
            ("recipes", Count(CreativeContextReferenceKind.Recipe)),
            ("concepts", Count(CreativeContextReferenceKind.RecipeConcept)),
            ("pictures", Count(CreativeContextReferenceKind.DamAsset, CreativeContextReferenceKind.GeneratedImage)),
            ("savedImagePrompts", Count(CreativeContextReferenceKind.PromptRecord)),
        ];

        return [.. counts.Where(item => item.Count > 0)];
    }
}
