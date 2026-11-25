namespace Share.Core.Tests;

public class PairUriTests
{
    [Fact]
    public void PairUriRoundTripAndRejects()
    {
        var u = PairUri.Parse("share://pair?code=n5ead5&url=https%3A%2F%2Fx.example%3A443");
        Assert.Equal(new PairUri("https://x.example:443", "N5EAD5"), u);
        Assert.Equal(u, PairUri.Parse(u!.ToString()));
        Assert.Equal(new PairUri("https://x/a b&c=1+2", "ABC"), PairUri.Parse(new PairUri("https://x/a b&c=1+2", "ABC").ToString()));
        // as produced by Go's url.Values and Java's URLEncoder
        Assert.Equal(new PairUri("https://x/a b", "ABC"), PairUri.Parse("share://pair?code=ABC&url=https%3A%2F%2Fx%2Fa+b"));
        // a browser or Windows may normalise the URI
        Assert.Equal(new PairUri("https://x", "ABC"), PairUri.Parse(" share://pair/?code=ABC&url=https%3A%2F%2Fx\n"));
        foreach (var bad in new[] { "https://pair?code=A&url=b", "share://other?code=A&url=b", "share://pair?code=A", "share://pair?url=b", "share://pair?code=&url=b", "share://pair", "", "N5EAD5" })
            Assert.Null(PairUri.Parse(bad));
    }
}
