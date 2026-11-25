using System.Text.Json;

namespace Share.Core.Tests;

public class ProtocolTests
{
    private static readonly byte[] Secret = Enumerable.Repeat((byte)1, 32).ToArray();
    private const string GoToken = "d1.9c4cc17b1fd7497fa6248a0cdccf057c9f63b1b4aa9547b5e06aa7457be89383";

    private static T From<T>(string json) => JsonSerializer.Deserialize<T>(json, ShareJson.Options)!;
    private static string To<T>(T v) => JsonSerializer.Serialize(v, ShareJson.Options);

    [Fact]
    public void DecodesTransferWithOffsetAndNanoTimes()
    {
        var t = From<Transfer>("""{"id":"a","sender_id":"s","kind":"files","files":[{"name":"x","size":3,"mime":"","mod_time":"2026-11-24T10:00:00.123456789+01:00"}],"broadcast":false,"created_at":"2026-11-24T09:00:00Z","expires_at":"2026-11-24T09:10:00Z","closed":false,"deliveries":null,"brandneu":1}""");
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 9, 0, 0, 123, TimeSpan.Zero), t.Files[0].ModTime, TimeSpan.FromMilliseconds(1));
        Assert.Empty(t.Deliveries);
        Assert.Equal(3, t.TotalSize);
    }

    [Fact]
    public void OmitsUnsetOptionalFields() => Assert.Equal("""{"fcm_token":"t"}""", To(new UpdateDeviceRequest(FcmToken: "t")));

    [Fact]
    public void WritesExactCreateJson() =>
        Assert.Equal("""{"kind":"text","text":"hi","targets":["x"],"broadcast":false}""", To(new CreateTransferRequest(Kind.Text, Broadcast: false) { Text = "hi", Targets = ["x"] }));

    [Fact]
    public void AlwaysEncodesModTime() => Assert.Contains("\"mod_time\":\"1970-01-01T00:00:00", To(new FileItem("a", 1)));

    [Fact]
    public void DecodesNullLanCandidates() =>
        Assert.Empty(From<AcceptResponse>("""{"lan_candidates":null,"cert_fingerprint":"","access_token":"","relay_base":""}""").LanCandidates);

    [Fact]
    public void DecodesDeliveryWithUnknownPath()
    {
        var d = From<Delivery>("""{"device_id":"d","status":"downloading","path":"teleport","bytes":5,"updated_at":"2026-11-24T09:00:00Z"}""");
        Assert.Null(d.Path);
        Assert.Equal(Status.Downloading, d.Status);
        Assert.Equal(DataPath.Relay, From<Delivery>("""{"device_id":"d","status":"accepted","path":"relay"}""").Path);
    }

    [Theory]
    [InlineData("""{"device_id":"d","status":"teleported"}""")]
    [InlineData("""{"device_id":"d","status":3}""")]
    [InlineData("""{"status":"accepted"}""")]
    public void BrokenDeliveriesAreJsonErrors(string json) => Assert.Throws<JsonException>(() => From<Delivery>(json));

    [Fact]
    public void AccessTokenMatchesGoReference() => Assert.Equal(GoToken, AccessToken.Create(Secret, "t1", "d1"));

    [Fact]
    public void VerifyRejectsForeignTransferTamperedAndGarbage()
    {
        Assert.Equal("d1", AccessToken.Verify(Secret, "t1", GoToken));
        Assert.Null(AccessToken.Verify(Secret, "t2", GoToken));
        Assert.Null(AccessToken.Verify(Secret, "t1", GoToken[..^1] + "4"));
        foreach (var bad in new[] { "", ".", "d1.zz", "d1.", ".abcd", "d1.9c4" }) Assert.Null(AccessToken.Verify(Secret, "t1", bad));
    }

    [Fact]
    public void BrokenTimeIsAJsonError() => Assert.Throws<JsonException>(() => From<PairCodeResponse>("""{"code":"A","expires_at":"gestern"}"""));

    [Fact]
    public void FinalStatuses()
    {
        Status[] final = [Status.Delivered, Status.Declined, Status.Missed, Status.Cancelled, Status.Failed];
        foreach (var s in Enum.GetValues<Status>()) Assert.Equal(final.Contains(s), s.IsFinal());
    }
}
