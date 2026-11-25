namespace Share.Core.Tests;

public class DiskFileTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private static readonly byte[] Data = TempDir.Random(300_000);

    public void Dispose() => _tmp.Dispose();

    private static byte[] Read(IFileSource s, long offset = 0)
    {
        using var m = new MemoryStream();
        using var i = s.OpenAt(offset);
        i.CopyTo(m);
        return m.ToArray();
    }

    [Fact]
    public void ReadsNameSizeMimeAndTime()
    {
        var p = _tmp.File("bild 1.JPG", Data);
        using var f = DiskFile.Open(p);
        Assert.Equal("bild 1.JPG", f.Item.Name);
        Assert.Equal(300_000, f.Item.Size);
        Assert.Equal("image/jpeg", f.Item.Mime);
        Assert.Equal(File.GetLastWriteTimeUtc(p), f.Item.ModTime.UtcDateTime);
        Assert.True(f.Unchanged());
        using var apk = DiskFile.Open(_tmp.File("a.apk", [1]));
        Assert.Equal("application/vnd.android.package-archive", apk.Item.Mime);
        using var x = DiskFile.Open(_tmp.File("a.unbekannt", [1]));
        Assert.Equal("application/octet-stream", x.Item.Mime);
        using var empty = DiskFile.Open(_tmp.File("leer", []));
        Assert.Empty(Read(empty));
    }

    [Fact]
    public async Task ReadsFromAnyOffsetInParallel()
    {
        using var f = DiskFile.Open(_tmp.File("a", Data));
        var parts = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => Read(f, i * 1000))));
        for (var i = 0; i < 8; i++) Assert.Equal(Data[(i * 1000)..], parts[i]);
        Assert.Empty(Read(f, 300_000));
    }

    [Fact]
    public void OtherProgramsMayWriteRenameAndDelete()
    {
        var p = _tmp.File("a", Data);
        using (var f = DiskFile.Open(p)) File.WriteAllBytes(p, [1, 2, 3]);
        using (var f = DiskFile.Open(p)) File.Move(p, _tmp.File("b"));
        using (var f = DiskFile.Open(_tmp.File("b"))) File.Delete(_tmp.File("b"));
        Assert.False(File.Exists(_tmp.File("b")));
    }

    [Fact]
    public void InPlaceChangeIsDetected()
    {
        var p = _tmp.File("a", Data);
        using var f = DiskFile.Open(p);
        Thread.Sleep(20);
        File.WriteAllBytes(p, new byte[300_000]);
        Assert.False(f.Unchanged());
    }

    [Fact]
    public void ReplacedFileKeepsServingTheOldContent()
    {
        var p = _tmp.File("a", Data);
        using var f = DiskFile.Open(p);
        var fresh = _tmp.File("neu", new byte[10]);
        File.Delete(p);
        File.Move(fresh, p);
        Assert.True(f.Unchanged());
        Assert.Equal(Data, Read(f));
    }

    [Fact]
    public void DirectoryMissingAndLockedAreReadable()
    {
        Assert.Contains("Ordner", Assert.Throws<SourceException>(() => DiskFile.Open(_tmp.Path)).Message);
        Assert.Contains("fehlt.txt", Assert.Throws<SourceException>(() => DiskFile.Open(_tmp.File("fehlt.txt"))).Message);
        var p = _tmp.File("gesperrt.txt", Data);
        using (new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Contains("gesperrt.txt", Assert.Throws<SourceException>(() => DiskFile.Open(p)).Message);
        foreach (var bad in new[] { "", "  ", "a\0b", "\\\\?\\", "::" }) Assert.Throws<SourceException>(() => DiskFile.Open(bad));
    }

    [Fact]
    public void ReadAfterReleaseIsIOException()
    {
        var f = DiskFile.Open(_tmp.File("a", Data));
        var open = f.OpenAt(0);
        f.Release();
        f.Release();
        Assert.Throws<IOException>(() => open.Read(new byte[10]));
        Assert.Throws<IOException>(() => Read(f));
        Assert.False(f.Unchanged());
    }

    [Fact]
    public void DeleteOnReleaseRemovesTheCopy()
    {
        var p = _tmp.File("kopie", Data);
        var f = DiskFile.Open(p, deleteOnRelease: true);
        Assert.Equal(Data, Read(f));
        f.Release();
        Assert.False(File.Exists(p));
    }
}
