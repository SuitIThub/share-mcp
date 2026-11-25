using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Share.Core.Tests;

public class IdentityTests
{
    // a throw-away key made with openssl, only ever used here
    private const string Sec1 = """
        -----BEGIN EC PRIVATE KEY-----
        MHcCAQEEIOa68g3uhPRKCQ9UdXDBbzJMrvuZdCmWs3eYIXR05yVwoAoGCCqGSM49
        AwEHoUQDQgAE/EX3omsMhojJedMzCAggoqes98gmqTTht/vQVUDGSLyo30CqXgPM
        tCKCmNseSQxHJWpw1jgg32j62gJM1kx+rg==
        -----END EC PRIVATE KEY-----
        """;

    private const string Pkcs8 = """
        -----BEGIN PRIVATE KEY-----
        MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg5rryDe6E9EoJD1R1
        cMFvMkyu+5l0KZazd5ghdHTnJXChRANCAAT8RfeiawyGiMl50zMICCCip6z3yCap
        NOG3+9BVQMZIvKjfQKpeA8y0IoKY2x5JDEclanDWOCDfaPraAkzWTH6u
        -----END PRIVATE KEY-----
        """;

    private const string Cert = """
        -----BEGIN CERTIFICATE-----
        MIIBfzCCASWgAwIBAgIUVkWLvvFlGa/Oriz3cZVV8xnCcF8wCgYIKoZIzj0EAwIw
        FTETMBEGA1UEAwwKc2hhcmUgdGVzdDAeFw0yNjExMjUxNzE4MjFaFw00NjAxMjQx
        NzE4MjFaMBUxEzARBgNVBAMMCnNoYXJlIHRlc3QwWTATBgcqhkjOPQIBBggqhkjO
        PQMBBwNCAAT8RfeiawyGiMl50zMICCCip6z3yCapNOG3+9BVQMZIvKjfQKpeA8y0
        IoKY2x5JDEclanDWOCDfaPraAkzWTH6uo1MwUTAdBgNVHQ4EFgQURps19lAkQ1/V
        CaRr2VQPEVNXs9UwHwYDVR0jBBgwFoAURps19lAkQ1/VCaRr2VQPEVNXs9UwDwYD
        VR0TAQH/BAUwAwEB/zAKBggqhkjOPQQDAgNIADBFAiEAh7EK7rQXsP/hvQUb+WNy
        9JISZCdYF1LQpHDqpQN9i9MCIACTZj67WA9HE6wssqoy383hIKAtYAEC2caEDNEL
        NVpb
        -----END CERTIFICATE-----
        """;

    private static X509Certificate2 Parse(Identity i) => X509Certificate2.CreateFromPem(i.CertPem);

    [Fact]
    public void FingerprintIsSha256OfDer()
    {
        var i = Identity.Generate("PC");
        Assert.Matches("^[0-9a-f]{64}$", i.Fingerprint);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Parse(i).RawData)), i.Fingerprint);
    }

    [Fact]
    public void SurvivesSaveAndLoad()
    {
        var i = Identity.Generate("PC");
        Assert.Equal(i.Fingerprint, Identity.Load(i.CertPem, i.KeyPem).Fingerprint);
    }

    [Fact]
    public async Task LoadsKeysWrittenByKotlinAndGo()
    {
        foreach (var key in new[] { Sec1, Pkcs8 })
        {
            var i = Identity.Load(Cert, key);
            Assert.Equal("abb65c907b839bc77db45905d708022bbbcadbb25f97b46c5590f48712d282d1", i.Fingerprint);
            await using var srv = new TlsEcho(i);
            using var http = new HttpClient(Pinning.CreateHandler(i.Fingerprint));
            Assert.Equal(HttpStatusCode.NoContent, (await http.GetAsync($"https://127.0.0.1:{srv.Port}/")).StatusCode);
        }
    }

    [Fact]
    public void BrokenPemIsACryptographicError()
    {
        Assert.ThrowsAny<Exception>(() => Identity.Load("x", "y"));
        Assert.ThrowsAny<Exception>(() => Identity.Load(Cert, Identity.Generate("andere").KeyPem));
    }

    [Fact]
    public void CertIsLongLivedP256()
    {
        var c = Parse(Identity.Generate("Ümläut \"x\", O=y"));
        Assert.Equal(256, c.GetECDsaPublicKey()!.KeySize);
        Assert.True(c.NotAfter > DateTime.Now.AddYears(19));
        Assert.True(c.NotBefore < DateTime.Now);
        Assert.StartsWith("CN=share ", c.Subject);
    }

    [Fact]
    public async Task PinnedClientAcceptsOnlyExactFingerprint()
    {
        var a = Identity.Generate("a");
        var b = Identity.Generate("b");
        await using var srv = new TlsEcho(a);
        var url = $"https://127.0.0.1:{srv.Port}/";
        using var ok = new HttpClient(Pinning.CreateHandler(a.Fingerprint));
        Assert.Equal(HttpStatusCode.NoContent, (await ok.GetAsync(url)).StatusCode);
        Assert.Equal(1, srv.Requests);
        Assert.All(srv.Protocols, p => Assert.True(p is SslProtocols.Tls12 or SslProtocols.Tls13));
        using var bad = new HttpClient(Pinning.CreateHandler(b.Fingerprint));
        await Assert.ThrowsAsync<HttpRequestException>(() => bad.GetAsync(url));
        using var plain = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => plain.GetAsync(url));
        Assert.Equal(1, srv.Requests);
    }

    [Fact]
    public async Task OldTlsIsRefused()
    {
        var a = Identity.Generate("a");
        await using var srv = new TlsEcho(a);
        using var tcp = new TcpClient("127.0.0.1", srv.Port);
#pragma warning disable SYSLIB0039, CA5397
        await using var tls = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await Assert.ThrowsAnyAsync<Exception>(() => tls.AuthenticateAsClientAsync("x", null, SslProtocols.Tls11, false));
#pragma warning restore SYSLIB0039, CA5397
    }
}

/// <summary>Minimal TLS server answering every request with 204.</summary>
public sealed class TlsEcho : IAsyncDisposable
{
    private readonly TcpListener _l = new(IPAddress.Loopback, 0);
    private readonly Task _loop;
    private int _requests;
    public int Requests => _requests;
    public readonly System.Collections.Concurrent.ConcurrentBag<SslProtocols> Protocols = [];
    public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;

    public TlsEcho(Identity id)
    {
        _l.Start();
        _loop = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try
                {
                    c = await _l.AcceptTcpClientAsync();
                }
                catch (Exception)
                {
                    return;
                }
                try
                {
                    await using var tls = new SslStream(c.GetStream());
                    await tls.AuthenticateAsServerAsync(Pinning.ServerOptions(id)).WaitAsync(TimeSpan.FromSeconds(3));
                    using var r = new StreamReader(tls);
                    if (await r.ReadLineAsync() is null) continue;
                    while (!string.IsNullOrEmpty(await r.ReadLineAsync())) { }
                    Interlocked.Increment(ref _requests);
                    Protocols.Add(tls.SslProtocol);
                    await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"));
                }
                catch (Exception)
                {
                    // refused handshakes are part of the tests
                }
                finally
                {
                    c.Dispose();
                }
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _l.Dispose();
        await _loop;
    }
}
