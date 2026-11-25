using System.Text;

namespace Share.App.Tests;

public sealed class AccountStoreTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly Account _acc = new("https://x.example", "dev1", "SECRET-TOKEN-123", "PC ü", Identity.Generate("PC"));
    private string FilePath => _tmp.File("sub\\account.bin");
    private AccountStore Store() => new(FilePath);

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void RoundTrip()
    {
        Assert.Null(Store().Load());
        Store().Save(_acc);
        var got = Store().Load()!;
        Assert.Equal((_acc.ServerUrl, _acc.DeviceId, _acc.Token, _acc.Name), (got.ServerUrl, got.DeviceId, got.Token, got.Name));
        Assert.Equal(_acc.Identity.Fingerprint, got.Identity.Fingerprint);
    }

    [Fact]
    public void OverwriteKeepsTheLatest()
    {
        Store().Save(_acc);
        Store().Save(_acc with { Name = "Neu" });
        Assert.Equal("Neu", Store().Load()!.Name);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(FilePath)!));
    }

    [Fact]
    public void NoPlaintextOnDisk()
    {
        Store().Save(_acc);
        var raw = Encoding.Latin1.GetString(File.ReadAllBytes(FilePath));
        foreach (var secret in new[] { "SECRET-TOKEN", "PRIVATE KEY", "x.example" }) Assert.DoesNotContain(secret, raw);
    }

    [Fact]
    public void CorruptFileLoadsAsNull()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        foreach (var bytes in new[] { [], new byte[5], Enumerable.Repeat((byte)3, 500).ToArray() })
        {
            File.WriteAllBytes(FilePath, bytes);
            Assert.Null(Store().Load());
        }
        Store().Save(_acc);
        var b = File.ReadAllBytes(FilePath);
        b[^1]++;
        File.WriteAllBytes(FilePath, b);
        Assert.Null(Store().Load());
    }

    [Fact]
    public void ClearRemoves()
    {
        Store().Save(_acc);
        Store().Clear();
        Assert.False(File.Exists(FilePath));
        Store().Clear();
    }
}
