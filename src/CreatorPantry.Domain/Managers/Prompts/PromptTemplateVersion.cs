using System.Globalization;

namespace CreatorPantry.Domain.Managers.Prompts;

/// <summary>A template's semantic version: exactly three non-negative parts, ordered numerically.</summary>
/// <remarks>
/// Not <see cref="Version"/>, which has a fourth part and treats absent parts as <c>-1</c> — two behaviours
/// this has no use for and would have to explain away. Parsing round-trips: a string only parses if
/// <see cref="ToString"/> reproduces it, so <c>01.2.0</c> is rejected rather than silently meaning
/// <c>1.2.0</c> and then disagreeing with the filename it was read from.
/// </remarks>
public readonly record struct PromptTemplateVersion(int Major, int Minor, int Patch)
    : IComparable<PromptTemplateVersion>
{
    public static bool TryParse(string? text, out PromptTemplateVersion version)
    {
        version = default;

        if (text is null)
        {
            return false;
        }

        var parts = text.Split('.');

        if (parts.Length != 3
            // NumberStyles.None rejects signs, whitespace, and thousands separators, which int.TryParse's
            // default (Integer) would otherwise accept — " 1", "+1" and "-1" are not versions.
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        var parsed = new PromptTemplateVersion(major, minor, patch);

        if (parsed.ToString() != text)
        {
            return false;
        }

        version = parsed;
        return true;
    }

    public static PromptTemplateVersion Parse(string text) =>
        TryParse(text, out var version)
            ? version
            : throw new FormatException($"'{text}' is not a major.minor.patch version.");

    public int CompareTo(PromptTemplateVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(PromptTemplateVersion left, PromptTemplateVersion right) =>
        left.CompareTo(right) < 0;

    public static bool operator <=(PromptTemplateVersion left, PromptTemplateVersion right) =>
        left.CompareTo(right) <= 0;

    public static bool operator >(PromptTemplateVersion left, PromptTemplateVersion right) =>
        left.CompareTo(right) > 0;

    public static bool operator >=(PromptTemplateVersion left, PromptTemplateVersion right) =>
        left.CompareTo(right) >= 0;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
