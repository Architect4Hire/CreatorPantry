using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;

namespace CreatorPantry.Domain.Modules.Nutrition.Managers;

/// <summary>
/// Finds high-risk methods and unsupported safety or medical claims in one recipe version, in code (14.5,
/// AIREC-GR-005, B-37). Pure: the same version, vocabulary and evidence are the same warnings.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Warnings, never verdicts.</strong> A warning says what the recipe's own record shows and that it needs
/// checking; it never says a method is unsafe, and no warning — or the absence of one — says anything is safe.
/// Every warning cites an approved reference or says verification is required, and today nothing is approved
/// (B-33), so every warning says verification is required. Nothing here blocks a recipe.
/// </para>
/// <para>
/// <strong>Structured choices first, then the creator's words.</strong> A technique the vocabulary flags with
/// <c>RequiresSafetyCaution</c> is a detection on its own. Text is matched by how a method or claim is worded,
/// never by a bare food word: "canned tomatoes", "fermented black beans", "pink Himalayan salt", "baby spinach"
/// and a cheesecake's water bath are ordinary, and each is pinned by a test that it raises nothing.
/// </para>
/// <para>
/// <strong>An ingredient line is something bought.</strong> A line naming kimchi, miso or prosciutto is not the
/// creator fermenting or curing; only a curing agent, a raw-egg or sushi-grade wording, or "pink salt" counts
/// from a line. Methods are read from steps, notes and the recipe's own prose.
/// </para>
/// <para>
/// <strong>Ambiguous wording is <see cref="FoodSafetyConfidence.Possible"/></strong>, and says what it might be;
/// the creator decides. Raw-protein and internal-temperature checks read the ingredient categories only to decide
/// whether a raw or doneness wording is about meat, poultry, seafood or eggs — never to infer a method the text
/// does not state.
/// </para>
/// <para>
/// <strong>A net, not a proof.</strong> A phrase list misses paraphrase it has not heard. A retired technique is
/// not in the active vocabulary this reads, so it raises nothing structurally — its step's text is still read, and
/// the result counts it in <c>UnrecognisedTechniques</c> so an empty list cannot hide it.
/// </para>
/// </remarks>
public static partial class FoodSafetyWarningDetector
{
    private const string MeatAndPoultry = "meat-and-poultry";
    private const string Seafood = "seafood";
    private const string DairyAndEggs = "dairy-and-eggs";
    private const string NoCook = "no-cook";

    private static readonly IReadOnlyDictionary<string, FoodSafetyCategory> TechniqueCategories =
        new Dictionary<string, FoodSafetyCategory>(StringComparer.Ordinal)
        {
            ["water-bath-canning"] = FoodSafetyCategory.Canning,
            ["pressure-canning"] = FoodSafetyCategory.Canning,
            ["ferment"] = FoodSafetyCategory.Fermentation,
            ["cure"] = FoodSafetyCategory.CuringOrSmoking,
            ["smoke"] = FoodSafetyCategory.CuringOrSmoking,
            ["sous-vide"] = FoodSafetyCategory.SousVide,
            ["dehydrate"] = FoodSafetyCategory.Drying,
            ["infuse-oil"] = FoodSafetyCategory.OilInfusion,
        };

    public static FoodSafetyWarningsServiceModel Detect(
        Guid recipeId,
        Guid recipeVersionId,
        FoodSafetyRecipeInput recipe,
        IReadOnlyDictionary<Guid, CookingTechniqueServiceModel> techniques,
        IReadOnlyDictionary<Guid, IngredientCategoryServiceModel> ingredients,
        FoodSafetyEvidenceInput evidence)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(techniques);
        ArgumentNullException.ThrowIfNull(ingredients);
        ArgumentNullException.ThrowIfNull(evidence);

        if (evidence.WhenNotCited is FoodSafetyEvidenceStatus.Cited)
        {
            throw new ArgumentException("A category without a citation cannot be reported as cited.", nameof(evidence));
        }

        var found = new Findings();
        var texts = Texts(recipe).ToList();
        var context = new RecipeContext(
            [.. recipe.Lines.Where(line => ProteinOf(line, ingredients) is not ProteinKind.None)],
            recipe.Lines.Any(line => ProteinOf(line, ingredients) is ProteinKind.Egg),
            recipe.Lines.Any(line => ProteinOf(line, ingredients) is ProteinKind.MeatOrSeafood),
            texts.Any(text => text.Scope is Scope.Prose && StorageCondition().IsMatch(text.Value)));

        var unrecognised = ReadTechniques(found, recipe, techniques, context.ProteinLines);

        foreach (var text in texts)
        {
            ReadText(found, text, context);
        }

