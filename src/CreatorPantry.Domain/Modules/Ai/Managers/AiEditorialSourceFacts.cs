using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// What the pinned recipe version itself says, reduced to what <see cref="AiEditorialClaimScanner"/> needs to
/// decide whether generated prose is supported.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Numbers are checked in context, not as one bag.</strong> A duration is supported only by the same
/// duration in the same unit — "3 weeks" is not supported by "3 eggs" or by "3 days" — and a temperature only by
/// a temperature. Any other figure (a quantity, a yield, a serving count) is supported by the same figure
/// anywhere the recipe states it. A stated time also supports the same time in another unit where that is an
/// exact restatement (90 minutes, 1.5 hours); any other arithmetic is a model doing maths the domain should do
/// (ai.md), and is reported.
/// </para>
/// <para>
/// <strong>Storage guidance is grounded in the creator's storage notes alone.</strong> A step that says "freeze
/// the dough" does not license advice to freeze the bread, and a number elsewhere in the recipe does not
/// license a storage duration.
/// </para>
/// <para>
/// <strong>Claims are supported only by the creator's own prose</strong> (title, description, headnote, notes,
/// storage notes, attribution, yield wording) — not by an ingredient line or a step. "gluten-free flour blend"
/// says something about a flour, not about the loaf.
/// </para>
/// </remarks>
public sealed record AiEditorialSourceFacts(
    IReadOnlySet<string> Numbers,
    IReadOnlySet<string> TimePairs,
    IReadOnlySet<string> Temperatures,
    string Prose,
    string StorageNotes,
    IReadOnlySet<string> StorageTimePairs,
    IReadOnlySet<string> StorageTemperatures)
{
    /// <summary>The facts, from a recipe version's snapshot.</summary>
    public static AiEditorialSourceFacts From(RecipeSnapshotDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var header = source.Recipe;
        var prose = new[] { header.Title, header.Description, header.Headnote, header.Notes, header.StorageNotes, header.AttributionText, header.YieldText };
        var other = new List<string?>();

        var numbers = new HashSet<string>(StringComparer.Ordinal);
        var times = new HashSet<string>(StringComparer.Ordinal);
        var temperatures = new HashSet<string>(StringComparer.Ordinal);

        foreach (var minutes in new[] { header.PrepTimeMinutes, header.CookTimeMinutes, header.RestTimeMinutes, header.TotalTimeMinutes })
        {
            AddMinutes(times, numbers, minutes);
        }

        AddNumber(numbers, header.YieldQuantity);
        AddNumber(numbers, header.ServingCount);
        AddNumber(numbers, header.ServingSize);

        foreach (var line in source.IngredientGroups.SelectMany(group => group.Ingredients))
        {
            other.AddRange([line.DisplayText, line.IngredientNameText, line.PreparationNote]);
            AddNumber(numbers, line.Quantity);
            AddNumber(numbers, line.QuantityUpper);
        }

        foreach (var step in source.InstructionGroups.SelectMany(group => group.Steps))
        {
            other.AddRange([step.Text, step.Note]);
            AddMinutes(times, numbers, step.DurationMinutes);

            if (step.TemperatureValue is { } temperature)
            {
                temperatures.Add(AiEditorialProse.Normalize(temperature));
                numbers.Add(AiEditorialProse.Normalize(temperature));
            }
        }

        foreach (var equipment in source.Equipment)
        {
            other.AddRange([equipment.DisplayText, equipment.Note]);
        }

        var proseText = Join(prose);
        Absorb(Join(other.Concat(prose)), numbers, times, temperatures);

        var storage = header.StorageNotes ?? string.Empty;
        var storageTimes = new HashSet<string>(StringComparer.Ordinal);
        var storageTemperatures = new HashSet<string>(StringComparer.Ordinal);
        Absorb(storage, new HashSet<string>(), storageTimes, storageTemperatures);

        return new AiEditorialSourceFacts(numbers, times, temperatures, AiEditorialProse.Canonicalize(proseText), AiEditorialProse.Canonicalize(storage), storageTimes, storageTemperatures);
    }

    /// <summary>Facts stated directly, for fixtures that do not carry a whole snapshot.</summary>
    /// <param name="numbers">Figures or short phrases such as <c>25 minutes</c>, read exactly as recipe text is.</param>
    /// <param name="prose">What the creator wrote about the recipe: the text claims are checked against.</param>
    public static AiEditorialSourceFacts Of(IEnumerable<string> numbers, string? prose, string? storageNotes)
    {
        var general = new HashSet<string>(StringComparer.Ordinal);
        var times = new HashSet<string>(StringComparer.Ordinal);
        var temperatures = new HashSet<string>(StringComparer.Ordinal);

        Absorb(string.Join(' ', numbers), general, times, temperatures);
        Absorb(Join([prose, storageNotes]), general, times, temperatures);

        var storageTimes = new HashSet<string>(StringComparer.Ordinal);
        var storageTemperatures = new HashSet<string>(StringComparer.Ordinal);
        Absorb(storageNotes ?? string.Empty, new HashSet<string>(), storageTimes, storageTemperatures);

        return new AiEditorialSourceFacts(
            general, times, temperatures,
            AiEditorialProse.Canonicalize(Join([prose, storageNotes])), AiEditorialProse.Canonicalize(storageNotes ?? string.Empty),
            storageTimes, storageTemperatures);
    }

    private static string Join(IEnumerable<string?> texts) => string.Join('\n', texts.Where(text => !string.IsNullOrWhiteSpace(text)));

    private static void Absorb(string text, HashSet<string> numbers, HashSet<string> times, HashSet<string> temperatures)
    {
        foreach (var token in AiEditorialProse.Tokens(text))
        {
            numbers.Add(token.Number);

            switch (token.Kind)
            {
                case AiEditorialTokenKind.Time:
                    times.Add(token.Pair);
                    break;
                case AiEditorialTokenKind.Temperature:
                    temperatures.Add(token.Number);
                    break;
            }
        }
    }

    private static void AddNumber(HashSet<string> numbers, decimal? value)
    {
        if (value is { } number)
        {
            numbers.Add(AiEditorialProse.Normalize(number));
        }
    }

    private static void AddMinutes(HashSet<string> times, HashSet<string> numbers, int? minutes)
    {
        if (minutes is not { } value)
        {
            return;
        }

        numbers.Add(AiEditorialProse.Normalize(value));
        times.Add(AiEditorialProse.Pair(value, "minute"));

        // Exact restatements only: 90 minutes is 1.5 hours; 100 minutes is not "1.67 hours".
        if (value % 30 == 0)
        {
            times.Add(AiEditorialProse.Pair(value / 60m, "hour"));
        }

        if (value % 1440 == 0)
        {
            times.Add(AiEditorialProse.Pair(value / 1440m, "day"));
        }
    }
}

