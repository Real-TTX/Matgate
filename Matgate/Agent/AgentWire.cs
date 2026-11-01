using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Matgate.Agent;

// The wire between the gateway and an agent. Shared as source by both sides (the agent project links
// this file), so the two can never disagree about it.
//
// One WebSocket per agent, opened BY the agent (outbound only - no port has to be open on the machine).
// Every message is one binary WebSocket message:
//
//   [4 bytes: length of the header, big endian][header: UTF-8 JSON][payload: raw bytes, may be empty]
//
// The header says what the message is: a request ("req", answered by exactly one "res" with the same
// id), an answer, or an event ("evt": no answer, used for terminal output/input and the agent's hello).
// Payloads carry file contents and terminal bytes without any base64 detour.
public sealed class AgentFrame
{
    public long Id { get; set; }

    // "req" | "res" | "evt"
    public string Kind { get; set; } = "";

    public string Op { get; set; } = "";

    // Set on a failed "res": a sentence for a person, shown as it is.
    public string? Error { get; set; }

    public JsonObject? Data { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public ReadOnlyMemory<byte> Payload { get; set; }

    public string? Text(string name) => Data?[name]?.GetValue<string>();

    public long Long(string name, long fallback = 0)
    {
        var node = Data?[name];
        return node is null ? fallback : node.GetValue<long>();
    }

    public int Int(string name, int fallback = 0)
    {
        var node = Data?[name];
        return node is null ? fallback : node.GetValue<int>();
    }

    public bool Bool(string name, bool fallback = false)
    {
        var node = Data?[name];
        return node is null ? fallback : node.GetValue<bool>();
    }
}

public static class AgentWire
{
    public const string ProtocolVersion = "1";

    // Header + payload. File chunks stay well below it (see ChunkSize).
    public const int MaxMessageBytes = 4 * 1024 * 1024;

    // What one read or write request moves. Small enough to keep a terminal on the same socket
    // responsive while a file is copied, big enough not to drown in round trips.
    public const int ChunkSize = 256 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Encode(AgentFrame frame)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(frame, Json);
        var message = new byte[4 + header.Length + frame.Payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(message, header.Length);
        header.CopyTo(message, 4);
        frame.Payload.Span.CopyTo(message.AsSpan(4 + header.Length));
        return message;
    }

    public static AgentFrame Decode(ReadOnlyMemory<byte> message)
    {
        if (message.Length < 4)
        {
            throw new InvalidDataException("Agent message too short.");
        }

        var headerLength = BinaryPrimitives.ReadInt32BigEndian(message.Span);
        if (headerLength < 2 || headerLength > message.Length - 4)
        {
            throw new InvalidDataException("Agent message header length is invalid.");
        }

        var frame = JsonSerializer.Deserialize<AgentFrame>(message.Span.Slice(4, headerLength), Json)
            ?? throw new InvalidDataException("Agent message header is empty.");
        frame.Payload = message[(4 + headerLength)..];
        return frame;
    }

    // Reads one whole message (WebSocket messages may arrive in fragments). Null when the socket closed.
    public static async Task<AgentFrame?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                throw new InvalidDataException("Agent message too large.");
            }

            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Decode(message.GetBuffer().AsMemory(0, (int)message.Length));
    }

    public static Task SendAsync(WebSocket socket, AgentFrame frame, CancellationToken cancellationToken)
    {
        return socket.SendAsync(Encode(frame), WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
    }

    public static string Utf8(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);
}
