namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// A creative context's row version as the opaque token a client holds between a read and an edit.
/// </summary>
/// <remarks>
/// The same wire shape the DAM publishes — base64 of the eight-byte row version — restated here rather than
/// shared, because a module's helpers do not cross its boundary. A client treats it as opaque either way.
/// </remarks>
public static class CreativeContextConcurrencyToken
{
    public const int ByteLength = 8;

    public static string From(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    public static bool IsWellFormed(string? token) => TryParse(token, out _);

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
