namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The opaque concurrency token published for a profile: its <c>RowVersion</c>, base64-encoded.
/// </summary>
public static class BrandConcurrencyToken
{
    /// <summary>SQL Server's <c>rowversion</c> is eight bytes.</summary>
    public const int ByteLength = 8;

    public static string From(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    /// <summary>True for any token this API could have issued, whether or not it is current.</summary>
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
