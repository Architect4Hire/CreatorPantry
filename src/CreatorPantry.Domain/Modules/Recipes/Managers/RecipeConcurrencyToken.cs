namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The published form of <c>Recipe.RowVersion</c>: the value a read hands out and an edit sends back to
/// prove which state of the recipe it was composed against.
/// </summary>
/// <remarks>
/// <para>
/// Both directions live here so they cannot drift. A read that encoded one way and a write that decoded
/// another would not fail loudly — it would reject every honest edit as a conflict, or worse, accept a
/// stale one.
/// </para>
/// <para>
/// <strong>Opaque to clients.</strong> Base64 is an implementation detail published under a name that says
/// what the value is for rather than what it is made of; a client stores it and returns it, and never
/// parses, compares or orders by it.
/// </para>
/// </remarks>
public static class RecipeConcurrencyToken
{
    /// <summary>
    /// The width of the underlying token, in bytes.
    /// </summary>
    /// <remarks>
    /// SQL Server's <c>rowversion</c> is always eight bytes — the same width
    /// <c>RecipeVersionConfiguration</c> pins <c>BasedOnRecipeRowVersion</c> to. Checking it lets a
    /// malformed token be reported as the client bug it is, instead of arriving as a conflict and sending a
    /// creator to look for a collaborator who was never there.
    /// </remarks>
    public const int ByteLength = 8;

    /// <summary>Publishes a recipe's concurrency token.</summary>
    public static string From(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    /// <summary>Whether a submitted token is well formed — not whether it is current.</summary>
    public static bool IsWellFormed(string? token) => TryParse(token, out _);

    /// <summary>
    /// Whether a submitted token names exactly the state <paramref name="rowVersion"/> is in.
    /// </summary>
    /// <remarks>
    /// A malformed token answers <c>false</c> rather than throwing: by the time this is asked, shape
    /// validation has already refused malformed tokens with a field error, and a check that could throw
    /// here would turn a rejected request into a 500 on any path that skipped it.
    /// </remarks>
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
