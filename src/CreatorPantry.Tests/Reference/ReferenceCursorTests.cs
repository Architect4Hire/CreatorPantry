using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Paging;

namespace CreatorPantry.Tests.Reference;

public sealed class ReferenceCursorTests
{
    private const string Scope = "resource=cuisines|q=";

    [Theory]
    [InlineData("Main Course", "main-course")]
    [InlineData("Sauté", "saute")]
    [InlineData("Sauce & Condiment", "sauce-condiment")]
    [InlineData("all-purpose flour", "all purpose flour")]
    [InlineData("", "each")]
    public void A_cursor_round_trips(string sortValue, string tieBreaker)
    {
        Assert.True(ReferenceCursor.TryDecode(ReferenceCursor.Encode(sortValue, tieBreaker, Scope), out var decoded));

        Assert.Equal(sortValue, decoded!.SortValue);
        Assert.Equal(tieBreaker, decoded.TieBreaker);
        Assert.Equal(ReferenceCursor.Fingerprint(Scope), decoded.ScopeFingerprint);
    }

    /// <summary>A display name containing the separator would otherwise split the cursor in the wrong place.</summary>
    [Fact]
    public void A_sort_value_containing_a_separator_still_round_trips()
    {
        var awkward = "Odd\u001FName";

        Assert.True(ReferenceCursor.TryDecode(ReferenceCursor.Encode(awkward, "odd", Scope), out var decoded));

        Assert.Equal(awkward, decoded!.SortValue);
        Assert.Equal("odd", decoded.TieBreaker);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!!")]
    [InlineData("////")]
    public void A_malformed_cursor_is_rejected_rather_than_throwing(string? value)
    {
        Assert.False(ReferenceCursor.TryDecode(value, out var decoded));
        Assert.Null(decoded);
    }

    /// <summary>
    /// A well-formed base64 payload with too few parts decodes to nothing rather than to a cursor with an
    /// empty tie-breaker, which would page from a position no row can be after.
    /// </summary>
    [Theory]
    [InlineData("just-some-text")]
    [InlineData("fingerprint\u001Fonly-two-parts")]
    [InlineData("\u001F\u001FSome Name")]
    [InlineData("fingerprint\u001F\u001FSome Name")]
    public void A_payload_with_a_missing_part_is_rejected(string payload)
    {
        var encoded = System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(payload));

        Assert.False(ReferenceCursor.TryDecode(encoded, out _));
    }

    // --- Scope binding ------------------------------------------------------------------------------------

    /// <summary>
    /// The point of the fingerprint: a cursor names a position in one specific ordered set, so a cursor from
    /// another set must be recognisable as not belonging here.
    /// </summary>
    [Fact]
    public void A_cursor_carries_the_fingerprint_of_the_query_that_issued_it()
    {
        var other = "resource=allergens|q=";

        Assert.True(ReferenceCursor.TryDecode(ReferenceCursor.Encode("Italian", "italian", Scope), out var decoded));

        Assert.Equal(ReferenceCursor.Fingerprint(Scope), decoded!.ScopeFingerprint);
        Assert.NotEqual(ReferenceCursor.Fingerprint(other), decoded.ScopeFingerprint);
    }

    [Theory]
    [InlineData("resource=cuisines|q=", "resource=allergens|q=")]
    [InlineData("resource=cuisines|q=", "resource=cuisines|q=italian")]
    [InlineData("resource=ingredients|category=|q=", "resource=ingredients|category=baking|q=")]
    public void Different_scopes_fingerprint_differently(string first, string second) =>
        Assert.NotEqual(ReferenceCursor.Fingerprint(first), ReferenceCursor.Fingerprint(second));

    [Fact]
    public void The_same_scope_always_fingerprints_the_same_way() =>
        Assert.Equal(ReferenceCursor.Fingerprint(Scope), ReferenceCursor.Fingerprint(Scope));

    /// <summary>Opaque, not secret — but it must at least not be a value a client would think to hand-write.</summary>
    [Fact]
    public void An_encoded_cursor_is_url_safe()
    {
        var encoded = ReferenceCursor.Encode("Sauce & Condiment", "sauce-condiment", Scope);

        Assert.Equal(Uri.EscapeDataString(encoded), encoded);
    }
}
