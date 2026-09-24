using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using CreatorPantry.Domain.Managers.Quantities;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Segments a free-form recipe ingredient line into candidate spans — quantity or range, an optional package
/// aside, a unit word, the ingredient name, preparation clauses, and optionality — without deciding what any
/// of it means.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lexical, not semantic.</strong> Every rule here is about number shape and punctuation position,
/// never about vocabulary. "large" in <c>"1-2 large eggs"</c> is captured as a unit candidate purely because
/// it is the word right after the quantity — the tokenizer has no way to know it is really an adjective, and
/// does not try to. That is 7.3's job, against the real ingredient/unit catalogue. This is also why the
/// restriction against inventing or silently choosing is easy to hold: nothing here ever claims to know what
/// a word means, so there is nothing to guess at.
/// </para>
/// <para>
/// <strong>Failure is a value, not an exception.</strong> A denominator of zero, or a range whose upper bound
/// does not exceed its lower one, matches its grammar's shape but not a valid <see cref="Quantity"/> — that
/// becomes <see cref="QuantityToken.IsInvalid"/> plus an <see cref="IngredientLineAmbiguityKind.InvalidQuantity"/>
/// entry, with the original text preserved, never a thrown exception and never a fabricated number.
/// </para>
/// <para>
/// Pure and stateless, like <see cref="RecipeComparer"/>: no persistence, no clock, no workspace. Given the
/// same line twice, it returns the same answer.
/// </para>
/// </remarks>
public static class IngredientLineTokenizer
{
    private static readonly IReadOnlyDictionary<char, (int Numerator, int Denominator)> UnicodeFractions =
        new Dictionary<char, (int, int)>
        {
            ['½'] = (1, 2),
            ['⅓'] = (1, 3),
            ['⅔'] = (2, 3),
            ['¼'] = (1, 4),
            ['¾'] = (3, 4),
            ['⅕'] = (1, 5),
            ['⅖'] = (2, 5),
            ['⅗'] = (3, 5),
            ['⅘'] = (4, 5),
            ['⅙'] = (1, 6),
            ['⅚'] = (5, 6),
            ['⅛'] = (1, 8),
            ['⅜'] = (3, 8),
            ['⅝'] = (5, 8),
            ['⅞'] = (7, 8),
        };

    private static readonly string UnicodeFractionClass = string.Concat(UnicodeFractions.Keys);

    private static readonly Regex MixedUnicodeNumberPattern =
        new($@"\G(?<whole>\d+)[ \t]?(?<frac>[{UnicodeFractionClass}])", RegexOptions.Compiled);

    private static readonly Regex UnicodeFractionPattern =
        new($@"\G(?<frac>[{UnicodeFractionClass}])", RegexOptions.Compiled);

    private static readonly Regex MixedNumberPattern =
        new(@"\G(?<whole>\d+)[ \t]+(?<num>\d+)/(?<den>\d+)", RegexOptions.Compiled);

    private static readonly Regex DecimalPattern =
        new(@"\G(?<value>\d+\.\d+)", RegexOptions.Compiled);

    private static readonly Regex SimpleFractionPattern =
        new(@"\G(?<num>\d+)/(?<den>\d+)", RegexOptions.Compiled);

    private static readonly Regex IntegerPattern =
        new(@"\G(?<value>\d+)", RegexOptions.Compiled);

    private static readonly Regex UnitWordPattern =
        new(@"\G[A-Za-z]+\.?", RegexOptions.Compiled);

