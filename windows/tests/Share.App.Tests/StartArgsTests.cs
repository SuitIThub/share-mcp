namespace Share.App.Tests;

public class StartArgsTests
{
    [Fact]
    public void Plain()
    {
        var a = StartArgs.Parse([]);
        Assert.Equal(new Command.Show(), a.Command);
        Assert.False(a.Background);
        Assert.Null(a.DataDir);
        Assert.False(a.AllowLocalHttp);
    }

    [Fact]
    public void Background()
    {
        var a = StartArgs.Parse(["--background"]);
        Assert.True(a.Background);
        Assert.Null(a.Command);
    }

    [Fact]
    public void SendPaths()
    {
        Assert.Equal(["C:\\Öl & Co\\a b.txt", "D:\\x"], Assert.IsType<Command.Send>(StartArgs.Parse(["--send", "C:\\Öl & Co\\a b.txt", "D:\\x"]).Command).Paths);
        Assert.Equal(["C:\\a.txt"], Assert.IsType<Command.Send>(StartArgs.Parse(["C:\\a.txt"]).Command).Paths);
        Assert.Equal([Path.GetFullPath("rel.txt")], Assert.IsType<Command.Send>(StartArgs.Parse(["rel.txt"]).Command).Paths);
        Assert.Equal(200, Assert.IsType<Command.Send>(StartArgs.Parse(["--send", .. Enumerable.Range(0, 200).Select(i => $"C:\\{i}.txt")]).Command).Paths.Count);
    }

    [Fact]
    public void SendWithoutPathsOpensTheWindow() => Assert.Equal(new Command.Show(), StartArgs.Parse(["--send"]).Command);

    [Fact]
    public void FilesNamedLikeOptionsAreFiles() =>
        Assert.Equal(["C:\\--background", Path.GetFullPath("--data-dir")], Assert.IsType<Command.Send>(StartArgs.Parse(["--send", "C:\\--background", "--data-dir"]).Command).Paths);

    [Fact]
    public void BrokenPathsDoNotThrow() => Assert.Equal(["a\0b", ""], Assert.IsType<Command.Send>(StartArgs.Parse(["--send", "a\0b", ""]).Command).Paths);

    [Fact]
    public void PairLinkIsForwarded()
    {
        Assert.Equal(new Command.Pair("share://pair?code=A&url=b"), StartArgs.Parse(["share://pair?code=A&url=b"]).Command);
        Assert.Equal(new Command.Show(), StartArgs.Parse(["share://other"]).Command);
    }

    [Fact]
    public void TestOptions()
    {
        var a = StartArgs.Parse(["--data-dir", "C:\\d", "--allow-local-http", "--send", "C:\\x"]);
        Assert.Equal("C:\\d", a.DataDir);
        Assert.True(a.AllowLocalHttp);
        Assert.IsType<Command.Send>(a.Command);
        Assert.Null(StartArgs.Parse(["--data-dir"]).DataDir);
    }

    [Fact]
    public void ToastActivationStartsInBackground() => Assert.True(StartArgs.Parse(["-ToastActivated"]).Background);
}
