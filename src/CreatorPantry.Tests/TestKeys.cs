using System.Security.Cryptography;

namespace CreatorPantry.Tests;

/// <summary>Throwaway ECDSA P-256 key pairs for internal-token tests. Generated per test run, never persisted.</summary>
internal sealed class TestKeyPair
{
    private TestKeyPair(string privatePem, string publicPem) => (PrivateKeyPem, PublicKeyPem) = (privatePem, publicPem);

    /// <summary>The key pair every in-process API and gateway host is configured with.</summary>
    public static TestKeyPair Shared { get; } = Create();

    public string PrivateKeyPem { get; }

    public string PublicKeyPem { get; }

    public static TestKeyPair Create(ECCurve? curve = null)
    {
        using var key = ECDsa.Create(curve ?? ECCurve.NamedCurves.nistP256);
        return new TestKeyPair(key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }
}
