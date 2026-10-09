using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Vocabulary.Facade;

namespace CreatorPantry.Domain.Modules.Content.Business;

public interface IContentSeedBusiness
{
    /// <summary>
    /// One content idea for the resolved workspace, selected from platform vocabulary and the workspace's own week.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Reproducible, within limits worth stating.</strong> The same token returns the same seed against
    /// the same catalogue state and the same week. It is not reproducible forever: retiring the entry a token
    /// selected changes that facet, and so does a creator rewriting the theme for the chosen day. Selection is
    /// per-candidate rather than positional (<see cref="ContentSeedSelector"/>), so merely <em>adding</em> an
    /// entry usually changes nothing.
    /// </para>
    /// <para>
    /// <strong>Nothing is written and no model is called.</strong> A pinned facet that names no entry, or names a
    /// retired one, is refused — a caller who asked for Thai and silently got Korean would have no way to tell.
    /// An absent facet is not an error: an empty catalogue, or a day the workspace has no theme for, yields a seed
    /// with fewer parts.
    /// </para>
    /// <para>
    /// <strong>This Business reads three other modules through their facades</strong> — Vocabulary for reference
    /// vocabulary, Brand for the workspace's declared channels, and this module's own weekly themes. That is the
    /// permitted shape of cross-module traffic and the reason this feature has no data layer of its own: it owns no
    /// table, so there is nothing below it to compose. Every one of those calls is a read.
    /// </para>
    /// <para>
    /// <strong>A seed can be built around a recipe.</strong> When the caller names one, the recipe is read through
    /// the recipe module's facade and its own cuisine, course and primary technique <em>are</em> the seed's
    /// cuisine, dish type and method. The token never chooses any of those three for a recipe that leaves one
    /// unset — an idea for a lemon tart that suggested a stir-fry would contradict the recipe it is for — so an
    /// unset one is absent unless the caller pinned it. Where the recipe states one, it wins over a pin: the
    /// recipe is the canonical fact. The token still chooses the photography style, channel, occasion and day.
    /// The same token and the same recipe version return the same seed.
    /// </para>
    /// </remarks>
    Task<OperationResult<ContentSeedServiceModel>> GenerateAsync(
        ContentSeedQueryViewModel model, CancellationToken cancellationToken);
}

