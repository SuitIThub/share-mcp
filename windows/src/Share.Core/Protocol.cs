using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Share.Core;

/// <summary>Wire format; counterpart of server/internal/protocol and docs/protocol.md.</summary>
public static class ShareJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectRequiredConstructorParameters = true,
        Converters = { new DataPathConverter(), new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };

    /// <summary>A path we do not know is no reason to reject a delivery.</summary>
    private sealed class DataPathConverter : JsonConverter<DataPath?>
    {
        public override DataPath? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            (reader.TokenType == JsonTokenType.String ? reader.GetString() : null) switch { "lan" => DataPath.Lan, "relay" => DataPath.Relay, _ => null };

        public override void Write(Utf8JsonWriter writer, DataPath? value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value == DataPath.Lan ? "lan" : "relay");
    }
}

public static class Limits
{
    /// <summary>Maximum size of a text/link payload in UTF-8 bytes.</summary>
    public const int MaxTextBytes = 65536;
    public const int DefaultPort = 47800;
    internal const int Buffer = 256 * 1024;
}

public enum DeviceType { Android, Windows, Agent }

public enum Kind { Text, Link, Files }

public enum Status { Waiting, Accepted, Downloading, Delivered, Declined, Missed, Cancelled, Failed }

/// <summary>How data travels (the protocol's "path").</summary>
public enum DataPath { Lan, Relay }

public static class StatusExtensions
{
    /// <summary>No further transition possible.</summary>
    public static bool IsFinal(this Status s) => s is Status.Delivered or Status.Declined or Status.Missed or Status.Cancelled or Status.Failed;
}

public sealed record Device(string Id, string Name, DeviceType Type)
{
    public bool Online { get; init; }
    public DateTimeOffset LastSeen { get; init; }
    public string CertFingerprint { get; init => field = value ?? ""; } = "";
}

public sealed record FileItem(string Name, long Size)
{
    public string Mime { get; init => field = value ?? ""; } = "";
    public DateTimeOffset ModTime { get; init; } = DateTimeOffset.UnixEpoch;
}

public sealed record Delivery(string DeviceId, Status Status)
{
    public DataPath? Path { get; init; }
    public long Bytes { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record Transfer(string Id, string SenderId, Kind Kind, DateTimeOffset ExpiresAt)
{
    public string Text { get; init => field = value ?? ""; } = "";
    public string Message { get; init => field = value ?? ""; } = "";
    public IReadOnlyList<FileItem> Files { get; init => field = value ?? []; } = [];
    public bool Broadcast { get; init; }
    public IReadOnlyList<string> Targets { get; init => field = value ?? []; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public bool Closed { get; init; }
    public IReadOnlyList<Delivery> Deliveries { get; init => field = value ?? []; } = [];

    [JsonIgnore]
    public long TotalSize => Files.Sum(f => f.Size);
}

public sealed record CreateTransferRequest(Kind Kind, bool Broadcast)
{
    [JsonPropertyOrder(-9)] public Kind Kind { get; init; } = Kind;
    [JsonPropertyOrder(-8)] public string? Text { get; init; }
    [JsonPropertyOrder(-7)] public string? Message { get; init; }
    [JsonPropertyOrder(-6)] public IReadOnlyList<FileItem>? Files { get; init; }
    [JsonPropertyOrder(-5)] public IReadOnlyList<string>? Targets { get; init; }
    [JsonPropertyOrder(-4)] public bool Broadcast { get; init; } = Broadcast;
    [JsonPropertyOrder(-3)] public int? Minutes { get; init; }
    [JsonPropertyOrder(-2)] public IReadOnlyList<string>? LanCandidates { get; init; }
}

public sealed record CreateTransferResponse(Transfer Transfer, string Secret);

public sealed record AcceptResponse
{
    public IReadOnlyList<string> LanCandidates { get; init => field = value ?? []; } = [];
    public string CertFingerprint { get; init => field = value ?? ""; } = "";
    public string AccessToken { get; init => field = value ?? ""; } = "";
    public string RelayBase { get; init => field = value ?? ""; } = "";
}

public sealed record PairRequest(string Code, string Name, DeviceType Type, string CertFingerprint);

public sealed record PairResponse(string DeviceId, string Token);

public sealed record PairCodeResponse(string Code, DateTimeOffset ExpiresAt);

public sealed record UpdateDeviceRequest(string? Name = null, string? FcmToken = null);

public sealed record ProgressRequest(long Bytes, DataPath? Path);

public sealed record TransferRef(string TransferId);

public sealed record DeliveryUpdate(string TransferId, Delivery Delivery);

public sealed record RelayRequest(string RelayId, string TransferId, int FileIndex = 0, long Offset = 0);

/// <summary>Per-offer, per-receiver token presented to the sender's local endpoint.</summary>
public static class AccessToken
{
    public static string Create(byte[] secret, string transferId, string deviceId) =>
        deviceId + "." + Convert.ToHexStringLower(Mac(secret, transferId, deviceId));

    /// <summary>Returns the receiver's device ID, or null if the token is invalid.</summary>
    public static string? Verify(byte[] secret, string transferId, string token)
    {
        var i = token.LastIndexOf('.');
        if (i <= 0) return null;
        var got = Hex.TryParse(token[(i + 1)..]);
        if (got is null) return null;
        var deviceId = token[..i];
        return CryptographicOperations.FixedTimeEquals(got, Mac(secret, transferId, deviceId)) ? deviceId : null;
    }

    private static byte[] Mac(byte[] secret, string transferId, string deviceId) =>
        HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(transferId + "|" + deviceId));
}

internal static class Hex
{
    public static byte[]? TryParse(string s)
    {
        if (s.Length % 2 != 0) return null;
        try
        {
            return Convert.FromHexString(s);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
