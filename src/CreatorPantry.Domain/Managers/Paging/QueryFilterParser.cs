namespace CreatorPantry.Domain.Managers.Paging;

/// <summary>
/// Turns the comma-separated strings a filtered query arrives as into typed lists, collecting a field-named
/// error for every entry it cannot read.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <c>RecipeSearchQueryFactory</c> when <c>TestRunHistoryQueryFactory</c> needed the
/// same parsing, and shared rather than copied because one of these rules is a correctness property rather than a
/// convenience: <see cref="TryParseDefined{TEnum}"/> refuses a <em>numeric</em> enum value, which
/// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> otherwise accepts. Two copies of that would
/// eventually be one copy of it, and the route that lost it would silently admit <c>?outcome=7</c> as a filter
/// comparing against a value no member has.
/// </para>
/// <para>
/// <strong>Promoted to the shared kernel by 12.9b</strong>, which is what the note here used to say should
/// happen: the DAM's search is the second module to want it, and it sits beside <see cref="ReferenceCursor"/>
/// and <see cref="PageBuilder"/> because every cursor-paged search needs all three. Still <c>internal</c> —
/// the commitment is to the assembly, not to anything outside it, and no host parses a query string.
/// </para>
/// <para>
/// Every method returns <c>null</c> for a value the caller did not send, and the errors list is appended to rather
/// than thrown from: a query naming three bad filters should be told about all three, which is what
/// <c>OperationError.Validation</c> publishes.
/// </para>
/// </remarks>
internal static class QueryFilterParser
{
    /// <summary>Splits a comma-separated list, or <c>null</c> when the caller did not send a value.</summary>
    /// <remarks>
    /// An empty value is treated as absent rather than as a filter matching nothing, because <c>?outcome=</c> is
    /// what a client sends when it has cleared a filter — refusing it would make clearing one an error. That
    /// includes a value that is nothing but separators: <c>?outcome=,,</c> names no outcome, so it is the same
    /// request as naming none at all, and returning an empty list would have made it a filter matching nothing.
    /// </remarks>
    internal static string[]? Split(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var entries = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return entries.Length == 0 ? null : entries;
    }

    internal static IReadOnlyList<Guid>? ParseIds(string? raw, string field, List<(string, string)> errors)
    {
        if (Split(raw) is not { } entries)
        {
            return null;
        }

        var ids = new List<Guid>(entries.Length);

        foreach (var entry in entries)
        {
            if (Guid.TryParseExact(entry, "D", out var id))
            {
                ids.Add(id);
            }
            else
            {
                errors.Add((field, $"'{entry}' is not an id."));
            }
        }

        return ids;
    }

    /// <summary>
    /// Parses a list of positive integers — a version number, a page, a count. Never negative and never zero.
    /// </summary>
    /// <remarks>
    /// The one place a number is range-checked rather than only parsed, because the callers' numbers are all
    /// one-based identities: a version number is positive by check constraint, so <c>?version=0</c> and
    /// <c>?version=-3</c> name nothing and are better refused by name than silently matched against nothing.
    /// </remarks>
    internal static IReadOnlyList<int>? ParsePositiveInts(string? raw, string field, List<(string, string)> errors)
    {
        if (Split(raw) is not { } entries)
        {
            return null;
        }

        var numbers = new List<int>(entries.Length);

        foreach (var entry in entries)
        {
            if (int.TryParse(entry, out var number) && number > 0)
            {
                numbers.Add(number);
            }
            else
            {
                errors.Add((field, $"'{entry}' is not a positive whole number."));
            }
        }

        return numbers;
    }

    internal static IReadOnlyList<TEnum>? ParseEnums<TEnum>(string? raw, string field, List<(string, string)> errors)
        where TEnum : struct, Enum
    {
        if (Split(raw) is not { } entries)
        {
            return null;
        }

        var values = new List<TEnum>(entries.Length);

        foreach (var entry in entries)
        {
            if (TryParseDefined<TEnum>(entry, out var value))
            {
                values.Add(value);
            }
            else
            {
                errors.Add((field, Accepted<TEnum>(entry)));
            }
        }

        return values;
    }

    internal static TEnum? ParseEnum<TEnum>(string? raw, string field, List<(string, string)> errors)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (TryParseDefined<TEnum>(raw.Trim(), out var value))
        {
            return value;
        }

        errors.Add((field, Accepted<TEnum>(raw.Trim())));

        return null;
    }

    /// <summary>
    /// Parses a name, case-insensitively, and only a name.
    /// </summary>
    /// <remarks>
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> also accepts numbers, so <c>outcome=7</c> would
    /// otherwise succeed and produce a value no member has — a filter that compiles into a comparison against
    /// nothing. The digit check refuses a numeric form outright rather than letting
    /// <see cref="Enum.IsDefined{TEnum}(TEnum)"/> accept one that happens to land on a member, because the numbers
    /// are an implementation detail of the enum and never part of the published contract.
    /// </remarks>
    private static bool TryParseDefined<TEnum>(string entry, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;

        return !char.IsAsciiDigit(entry[0])
            && entry[0] != '-'
            && Enum.TryParse(entry, ignoreCase: true, out value)
            && Enum.IsDefined(value);
    }

    private static string Accepted<TEnum>(string entry)
        where TEnum : struct, Enum =>
        $"'{entry}' is not one of: {string.Join(", ", Enum.GetNames<TEnum>())}.";
}
