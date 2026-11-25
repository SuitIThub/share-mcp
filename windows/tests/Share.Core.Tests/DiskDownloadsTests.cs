using System.Text;

namespace Share.Core.Tests;

public sealed class DiskDownloadsTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private string Dir => _tmp.File("dl");
    private DiskDownloads Store() => new(Dir, _tmp.File("state.json"));
    private string[] Files() => Directory.Exists(Dir) ? [.. Directory.GetFiles(Dir).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)] : [];

    private string Download(string name, string data = "x", string tid = "t1", int n = 0)
    {
        var p = Store().Part(tid, n, new FileItem(name, data.Length));
        using (var s = p.Append(0)) s.Write(Encoding.UTF8.GetBytes(data));
        return p.Finish();
    }

    [Fact]
    public void PartSurvivesNewInstance()
    {
        var item = new FileItem("a.bin", 5000);
        var data = TempDir.Random(5000);
        var p = Store().Part("t1", 0, item);
        Assert.Equal(0, p.Size());
        using (var s = p.Append(0)) s.Write(data, 0, 1000);
        var q = Store().Part("t1", 0, item);
        Assert.Equal(1000, q.Size());
        Assert.Null(q.FinishedRef);
        using (var s = q.Append(1000)) s.Write(data, 1000, 4000);
        var path = q.Finish();
        Assert.Equal(Path.Combine(Dir, "a.bin"), path);
        Assert.Equal(data, File.ReadAllBytes(path));
        Assert.Equal(["a.bin"], Files());
        Assert.Equal(path, Store().Part("t1", 0, item).FinishedRef);
    }

    [Fact]
    public void NameConflictGetsSuffix()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, "A.TXT"), "alt");
        Directory.CreateDirectory(Path.Combine(Dir, "ordner"));
        Assert.Equal("a (1).txt", Path.GetFileName(Download("a.txt")));
        Assert.Equal("ordner (1)", Path.GetFileName(Download("ordner", tid: "t2")));
        Assert.Equal("alt", File.ReadAllText(Path.Combine(Dir, "A.TXT")));
    }

    [Fact]
    public void FortyDownloadsOfTheSameName()
    {
        var refs = Enumerable.Range(1, 40).Select(i => Download("app.apk", "v" + i, "t" + i)).ToList();
        Assert.Equal(40, refs.Distinct().Count());
        for (var i = 0; i < 40; i++) Assert.Equal("v" + (i + 1), File.ReadAllText(refs[i]));
    }

    [Fact]
    public void TwoParallelFinishesNeverOverwrite()
    {
        var parts = Enumerable.Range(0, 20).Select(i =>
        {
            var p = Store().Part("t" + i, 0, new FileItem("gleich.txt", 2));
            using (var s = p.Append(0)) s.Write(Encoding.UTF8.GetBytes(i.ToString("00")));
            return p;
        }).ToList();
        var refs = new string[20];
        Parallel.For(0, 20, i => refs[i] = parts[i].Finish());
        for (var i = 0; i < 20; i++) Assert.Equal(i.ToString("00"), File.ReadAllText(refs[i]));
        Assert.Equal(20, Files().Length);
    }

    [Theory]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("a:b", "a_b")]
    [InlineData("..\\..\\x", "x")]
    [InlineData("C:\\Windows\\x.dll", "x.dll")]
    [InlineData("a.txt:strom", "a.txt_strom")]
    [InlineData("", "file")]
    public void HostileAndReservedNames(string name, string want)
    {
        Assert.Equal(Path.Combine(Dir, want), Download(name));
        Assert.Equal([want], Files());
    }

    [Fact]
    public void VeryLongNamesWork()
    {
        var path = Download(new string('ä', 300) + ".txt");
        Assert.EndsWith(".txt", path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void EmptyFile()
    {
        var path = Store().Part("t1", 0, new FileItem("leer.txt", 0)).Finish();
        Assert.Equal(0, new FileInfo(path).Length);
    }

    [Fact]
    public void ForgetRemovesOnlyThatOffer()
    {
        foreach (var t in new[] { "t1", "t2", "t1b" })
            using (var s = Store().Part(t, 0, new FileItem(t + ".bin", 9)).Append(0))
                s.WriteByte(1);
        Assert.Equal(3, Files().Length);
        Store().Forget("t1");
        Assert.Equal(2, Files().Length);
        Store().Retain(new HashSet<string> { "t2" });
        Assert.Single(Files());
        Assert.Equal(1, Store().Part("t2", 0, new FileItem("t2.bin", 9)).Size());
    }

    [Fact]
    public void ForgetKeepsFinishedFiles()
    {
        var path = Download("fertig.txt");
        Store().Forget("t1");
        Assert.True(File.Exists(path));
        Assert.Null(Store().Part("t1", 0, new FileItem("fertig.txt", 1)).FinishedRef);
    }

    [Fact]
    public void FinishedRefIsGoneWhenTheUserDeletedTheFile()
    {
        File.Delete(Download("a.txt"));
        Assert.Null(Store().Part("t1", 0, new FileItem("a.txt", 1)).FinishedRef);
    }

    [Fact]
    public void ResetStartsOver()
    {
        var p = Store().Part("t1", 0, new FileItem("a.txt", 2));
        using (var s = p.Append(0)) s.Write(new byte[10]);
        p.Reset();
        Assert.Equal(0, p.Size());
        using (var s = p.Append(0)) s.Write("ok"u8);
        Assert.Equal("ok", File.ReadAllText(p.Finish()));
    }

    [Fact]
    public void AppendCutsOffWhatIsBehindTheOffset()
    {
        var p = Store().Part("t1", 0, new FileItem("a.txt", 3));
        using (var s = p.Append(0)) s.Write("abcdef"u8);
        using (var s = p.Append(2)) s.Write("X"u8);
        Assert.Equal("abX", File.ReadAllText(p.Finish()));
    }

    [Fact]
    public void AppendBehindTheEndIsBusy()
    {
        var p = Store().Part("t1", 0, new FileItem("a.txt", 9));
        using (var s = p.Append(0)) s.Write("abc"u8);
        Assert.Throws<StorageBusyException>(() => p.Append(4));
        Assert.Throws<StorageBusyException>(() => Store().Part("t9", 0, new FileItem("b.txt", 9)).Append(4));
    }

    /// <summary>A virus scanner may hold the part or the target for a moment.</summary>
    [Fact]
    public void LockedPartIsBusyNotLost()
    {
        var p = Store().Part("t1", 0, new FileItem("a.txt", 3));
        using (var s = p.Append(0)) s.Write("abc"u8);
        using (new FileStream(Directory.GetFiles(Dir).Single(), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(3, p.Size());
            Assert.Throws<StorageBusyException>(() => p.Append(3));
            Assert.Throws<StorageBusyException>(() => p.Finish());
        }
        Assert.Equal("abc", File.ReadAllText(p.Finish()));
        Assert.Equal(["a.txt"], Files());
    }

    [Fact]
    public void CorruptStateFileStartsEmpty()
    {
        foreach (var junk in new[] { "", "{", "[]", "null", "{\"a\":5}" })
        {
            File.WriteAllText(_tmp.File("state.json"), junk);
            Assert.Null(Store().Part("t1", 0, new FileItem("a.txt", 1)).FinishedRef);
        }
        Download("a.txt");
        Assert.NotNull(Store().Part("t1", 0, new FileItem("a.txt", 1)).FinishedRef);
    }

    [Fact]
    public void MissingDirectoryIsCreated()
    {
        Assert.False(Directory.Exists(Dir));
        Download("a.txt");
        Assert.True(Directory.Exists(Dir));
    }

    [Fact]
    public void UnusableDirectoryIsIOException()
    {
        File.WriteAllText(_tmp.File("datei"), "");
        var s = new DiskDownloads(_tmp.File("datei"), _tmp.File("state.json"));
        Assert.ThrowsAny<IOException>(() => s.Part("t1", 0, new FileItem("a", 1)).Append(0));
    }

    [Fact]
    public void DownloadsFolderIsTheUsersOwn() => Assert.True(Directory.Exists(DiskDownloads.DefaultDirectory));

    /// <summary>A virus scanner, backup or indexer may have the state file open at any moment.</summary>
    [Fact]
    public void BusyStateFileIsOnlyBusyAndLosesNothing()
    {
        var item = new FileItem("a.bin", 10);
        using (var s = Store().Part("t1", 0, item).Append(0)) s.Write(new byte[5]);
        Assert.Throws<StorageBusyException>(() =>
        {
            using (new FileStream(_tmp.File("state.json"), FileMode.Open, FileAccess.Read, FileShare.Read)) Store().Part("t2", 0, item).Append(0).Dispose();
        });
        Assert.Throws<StorageBusyException>(() =>
        {
            using (new FileStream(_tmp.File("state.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Store().Part("t2", 0, item).Append(0).Dispose();
        });
        Assert.Equal(5, Store().Part("t1", 0, item).Size());
        Store().Forget("t1"); // still knows the part
        Assert.DoesNotContain(Files(), f => f.Contains("t1"));
    }

    [Fact]
    public async Task BrieflyBusyStateFileIsWaitedFor()
    {
        var item = new FileItem("a.bin", 10);
        using (Store().Part("t1", 0, item).Append(0)) { }
        var open = new FileStream(_tmp.File("state.json"), FileMode.Open, FileAccess.Read, FileShare.None);
        var closing = Task.Delay(150).ContinueWith(_ => open.Dispose());
        using (Store().Part("t2", 0, item).Append(0)) { }
        await closing;
    }

    /// <summary>Once the file is in place it is finished, whether or not that can be written down.</summary>
    [Fact]
    public void FinishingWithBusyStateFileDoesNotDownloadAgain()
    {
        var item = new FileItem("a.bin", 5);
        var store = Store();
        var p = store.Part("t1", 0, item);
        using (var s = p.Append(0)) s.Write(new byte[5]);
        string done;
        using (new FileStream(_tmp.File("state.json"), FileMode.Open, FileAccess.Read, FileShare.Read)) done = p.Finish();
        Assert.True(File.Exists(done));
        Assert.Equal(done, store.Part("t1", 0, item).FinishedRef);
    }
}