public enum AiEditorialTokenKind
{
    Plain = 0,
    Time = 1,
    Temperature = 2,
}

/// <summary>One figure found in prose, with what it measures when the prose says.</summary>
/// <param name="Number">The normalized decimal.</param>
/// <param name="Pair"><c>number|unit</c> for a duration, otherwise the empty string.</param>
/// <param name="Text">The span as written, for a warning to quote.</param>
public readonly record struct AiEditorialToken(AiEditorialTokenKind Kind, string Number, string Pair, string Text);

/// <summary>
/// Canonical form and figure extraction for editorial prose. Both the scanner and the source facts read text
/// through here, so a claim and the recipe it is checked against are always compared in the same shape.
/// </summary>
/// <remarks>
/// Canonical form is what makes the net hold against formatting: compatibility normalization (fullwidth digits,
/// ligatures, non-breaking spaces), format characters removed (zero-width joiners), every dash variant a plain
/// hyphen, whitespace collapsed, and vulgar fractions spelled out before normalization would glue them to the
/// digit before them ("2½" must not become "21/2").
/// </remarks>
public static partial class AiEditorialProse
{
    private static readonly Dictionary<char, string> Fractions = new()
    {
        ['½'] = " 1/2 ", ['⅓'] = " 1/3 ", ['⅔'] = " 2/3 ", ['¼'] = " 1/4 ", ['¾'] = " 3/4 ", ['⅕'] = " 1/5 ",
        ['⅖'] = " 2/5 ", ['⅗'] = " 3/5 ", ['⅘'] = " 4/5 ", ['⅙'] = " 1/6 ", ['⅚'] = " 5/6 ", ['⅛'] = " 1/8 ",
        ['⅜'] = " 3/8 ", ['⅝'] = " 5/8 ", ['⅞'] = " 7/8 ",
    };

    private const string Units =
        @"(?:minutes?|mins?|hours?|hrs?|days?|weeks?|months?|years?|degrees?|cups?|tablespoons?|teaspoons?|tbsp|tsp|grams?|kg|ounces?|oz|pounds?|lbs?|ml|litres?|liters?|quarts?|inches|inch|cloves?|cans?|servings?|slices?|loaves|loaf|pieces?)";

    private const string Numerals =
        @"(?:(?:twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety)(?:[-\s](?:one|two|three|four|five|six|seven|eight|nine))?|zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|dozen)";

