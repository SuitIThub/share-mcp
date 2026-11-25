namespace Share.App.Tests;

public sealed class ShareTargetTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly List<string> _log = [];

    public void Dispose() => _tmp.Dispose();

    private sealed class Data(List<string> log) : IShareData
    {
        public List<SharedItem> Items = [];
        public string? TextValue, LinkValue;
        public byte[]? Image;
        public Exception? Fail;

        public Task<IReadOnlyList<SharedItem>> FilesAsync() => Fail is null ? Task.FromResult<IReadOnlyList<SharedItem>>(Items) : Task.FromException<IReadOnlyList<SharedItem>>(Fail);
        public Task<string?> LinkAsync() => Task.FromResult(LinkValue);
        public Task<string?> TextAsync() => Task.FromResult(TextValue);
        public Task<Stream?> ImageAsync() => Task.FromResult<Stream?>(Image is null ? null : new MemoryStream(Image));
        public void Started() => log.Add("started");
        public void DataRetrieved() => log.Add("retrieved");
        public void Completed() => log.Add("completed");
        public void Error(string message) => log.Add("error " + message);
    }

    private async Task<List<Command>> Run(Data d, bool accept = true)
    {
        var sent = new List<Command>();
        await ShareTarget.HandleAsync(d, _tmp.File("temp"), c =>
        {
            _log.Add("sent");
            sent.Add(c);
            return Task.FromResult(accept);
        });
        return sent;
    }

    private static SharedItem Item(string name, string? path, byte[]? content = null) => new(name, path, () => Task.FromResult<Stream>(new MemoryStream(content ?? [])));

    [Fact]
    public async Task FilesWithPath()
    {
        var sent = await Run(new Data(_log) { Items = [Item("a.txt", "C:\\a.txt"), Item("b.txt", "C:\\b.txt")], TextValue = "auch Text" });
        Assert.Equal(["C:\\a.txt", "C:\\b.txt"], Assert.IsType<Command.Send>(Assert.Single(sent)).Paths);
    }

    [Fact]
    public async Task SharedFilesAreHandedOverBeforeReportCompleted()
    {
        await Run(new Data(_log) { Items = [Item("a.txt", "C:\\a.txt")] });
        Assert.Equal(["started", "retrieved", "sent", "completed"], _log); // completing lets the other program clean up
    }

    [Fact]
    public async Task FilesWithoutPathAreCopied()
    {
        var sent = await Run(new Data(_log) { Items = [Item("foto:1?.jpg", "", [1, 2, 3]), Item("foto:1?.jpg", null, [4]), Item("c.txt", "C:\\c.txt")] });
        var paths = Assert.IsType<Command.Send>(Assert.Single(sent)).Paths;
        Assert.Equal([1, 2, 3], File.ReadAllBytes(paths[0]));
        Assert.Equal([4], File.ReadAllBytes(paths[1]));
        Assert.Equal("foto_1_.jpg", Path.GetFileName(paths[0]));
        Assert.Equal("foto_1_.jpg", Path.GetFileName(paths[1])); // the receiver sees the real name
        Assert.StartsWith(_tmp.File("temp"), paths[0]);
        Assert.Equal("C:\\c.txt", paths[2]);
    }

    [Fact]
    public async Task LinkTextAndImage()
    {
        Assert.Equal(new Command.SendText("https://x.de/"), Assert.Single(await Run(new Data(_log) { LinkValue = "https://x.de/", TextValue = "Titel" })));
        Assert.Equal(new Command.SendText("hallo"), Assert.Single(await Run(new Data(_log) { TextValue = "hallo", Image = [1] })));
        var image = Assert.IsType<Command.Send>(Assert.Single(await Run(new Data(_log) { Image = [9, 8] }))).Paths.Single();
        Assert.Equal([9, 8], File.ReadAllBytes(image));
        Assert.Matches(@"^Bild .*\.png$", Path.GetFileName(image));
    }

    [Fact]
    public async Task NothingUsable()
    {
        Assert.Empty(await Run(new Data(_log)));
        Assert.Empty(await Run(new Data(_log) { TextValue = "  " }));
        Assert.Contains("error Hier gibt es nichts, was gesendet werden kann", _log);
        Assert.DoesNotContain("completed", _log);
    }

    [Fact]
    public async Task ReadErrorIsReported()
    {
        Assert.Empty(await Run(new Data(_log) { Fail = new System.Runtime.InteropServices.COMException("weg") }));
        Assert.Equal("error Der Inhalt konnte nicht gelesen werden", _log[^1]);
        Assert.Empty(await Run(new Data(_log) { Items = [new SharedItem("a", null, () => throw new IOException("kaputt"))] }));
        Assert.Equal("error Der Inhalt konnte nicht gelesen werden", _log[^1]);
    }

    [Fact]
    public async Task UnresponsiveProgramIsReported()
    {
        await Run(new Data(_log) { TextValue = "x" }, accept: false);
        Assert.Equal("error Share reagiert nicht", _log[^1]);
    }
}
