using System.Security.Cryptography;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// Mints the token a seed is generated from. The <em>only</em> source of randomness in the seed generator.
/// </summary>
/// <remarks>
/// <para>
/// Injected so a test is deterministic by construction rather than by care. Everything downstream of a token is
/// a pure function of it (<see cref="ContentSeedSelector"/>), so a fixed token fixes the whole result — where
/// injecting a shared <c>Random</c> would make the output depend on the order the facets happened to draw from
/// it, and a later facet added in the middle would silently change every seed after it.
/// </para>
/// <para>
/// A caller that supplies its own token never reaches this: the token is the reproducible part of the contract,
/// so generating one is only ever the fallback for a caller that did not ask for a particular seed.
/// </para>
/// </remarks>
public interface IContentSeedTokenSource
{
    /// <summary>A fresh token. Must satisfy <c>ContentSeedPolicy.IsToken</c>.</summary>
    string Next();
}

/// <summary>
/// Eight random bytes as url-safe base64, which is eleven characters a creator can paste into a link.
/// </summary>
/// <remarks>
/// <c>RandomNumberGenerator</c> rather than <c>Random.Shared</c>, not because a seed token is a secret — it is
/// not, and it protects nothing — but because two creators generating a seed in the same millisecond on the same
/// server should not be able to get the same one. Eight bytes make that collision irrelevant at any volume this
/// endpoint will see.
/// </remarks>
internal sealed class ContentSeedTokenSource : IContentSeedTokenSource
{
    public string Next()
    {
        Span<byte> bytes = stackalloc byte[ContentSeedPolicy.TokenBytes];
        RandomNumberGenerator.Fill(bytes);

        return WebEncoders.Base64UrlEncode(bytes);
    }

    /// <summary>
    /// Url-safe base64 without padding, written out rather than taken from a web package: this assembly is the
    /// domain and has no business depending on one.
    /// </summary>
    private static class WebEncoders
    {
        public static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
