using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace CreatorPantry.Domain.Managers.Paging;

/// <summary>
/// The position of the last row on a page, as a keyset: the value the list is ordered by, the row's unique
/// natural key to break ties, and a fingerprint of the query it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// A keyset rather than an offset. <c>OFFSET 50</c> re-counts the rows before it on every page and silently
/// skips or repeats entries when the underlying set changes between requests; "resume after this exact row"
/// does neither.
/// </para>
/// <para>
/// The tie-breaker is not decoration. Display names are not unique — two cuisines could legitimately share
/// one — and a cursor on the name alone would then either skip the second or loop on the first forever. It is
/// the row's <em>code</em> or normalized name rather than its id, for a reason that is about SQL and not
/// about design: every one of these tables already has a unique index on that string, EF Core translates a
/// string comparison to an indexable predicate, and it cannot translate a <see cref="Guid"/> comparison at
/// all — C# and SQL Server do not even order GUIDs the same way.
/// </para>
/// <para>
/// <strong>The scope fingerprint is what makes a wrong cursor an error instead of a wrong answer.</strong> A
/// cursor means "after Italian" — which is a position in one specific ordered set. Replayed against a
/// different resource, or the same resource under a different filter, it still decodes and still produces a
/// page, just not the page it names: rows silently skipped or repeated, which is the exact failure the keyset
/// design exists to prevent. Binding the query into the cursor turns that into
/// <see cref="ReferenceErrorCodes.CursorInvalid"/>. The page size is deliberately <em>not</em> part of the
/// scope: a position is a position, so changing how many rows follow it is legitimate.
/// </para>
/// <para>
/// The fingerprint is a checksum, not a signature. It catches a cursor used in the wrong place; it is not
/// keyed, and it is not a defence against a caller who edits one deliberately. It does not need to be —
/// everything a cursor can reach is global reference data the caller may already read, and the decoded halves
/// are only ever used as a <c>WHERE</c> predicate against the route's own table.
/// </para>
/// <para>
/// Opaque to clients by construction: it is base64url, so nobody builds one by hand, and its layout stays
/// free to change. It must never carry anything a caller may not already see — everything in it comes from a
/// row that caller just read.
/// </para>
/// <para>
/// One consequence worth stating: a cursor is only comparable inside the database that produced it, because
/// the ordering it resumes depends on that database's collation. SQL Server's default is case-insensitive and
/// SQLite's is not, so the same catalogue can order two rows differently between them. Harmless here — a
/// cursor never travels between databases, and within one the <c>WHERE</c> and the <c>ORDER BY</c> always
/// agree — but it is why this carries the sort value rather than an index into a sequence.
/// </para>
/// </remarks>
/// <param name="SortValue">The ordering value of the last row returned, such as its display name.</param>
/// <param name="TieBreaker">That row's unique natural key, which always breaks a tie on <paramref name="SortValue"/>.</param>
/// <param name="ScopeFingerprint">Identifies the resource and filters this position belongs to.</param>
public sealed record ReferenceCursor(string SortValue, string TieBreaker, string ScopeFingerprint)
{
    /// <summary>Separates the parts. A control character, so it cannot occur in a display name or a code.</summary>
    private const char Separator = '\u001F';

    /// <summary>
    /// Bytes of the scope digest kept. Eight is ample for catching a misapplied cursor — the thing being
    /// guarded against is a mistake, not a forgery — and keeps the encoded cursor short.
    /// </summary>
    private const int FingerprintBytes = 8;

    public static string Encode(string sortValue, string tieBreaker, string scope) =>
        Base64Url.EncodeToString(
            Encoding.UTF8.GetBytes($"{Fingerprint(scope)}{Separator}{tieBreaker}{Separator}{sortValue}"));

    /// <summary>
    /// Decodes a client-supplied cursor without checking which query it came from. Returns <c>false</c> for
    /// anything malformed rather than throwing: a cursor arrives from a query string, so a bad one is an
    /// ordinary bad request, not an exception.
    /// </summary>
    /// <remarks>
    /// Structure only, so a validator with no knowledge of the route can run it. Comparing
    /// <see cref="ScopeFingerprint"/> against <see cref="Fingerprint"/> is the caller's job, done where the
    /// resource and filters are known.
    /// </remarks>
    public static bool TryDecode(string? value, out ReferenceCursor? cursor)
    {
        cursor = null;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = Base64Url.DecodeFromChars(value);
        }
        catch (FormatException)
        {
            return false;
        }

        var text = Encoding.UTF8.GetString(decoded);

        var firstSeparator = text.IndexOf(Separator, StringComparison.Ordinal);
        if (firstSeparator <= 0)
        {
            return false;
        }

        var secondSeparator = text.IndexOf(Separator, firstSeparator + 1);

        // A tie-breaker is never empty; a sort value may be, so only the left two parts are length-checked.
        if (secondSeparator <= firstSeparator + 1)
        {
            return false;
        }

        cursor = new ReferenceCursor(
            SortValue: text[(secondSeparator + 1)..],
            TieBreaker: text[(firstSeparator + 1)..secondSeparator],
            ScopeFingerprint: text[..firstSeparator]);

        return true;
    }

    /// <summary>
    /// Decodes a cursor and checks it belongs to this scope. No cursor is the first page and always succeeds.
    /// </summary>
    /// <remarks>
    /// The half of cursor handling a validator cannot do, because a validator does not know the route. Every
    /// module's query factory calls this rather than reimplementing the comparison — three copies of "does
    /// this cursor belong here" is three chances for one of them to stop asking.
    /// </remarks>
    public static bool TryResolve(string? value, string scope, out ReferenceCursor? cursor)
    {
        cursor = null;

        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (!TryDecode(value, out var decoded) || decoded!.ScopeFingerprint != Fingerprint(scope))
        {
            return false;
        }

        cursor = decoded;

        return true;
    }

    /// <summary>The fingerprint a cursor issued for this scope carries.</summary>
    public static string Fingerprint(string scope) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)).AsSpan(0, FingerprintBytes));
}
