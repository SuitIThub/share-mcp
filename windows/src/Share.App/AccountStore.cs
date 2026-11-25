using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Share.Core;

namespace Share.App;

/// <summary>The pairing (token, certificate, key), encrypted for this Windows user.</summary>
public sealed class AccountStore(string file)
{
    private static readonly byte[] Entropy = "share-account-1"u8.ToArray();

    private sealed record Stored(string ServerUrl, string DeviceId, string Token, string Name, string Cert, string Key);

    /// <summary>Null if not paired or if the file cannot be decrypted (then the device has to be paired again).</summary>
    public Account? Load()
    {
        try
        {
            if (!File.Exists(file)) return null;
            var s = JsonSerializer.Deserialize<Stored>(ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser))!;
            return new Account(s.ServerUrl, s.DeviceId, s.Token, s.Name, Identity.Load(s.Cert, s.Key));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException or NullReferenceException or ArgumentException)
        {
            Trace.TraceWarning("cannot read pairing: " + e.GetType().Name);
            return null;
        }
    }

    public void Save(Account a)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(new Stored(a.ServerUrl, a.DeviceId, a.Token, a.Name, a.Identity.CertPem, a.Identity.KeyPem));
        File.WriteAllBytes(file + ".tmp", ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser));
        File.Move(file + ".tmp", file, true);
    }

    public void Clear() => File.Delete(file);
}
