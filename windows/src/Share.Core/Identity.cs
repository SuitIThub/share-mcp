using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Share.Core;

/// <summary>The device's self-signed TLS certificate; its fingerprint is what peers pin.</summary>
public sealed class Identity
{
    private Identity(string certPem, string keyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(keyPem);
        using var bare = X509Certificate2.CreateFromPem(certPem);
        using var both = bare.CopyWithPrivateKey(key); // they must belong together
        CertPem = certPem;
        KeyPem = keyPem;
        Fingerprint = FingerprintOf(bare);
    }

    public string CertPem { get; }
    public string KeyPem { get; }
    public string Fingerprint { get; }

    /// <summary>The certificate for a TLS server; dispose it when serving ends.</summary>
    public ServerCertificate UseServerCertificate() => ServerCertificate.Use(this);

    public static Identity Generate(string name)
    {
        // the name is cosmetic; keep it free of characters that are special in distinguished names
        var clean = new string([.. name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-').Take(40)]).Trim();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=share " + clean, key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var now = DateTimeOffset.UtcNow;
        using var cert = req.CreateSelfSigned(now.AddHours(-1), now.AddYears(20));
        return new Identity(cert.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    public static Identity Load(string certPem, string keyPem) => new(certPem, keyPem);

    internal static string FingerprintOf(X509Certificate c) => Convert.ToHexStringLower(SHA256.HashData(c.GetRawCertData()));
}

/// <summary>
/// Schannel cannot use ephemeral keys, so while serving, the key lives in a key container on disk. It has a fixed name per
/// identity: after a crash the next use takes it over, and nothing accumulates.
/// </summary>
public sealed class ServerCertificate : IDisposable
{
    private static readonly Dictionary<string, (X509Certificate2 Certificate, CngKey? Key, int Users)> Used = [];
    private readonly string _fingerprint;
    private int _open = 1;

    private ServerCertificate(string fingerprint, X509Certificate2 certificate) => (_fingerprint, Certificate) = (fingerprint, certificate);

    public X509Certificate2 Certificate { get; }

    internal static ServerCertificate Use(Identity id)
    {
        lock (Used)
        {
            if (!Used.TryGetValue(id.Fingerprint, out var u))
            {
                using var bare = X509Certificate2.CreateFromPem(id.CertPem);
                using var key = ECDsa.Create();
                key.ImportFromPem(id.KeyPem);
                if (OperatingSystem.IsWindows())
                {
                    var stored = Store(key, "Share-" + id.Fingerprint);
                    using var cng = new ECDsaCng(stored);
                    u = (bare.CopyWithPrivateKey(cng), stored, 0);
                }
                else
                {
                    u = (bare.CopyWithPrivateKey(key), null, 0);
                }
            }
            Used[id.Fingerprint] = u with { Users = u.Users + 1 };
            return new ServerCertificate(id.Fingerprint, u.Certificate);
        }
    }

    [SupportedOSPlatform("windows")]
    private static CngKey Store(ECDsa key, string name)
    {
        var p = key.ExportParameters(true);
        byte[] blob = [0x45, 0x43, 0x53, 0x32, 32, 0, 0, 0, .. p.Q.X!, .. p.Q.Y!, .. p.D!]; // BCRYPT_ECCKEY_BLOB for a private P-256 key
        var how = new CngKeyCreationParameters { KeyCreationOptions = CngKeyCreationOptions.OverwriteExistingKey, ExportPolicy = CngExportPolicies.None };
        how.Parameters.Add(new CngProperty(CngKeyBlobFormat.EccPrivateBlob.Format, blob, CngPropertyOptions.None));
        return CngKey.Create(CngAlgorithm.ECDsaP256, name, how);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _open, 0) == 0) return;
        lock (Used)
        {
            var u = Used[_fingerprint];
            if (u.Users > 1)
            {
                Used[_fingerprint] = u with { Users = u.Users - 1 };
                return;
            }
            Used.Remove(_fingerprint);
            u.Certificate.Dispose();
            if (!OperatingSystem.IsWindows() || u.Key is null) return;
            try
            {
                u.Key.Delete();
            }
            catch (CryptographicException)
            {
                u.Key.Dispose(); // the next use takes it over
            }
        }
    }
}

public static class Pinning
{
    /// <summary>Accepts only a peer whose leaf certificate has exactly this fingerprint; host name and chain are irrelevant.</summary>
    public static SocketsHttpHandler CreateHandler(string fingerprint) => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        SslOptions = new SslClientAuthenticationOptions
        {
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && Identity.FingerprintOf(cert) == fingerprint,
        },
    };

    internal static SslServerAuthenticationOptions ServerOptions(ServerCertificate c) => new()
    {
        ServerCertificate = c.Certificate,
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        ClientCertificateRequired = false,
    };
}