    [GeneratedRegex($@"\b(?<n>{Numerals})(?:\s+|-)(?<u>{Units})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberWord();

    [GeneratedRegex($@"\bhalf an? (?<u>hour|day|week)\b|\b(?:a )?couple of (?<c>{Units})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Colloquial();

    // One alternation, in order: a duration, then a temperature, then any other figure. A figure belongs to the
    // first shape that claims it, so "220C" is a temperature and "25 minutes" is a duration, not two plain numbers.
    [GeneratedRegex(
        @"(?<![\w.])(?<num>[0-9]+(?:\.[0-9]+)?(?:\s*/\s*[0-9]+)?)(?:(?:\s*-?\s*(?<time>minutes?|mins?|hours?|hrs?|days?|weeks?|months?)\b)|(?:\s*(?:°|º|degrees?\b)\s*[cf]?\b)|(?:\s*[cf]\b))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Figure();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?<=[0-9]),(?=[0-9]{3}(?![0-9]))", RegexOptions.CultureInvariant)]
    private static partial Regex ThousandsSeparator();

    private static readonly Dictionary<string, decimal> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13,
        ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18,
        ["nineteen"] = 19, ["dozen"] = 12, ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    /// <summary>The canonical form of <paramref name="text"/>. See the type's remarks.</summary>
    public static string Canonicalize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var spelled = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            spelled.Append(Fractions.TryGetValue(character, out var fraction) ? fraction : character);
        }

        var normalized = spelled.ToString().Normalize(NormalizationForm.FormKC);
        var clean = new StringBuilder(normalized.Length);

        foreach (var character in normalized)
        {
            var category = char.GetUnicodeCategory(character);

            if (category is UnicodeCategory.Format || (char.IsControl(character) && !char.IsWhiteSpace(character)))
            {
                continue;
            }

            clean.Append(character is >= '‐' and <= '―' or '−' or '﹘' or '﹣' or '－' ? '-'
                : character is '⁄' ? '/'
                : character);
        }

        var collapsed = Whitespace().Replace(clean.ToString(), " ").Trim();
        collapsed = ThousandsSeparator().Replace(collapsed, string.Empty);

        // Numbers written as words become digits, but only beside a unit: "two hours" is a claim, "one of the best" is prose.
        collapsed = NumberWord().Replace(collapsed, match => $"{WordValue(match.Groups["n"].Value).ToString("0.####", CultureInfo.InvariantCulture)} {match.Groups["u"].Value}");

        return Colloquial().Replace(collapsed, match => match.Groups["u"].Success
            ? $"0.5 {match.Groups["u"].Value}"
            : $"2 {match.Groups["c"].Value}");
    }

    /// <summary>Every figure in <paramref name="text"/>, read from its canonical form.</summary>
    public static IReadOnlyList<AiEditorialToken> Tokens(string text)
    {
        var tokens = new List<AiEditorialToken>();

        foreach (Match match in Figure().Matches(Canonicalize(text)))
        {
            var number = Parse(match.Groups["num"].Value);

            if (match.Groups["time"].Success)
            {
                tokens.Add(new AiEditorialToken(AiEditorialTokenKind.Time, number, Pair(number, match.Groups["time"].Value), match.Value.Trim()));
            }
            else if (Regex.IsMatch(match.Value, @"(?:°|º|degrees?|\s*[cf]\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                && !match.Groups["num"].Value.Contains('/', StringComparison.Ordinal))
            {
                tokens.Add(new AiEditorialToken(AiEditorialTokenKind.Temperature, number, string.Empty, match.Value.Trim()));
            }
            else
            {
                tokens.Add(new AiEditorialToken(AiEditorialTokenKind.Plain, number, string.Empty, match.Value.Trim()));
            }
        }

        return tokens;
    }

    public static string Normalize(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary><c>number|unit</c> with the unit reduced to its family, so "hrs" and "hours" agree.</summary>
    public static string Pair(decimal number, string unit) => $"{Normalize(number)}|{Family(unit)}";

    private static string Pair(string number, string unit) => $"{number}|{Family(unit)}";

    private static string Family(string unit)
    {
        var lower = unit.ToLowerInvariant();

        return lower.StartsWith("min", StringComparison.Ordinal) ? "minute"
            : lower.StartsWith("h", StringComparison.Ordinal) ? "hour"
            : lower.StartsWith("d", StringComparison.Ordinal) ? "day"
            : lower.StartsWith("w", StringComparison.Ordinal) ? "week"
            : lower.StartsWith("mo", StringComparison.Ordinal) ? "month"
            : lower;
    }

    /// <summary>
    /// The normalized decimal for a run of digits or a fraction. A run too long to be a real quantity is returned
    /// as written, so it can never match anything the recipe says and is always reported — never thrown on.
    /// </summary>
    private static string Parse(string raw)
    {
        var compact = raw.Replace(" ", string.Empty, StringComparison.Ordinal);

        if (compact.Contains('/', StringComparison.Ordinal))
        {
            var parts = compact.Split('/');

            return parts.Length == 2
                && decimal.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var numerator)
                && decimal.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var denominator)
                && denominator != 0
                    ? Normalize(Math.Round(numerator / denominator, 4))
                    : compact;
        }

        return decimal.TryParse(compact, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? Normalize(value)
            : compact;
    }

    private static decimal WordValue(string words)
    {
        var parts = words.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries);

        return parts.Sum(part => Words.GetValueOrDefault(part, 0m));
    }
}
