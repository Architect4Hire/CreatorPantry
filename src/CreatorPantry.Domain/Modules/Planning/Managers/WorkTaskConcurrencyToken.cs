namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>
/// A task's row version as the opaque token a client quotes back on a later edit. The same encoding the
/// content board's token uses, so one client reads them the same way.
/// </summary>
public static class WorkTaskConcurrencyToken
{
    /// <summary>A SQL Server <c>rowversion</c> is eight bytes.</summary>
    public const int ByteLength = 8;

    public static string From(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    /// <summary>Whether the token could ever have been issued, which is what tells a malformed one from a stale one.</summary>
    public static bool IsWellFormed(string? token) => TryParse(token, out _);

    /// <summary>True when <paramref name="token"/> is the token of exactly this row version.</summary>
    public static bool Matches(string? token, byte[] rowVersion) =>
        TryParse(token, out var submitted) && submitted.AsSpan().SequenceEqual(rowVersion);

    private static bool TryParse(string? token, out byte[] bytes)
    {
        bytes = [];

        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[ByteLength];

        if (!Convert.TryFromBase64String(token, buffer, out var written) || written != ByteLength)
        {
            return false;
        }

        bytes = buffer.ToArray();

        return true;
    }
}
