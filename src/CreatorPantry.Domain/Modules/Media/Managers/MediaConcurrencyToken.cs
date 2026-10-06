namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// Turns an asset's <c>rowversion</c> into the opaque token a client quotes back, and checks one it quoted.
/// </summary>
/// <remarks>
/// <para>
/// One per module, following <c>RecipeConcurrencyToken</c> and <c>BrandConcurrencyToken</c>: the encoding is
/// identical, but a token is part of each module's published contract, and sharing one helper would make three
/// contracts move together whenever any of them had to change.
/// </para>
/// <para>
/// <strong>Base64 of the eight bytes, and opaque by intent.</strong> A client stores it and sends it back; it
/// never parses it, compares it for ordering, or infers anything from it. Publishing the bytes as a token rather
/// than as a number is what keeps that true — a client cannot be tempted to treat a version as a counter.
/// </para>
/// <para>
/// 12.9c publishes it on the detail read; 12.9d is the first caller to check one.
/// </para>
/// </remarks>
public static class MediaConcurrencyToken
{
    /// <summary>SQL Server's <c>rowversion</c> is eight bytes.</summary>
    public const int ByteLength = 8;

    public static string From(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    /// <summary>True for any token this API could have issued, whether or not it is still current.</summary>
    /// <remarks>
    /// Separate from <see cref="Matches"/> so a caller can tell a malformed token from a stale one: the first is
    /// a bad request naming the field, the second a 409 the client resolves by re-reading.
    /// </remarks>
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
