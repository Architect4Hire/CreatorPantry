namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// A card's row version as the opaque token a client quotes back on an edit or a move. The same encoding
/// <see cref="CreativeContextConcurrencyToken"/> uses.
/// </summary>
public static class ContentCardConcurrencyToken
{
    public static string From(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    /// <summary>True for a token this API could have issued, whatever card it was issued for.</summary>
    public static bool IsWellFormed(string? token) => CreativeContextConcurrencyToken.IsWellFormed(token);

    /// <summary>True when <paramref name="token"/> is the token of exactly this row version.</summary>
    public static bool Matches(string? token, byte[] rowVersion) => CreativeContextConcurrencyToken.Matches(token, rowVersion);
}