    /// <exception cref="ArgumentNullException"><paramref name="line"/> is <see langword="null"/>.</exception>
    public static IngredientLineTokens Tokenize(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (IsGroupMarker(line))
            return new IngredientLineTokens { OriginalText = line, IsGroupMarker = true };

        var pos = 0;
        SkipWhitespace(line, ref pos);

        var quantityToken = TryMatchQuantityOrRange(line, ref pos);

        SkipWhitespace(line, ref pos);
        var packageQuantity = quantityToken is { IsInvalid: false }
            ? TryMatchPackageQuantity(line, ref pos)
            : null;

        SkipWhitespace(line, ref pos);
        var unitCandidate = quantityToken is { IsInvalid: false }
            ? TryMatchUnitCandidate(line, ref pos)
            : null;

        SkipWhitespace(line, ref pos);

        var (ingredientText, preparationText, isOptional) = SplitRemainder(line, pos);

        var ambiguities = new List<IngredientLineAmbiguity>();
        if (quantityToken is null)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                ambiguities.Add(new IngredientLineAmbiguity(
                    IngredientLineAmbiguityKind.NoQuantityDetected,
                    new IngredientLineSpan(0, line.Length, line),
                    "No numeric quantity or range was found at the start of this line."));
            }
        }
        else if (quantityToken.IsInvalid)
        {
            ambiguities.Add(new IngredientLineAmbiguity(
                IngredientLineAmbiguityKind.InvalidQuantity,
                quantityToken.Span,
                $"\"{quantityToken.Span.Text}\" has the shape of a quantity but is not a valid one."));
        }

        return new IngredientLineTokens
        {
            OriginalText = line,
            Quantity = quantityToken,
            PackageQuantity = packageQuantity,
            UnitCandidate = unitCandidate,
            IngredientText = ingredientText,
            PreparationText = preparationText,
            IsOptional = isOptional,
            Ambiguities = ambiguities,
        };
    }

    private static bool IsGroupMarker(string line)
    {
        var trimmed = line.Trim();

        return trimmed.Length > 0 && trimmed[^1] == ':' && !trimmed.Any(char.IsDigit);
    }

    private static void SkipWhitespace(string text, ref int pos)
    {
        while (pos < text.Length && (text[pos] == ' ' || text[pos] == '\t'))
            pos++;
    }

    // ---- Quantity / range ----

    private readonly record struct SingleQuantityMatch(int Length, Quantity? Value, bool IsInvalid);

    private static QuantityToken? TryMatchQuantityOrRange(string line, ref int pos)
    {
        var left = TryMatchSingleQuantity(line, pos);
        if (left is null)
            return null;

        var separatorEnd = TryMatchRangeSeparator(line, pos + left.Value.Length);
        var right = separatorEnd is int sepEnd ? TryMatchSingleQuantity(line, sepEnd) : null;

        if (separatorEnd is int rangeEndStart && right is not null)
        {
            var totalLength = rangeEndStart + right.Value.Length - pos;
            var span = new IngredientLineSpan(pos, totalLength, line.Substring(pos, totalLength));
            pos += totalLength;

            if (left.Value.IsInvalid || right.Value.IsInvalid)
                return new QuantityToken { Span = span, IsInvalid = true };

            try
            {
                var range = QuantityRange.Create(left.Value.Value!.Value, right.Value.Value!.Value);

                return new QuantityToken { Span = span, Range = range };
            }
            catch (ArgumentException)
            {
                // Shape matched — two numbers either side of a separator — but the upper bound does not
                // exceed the lower one ("3-2 cups"). Preserve the text; do not invent an order for it.
                return new QuantityToken { Span = span, IsInvalid = true };
            }
        }

        var singleSpan = new IngredientLineSpan(pos, left.Value.Length, line.Substring(pos, left.Value.Length));
        pos += left.Value.Length;

        return left.Value.IsInvalid
            ? new QuantityToken { Span = singleSpan, IsInvalid = true }
            : new QuantityToken { Span = singleSpan, Value = left.Value.Value };
    }

    private static SingleQuantityMatch? TryMatchSingleQuantity(string text, int start)
    {
        var mixedUnicode = MixedUnicodeNumberPattern.Match(text, start);
        if (mixedUnicode.Success && !HasLetterBoundaryConflict(text, start + mixedUnicode.Length))
        {
            var whole = BigInteger.Parse(mixedUnicode.Groups["whole"].Value, CultureInfo.InvariantCulture);
            var (num, den) = UnicodeFractions[mixedUnicode.Groups["frac"].Value[0]];
            var value = Quantity.FromFraction((whole * den) + num, den);

            return new SingleQuantityMatch(mixedUnicode.Length, value, false);
        }

        var mixedNumber = MixedNumberPattern.Match(text, start);
        if (mixedNumber.Success && !HasLetterBoundaryConflict(text, start + mixedNumber.Length))
        {
            var whole = BigInteger.Parse(mixedNumber.Groups["whole"].Value, CultureInfo.InvariantCulture);
            var num = BigInteger.Parse(mixedNumber.Groups["num"].Value, CultureInfo.InvariantCulture);
            var den = BigInteger.Parse(mixedNumber.Groups["den"].Value, CultureInfo.InvariantCulture);

            return den.IsZero
                ? new SingleQuantityMatch(mixedNumber.Length, null, true)
                : new SingleQuantityMatch(mixedNumber.Length, Quantity.FromFraction((whole * den) + num, den), false);
        }

        var decimalMatch = DecimalPattern.Match(text, start);
        if (decimalMatch.Success && !HasLetterBoundaryConflict(text, start + decimalMatch.Length))
        {
            var value = decimal.Parse(decimalMatch.Groups["value"].Value, CultureInfo.InvariantCulture);

            return new SingleQuantityMatch(decimalMatch.Length, Quantity.FromDecimal(value), false);
        }

        var simpleFraction = SimpleFractionPattern.Match(text, start);
        if (simpleFraction.Success && !HasLetterBoundaryConflict(text, start + simpleFraction.Length))
        {
            var num = BigInteger.Parse(simpleFraction.Groups["num"].Value, CultureInfo.InvariantCulture);
            var den = BigInteger.Parse(simpleFraction.Groups["den"].Value, CultureInfo.InvariantCulture);

            return den.IsZero
                ? new SingleQuantityMatch(simpleFraction.Length, null, true)
                : new SingleQuantityMatch(simpleFraction.Length, Quantity.FromFraction(num, den), false);
        }

        var unicodeFraction = UnicodeFractionPattern.Match(text, start);
        if (unicodeFraction.Success && !HasLetterBoundaryConflict(text, start + unicodeFraction.Length))
        {
            var (num, den) = UnicodeFractions[unicodeFraction.Groups["frac"].Value[0]];

            return new SingleQuantityMatch(unicodeFraction.Length, Quantity.FromFraction(num, den), false);
        }

        var integer = IntegerPattern.Match(text, start);
        if (integer.Success && !HasLetterBoundaryConflict(text, start + integer.Length))
        {
            var value = BigInteger.Parse(integer.Groups["value"].Value, CultureInfo.InvariantCulture);

            return new SingleQuantityMatch(integer.Length, Quantity.FromFraction(value, BigInteger.One), false);
        }

        return null;
    }

    /// <summary>
    /// True when a letter sits immediately after a numeric match with no separator — "9x13" or "2lb" without
    /// a space. Without a unit vocabulary, digits glued to letters cannot be told apart from a non-numeric
    /// token that merely starts with digits, so neither is read as a quantity; both are left for a human or
    /// the matcher to interpret in context.
    /// </summary>
    private static bool HasLetterBoundaryConflict(string text, int endIndex) =>
        endIndex < text.Length && char.IsLetter(text[endIndex]);

    /// <summary>Matches a range separator starting at <paramref name="pos"/> and returns the position right after it, or null.</summary>
    /// <remarks>
    /// The word "to" only counts when whitespace precedes it in the line — "2to3" is not a range, because
    /// nothing marks "to" as a standalone word there rather than an accident of two numbers colliding.
    /// </remarks>
    private static int? TryMatchRangeSeparator(string text, int pos)
    {
        var i = pos;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
            i++;

        if (i < text.Length && (text[i] == '-' || text[i] == '–' || text[i] == '—'))
        {
            i++;
        }
        else if (i > pos
            && i + 1 < text.Length
            && text[i] == 't' && text[i + 1] == 'o'
            && (i + 2 == text.Length || text[i + 2] == ' ' || text[i + 2] == '\t'))
        {
            i += 2;
        }
        else
        {
            return null;
        }

        while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
            i++;

        return i;
    }

    // ---- Package quantity ----

    private static PackageQuantityToken? TryMatchPackageQuantity(string text, ref int pos)
    {
        if (pos >= text.Length || text[pos] != '(')
            return null;

        var innerStart = pos + 1;
        var inner = TryMatchSingleQuantity(text, innerStart);
        if (inner is null)
            return null;

        var afterQuantity = innerStart + inner.Value.Length;
        var unitStart = afterQuantity;
        while (unitStart < text.Length && (text[unitStart] == ' ' || text[unitStart] == '\t'))
            unitStart++;

        if (unitStart == afterQuantity)
            return null; // no whitespace between the number and the unit word — not this grammar

        var unitMatch = UnitWordPattern.Match(text, unitStart);
        if (!unitMatch.Success)
            return null;

        var afterUnit = unitStart + unitMatch.Length;
        var closeIndex = afterUnit;
        while (closeIndex < text.Length && (text[closeIndex] == ' ' || text[closeIndex] == '\t'))
            closeIndex++;

        if (closeIndex >= text.Length || text[closeIndex] != ')')
            return null;

        var start = pos;
        pos = closeIndex + 1;

        var quantitySpan = new IngredientLineSpan(innerStart, inner.Value.Length, text.Substring(innerStart, inner.Value.Length));
        var quantityToken = inner.Value.IsInvalid
            ? new QuantityToken { Span = quantitySpan, IsInvalid = true }
            : new QuantityToken { Span = quantitySpan, Value = inner.Value.Value };

        return new PackageQuantityToken
        {
            Span = new IngredientLineSpan(start, pos - start, text.Substring(start, pos - start)),
            Quantity = quantityToken,
            Unit = new IngredientLineSpan(unitStart, unitMatch.Length, unitMatch.Value),
        };
    }

    // ---- Unit candidate ----

    private static IngredientLineSpan? TryMatchUnitCandidate(string text, ref int pos)
    {
        var match = UnitWordPattern.Match(text, pos);
        if (!match.Success)
            return null;

        var span = new IngredientLineSpan(pos, match.Length, match.Value);
        pos += match.Length;

        return span;
    }

    // ---- Ingredient text, preparation text, optionality ----

    private static (IngredientLineSpan? IngredientText, IReadOnlyList<IngredientLineSpan> PreparationText, bool IsOptional)
        SplitRemainder(string line, int remainderStart)
    {
        var remainder = line[remainderStart..];
        var (content, isOptional) = ExtractTrailingOptional(remainder);

        var segments = SplitTopLevelCommas(content);
        var spans = new List<IngredientLineSpan>(segments.Count);

        foreach (var (start, length) in segments)
        {
            var (trimmedStart, trimmedLength) = TrimSpan(content, start, length);
            if (trimmedLength == 0)
                continue;

            var absoluteStart = remainderStart + trimmedStart;
            spans.Add(new IngredientLineSpan(absoluteStart, trimmedLength, line.Substring(absoluteStart, trimmedLength)));
        }

        var ingredientText = spans.Count > 0 ? spans[0] : (IngredientLineSpan?)null;
        var preparationText = spans.Count > 1 ? spans.GetRange(1, spans.Count - 1) : [];

        return (ingredientText, preparationText, isOptional);
    }

    /// <summary>Strips a trailing <c>(optional)</c> or <c>, optional</c> marker, reporting whether one was found.</summary>
    private static (string Content, bool IsOptional) ExtractTrailingOptional(string remainder)
    {
        var end = remainder.Length;
        while (end > 0 && (remainder[end - 1] == ' ' || remainder[end - 1] == '\t'))
            end--;

        if (end == 0)
            return (remainder, false);

        if (remainder[end - 1] == ')')
        {
            var openIndex = remainder.LastIndexOf('(', end - 1);
            if (openIndex >= 0)
            {
                var inner = remainder[(openIndex + 1)..(end - 1)].Trim();
                if (string.Equals(inner, "optional", StringComparison.OrdinalIgnoreCase))
                    return (StripTrailingCommaAndSpace(remainder, openIndex), true);
            }

            return (remainder, false);
        }

        const string optionalWord = "optional";
        if (end >= optionalWord.Length
            && string.Equals(remainder[(end - optionalWord.Length)..end], optionalWord, StringComparison.OrdinalIgnoreCase))
        {
            var wordStart = end - optionalWord.Length;
            var isStandaloneWord = wordStart == 0 || !char.IsLetterOrDigit(remainder[wordStart - 1]);

            if (isStandaloneWord)
                return (StripTrailingCommaAndSpace(remainder, wordStart), true);
        }

        return (remainder, false);
    }

    private static string StripTrailingCommaAndSpace(string text, int end)
    {
        while (end > 0 && (text[end - 1] == ' ' || text[end - 1] == '\t'))
            end--;

        if (end > 0 && text[end - 1] == ',')
            end--;

        return text[..end];
    }

    /// <summary>Splits on commas that are not inside parentheses, so a package aside's own comma stays intact.</summary>
    private static List<(int Start, int Length)> SplitTopLevelCommas(string text)
    {
        var segments = new List<(int, int)>();
        var depth = 0;
        var segmentStart = 0;

        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    if (depth > 0)
                        depth--;
                    break;
                case ',' when depth == 0:
                    segments.Add((segmentStart, i - segmentStart));
                    segmentStart = i + 1;
                    break;
            }
        }

        segments.Add((segmentStart, text.Length - segmentStart));

        return segments;
    }

    private static (int Start, int Length) TrimSpan(string text, int start, int length)
    {
        var end = start + length;
        while (start < end && char.IsWhiteSpace(text[start]))
            start++;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
            end--;

        return (start, end - start);
    }
}
