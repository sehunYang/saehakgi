using System.Buffers.Binary;
using System.Text;

namespace Saehakgi.Core.Native;

/// <summary>
/// Chrome/Edge native-messaging wire format: a 4-byte little-endian length prefix
/// followed by that many bytes of UTF-8 JSON, over stdin/stdout.
/// </summary>
public static class NativeMessaging
{
    private const int MaxMessageBytes = 64 * 1024 * 1024; // browser caps host->browser at ~1MB; be generous inbound

    /// <summary>Reads one message; returns null at end of stream.</summary>
    public static string? ReadMessage(Stream input)
    {
        var lengthBuffer = new byte[4];
        if (!TryReadExact(input, lengthBuffer)) return null; // clean EOF

        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        if (length <= 0 || length > MaxMessageBytes)
            throw new InvalidDataException($"Invalid native message length: {length}");

        var payload = new byte[length];
        if (!TryReadExact(input, payload)) throw new EndOfStreamException("Truncated native message.");
        return Encoding.UTF8.GetString(payload);
    }

    public static void WriteMessage(Stream output, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        output.Write(length);
        output.Write(bytes);
        output.Flush();
    }

    private static bool TryReadExact(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                if (total == 0) return false; // nothing more to read
                throw new EndOfStreamException("Unexpected end of native message stream.");
            }
            total += read;
        }
        return true;
    }
}
