namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// Builds the string that identifies one ordered set of prompts, so a cursor can be bound to it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The workspace is in the scope, and that is the point.</strong> Nothing in the shared paging kernel
/// puts it there, because reference data has no workspace to put. For workspace-owned data the ordered set a
/// cursor names depends on the resolved workspace as much as on the filters — and that workspace arrives
/// ambiently, through the query filter, not as a request field. A cursor minted in one workspace and replayed
/// in another would otherwise decode cleanly and produce a page of the wrong library.
/// </para>
/// <para>
/// <strong>The ordering is named even though there is only one.</strong> A position in one ordering is
/// meaningless in another, so the scope has to carry it — and writing it now rather than when a second ordering
/// arrives means that change does not silently invalidate every cursor a client is holding.
/// </para>
/// <para>
/// <strong>The page size is deliberately not in it</strong>, nor is <c>includeTotal</c>: a position is a
/// position, so asking for a different number of rows after it, or for a count beside them, is legitimate. The
/// same rule <see cref="CreatorPantry.Domain.Managers.Paging.ReferenceCursor"/> states.
/// </para>
/// <para>
/// <strong>Both free-text parts are length-prefixed, and that is not decoration.</strong> This string is
/// assembled from <c>|</c> and <c>=</c>, and <em>two</em> of its values are text a caller chooses. The channel
/// is not validated against the catalogue here — a read has to keep answering for retired keys, so
/// <see cref="PromptSearchPolicy.NormalizeChannel"/> only trims — which means
/// <c>?channel=instagram|q=foo</c> and <c>?channel=instagram&amp;search=foo</c> would otherwise build the
/// identical scope and accept each other's cursors. Same workspace and same caller, so never a leak, but
/// exactly the silent skipping and repeating of rows the keyset exists to prevent. Writing each value's length
/// before it makes the string unambiguous whatever the value contains, which <c>ReferenceQueryKey</c>'s
/// put-the-term-last rule cannot do once there are two such values.
/// </para>
/// <para>
/// <strong>None of this is an authorization control.</strong> The fingerprint is an unkeyed checksum that
/// catches a cursor used in the wrong place; workspace isolation is the global query filter, which scopes the
/// rows whatever cursor arrives. A cursor edited by hand reaches only rows its own workspace can already read.
/// </para>
/// </remarks>
public static class PromptSearchScope
{
    /// <summary>The resource this scope belongs to, matching the route's collection segment.</summary>
    public const string Resource = "prompts";

    /// <summary>
    /// The one ordering this seam offers, named so the scope can carry it. Not an enum: an enum with a single
    /// member is a decision dressed up as a choice, and a second ordering needs an index before it needs a name.
    /// </summary>
    public const string Ordering = "CreatedDescending";

    public static string Build(Guid workspaceId, PromptSearchFilters filters)
    {
        ArgumentNullException.ThrowIfNull(filters);

        return string.Join(
            '|',
            $"resource={Resource}",
            $"workspace={workspaceId:D}",
            $"sort={Ordering}",
            $"channel={Counted(filters.ChannelKey)}",
            $"q={Counted(filters.Search)}");
    }

    /// <summary>
    /// A caller-supplied value, with its length in front so the assembled string cannot be read two ways.
    /// </summary>
    /// <remarks>
    /// An absent value is distinct from an empty one: <c>-</c> rather than <c>0:</c>. Nothing depends on the
    /// difference today — neither filter can arrive as the empty string, since both normalize it to null — but
    /// a scope that conflated "no filter" with "a filter matching everything" would be the same bug this method
    /// exists to prevent, one layer up.
    /// </remarks>
    private static string Counted(string? value) => value is null ? "-" : $"{value.Length}:{value}";
}