internal sealed class ContentSeedBusiness(
    IVocabularyFacade vocabulary,
    IWorkspaceWeeklyThemeFacade weeklyThemes,
    IBrandProfileFacade brandProfile,
    IContentChannelCatalog channels,
    IPhotographyStyleCatalog photographyStyles,
    IOccasionCatalog occasions,
    IRecipeFacade recipes,
    IContentSeedTokenSource tokens) : IContentSeedBusiness
{
    /// <summary>
    /// The seven days as their names, which are what the selection hashes.
    /// </summary>
    /// <remarks>
    /// Names rather than the enum's numbers, so the day a token picks does not change if the facet is ever
    /// selected over a different ordering — and because <see cref="ContentSeedSelector"/> scores reference types,
    /// which an enum is not.
    /// </remarks>
    private static readonly string[] Days = Enum.GetNames<DayOfWeek>();

    public async Task<OperationResult<ContentSeedServiceModel>> GenerateAsync(
        ContentSeedQueryViewModel model, CancellationToken cancellationToken)
    {
        // A caller's own token is honoured; otherwise one is minted, which is this generator's only randomness.
        var token = ContentSeedInputChecks.Normalize(model.Token) ?? tokens.Next();

        var cuisines = await vocabulary.ListActiveCuisinesAsync(cancellationToken);
        var courses = await vocabulary.ListActiveCoursesAsync(cancellationToken);
        var techniques = await vocabulary.ListActiveTechniquesAsync(cancellationToken);

        var failures = new List<(string Field, string Error)>();

        var recipe = await RecipeAsync(model, failures, cancellationToken);

        var cuisine = ResolveAround(
            recipe, recipe?.CuisineId, entry => entry.Id,
            token, ContentSeedPolicy.Facets.Cuisine, nameof(model.Cuisine), model.Cuisine,
            cuisines, entry => entry.Code, failures);
        var dishType = ResolveAround(
            recipe, recipe?.CourseId, entry => entry.Id,
            token, ContentSeedPolicy.Facets.DishType, nameof(model.DishType), model.DishType,
            courses, entry => entry.Code, failures);
        var method = ResolveAround(
            recipe, recipe?.PrimaryTechniqueId, entry => entry.Id,
            token, ContentSeedPolicy.Facets.Method, nameof(model.Method), model.Method,
            techniques, entry => entry.Code, failures);

        // The code-owned catalogues keep retired entries, so an active-only candidate set is built here and a
        // pinned key is checked against the whole catalogue — which lets a retired key be refused as retired
        // rather than reported as unknown.
        var style = ResolveCatalogue(
            token, ContentSeedPolicy.Facets.PhotographyStyle, nameof(model.PhotographyStyle), model.PhotographyStyle,
            photographyStyles.All, entry => (entry.Key, entry.IsActive), failures);
        var occasion = ResolveCatalogue(
            token, ContentSeedPolicy.Facets.Occasion, nameof(model.Occasion), model.Occasion,
            occasions.All, entry => (entry.Key, entry.IsActive), failures);
        var channel = ResolveCatalogue(
            token, ContentSeedPolicy.Facets.Channel, nameof(model.Channel), model.Channel,
            await ChannelCandidatesAsync(cancellationToken), entry => (entry.Key, entry.IsActive), failures);

        if (model.Day is { } pinnedDay && !Enum.IsDefined(pinnedDay))
        {
            failures.Add((nameof(model.Day), "Name a day of the week, such as Monday."));
        }

        if (failures.Count > 0)
        {
            return OperationResult<ContentSeedServiceModel>.Failure(OperationError.Validation(
                ContentErrorCodes.ContentSeedInvalid, CannotGenerate, failures));
        }

        // Days are never empty, so the selection always answers.
        var day = model.Day
            ?? Enum.Parse<DayOfWeek>(
                ContentSeedSelector.Select(token, ContentSeedPolicy.Facets.Day, Days, name => name)!);

        var cuisineFacet = Facet(
            cuisine.Pinned, cuisine.Chosen?.Code, cuisine.Chosen?.DisplayName, cuisine.FromRecipe);
        var dishTypeFacet = Facet(
            dishType.Pinned, dishType.Chosen?.Code, dishType.Chosen?.DisplayName, dishType.FromRecipe);

        // A recipe's own technique carries its caution exactly as a drawn or pinned one does.
        var methodFacet = method.Chosen is { } chosenMethod
            ? new ContentSeedMethodServiceModel(
                chosenMethod.Code,
                chosenMethod.DisplayName,
                method.Pinned,
                chosenMethod.RequiresSafetyCaution,
                method.FromRecipe)
            : null;
        var styleFacet = Facet(style.Pinned, style.Chosen?.Key, style.Chosen?.DisplayName);
        var channelFacet = Facet(channel.Pinned, channel.Chosen?.Key, channel.Chosen?.DisplayName);
        var occasionFacet = Facet(occasion.Pinned, occasion.Chosen?.Key, occasion.Chosen?.DisplayName);
        var dayFacet = await DayFacetAsync(day, model.Day is not null, cancellationToken);

        return OperationResult<ContentSeedServiceModel>.Success(new ContentSeedServiceModel(
            token,
            cuisineFacet,
            dishTypeFacet,
            methodFacet,
            styleFacet,
            channelFacet,
            dayFacet,
            occasionFacet,
            ContentSeedDescription.For(
                cuisineFacet, dishTypeFacet, methodFacet, styleFacet, channelFacet, dayFacet, occasionFacet,
                recipe?.Title),
            recipe is null
                ? null
                : new ContentSeedRecipeServiceModel(recipe.RecipeId, recipe.RecipeVersionId, recipe.Title)));
    }

    /// <summary>What a seed takes from the recipe it is built around, and nothing else of it.</summary>
    private sealed record RecipeFacts(
        Guid RecipeId,
        Guid? RecipeVersionId,
        string Title,
        Guid? CuisineId,
        Guid? CourseId,
        Guid? PrimaryTechniqueId);

    /// <summary>
    /// The recipe the caller named, or null when they named none — or named one that cannot be read, which is
    /// recorded as a failure.
    /// </summary>
    /// <remarks>
    /// Facade to facade, in the resolved workspace: a recipe that does not exist and one that belongs to another
    /// workspace get the same answer, so neither discloses the other (tenancy.md). A pinned version is read as it
    /// was archived, so an idea is built around the recipe the creator linked rather than one it has since become.
    /// </remarks>
    private async Task<RecipeFacts?> RecipeAsync(
        ContentSeedQueryViewModel model,
        List<(string Field, string Error)> failures,
        CancellationToken cancellationToken)
    {
        if (model.RecipeId is not { } recipeId)
        {
            return null;
        }

        if (model.RecipeVersionId is { } versionId)
        {
            var pinned = await recipes.GetSnapshotAsync(recipeId, versionId, cancellationToken);
            if (pinned.Succeeded)
            {
                var header = pinned.Value!.Document.Recipe;

                return new RecipeFacts(
                    recipeId, versionId, header.Title, header.CuisineId, header.CourseId, header.PrimaryTechniqueId);
            }
        }
        else
        {
            var detail = await recipes.GetDetailAsync(recipeId, cancellationToken);
            if (detail.Succeeded)
            {
                var current = detail.Value!;

                return new RecipeFacts(
                    recipeId, null, current.Title, current.CuisineId, current.CourseId, current.PrimaryTechniqueId);
            }
        }

        failures.Add((nameof(model.RecipeId), "That recipe could not be found."));

        return null;
    }

    private const string CannotGenerate = "A content seed could not be generated.";

    /// <summary>
    /// The channels a seed may choose from: the brand's own declared defaults when it has any, else the whole
    /// catalogue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one weighting in this generator that is not uniform, and the only one grounded in data rather than
    /// guessed: a workspace that listed its default channels has said where it publishes, in its own words, so a
    /// seed suggesting a channel it has never used would be worse than useless.
    /// </para>
    /// <para>
    /// Facade to facade, and read-only. A workspace with no brand profile, or one with a profile but no channel
    /// defaults, falls back to the catalogue rather than producing no channel at all — a creator who has not
    /// filled in a brand profile still deserves a seed. A default naming a channel the catalogue has since retired
    /// is dropped: it may stay where it is stored, but a seed is a new choice.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<ContentChannel>> ChannelCandidatesAsync(CancellationToken cancellationToken)
    {
        var profile = await brandProfile.GetAsync(cancellationToken);
        if (!profile.Succeeded)
        {
            return channels.All;
        }

        var declared = profile.Value!.ChannelDefaults
            .Select(channel => channels.Find(channel.ChannelKey))
            .OfType<ContentChannel>()
            .Where(channel => channel.IsActive)
            .ToList();

        return declared.Count > 0 ? declared : channels.All;
    }

    private async Task<ContentSeedDayServiceModel> DayFacetAsync(
        DayOfWeek day, bool pinned, CancellationToken cancellationToken)
    {
        // The workspace's own themes, never a platform list. Most days have none, and that is the normal case
        // rather than a degraded one: the seed then names the day alone.
        var week = await weeklyThemes.GetAsync(cancellationToken);
        var theme = week.Succeeded
            ? week.Value!.Themes.FirstOrDefault(entry => entry.RetiredAt is null && entry.Day == day)
            : null;

        return new ContentSeedDayServiceModel(
            day,
            pinned,
            theme is null ? null : new ContentSeedFacetServiceModel(theme.Key, theme.DisplayName, Pinned: false));
    }

    private static ContentSeedFacetServiceModel? Facet(
        bool pinned, string? key, string? displayName, bool fromRecipe = false) =>
        key is null || displayName is null
            ? null
            : new ContentSeedFacetServiceModel(key, displayName, pinned, fromRecipe);

    /// <summary>
    /// Resolves one of the three facets a recipe can state: the recipe's own entry when the seed is built around
    /// one, and otherwise exactly what <see cref="Resolve{T}"/> answers.
    /// </summary>
    /// <remarks>
    /// With a recipe, the token is never asked. What the recipe states is the answer; an entry it names that has
    /// since been retired is absent rather than replaced, because a substitute would be a fact about the recipe
    /// that is not true. What it leaves unset is absent too, unless the caller pinned it.
    /// </remarks>
    private static (T? Chosen, bool Pinned, bool FromRecipe) ResolveAround<T>(
        RecipeFacts? recipe,
        Guid? recipeEntryId,
        Func<T, Guid> idOf,
        string token,
        string facet,
        string field,
        string? pinnedKey,
        IReadOnlyList<T> candidates,
        Func<T, string> keyOf,
        List<(string Field, string Error)> failures)
        where T : class
    {
        if (recipe is not null && recipeEntryId is { } entryId)
        {
            var own = candidates.FirstOrDefault(candidate => idOf(candidate) == entryId);

            return (own, Pinned: false, FromRecipe: own is not null);
        }

        if (recipe is not null && ContentSeedInputChecks.Normalize(pinnedKey) is null)
        {
            return (null, Pinned: false, FromRecipe: false);
        }

        var (chosen, pinned) = Resolve(token, facet, field, pinnedKey, candidates, keyOf, failures);

        return (chosen, pinned, FromRecipe: false);
    }

    /// <summary>
    /// Resolves one facet over a database-backed catalogue: the pinned entry, or the one this token selects.
    /// </summary>
    private static (T? Chosen, bool Pinned) Resolve<T>(
        string token,
        string facet,
        string field,
        string? pinnedKey,
        IReadOnlyList<T> candidates,
        Func<T, string> keyOf,
        List<(string Field, string Error)> failures)
        where T : class
    {
        var key = ContentSeedInputChecks.Normalize(pinnedKey);
        if (key is null)
        {
            // Nothing pinned: the token chooses. A facet with no active entries is simply absent.
            return (ContentSeedSelector.Select(token, facet, candidates, keyOf), Pinned: false);
        }

        var pinned = candidates.FirstOrDefault(candidate =>
            string.Equals(keyOf(candidate), key, StringComparison.Ordinal));

        if (pinned is null)
        {
            // Retired and unknown are one answer here, because the active set is all this read returned. The
            // code-owned catalogues can tell them apart, and do.
            failures.Add((field, "That is not something CreatorPantry knows, or it is no longer available."));
        }

        return (pinned, Pinned: true);
    }

    /// <summary>
    /// Resolves one facet over a code-owned catalogue, which keeps retired entries and so can say which it is.
    /// </summary>
    private static (T? Chosen, bool Pinned) ResolveCatalogue<T>(
        string token,
        string facet,
        string field,
        string? pinnedKey,
        IReadOnlyList<T> catalogue,
        Func<T, (string Key, bool IsActive)> describe,
        List<(string Field, string Error)> failures)
        where T : class
    {
        var key = ContentSeedInputChecks.Normalize(pinnedKey);
        if (key is null)
        {
            var active = catalogue.Where(entry => describe(entry).IsActive).ToList();

            return (ContentSeedSelector.Select(token, facet, active, entry => describe(entry).Key), Pinned: false);
        }

        var found = catalogue.FirstOrDefault(entry => string.Equals(describe(entry).Key, key, StringComparison.Ordinal));

        if (found is null)
        {
            failures.Add((field, "That is not something CreatorPantry knows."));

            return (null, Pinned: true);
        }

        if (!describe(found).IsActive)
        {
            // Retired entries stay readable where they are already stored, but a seed is a new choice.
            failures.Add((field, "That is no longer available."));

            return (null, Pinned: true);
        }

        return (found, Pinned: true);
    }
}
