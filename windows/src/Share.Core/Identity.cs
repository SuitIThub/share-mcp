using System.Net.Security;
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
        using var both = bare.CopyWithPrivateKey(key);
        CertPem = certPem;
        KeyPem = keyPem;
        Fingerprint = FingerprintOf(bare);
        // Schannel cannot use ephemeral keys, so the key has to pass through a PFX into a (temporary) key container
        ServerCertificate = X509CertificateLoader.LoadPkcs12(both.Export(X509ContentType.Pfx), null);
    }

    public string CertPem { get; }
    public string KeyPem { get; }
    public string Fingerprint { get; }
    public X509Certificate2 ServerCertificate { get; }

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

    internal static SslServerAuthenticationOptions ServerOptions(Identity id) => new()
    {
        ServerCertificate = id.ServerCertificate,
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        ClientCertificateRequired = false,
    };
}