        return new FoodSafetyWarningsServiceModel(
            recipeId,
            recipeVersionId,
            recipe.VersionNumber,
            FoodSafetyWarningPolicy.MethodVersion,
            [.. found.Build(evidence)],
            unrecognised,
            FoodSafetyWarningPolicy.Disclaimer);
    }

    private enum ProteinKind
    {
        None,
        MeatOrSeafood,
        Egg,
    }

    /// <param name="ProteinLines">Lines whose platform ingredient is meat, poultry, seafood or egg.</param>
    /// <param name="HasMeatOrSeafood">
    /// Scopes the internal-temperature check. Eggs alone do not: a cake's thermometer reading is about the crumb.
    /// </param>
    /// <param name="StorageStated">Whether any of the recipe's prose says how the food is kept.</param>
    private sealed record RecipeContext(
        IReadOnlyList<FoodSafetyLineInput> ProteinLines, bool HasEggs, bool HasMeatOrSeafood, bool StorageStated)
    {
        public bool HasProtein => ProteinLines.Count > 0;
    }

    // ---- Structured choices ----

    /// <returns>How many chosen techniques are not in the active vocabulary, and so could not be read.</returns>
    private static int ReadTechniques(
        Findings found,
        FoodSafetyRecipeInput recipe,
        IReadOnlyDictionary<Guid, CookingTechniqueServiceModel> techniques,
        IReadOnlyList<FoodSafetyLineInput> proteinLines)
    {
        var chosen = new List<(FoodSafetyField Field, Guid? ItemId, CookingTechniqueServiceModel Technique)>();
        var unrecognised = 0;

        foreach (var (field, itemId, id) in recipe.Steps
            .Select(step => (FoodSafetyField.StepTechnique, (Guid?)step.Id, step.TechniqueId))
            .Prepend((FoodSafetyField.PrimaryTechnique, null, recipe.PrimaryTechniqueId)))
        {
            if (id is null)
            {
                continue;
            }

            if (techniques.TryGetValue(id.Value, out var technique))
            {
                chosen.Add((field, itemId, technique));
            }
            else
            {
                unrecognised++;
            }
        }

        foreach (var (field, itemId, technique) in chosen)
        {
            var trigger = new FoodSafetyTriggerServiceModel(field, itemId, FoodSafetyTriggerKind.Technique, technique.Code);

            if (TechniqueCategories.TryGetValue(technique.Code, out var category))
            {
                found.Add(category, FoodSafetyConfidence.Detected, trigger);
            }
            else if (technique.Code == NoCook)
            {
                // No-cook is not itself a caution; with meat, poultry, seafood or eggs in the recipe it is raw service.
                if (proteinLines.Count > 0)
                {
                    found.Add(FoodSafetyCategory.RawOrUndercookedProtein, FoodSafetyConfidence.Detected, trigger);
                    AddProteinLines(found, proteinLines);
                }
            }
            else if (technique.RequiresSafetyCaution)
            {
                found.Add(FoodSafetyCategory.OtherFlaggedMethod, FoodSafetyConfidence.Detected, trigger, technique.DisplayName);
            }
        }

        return unrecognised;
    }

    private static ProteinKind ProteinOf(FoodSafetyLineInput line, IReadOnlyDictionary<Guid, IngredientCategoryServiceModel> ingredients) =>
        line.IngredientId is { } id && ingredients.TryGetValue(id, out var ingredient)
            ? ingredient.FoodCategoryCode switch
            {
                MeatAndPoultry or Seafood => ProteinKind.MeatOrSeafood,
                DairyAndEggs when EggName().IsMatch(ingredient.CanonicalName) => ProteinKind.Egg,
                _ => ProteinKind.None,
            }
            : ProteinKind.None;

    private static void AddProteinLines(Findings found, IReadOnlyList<FoodSafetyLineInput> proteinLines)
    {
        foreach (var line in proteinLines)
        {
            found.Add(
                FoodSafetyCategory.RawOrUndercookedProtein,
                FoodSafetyConfidence.Possible,
                new FoodSafetyTriggerServiceModel(FoodSafetyField.IngredientLine, line.Id, FoodSafetyTriggerKind.IngredientCategory, Quote(line.DisplayText)));
        }
    }

    // ---- The creator's words ----

    private enum Scope
    {
        Prose,
        IngredientLine,
        Equipment,
    }

    private sealed record ScannedText(FoodSafetyField Field, Guid? ItemId, string Value, Scope Scope);

    private static IEnumerable<ScannedText> Texts(FoodSafetyRecipeInput recipe)
    {
        IEnumerable<ScannedText> All()
        {
            yield return new(FoodSafetyField.Title, null, recipe.Title, Scope.Prose);
            yield return new(FoodSafetyField.Description, null, recipe.Description ?? string.Empty, Scope.Prose);
            yield return new(FoodSafetyField.Headnote, null, recipe.Headnote ?? string.Empty, Scope.Prose);

            foreach (var line in recipe.Lines)
            {
                yield return new(FoodSafetyField.IngredientLine, line.Id, line.DisplayText, Scope.IngredientLine);
                yield return new(FoodSafetyField.IngredientPreparation, line.Id, line.PreparationNote ?? string.Empty, Scope.Prose);
            }

            foreach (var item in recipe.Equipment)
            {
                yield return new(FoodSafetyField.Equipment, item.Id, item.DisplayText, Scope.Equipment);
                yield return new(FoodSafetyField.EquipmentNote, item.Id, item.Note ?? string.Empty, Scope.Prose);
            }

            foreach (var step in recipe.Steps)
            {
                yield return new(FoodSafetyField.Step, step.Id, step.Text, Scope.Prose);
                yield return new(FoodSafetyField.StepNote, step.Id, step.Note ?? string.Empty, Scope.Prose);
            }

            yield return new(FoodSafetyField.Notes, null, recipe.Notes ?? string.Empty, Scope.Prose);
            yield return new(FoodSafetyField.StorageNotes, null, recipe.StorageNotes ?? string.Empty, Scope.Prose);
        }

        return All()
            .Where(text => !string.IsNullOrWhiteSpace(text.Value))
            .Select(text => text with { Value = Canonicalize(text.Value) });
    }

    private static void ReadText(Findings found, ScannedText text, RecipeContext recipe)
    {
        var at = new Hits(found, text);

        if (text.Scope is Scope.Equipment)
        {
            at.Matches(FoodSafetyCategory.Canning, FoodSafetyConfidence.Detected, CanningEquipment());
            at.Matches(FoodSafetyCategory.Fermentation, FoodSafetyConfidence.Detected, FermentationEquipment());
            at.Matches(FoodSafetyCategory.CuringOrSmoking, FoodSafetyConfidence.Detected, CuringEquipment());
            at.Matches(FoodSafetyCategory.SousVide, FoodSafetyConfidence.Detected, SousVideWords());
            at.Matches(FoodSafetyCategory.Drying, FoodSafetyConfidence.Detected, DryingEquipment());
            return;
        }

        // From any field, a line included: a curing agent is the method's own ingredient.
        at.Matches(FoodSafetyCategory.CuringOrSmoking, FoodSafetyConfidence.Detected, CuringAgent());
        if (!at.Has(FoodSafetyCategory.CuringOrSmoking))
        {
            at.Matches(FoodSafetyCategory.CuringOrSmoking, FoodSafetyConfidence.Possible, PinkSalt(), sentence => !OtherPinkSalt().IsMatch(sentence));
        }

        ReadRawProtein(at, text, recipe);

        if (text.Scope is Scope.IngredientLine)
        {
            return;
        }

        // Canning. A freezer's headspace and a blender jar's "process for 30 seconds" are not it.
        at.Matches(FoodSafetyCategory.Canning, FoodSafetyConfidence.Detected, CanningWords(), sentence => !NotCanning().IsMatch(sentence));
        if (!at.Has(FoodSafetyCategory.Canning))
        {
            at.Sentences(FoodSafetyCategory.Canning, FoodSafetyConfidence.Possible, sentence =>
                Jar().IsMatch(sentence)
                && !Refrigerated().IsMatch(sentence)
                && (WaterBath().IsMatch(sentence) || (SealOrLid().IsMatch(sentence) && ShelfStorage().IsMatch(sentence))));
        }

        // Fermentation. Bread dough, starters and batters ferment too, and are not what this check is about.
        at.Matches(FoodSafetyCategory.Fermentation, FoodSafetyConfidence.Detected, FermentationWords(), sentence => !BreadContext().IsMatch(sentence));
        if (!at.Has(FoodSafetyCategory.Fermentation))
        {
            at.Matches(FoodSafetyCategory.Fermentation, FoodSafetyConfidence.Possible, LeftOutForDays(),
                sentence => !BreadContext().IsMatch(sentence) && !Refrigerated().IsMatch(sentence));
        }

        // Curing and smoking, as a method.
        at.Matches(FoodSafetyCategory.CuringOrSmoking, FoodSafetyConfidence.Detected, CuringWords(), sentence => !NotFoodCuring().IsMatch(sentence));

        // Sous vide.
        at.Matches(FoodSafetyCategory.SousVide, FoodSafetyConfidence.Detected, SousVideWords());
        if (!at.Has(FoodSafetyCategory.SousVide))
        {
            at.Sentences(FoodSafetyCategory.SousVide, FoodSafetyConfidence.Possible, sentence =>
                WaterBath().IsMatch(sentence)
                && Hours().IsMatch(sentence)
                && !BakingWaterBath().IsMatch(sentence)
                && Temperatures(sentence).Any(IsLowHoldingTemperature));
        }

        ReadInternalTemperature(at, recipe);

        // Pregnancy and medical-diet claims, then mere mentions.
        at.Matches(FoodSafetyCategory.PregnancyOrMedicalDiet, FoodSafetyConfidence.Detected, MedicalClaim());
        if (!at.Has(FoodSafetyCategory.PregnancyOrMedicalDiet))
        {
            at.Matches(FoodSafetyCategory.PregnancyOrMedicalDiet, FoodSafetyConfidence.Possible, MedicalMention());
        }

        // Safety and keeping claims, then a keeping time where nothing in the recipe says how the food is kept.
        // Dough, bread and biscuits that need no fridge are ordinary baking, not a claim about keeping.
        at.Matches(FoodSafetyCategory.UnsupportedSafetyClaim, FoodSafetyConfidence.Detected, SafetyClaim(), sentence => !FoodSafeObject().IsMatch(sentence));
        at.Matches(FoodSafetyCategory.UnsupportedSafetyClaim, FoodSafetyConfidence.Detected, NoRefrigerationClaim(), sentence => !ShelfNormalBaking().IsMatch(sentence));
        if (!at.Has(FoodSafetyCategory.UnsupportedSafetyClaim) && !recipe.StorageStated)
        {
            at.Matches(FoodSafetyCategory.UnsupportedSafetyClaim, FoodSafetyConfidence.Possible, KeepingTime());
        }

        at.Matches(FoodSafetyCategory.Drying, FoodSafetyConfidence.Detected, DryingWords());

        // An oil infused and used at once — a tadka, a stir-fry — is cooking. The caution is about keeping it.
        if (OilKept().IsMatch(text.Value))
        {
            at.Sentences(FoodSafetyCategory.OilInfusion, FoodSafetyConfidence.Detected, sentence =>
                InfuseOil().IsMatch(sentence) && Aromatic().IsMatch(sentence));
        }

        at.Matches(FoodSafetyCategory.OilInfusion, FoodSafetyConfidence.Detected, GarlicConfit());
        if (!at.Has(FoodSafetyCategory.OilInfusion))
        {
            at.Matches(FoodSafetyCategory.OilInfusion, FoodSafetyConfidence.Possible, InfusedOil());
        }
    }

    private static void ReadRawProtein(Hits at, ScannedText text, RecipeContext recipe)
    {
        // A raw dish, raw eggs that are not cooked afterwards, a runny yolk, or "served raw" about a protein:
        // detected when the recipe has meat, poultry, seafood or eggs, and possible when its lines do not say.
        // A caution the creator wrote ("never serve raw chicken") is not raw service.
        var named = RawDish().Matches(text.Value).Cast<Match>()
            .Concat(RawEgg().Matches(text.Value).Cast<Match>().Where(match => !CookedAfterwards().IsMatch(SentenceAround(text.Value, match))))
            .Concat(ServedRaw().Matches(text.Value).Cast<Match>().Where(match => ProteinWord().IsMatch(SentenceAround(text.Value, match))))
            .OrderBy(match => match.Index)
            .ToList();

        if (named.Count > 0)
        {
            var confidence = recipe.HasProtein ? FoodSafetyConfidence.Detected : FoodSafetyConfidence.Possible;
            foreach (var match in named)
            {
                at.Add(FoodSafetyCategory.RawOrUndercookedProtein, confidence, match.Value);
            }

            if (recipe.HasProtein)
            {
                AddProteinLines(at.Found, recipe.ProteinLines);
            }

            return;
        }

        // "Raw chicken" alone is not here: it is how ordinary recipes say what goes into the pan.
        at.Matches(FoodSafetyCategory.RawOrUndercookedProtein, FoodSafetyConfidence.Possible, Rare());

        // Dishes often made with raw egg, and often not: possible, and only where the recipe has eggs to make them with.
        if (text.Scope is Scope.Prose && recipe.HasEggs)
        {
            at.Matches(FoodSafetyCategory.RawOrUndercookedProtein, FoodSafetyConfidence.Possible, RawEggDish());
        }
    }

    private static void ReadInternalTemperature(Hits at, RecipeContext recipe)
    {
        var fieldStatesAFigure = Temperatures(at.Text.Value).Any();
        var sentences = Sentences(at.Text.Value).ToList();

        for (var i = 0; i < sentences.Count; i++)
        {
            var sentence = sentences[i];
            // "Cook the chicken to 165°F. Check the thickest part." names its protein one sentence away.
            var aboutProtein = new[] { i - 1, i, i + 1 }.Any(j => j >= 0 && j < sentences.Count && ProteinWord().IsMatch(sentences[j]));
            if (!(recipe.HasMeatOrSeafood || aboutProtein) || (BakedGood().IsMatch(sentence) && !aboutProtein))
            {
                continue;
            }

            // A doneness cue ("internal temperature", "thickest part") is read whatever else the sentence mentions.
            // A bare "thermometer" or "reads" is not, beside a pan of oil, a syrup or a preheating oven.
            var strong = DonenessWords().Match(sentence);
            var cue = strong.Success ? strong
                : ThermometerReading().Match(sentence) is { Success: true } weak && !NotAnInternalTemperature().IsMatch(sentence) ? weak
                : null;

            if (cue is null)
            {
                continue;
            }

            var stated = Temperatures(sentence).ToList();
            if (stated.Count > 0)
            {
                // The figures the cue is about: those after it ("until the thickest part reads 165°F", not the 425°F
                // oven before it), or all of them when it comes last ("165°F internal").
                var afterCue = stated.Where(temperature => temperature.Index > cue.Index).ToList();
                Record(at, sentence, afterCue.Count > 0 ? afterCue : stated);
            }
            else if (strong.Success)
            {
                // "Cook the chicken to 165°F. Check with a thermometer in the thickest part." — the figure is beside it.
                var beside = new[] { i - 1, i + 1 }
                    .Where(j => j >= 0 && j < sentences.Count && !NotAnInternalTemperature().IsMatch(sentences[j]))
                    .Select(j => Temperatures(sentences[j]).ToList())
                    .FirstOrDefault(figures => figures.Count > 0);

                if (beside is not null)
                {
                    Record(at, sentence, beside);
                }
                else if (!fieldStatesAFigure)
                {
                    at.Add(FoodSafetyCategory.InternalTemperature, FoodSafetyConfidence.Possible, sentence);
                }
            }
        }

        static void Record(Hits at, string sentence, IEnumerable<(decimal Value, FoodSafetyTemperatureScale? Scale, int Index)> figures)
        {
            at.Add(FoodSafetyCategory.InternalTemperature, FoodSafetyConfidence.Detected, sentence);
            foreach (var (value, scale, _) in figures)
            {
                if (scale is { } known)
                {
                    at.Found.Temperature(new FoodSafetyStatedTemperatureServiceModel(value, known, at.Text.Field, at.Text.ItemId));
                }
            }
        }
    }

    // ---- Text helpers ----

    /// <summary>One field's hits, written through to the findings with that field's location.</summary>
    private sealed class Hits(Findings found, ScannedText text)
    {
        private readonly HashSet<FoodSafetyCategory> _here = [];

        public Findings Found => found;

        public ScannedText Text => text;

        public bool Has(FoodSafetyCategory category) => _here.Contains(category);

        public void Add(FoodSafetyCategory category, FoodSafetyConfidence confidence, string matched)
        {
            _here.Add(category);
            found.Add(category, confidence, new FoodSafetyTriggerServiceModel(text.Field, text.ItemId, FoodSafetyTriggerKind.Text, Quote(matched)));
        }

        public void Matches(FoodSafetyCategory category, FoodSafetyConfidence confidence, Regex pattern, Func<string, bool>? sentenceAllows = null)
        {
            foreach (Match match in pattern.Matches(text.Value))
            {
                if (sentenceAllows is null || sentenceAllows(SentenceAround(text.Value, match)))
                {
                    Add(category, confidence, match.Value);
                }
            }
        }

        public void Sentences(FoodSafetyCategory category, FoodSafetyConfidence confidence, Func<string, bool> matches)
        {
            foreach (var sentence in FoodSafetyWarningDetector.Sentences(text.Value).Where(matches))
            {
                Add(category, confidence, sentence);
            }
        }
    }

    /// <summary>
    /// Compatibility forms, dashes, quotes and invisible characters folded, so "℉", fullwidth digits, a non-breaking
    /// hyphen or a zero-width space do not slip past a phrase. Line breaks are kept: they end a sentence.
    /// </summary>
    private static string Canonicalize(string value)
    {
        // Ordinal and ring marks are written as degree signs; folded first, because compatibility normalization
        // would turn "º" into the letter o.
        var normalized = value.Replace('º', '°').Replace('˚', '°').Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);

        foreach (var c in normalized)
        {
            switch (c)
            {
                case '­' or '​' or '‌' or '‍' or '⁠' or '﻿':
                    break;
                case >= '‐' and <= '―' or '−':
                    builder.Append('-');
                    break;
                case '‘' or '’':
                    builder.Append('\'');
                    break;
                case '\r' or '\n':
                    builder.Append('\n');
                    break;
                default:
                    builder.Append(char.IsWhiteSpace(c) ? ' ' : c);
                    break;
            }
        }

        return builder.ToString();
    }

    private static bool IsBoundary(string text, int i) =>
        text[i] is '\n' or ';' or '!' or '?' || (text[i] == '.' && (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])));

    private static string SentenceAround(string text, Match match)
    {
        var begin = match.Index;
        while (begin > 0 && !IsBoundary(text, begin - 1))
        {
            begin--;
        }

        var end = match.Index + match.Length;
        while (end < text.Length && !IsBoundary(text, end))
        {
            end++;
        }

        return text[begin..end];
    }

    private static IEnumerable<string> Sentences(string text)
    {
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || IsBoundary(text, i))
            {
                var sentence = text[start..i].Trim();
                if (sentence.Length > 0)
                {
                    yield return sentence;
                }

                start = i + 1;
            }
        }
    }

    private static string Quote(string value)
    {
        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Length <= FoodSafetyWarningPolicy.MaxMatchedLength
            ? collapsed
            : string.Concat(collapsed.AsSpan(0, FoodSafetyWarningPolicy.MaxMatchedLength - 1), "…");
    }

    /// <summary>
    /// Every temperature written in <paramref name="text"/>, in order, with where it starts. Both ends of a range
    /// ("145-160°F") are read, the lower taking the upper's scale; the scale is null for a bare "165°".
    /// </summary>
    private static IEnumerable<(decimal Value, FoodSafetyTemperatureScale? Scale, int Index)> Temperatures(string text) =>
        Temperature().Matches(text).Cast<Match>()
            .Concat(TemperatureRangeLow().Matches(text).Cast<Match>())
            .OrderBy(match => match.Index)
            .Select(match => (
                decimal.Parse(match.Groups["value"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture),
                match.Groups["scale"] is { Success: true } group
                    ? group.Value[0] is 'f' or 'F' ? FoodSafetyTemperatureScale.Fahrenheit : FoodSafetyTemperatureScale.Celsius
                    : (FoodSafetyTemperatureScale?)null,
                match.Index));

    /// <summary>A water bath held where sous vide holds one, not where a stove or oven does.</summary>
    private static bool IsLowHoldingTemperature((decimal Value, FoodSafetyTemperatureScale? Scale, int Index) temperature) =>
        (temperature.Value, temperature.Scale) switch
        {
            (>= 40m and <= 75m, FoodSafetyTemperatureScale.Celsius) => true,
            (>= 104m and <= 167m, FoodSafetyTemperatureScale.Fahrenheit) => true,
            _ => false,
        };

    // ---- Findings ----

    private sealed class Findings
    {
        private readonly Dictionary<FoodSafetyCategory, Finding> _byCategory = [];

        public void Add(FoodSafetyCategory category, FoodSafetyConfidence confidence, FoodSafetyTriggerServiceModel trigger, string? techniqueName = null)
        {
            if (!_byCategory.TryGetValue(category, out var finding))
            {
                _byCategory[category] = finding = new Finding();
            }

            finding.Confidence = (FoodSafetyConfidence)Math.Max((int)finding.Confidence, (int)confidence);
            finding.TechniqueName ??= techniqueName;

            if (!finding.Triggers.Any(existing => existing.Field == trigger.Field
                && existing.ItemId == trigger.ItemId
                && existing.Kind == trigger.Kind
                && string.Equals(existing.Matched, trigger.Matched, StringComparison.OrdinalIgnoreCase)))
            {
                finding.Triggers.Add(trigger);
            }
        }

        public void Temperature(FoodSafetyStatedTemperatureServiceModel temperature)
        {
            var finding = _byCategory[FoodSafetyCategory.InternalTemperature];
            if (!finding.Temperatures.Contains(temperature))
            {
                finding.Temperatures.Add(temperature);
            }
        }

        public IEnumerable<FoodSafetyWarningServiceModel> Build(FoodSafetyEvidenceInput evidence) =>
            _byCategory.OrderBy(entry => entry.Key).Select(entry => new FoodSafetyWarningServiceModel(
                entry.Key,
                entry.Value.Confidence,
                FoodSafetyWarningPolicy.Message(entry.Key, entry.Value.Confidence, entry.Value.TechniqueName),
                [.. entry.Value.Triggers],
                EvidenceFor(entry.Key, evidence),
                [.. entry.Value.Temperatures]));

        private static FoodSafetyEvidenceServiceModel EvidenceFor(FoodSafetyCategory category, FoodSafetyEvidenceInput evidence) =>
            evidence.Citations.TryGetValue(category, out var citation)
                ? new(FoodSafetyEvidenceStatus.Cited, citation, FoodSafetyWarningPolicy.Statement(FoodSafetyEvidenceStatus.Cited, citation))
                : new(evidence.WhenNotCited, null, FoodSafetyWarningPolicy.Statement(evidence.WhenNotCited));

        private sealed class Finding
        {
            public FoodSafetyConfidence Confidence { get; set; } = FoodSafetyConfidence.Possible;

            public string? TechniqueName { get; set; }

            public List<FoodSafetyTriggerServiceModel> Triggers { get; } = [];

            public List<FoodSafetyStatedTemperatureServiceModel> Temperatures { get; } = [];
        }
    }

    // ---- Phrases. Each is a closed list: what it misses is a gap to add, never a reason to match bare words. ----

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"\begg(?:s|\s+whites?|\s+yolks?)?\b|\byolks?\b", Options)]
    private static partial Regex EggName();

    [GeneratedRegex(
        @"\b(?:eggs?|yolks?|egg whites?|beef|steaks?|veal|tuna|salmon|fish|scallops?|oysters?|clams?|mussels?|shrimp|prawns?|crabs?|lobsters?|meats?|lamb|venison|chickens?|poultry|turkeys?|duck|pork|seafood|shellfish|liver|sausages?|bacon|ham|burgers?|patties|cod|halibut|trout|snapper|squid|octopus|bison|goat|rabbit|mince|thighs?|breasts?|drumsticks?)\b",
        Options)]
    private static partial Regex ProteinWord();

    // A scale glued to the figure ("165F"), after a degree word or sign, or — capitals only, so "12 c" stays cups —
    // after a space ("165 F").
    [GeneratedRegex(
        @"(?<value>\d{2,3}(?:\.\d+)?)(?:\s*(?:°|degrees?\b|deg\b\.?)\s*(?:(?<scale>fahrenheit|celsius|centigrade|f|c)\b)?|(?<scale>f|c)\b|\s+(?-i:(?<scale>F|C))\b)",
        Options)]
    private static partial Regex Temperature();

    /// <summary>The lower end of a range whose upper end carries the scale: "145" in "145-160°F" or "145 to 160 F".</summary>
    [GeneratedRegex(
        @"(?<value>\d{2,3}(?:\.\d+)?)\s*(?:-|to)\s*(?=\d{2,3}(?:\.\d+)?(?:\s*(?:°|degrees?\b|deg\b\.?)\s*(?:(?<scale>fahrenheit|celsius|centigrade|f|c)\b)?|(?<scale>f|c)\b|\s+(?-i:(?<scale>F|C))\b))",
        Options)]
    private static partial Regex TemperatureRangeLow();

    /// <summary>
    /// A hold long enough to matter: hours, overnight, or a figure in minutes from 45 upwards (14.7, M-6).
    /// </summary>
    /// <remarks>
    /// Minutes were missing, so "hold at 63 °C in a water bath for 90 minutes" — a sous vide by any reading —
    /// raised nothing at all, because the rule wanted the literal word "hours". The floor is there because a
    /// low water bath for ten minutes is a tempering or a melting step, not a cook.
    /// </remarks>
    [GeneratedRegex(
        @"\b\d+(?:\.\d+)?\s*(?:-\s*\d+\s*)?(?:hours?|hrs?)\b|\bovernight\b"
            + @"|\b(?:4[5-9]|[5-9]\d|\d{3,})\s*(?:minutes?|mins?)\b",
        Options)]
    private static partial Regex Hours();

    // Canning.

    [GeneratedRegex(
        @"\bwater[- ]bath[- ]can(?:ning|ned|ner)?\b|\bboiling[- ]water[- ](?:bath|canner|canning|process(?:ing)?)\b|\bpressure[- ]can(?:ning|ned|ner|s)?\b|\bhome[- ]can(?:ning|ned)\b|\bcanning\b(?!\s+(?:salt|jars?|lids?))|\bheadspace\b|\bprocess(?:ed|ing)?\s+(?:the\s+)?(?:filled\s+|sealed\s+|closed\s+)?jars\b|\bjars?\b[^.;!?\n]{0,40}?\bprocess(?:ed|ing)?\s+(?:for|in)\b",
        Options)]
    private static partial Regex CanningWords();

    [GeneratedRegex(@"\bfreez\w*|\bfrozen\b|\bblender\b|\bfood processor\b|\bprocessor\b|\bblend\w*", Options)]
    private static partial Regex NotCanning();

    [GeneratedRegex(@"\b(?:pressure|water[- ]bath|boiling[- ]water)\s+canner\b|\bcanner\b|\bjar lifter\b|\bcanning rack\b", Options)]
    private static partial Regex CanningEquipment();

    [GeneratedRegex(@"\bjars?\b", Options)]
    private static partial Regex Jar();

    [GeneratedRegex(@"\bseal(?:s|ed|ing)?\b|\blids?\b", Options)]
    private static partial Regex SealOrLid();

    [GeneratedRegex(@"\bpantry\b|\bcupboard\b|\bshelf\b|\bcool,? dark\b|\broom temperature\b|\bboiling\b", Options)]
    private static partial Regex ShelfStorage();

    [GeneratedRegex(@"\bwater bath\b", Options)]
    private static partial Regex WaterBath();

    [GeneratedRegex(@"\bfridge\b|\brefrigerat\w*|\bfreez\w*|\bchill(?:ed)?\b", Options)]
    private static partial Regex Refrigerated();

    // Fermentation.

    [GeneratedRegex(
        @"\blacto[- ]?ferment\w*|\bscoby\b|\b(?:let|leave|allow|set)\b[^.;!?\n]{0,40}?\bferment(?:s|ing)?\b|\bferment(?:s|ing)?\s+(?:for|at|until|in|on|somewhere|undisturbed|out)\b|\bfermentation\b|\bfermenting\b|\bto ferment\b",
        Options)]
    private static partial Regex FermentationWords();

    [GeneratedRegex(@"\bfermentation\s+(?:crock|weights?|lids?|jar)\b|\bfermenting crock\b|\bairlocks?\b|\bpickling weights?\b", Options)]
    private static partial Regex FermentationEquipment();

    [GeneratedRegex(
        @"\broom temperature for (?:\d+|a few|several|one|two|three|four|five|six|seven|ten|fourteen)\s*(?:to\s*\d+\s*)?(?:days?|weeks?)\b|\b(?:leave|let\s+(?:it|them|the\s+\w+)\s+(?:sit|stand|rest)|set aside|leave out)\b[^.;!?\n]{0,40}?\bfor (?:\d+|a few|several|one|two|three|four|five|six|seven)\s*(?:to\s*\d+\s*)?(?:days?|weeks?)\b",
        Options)]
    private static partial Regex LeftOutForDays();

    [GeneratedRegex(@"\b(?:dough|bulk|proof\w*|rise|rising|starter|levain|loaf|loaves|yeast|poolish|biga|sourdough|bread|batter)\b", Options)]
    private static partial Regex BreadContext();

    // Curing and smoking.

    // Not when a label says it is absent: "no sodium nitrite added", "sodium nitrite-free".
    [GeneratedRegex(
        @"(?<!\b(?:no|without|free of|zero)\s+(?:added\s+)?)\b(?:curing salt|prague powder|insta[- ]?cure|sodium nitri(?:te|tes)|sodium nitrate|saltpet(?:re|er))\b(?![- ]free)|\bcure\s*(?:#|no\.?\s*|number\s*)[12]\b",
        Options)]
    private static partial Regex CuringAgent();

    [GeneratedRegex(
        @"\b(?:dry|salt|sugar|wet)[- ]cur(?:e|es|ing)\b|\bcur(?:e|ing)\s+(?:the|it|them|for|in|overnight)\b|\bgravl(?:ax|ox)\b|\bcold[- ]smok(?:e|ed|ing)\b|\bsmok(?:e|ing)\s+(?:the|it|them|for|at|low)\b|\bhome[- ]cured\b",
        Options)]
    private static partial Regex CuringWords();

    [GeneratedRegex(@"\bcast[- ]iron\b|\bskillet\b|\bwok\b|\bpan\b|\bconcrete\b|\bresin\b|\bglaze\b|\bcommon cold\b", Options)]
    private static partial Regex NotFoodCuring();

    [GeneratedRegex(@"\bsmoker\b|\bcuring chamber\b|\bcold[- ]smoke generator\b", Options)]
    private static partial Regex CuringEquipment();

    [GeneratedRegex(@"\bpink salt\b", Options)]
    private static partial Regex PinkSalt();

    [GeneratedRegex(@"\bhimalayan\b|\brock\b|\bsea\b|\btable\b|\bfinishing\b|\bflak(?:y|ed)\b|\bkosher\b|\bmineral\b", Options)]
    private static partial Regex OtherPinkSalt();

    // Sous vide.

    [GeneratedRegex(@"\bsous[- ]?vide\b|\bimmersion circulator\b|\bprecision cooker\b|\bimmersion cook(?:er|ing)\b", Options)]
    private static partial Regex SousVideWords();

    [GeneratedRegex(@"\boven\b|\bbak(?:e|ed|ing)\b|\bcheesecake\b|\bcustards?\b|\bramekins?\b|\bjars?\b|\broasting pan\b|\bbain[- ]marie\b|\bchocolate\b|\bmelt\w*|\bproof\w*|\byeast\b|\bdough\b", Options)]
    private static partial Regex BakingWaterBath();

    // Raw and undercooked protein.

    // A vegetable or fruit carpaccio, ceviche, crudo or tartare is a dish name, not raw protein.
    [GeneratedRegex(
        @"(?<!\b(?:beet|beetroot|pineapple|mushroom|tomato|zucchini|courgette|melon|watermelon|fruit|vegetable|cauliflower|kohlrabi|radish|fennel|citrus|avocado)\s+)\b(?:(?:(?:steak|beef|tuna|salmon|venison)\s+)?tartare\b(?!\s+sauce)|carpaccio\b|ceviche\b|crudo\b)|\bsashimi\b(?![- ]grade)|\btiradito\b|\bkibbeh nayyeh\b|\byukhoe\b|\bkitfo\b|\brunny yolks?\b",
        Options)]
    private static partial Regex RawDish();

    // Not a caution the creator wrote ("do not eat the raw egg").
    [GeneratedRegex(
        @"(?<!\b(?:not|never|don't|avoid|no)\s+(?:\w+\s+){0,2})\b(?:raw|uncooked)\s+(?:eggs?|egg yolks?|yolks?|egg whites?)\b(?!\s+wash)",
        Options)]
    private static partial Regex RawEgg();

    /// <summary>A raw egg that goes into something cooked — a meatloaf, a glaze brushed on and baked.</summary>
    [GeneratedRegex(@"\bbak\w*|\bfr(?:y|ied|ying)\b|\bboil\w*|\broast\w*|\bsimmer\w*|\bscrambl\w*|\bbrush\w*|\bpoach\w*|\bcook\w*|\bmeat ?loaf\b|\bmeatballs?\b|\bbinder\b", Options)]
    private static partial Regex CookedAfterwards();

    [GeneratedRegex(
        @"(?<!\b(?:not|never|don't|avoid|shouldn't)\s+(?:\w+\s+){0,2})\b(?:serve[ds]?|eaten|enjoy(?:ed)?)\s+raw\b",
        Options)]
    private static partial Regex ServedRaw();

    [GeneratedRegex(
        @"\bmedium[- ]rare\b|\b(?:cook(?:ed)?|sear(?:ed)?|grill(?:ed)?|serve[ds]?|keep it|until)\s+(?:it\s+|them\s+|just\s+)?rare\b|\brare\s+(?:steak|beef|lamb|duck|tuna|venison|in the (?:middle|center|centre))\b|\b(?:sushi|sashimi)[- ]grade\b|\bsoft[- ](?:boiled|scrambled)\s+eggs?\b|\bjammy\s+(?:eggs?|yolks?)\b|(?<!\b(?:no longer|not)\s+)\b(?:pink|raw)\s+(?:in the (?:middle|center|centre)|inside|at the (?:center|centre))\b|\b(?:center|centre|middle|inside)\s+(?:should\s+|will\s+)?(?:stay|stays|remain|remains|be|is)\s+(?:still\s+)?(?:pink|raw|rare)\b|\bundercooked\b|\bpoke bowls?\b|\braw\s+(?:oysters?|clams?)\b",
        Options)]
    private static partial Regex Rare();

    // Often made with raw egg and often not. Mayonnaise and aioli only when the recipe makes them.
    [GeneratedRegex(
        @"\b(?:hollandaise|b[ée]arnaise|caesar dressing|tiramisu|mousse|eggnog|zabaglione|sabayon|carbonara)\b|\b(?:homemade|from[- ]scratch)\s+(?:mayonnaise|mayo|aioli)\b|\b(?:make|making|whisk\w*|emulsif\w*|blend\w*)\b[^.;!?\n]{0,40}?\b(?:mayonnaise|mayo|aioli)\b",
        Options)]
    private static partial Regex RawEggDish();

    // Internal temperature.

    [GeneratedRegex(
        @"\binternal(?:\s+temp(?:erature)?)?\b|\bcore temp(?:erature)?\b|\b(?:instant[- ]read|meat|probe|digital)\s+thermometer\b|\bthermometer\s+(?:inserted|reads|registers|says)\b|\bthickest part\b",
        Options)]
    private static partial Regex DonenessWords();

    [GeneratedRegex(@"\bthermometer\b|\bregisters?\b|\breads?\s+\d", Options)]
    private static partial Regex ThermometerReading();

    [GeneratedRegex(
        @"\bpreheat\w*|\boven thermometer\b|\b(?:heat|bring)\s+(?:the\s+)?(?:oil|fat|lard)\b|\b(?:oil|fat|lard)\s+(?:to|reaches|registers|temperature)\b|\bfrying\b|\bdeep[- ]fr\w+|\bcandy thermometer\b|\bsugar thermometer\b|\bsyrup\b|\bcaramel\b|\bsoft[- ]ball\b|\bhard[- ]crack\b|\bjam\b|\bset(?:ting)? point\b|\bchocolate\b|\btemper(?:ed|ing)?\b|\bmilk\b",
        Options)]
    private static partial Regex NotAnInternalTemperature();

    /// <summary>A bake whose thermometer reading is about the crumb. Skipped unless the sentence also names a protein.</summary>
    [GeneratedRegex(@"\bbread\b(?!\s*crumbs)|\bloaf\b|\bloaves\b|\bcakes?\b|\bbrioche\b|\bcrumb\b|\bpizza\b|\bmuffins?\b|\brolls\b|\bfocaccia\b|\bbagels?\b", Options)]
    private static partial Regex BakedGood();

    // Pregnancy and medical diets.

    [GeneratedRegex(
        @"\b(?:pregnancy|pregnant)[- ](?:safe|approved)\b|\b(?:baby|infant|toddler)[- ]safe\b|\b(?:diabetic|diabetes|celiac|coeliac|kidney|renal|ibs|fodmap)[- ](?:safe|friendly|approved)\b|\b(?:heart|gut|liver)[- ]safe\b|\b(?:safe|suitable|ideal|perfect|good|great|fine|okay|ok|appropriate|recommended|approved)\s+(?:to\s+eat\s+)?(?:for|during|while)\s+(?:a\s+|your\s+)?(?:pregnan\w+|expecting mothers?|moms?[- ]to[- ]be|mums?[- ]to[- ]be|breastfeeding|nursing mothers?|diabetics?|celiacs?|coeliacs?|(?:people|anyone|those) with (?:diabetes|celiac|coeliac|kidney disease|ibs|crohn'?s|heart disease|high blood pressure)|(?:diabetic|renal|cardiac|low[- ]fodmap|gestational diabetes) diets?|gestational diabetes)\b|\b(?:safe|suitable|appropriate|recommended|fine|okay|ok)\s+(?:to\s+(?:eat|serve)\s+)?for\s+(?:babies|infants|toddlers|young children|little ones)\b|\b(?:cures?|treats?|prevents?|heals?|reverses?|fights?)\s+(?:the\s+)?(?:common\s+)?(?:colds?|flu|cancer|diabetes|inflammation|ibs|anxiety|depression|acne|arthritis|disease|infections?|morning sickness)\b|\b(?:lowers?|reduces?|regulates?|balances?|stabili[sz]es?|controls?)\s+(?:your\s+)?(?:blood sugar|blood pressure|cholesterol|insulin)\b",
        Options)]
    private static partial Regex MedicalClaim();

    [GeneratedRegex(
        @"\bpregnan\w*|\bbreastfeeding\b|\bgestational\b|\bdiabet\w*|\bceliac\b|\bcoeliac\b|\bkidney disease\b|\bcrohn'?s\b|\birritable bowel\b|\bmorning sickness\b|\bimmunocompromised\b|\bchemotherapy\b",
        Options)]
    private static partial Regex MedicalMention();

    // Safety and keeping claims.

    [GeneratedRegex(
        @"(?<!\b(?:not|longer|never|isn't|aren't|wasn't|nor)\s+(?:(?:perfectly|completely|totally|entirely|always|still)\s+)?)\b(?:(?:perfectly|completely|totally|100%|always|still|entirely)\s+)?safe\s+(?:to\s+(?:eat|serve|consume|store|keep|leave|give)\b|at room temperature\b|for (?:days|weeks|months)\b)|\bfood[- ]safe\b|\bkills?\s+(?:all\s+|any\s+|the\s+|off\s+)?(?:bacteria|germs|pathogens|botulism|salmonella|listeria|e\.?\s?coli|parasites)\b|\b(?:is|are|it's|they're|becomes?|stays?|keeps?|remains?|making (?:it|them)|makes? (?:it|them))\s+(?:\w+\s+)?shelf[- ]stable\b|\b(?:won't|will not|can't|cannot|never|doesn't|does not)\s+(?:spoil|go bad|go off|grow mou?ld)\b|\b(?:keeps?|lasts?|good)\s+(?:for\s+)?(?:up to\s+)?(?:\d+|a few|several|many|two|three|six|twelve|a)\s*(?:months?|years?)\s+(?:at room temperature|in the pantry|in the cupboard|on the (?:shelf|counter))\b|\b(?:at room temperature|in the pantry|in the cupboard|on the counter)\s+for\s+(?:up to\s+)?(?:\d+|a few|several|many|two|three|six|twelve)\s*(?:months?|years?)\b|\b(?:botulism|bacteria|salmonella)[- ]free\b|\bno risk of\s+(?:food poisoning|botulism|salmonella|listeria|bacteria|illness|getting sick)\b|\bno\s+(?:botulism|salmonella|listeria|bacteria|food poisoning|illness)\s+risk\b|\bno\s+(?:chance|danger|possibility)\s+of\s+(?:food poisoning|botulism|salmonella|listeria|bacteria|illness|getting sick)\b|\b(?:prevents?|stops?|eliminates?|destroys?)\s+(?:all\s+|any\s+|the\s+)?(?:bacteria|germs|pathogens|botulism|salmonella|listeria|e\.?\s?coli|parasites|food poisoning|spoilage)\b|\bguaranteed?\s+(?:safe|fresh|to keep)\b|\b(?:vinegar|salt|sugar|alcohol|acid|acidity|honey|oil)\s+(?:preserves (?:it|them|everything)|keeps (?:it|them) safe|makes (?:it|them) safe)\b|\ballerg(?:y|en)[- ](?:safe|friendly)\b|\bsafe for (?:people with |those with |anyone with )?(?:\w+[- ])?allerg\w*",
        Options)]
    private static partial Regex SafetyClaim();

    [GeneratedRegex(@"\bfood[- ]safe\s+(?:containers?|bags?|plastic|gloves|markers?|paint|wax|finish|boards?|silicone|dyes?|colou?ring|glitter|spray|grade)\b", Options)]
    private static partial Regex FoodSafeObject();

    [GeneratedRegex(@"\b(?:no need to|don't need to|doesn't need to|does not need to|do not need to|never needs? to|needn't)\s+(?:be\s+)?refrigerat\w*|\bwithout refrigeration\b", Options)]
    private static partial Regex NoRefrigerationClaim();

    [GeneratedRegex(@"\bdough\b|\bbatter\b|\bbread\b|\bloaf\b|\bloaves\b|\bcookies?\b|\bbiscuits?\b|\bcrackers?\b|\bgranola\b|\brise\w*|\bproof\w*|\bbefore (?:baking|rolling|shaping)\b", Options)]
    private static partial Regex ShelfNormalBaking();

    [GeneratedRegex(
        @"\b(?:keeps?|lasts?|will keep|good)\s+(?:well\s+)?(?:for\s+)?(?:up to\s+)?(?:\d+|a|one|two|three|four|five|six|seven|ten|several|a few|a couple of)\s*(?:to\s*\d+\s*)?(?:days?|weeks?|months?)\b",
        Options)]
    private static partial Regex KeepingTime();

    [GeneratedRegex(
        @"\bfridge\b|\brefrigerat\w*|\bfreez\w*|\bfrozen\b|\bairtight\b|\bcontainer\b|\broom temperature\b|\bpantry\b|\bcupboard\b|\bcounter\b|\bcool,? dry\b|\bjars?\b|\btins?\b|\bchilled\b|\bcovered\b",
        Options)]
    private static partial Regex StorageCondition();

    // Drying.

    // Not air-dried cookies or pasta, nor "to keep the chicken from dehydrating".
    [GeneratedRegex(@"(?<!\b(?:from|prevent|stop|avoid)\s+)\bdehydrat(?:e|es|ing)\b|\bdehydrator\b|\bjerky\b(?!\s+(?:seasoning|spice|rub))|\b(?:oven|sun)[- ]dry(?:ing)?\s+(?:the|it|them|for|at|until)\b|\bfruit leathers?\b", Options)]
    private static partial Regex DryingWords();

    [GeneratedRegex(@"\bdehydrator\b", Options)]
    private static partial Regex DryingEquipment();

    // Oil infusion.

    // The verb and the oil close together: "infuse the cream with rosemary, then whisk in the oil" is not oil infusion.
    [GeneratedRegex(@"\binfus(?:e|es|ing)\b[^.;!?\n]{0,40}?\boils?\b|\boils?\b[^.;!?\n]{0,40}?\binfus(?:e|es|ing)\b", Options)]
    private static partial Regex InfuseOil();

    [GeneratedRegex(@"\b(?:garlic|herbs?|basil|rosemary|thyme|oregano|chil(?:l)?i(?:es|s)?|peppers?|ginger|shallots?|lemon peel|citrus peel)\b", Options)]
    private static partial Regex Aromatic();

    [GeneratedRegex(@"\bgarlic confit\b|\bconfit (?:the\s+)?garlic\b", Options)]
    private static partial Regex GarlicConfit();

    [GeneratedRegex(@"\bstor\w*|\bkeep\w*|\bjars?\b|\bbottl\w*|\brefrigerat\w*|\bfridge\b|\bdays?\b|\bweeks?\b|\bpantry\b|\bshelf\b|\bleftovers?\b|\bmake[- ]ahead\b", Options)]
    private static partial Regex OilKept();

    [GeneratedRegex(@"\b[\w-]+[- ]infused oils?\b|\binfused oils?\b", Options)]
    private static partial Regex InfusedOil();
}
