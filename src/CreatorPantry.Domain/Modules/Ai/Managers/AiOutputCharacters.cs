using System.Globalization;
using System.Text;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>What <see cref="AiOutputCharacters.Find"/> found in a piece of model text.</summary>
internal enum AiOutputCharacterFault
{
    None = 0,

    /// <summary>A control character other than a tab or a line break.</summary>
    Control = 1,

    /// <summary>An invisible formatting character that is not holding an emoji together.</summary>
    Invisible = 2,
}

/// <summary>
/// The character rule a model's text is held to before it is stored: no control character, and no invisible
/// one that is not part of an emoji.
/// </summary>
/// <remarks>
/// <para>
/// The rule is <see cref="AiChannelPostsOutputValidator"/>'s, where it is explained: text that is shown to a
/// creator and copied onward must not carry something they cannot see. It is stated here once for the
/// validators written after it; the posts and outline validators still hold their own copies.
/// </para>
/// <para>
/// It reports what it found and decides nothing. The message, the reason code and whether the answer is worth
/// a second ask are the caller's.
/// </para>
/// </remarks>
internal static class AiOutputCharacters
{
    private const int ZeroWidthJoiner = 0x200D;

    private const int EmojiVariationSelector = 0xFE0F;

    private const int KeycapMark = 0x20E3;

    public static AiOutputCharacterFault Find(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var runes = value.EnumerateRunes().ToArray();

        for (var index = 0; index < runes.Length; index++)
        {
            var rune = runes[index];
            var category = Rune.GetUnicodeCategory(rune);

            if (category is UnicodeCategory.Control && rune.Value is not ('\t' or '\n' or '\r'))
            {
                return AiOutputCharacterFault.Control;
            }

            if (IsInvisible(rune, category) && !HoldsAnEmojiTogether(runes, index))
            {
                return AiOutputCharacterFault.Invisible;
            }
        }

        return AiOutputCharacterFault.None;
    }

    private static bool IsInvisible(Rune rune, UnicodeCategory category) =>
        category is UnicodeCategory.Format
        || rune.Value is (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF)
        || rune.Value is 0x034F or 0x115F or 0x1160 or 0x3164 or 0xFFA0 or 0x2800;

    private static bool HoldsAnEmojiTogether(Rune[] runes, int index)
    {
        if (index == 0 || runes[index].Value is not (ZeroWidthJoiner or EmojiVariationSelector))
        {
            return false;
        }

        var before = runes[index - 1];

        if (Rune.GetUnicodeCategory(before) is UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol
            || (runes[index].Value is ZeroWidthJoiner && before.Value is EmojiVariationSelector))
        {
            return true;
        }

        return runes[index].Value is EmojiVariationSelector
            && before.Value is (>= '0' and <= '9') or '#' or '*'
            && index + 1 < runes.Length
            && runes[index + 1].Value is KeycapMark;
    }
}
