using System.Security.Cryptography;
using System.Text;

namespace CreatorPantry.Domain.Managers.Reference;

/// <summary>
/// Derives a seeded row's primary key from its natural key, so the catalogue's identifiers are a function of
/// its content rather than of when and where it was first seeded.
/// </summary>
/// <remarks>
/// <para>
/// A developer's database, a CI run, and a deployed environment therefore hold the <em>same</em> ids for the
/// same rows. That is what lets the seed set cross-reference itself without a lookup pass — an ingredient
/// names its default count unit by code and gets the right <see cref="Guid"/> — and it is what lets the
/// idempotency test compare whole rows across runs instead of merely counting them.
/// </para>
/// <para>
/// The derivation is one-way and namespaced, so two tables that happen to share a natural key ("bake" as both
/// a technique and, one day, something else) never collide.
/// </para>
/// </remarks>
internal static class SeedId
{
    /// <summary>
    /// Versioned, because changing the derivation re-points every seeded row. A future change that must not
    /// orphan existing data bumps this deliberately and migrates; it never edits the string in place.
    /// </summary>
    private const string Namespace = "creatorpantry.seed.v1";

    /// <summary>Derives the id for one row from the table it lives in and the natural key within that table.</summary>
    public static Guid For(string table, params string[] naturalKey)
    {
        var material = $"{Namespace}|{table}|{string.Join('|', naturalKey)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));

        Span<byte> id = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(id);

        // RFC 9562 version 8 (custom) and variant 10xx. Truncated SHA-256 is not a version 5 UUID — that one is
        // defined over SHA-1 — so claiming version 5 here would misdescribe how the value was produced. Setting
        // the bits at all matters because the result is stored as a uniqueidentifier and read by tools that
        // expect a well-formed UUID rather than sixteen arbitrary bytes.
        id[6] = (byte)((id[6] & 0x0F) | 0x80);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);

        return new Guid(id, bigEndian: true);
    }
}
