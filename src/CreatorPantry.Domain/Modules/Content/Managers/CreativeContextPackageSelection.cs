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

    /// <summary>The day's theme's standing notes. Only with <see cref="Day"/>: there is no theme without one.</summary>
    DayNotes = 256,

    /// <summary>Titles of what was already made for the day's theme. Only with <see cref="Day"/>.</summary>
    DayHistory = 512,

    /// <summary>The brief the creator chose to work from (LF.1.4).</summary>
    WorkingBrief = 1024,

    /// <summary>A referenced recipe's accepted editorial copy. Only with <see cref="Recipe"/>.</summary>
    Editorial = 2048,

    /// <summary>A referenced recipe's accepted SEO key phrases. Only with <see cref="Recipe"/>.</summary>
    SeoKeyPhrases = 4096,
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

    /// <summary>The day a piece of work is for, and everything about that day: what a day-only run keeps.</summary>
    public const CreativeContextSections DaySections =
        CreativeContextSections.Day | CreativeContextSections.DayNotes | CreativeContextSections.DayHistory;

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

    /// <summary>
    /// The theme's own column width, so notes are never over it; held here so the cap is stated where every
    /// other one is, and so a wider column later does not silently widen a prompt.
    /// </summary>
    public const int MaxStandingNotesLength = 2_000;

    /// <summary>How many already-made titles a package carries. Enough to steer away from, few enough to read.</summary>
    public const int MaxAlreadyMade = 10;

    /// <summary>A working title's own column width. A recipe title longer than this is left out, not cut.</summary>
    public const int MaxAlreadyMadeTitleLength = 200;

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

    /// <summary>The whole-package budget for a long-form task (LF.1.4), three times every other task's.</summary>
    /// <remarks>
    /// An article is written from its recipes and from the copy already accepted for them, and a recipe with
    /// its editorial package is some four thousand estimated tokens on its own. This fits about three, so an
    /// issue naming five drops the last two whole and says so — rather than carrying five that have each been
    /// cut into uselessness.
    /// </remarks>
    public const int LongFormMaxEstimatedTokens = 12_000;

    /// <summary>How many recipes a long-form package may carry: a newsletter issue covers several (B-35).</summary>
    public const int LongFormMaxRecipes = 5;

    /// <summary>How much of one accepted editorial revision a package may carry, across all its parts.</summary>
    public const int MaxEditorialLength = 10_000;

    /// <summary>The longest one prose part — headnote, introduction, storage, call to action — is carried as.</summary>
    public const int MaxEditorialProseLength = 2_000;

    /// <summary>The longest one field of a tip, a substitution or a question and answer is carried as.</summary>
    public const int MaxEditorialListItemLength = 600;

    public const int MaxKeyPhrases = 10;

    public const int MaxKeyPhraseLength = 100;

    /// <summary>The seven parts of an editorial package, by the names a rendered prompt uses, in its own order.</summary>
    public static class EditorialSections
    {
        public const string Headnote = "headnote";
        public const string Introduction = "introduction";
        public const string Tips = "tips";
        public const string Substitutions = "substitutions";
        public const string StorageReheating = "storageReheating";
        public const string Faq = "faq";
        public const string Cta = "cta";
    }

    /// <summary>Everything a long-form writer may be shown but the already-made list.</summary>
    /// <remarks>
    /// Not the context's channel list: an article is not written for a channel, and its teasers are the posts
    /// step's. Saved prompts are in — "from a saved prompt" is one of the three ways a piece starts (B-32).
    /// </remarks>
    private const CreativeContextSections LongForm =
        CreativeContextSections.WorkingTitle
        | CreativeContextSections.WorkingBrief
        | CreativeContextSections.PictureBrief
        | CreativeContextSections.Day
        | CreativeContextSections.DayNotes
        | CreativeContextSections.Recipe
        | CreativeContextSections.Editorial
        | CreativeContextSections.SeoKeyPhrases
        | CreativeContextSections.Concept
        | CreativeContextSections.Pictures
        | CreativeContextSections.Prompts;

    /// <summary>Whether a task writes long-form, and so gets the long-form caps, budget and degradation order.</summary>
    public static bool IsLongForm(AiTaskType taskType) =>
        taskType is AiTaskType.LongFormOutline or AiTaskType.LongFormSection;

    public static int MaxEstimatedTokensFor(AiTaskType taskType) =>
        IsLongForm(taskType) ? LongFormMaxEstimatedTokens : MaxEstimatedTokens;

    public static int MaxRecipesFor(AiTaskType taskType) => IsLongForm(taskType) ? LongFormMaxRecipes : MaxRecipes;

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
        //
        // The day's notes and what was already made for it too (AF.7.4): this is the task that decides what
        // to cook, so it is the one a standing note and a list of what the day has already had bear on most.
        AiTaskType.RecipeConcepts =>
            CreativeContextSections.WorkingTitle
            | CreativeContextSections.Channels
            | CreativeContextSections.Day
            | CreativeContextSections.DayNotes
            | CreativeContextSections.DayHistory,

        // The chosen concept is the source. Channels do not shape a recipe.
        AiTaskType.RecipeFirstDraft =>
            CreativeContextSections.WorkingTitle | CreativeContextSections.Day | CreativeContextSections.Concept,

        // Recipe steps included, on purpose: a process shot is a picture of a step, and "fold until just
        // combined" is what tells an image task what the bowl looks like. The steps' times and temperatures
        // travel as the creator's text and are not for an image task to restate as facts — that is its
        // template's rule to state.
        //
        // The day's notes and what was already made for it too (AF.7.4): a standing note is as often about how
        // a day's pictures look as about what is cooked, and a shoot that repeats last week's is the
        // repetition the list is there to prevent.
        AiTaskType.PhotographyConcept =>
            Everything | CreativeContextSections.DayNotes | CreativeContextSections.DayHistory,

        // The one task that may also read a saved prompt: it writes one.
        //
        // The day's notes, and deliberately not what was already made for the day (AF.7.4): by the time a
        // prompt is written the creator has chosen the concept and the shot, so the subject is decided and a
        // list of earlier titles could only argue with a choice already made.
        AiTaskType.ImagePrompt =>
            Everything | CreativeContextSections.Prompts | CreativeContextSections.DayNotes,

        // The day's notes and what was already made for it reach a task only once its template says how to
        // treat them (AF.7.4), which is why they are flags of their own rather than part of Day.
        //
        // What the piece is about, so the posts are about it too. Not the context's own channel list: the
        // channels to write are the request's, stated in the task, and a second list would be a second answer
        // to "which channels". Not saved image prompts: a prompt describes a picture to a renderer, and its
        // words are not the creator's about their food.
        AiTaskType.ChannelPosts =>
            CreativeContextSections.WorkingTitle
            | CreativeContextSections.PictureBrief
            | CreativeContextSections.Day
            | CreativeContextSections.DayNotes
            | CreativeContextSections.DayHistory
            | CreativeContextSections.Recipe
            | CreativeContextSections.Concept
            | CreativeContextSections.Pictures,

        // The plan decides the piece's angle, so it is the one long-form task that is also shown what the day
        // has already had (LF.1.4).
        AiTaskType.LongFormOutline => LongForm | CreativeContextSections.DayHistory,

        // By the time a section is written the plan is approved, and a list of earlier titles could only argue
        // with it — the reason an image prompt is not shown the list either.
        AiTaskType.LongFormSection => LongForm,

        _ => CreativeContextSections.None,
    };

    /// <summary>
    /// The accepted editorial copy a package may carry for one recipe, and whether any part was left out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Parts are taken in the editorial package's own order and carried whole. A prose part over
    /// <see cref="MaxEditorialProseLength"/> is left out, and so is a list with any item that has a field over
    /// <see cref="MaxEditorialListItemLength"/> — the list, not the item, because nothing beside it would say
    /// how many it had lost.
    /// </para>
    /// <para>
    /// A part that would take the whole past <see cref="MaxEditorialLength"/> is left out and the ones after it
    /// are still tried: they are separate named parts, and the entry says which are here, so nothing reads as
    /// complete that is not.
    /// </para>
    /// <para>Null when no part is carried: an entry with nothing in it would only say an acceptance exists.</para>
    /// </remarks>
    public static (CreativeContextEditorialEntry? Entry, bool Omitted) FitEditorial(AcceptedEditorialServiceModel accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);

        var carried = new List<string>();
        var omitted = false;
        var length = 0;

        string? Prose(string name, string? text)
        {
            var value = text?.Trim();

            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            if (value.Length > MaxEditorialProseLength || length + value.Length > MaxEditorialLength)
            {
                omitted = true;

                return null;
            }

            length += value.Length;
            carried.Add(name);

            return value;
        }

        IReadOnlyList<TOut> Items<TIn, TOut>(
            string name, IEnumerable<TIn> items, Func<TIn, string?[]> fields, Func<string[], TOut> build)
        {
            var kept = new List<TOut>();
            var cost = 0;

            foreach (var item in items)
            {
                var values = fields(item).Select(field => field?.Trim() ?? string.Empty).ToArray();

                if (values.All(string.IsNullOrEmpty))
                {
                    continue;
                }

                // The whole list, not the items from here on: a part the entry names as carried is all there,
                // and a list of two tips that says nothing of the three it lost would read as the creator's
                // five.
                if (values.Any(value => value.Length > MaxEditorialListItemLength))
                {
                    omitted = true;

                    return [];
                }

                cost += values.Sum(value => value.Length);
                kept.Add(build(values));
            }

            if (kept.Count == 0)
            {
                return [];
            }

            if (length + cost > MaxEditorialLength)
            {
                omitted = true;

                return [];
            }

            length += cost;
            carried.Add(name);

            return kept;
        }

        var headnote = Prose(EditorialSections.Headnote, accepted.Headnote);
        var introduction = Prose(EditorialSections.Introduction, accepted.Introduction);
        var tips = Items(EditorialSections.Tips, accepted.Tips, tip => [tip], values => values[0]);
        var substitutions = Items(
            EditorialSections.Substitutions,
            accepted.Substitutions,
            item => [item.Suggestion, item.CulinaryNote],
            values => new CreativeContextEditorialSubstitution(values[0], values[1]));
        var storage = Prose(EditorialSections.StorageReheating, accepted.StorageReheating);
        var faq = Items(
            EditorialSections.Faq,
            accepted.Faq,
            item => [item.Question, item.Answer],
            values => new CreativeContextEditorialFaq(values[0], values[1]));
        var cta = Prose(EditorialSections.Cta, accepted.Cta);

        return carried.Count == 0
            ? (null, omitted)
            : (new CreativeContextEditorialEntry(
                accepted.RevisionNumber, headnote, introduction, tips, substitutions, storage, faq, cta, carried), omitted);
    }

    /// <summary>The accepted key phrases a package may carry; null when there are none to carry.</summary>
    /// <remarks>Whole phrases from the front, stopping at the first that does not fit, and the rest counted.</remarks>
    public static CreativeContextSeoEntry? FitKeyPhrases(AcceptedSeoServiceModel accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);

        var (kept, omitted) = KeepWholeLines(accepted.KeyPhrases, MaxKeyPhrases, MaxKeyPhraseLength);

        return kept.Count == 0 ? null : new CreativeContextSeoEntry(accepted.RevisionNumber, kept, omitted);
    }

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
        EstimateTokens(words?.WorkingTitle) + EstimateTokens(words?.PictureBrief) + EstimateTokens(words?.WorkingBrief);

    public static int Estimate(IReadOnlyList<CreativeContextChannelEntry> channels) =>
        channels.Sum(channel => EstimateTokens(channel.DisplayName));

    public static int Estimate(CreativeContextDayEntry? day) =>
        EstimateTokens(day?.ThemeName)
        + EstimateTokens(day?.ThemeDescription)
        + EstimateTokens(day?.StandingNotes)
        + (day?.AlreadyMade.Sum(EstimateTokens) ?? 0);

    public static int Estimate(CreativeContextRecipeEntry recipe) =>
        EstimateTokens(recipe.Title)
        + EstimateTokens(recipe.YieldText)
        + recipe.Ingredients.Sum(EstimateTokens)
        + recipe.Steps.Sum(EstimateTokens)
        + Estimate(recipe.Editorial)
        + (recipe.Seo?.KeyPhrases.Sum(EstimateTokens) ?? 0);

    public static int Estimate(CreativeContextEditorialEntry? editorial) =>
        editorial is null
            ? 0
            : EstimateTokens(editorial.Headnote)
                + EstimateTokens(editorial.Introduction)
                + editorial.Tips.Sum(EstimateTokens)
                + editorial.Substitutions.Sum(item => EstimateTokens(item.Suggestion) + EstimateTokens(item.CulinaryNote))
                + EstimateTokens(editorial.StorageReheating)
                + editorial.Faq.Sum(item => EstimateTokens(item.Question) + EstimateTokens(item.Answer))
                + EstimateTokens(editorial.Cta);

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

        // The long-form fields (LF.1.4) are written only where they exist. Writing an absent one as null, as
        // the fields above are, would move the checksum of every package that never carried it — and a stored
        // generation's record of what it was grounded on would stop matching a package assembled today.
        if (words?.WorkingBrief is { } workingBrief)
        {
            Field("workingBrief", workingBrief);
        }

        foreach (var channel in channels)
        {
            Field($"channel:{channel.Key}", channel.DisplayName);
        }

        Field("day", day?.Day?.ToString());
        Field("themeKey", day?.ThemeKey);
        Field("themeRevision", Number(day?.ThemeRevision));
        Field("themeName", day?.ThemeName);
        Field("themeDescription", day?.ThemeDescription);
        Field("themeStandingNotes", day?.StandingNotes);

        // In order, because the order is rendered: newest first is part of what a model is shown.
        for (var index = 0; index < (day?.AlreadyMade.Count ?? 0); index++)
        {
            Field($"alreadyMade:{index.ToString(CultureInfo.InvariantCulture)}", day!.AlreadyMade[index]);
        }

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

            if (recipe.Editorial is { } editorial)
            {
                Field($"{id}:editorial:revision", Number(editorial.RevisionNumber));
                Field($"{id}:editorial:carried", string.Join(',', editorial.SectionsCarried));
                Field($"{id}:editorial:headnote", editorial.Headnote);
                Field($"{id}:editorial:introduction", editorial.Introduction);

                foreach (var tip in editorial.Tips)
                {
                    Field($"{id}:editorial:tip", tip);
                }

                // Two fields each rather than one joined value, so neither half can forge the other's start.
                foreach (var substitution in editorial.Substitutions)
                {
                    Field($"{id}:editorial:substitution", substitution.Suggestion);
                    Field($"{id}:editorial:substitutionNote", substitution.CulinaryNote);
                }

                Field($"{id}:editorial:storageReheating", editorial.StorageReheating);

                foreach (var item in editorial.Faq)
                {
                    Field($"{id}:editorial:question", item.Question);
                    Field($"{id}:editorial:answer", item.Answer);
                }

                Field($"{id}:editorial:cta", editorial.Cta);
            }

            if (recipe.Seo is { } seo)
            {
                Field($"{id}:seo:revision", Number(seo.RevisionNumber));

                foreach (var phrase in seo.KeyPhrases)
                {
                    Field($"{id}:seo:keyPhrase", phrase);
                }

                Field($"{id}:seo:keyPhrasesNotShown", Number(seo.OmittedKeyPhraseCount));
            }
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
