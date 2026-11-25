using System.Text;

namespace Share.Core.Tests;

public class NamesTests
{
    [Theory]
    [InlineData("../x", "x")]
    [InlineData("../evil.txt", "evil.txt")]
    [InlineData("a/b.txt", "b.txt")]
    [InlineData("a\\b.txt", "b.txt")]
    [InlineData("CON", "_CON")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("lpt1.log", "_lpt1.log")]
    [InlineData("", "file")]
    [InlineData("..", "file")]
    [InlineData("a:b?.txt", "a_b_.txt")]
    [InlineData("x:y?.txt", "x_y_.txt")]
    [InlineData("x. ", "x")]
    [InlineData("  .hidden. ", ".hidden")]
    [InlineData("app.apk", "app.apk")]
    [InlineData("a\0b\nc", "a_b_c")]
    [InlineData("ümläut ß.txt", "ümläut ß.txt")]
    public void SafeNames(string name, string want) => Assert.Equal(want, Names.SafeFileName(name));

    [Fact]
    public void LongNameKeepsExtensionAndNeverSplitsSurrogates()
    {
        var o = Names.SafeFileName(string.Concat(Enumerable.Repeat("😀", 150)) + ".txt");
        Assert.True(Encoding.UTF8.GetByteCount(o) <= 200);
        Assert.EndsWith(".txt", o);
        Assert.Equal(o, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(o)));
        var ascii = Names.SafeFileName(new string('a', 300) + ".apk");
        Assert.Equal(200, ascii.Length);
        Assert.EndsWith(".apk", ascii);
    }

    [Fact]
    public void UniqueNames()
    {
        Assert.Equal("a.txt", Names.UniqueName("a.txt", _ => false));
        Assert.Equal("a (1).txt", Names.UniqueName("a.txt", n => n == "a.txt"));
        Assert.Equal("a (2).txt", Names.UniqueName("a.txt", n => n is "a.txt" or "a (1).txt"));
        Assert.Equal("noext (1)", Names.UniqueName("noext", n => n == "noext"));
        Assert.Equal(".hidden (1)", Names.UniqueName(".hidden", n => n == ".hidden"));
    }
}
