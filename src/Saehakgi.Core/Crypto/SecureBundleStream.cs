using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Saehakgi.Core.Crypto;

/// <summary>
/// Streaming, authenticated encryption of an arbitrary byte stream using
/// chunked AES-256-GCM. Streams to/from disk so multi-GB folder payloads never
/// need to fit in memory.
///
/// Format:
///   Header : MAGIC(8) "SAEHKGI1" | kdfId(1) | iterations(int32 LE) | salt(16) | chunkSize(int32 LE)
///   Chunks : repeated [ plaintextLen(int32 LE) | nonce(12) | tag(16) | ciphertext(plaintextLen) ]
///            AAD per chunk = 8-byte big-endian chunk counter (binds ordering).
///            A final plaintextLen==0 chunk is an authenticated terminator; its
///            absence on read means the stream was truncated.
/// </summary>
public static class SecureBundleStream
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SAEHKGI1");
    private const byte KdfPbkdf2Sha256 = 1;
    private const int DefaultChunkSize = 1 << 20; // 1 MiB
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static void Encrypt(Stream plaintext, Stream output, string passphrase, int chunkSize = DefaultChunkSize)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));

        byte[] salt = PassphraseCrypto.NewSalt();
        int iterations = PassphraseCrypto.Pbkdf2Iterations;
        byte[] key = PassphraseCrypto.DeriveKey(passphrase, salt, iterations);
        try
        {
            output.Write(Magic);
            output.WriteByte(KdfPbkdf2Sha256);
            WriteInt32LE(output, iterations);
            output.Write(salt);
            WriteInt32LE(output, chunkSize);

            using var gcm = new AesGcm(key, TagSize);
            var buffer = new byte[chunkSize];
            long counter = 0;
            int read;
            while ((read = ReadFull(plaintext, buffer, 0, buffer.Length)) > 0)
            {
                WriteChunk(output, gcm, buffer.AsSpan(0, read), counter++);
                if (read < buffer.Length) break; // final partial chunk consumed
            }
            WriteChunk(output, gcm, ReadOnlySpan<byte>.Empty, counter); // authenticated terminator
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static void Decrypt(Stream input, Stream output, string passphrase)
    {
        byte[] magic = ReadExact(input, Magic.Length);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Not a saehakgi bundle (bad magic header).");

        int kdfId = input.ReadByte();
        if (kdfId != KdfPbkdf2Sha256)
            throw new InvalidDataException($"Unsupported KDF id: {kdfId}.");

        int iterations = ReadInt32LE(input);
        byte[] salt = ReadExact(input, PassphraseCrypto.SaltSizeBytes);
        int chunkSize = ReadInt32LE(input);
        if (chunkSize <= 0 || chunkSize > (64 << 20))
            throw new InvalidDataException("Invalid chunk size in header.");

        byte[] key = PassphraseCrypto.DeriveKey(passphrase, salt, iterations);
        try
        {
            using var gcm = new AesGcm(key, TagSize);
            long counter = 0;
            Span<byte> aad = stackalloc byte[8];
            while (true)
            {
                int len = ReadInt32LE(input);
                if (len < 0 || len > chunkSize)
                    throw new InvalidDataException("Corrupted bundle (bad chunk length).");

                byte[] nonce = ReadExact(input, NonceSize);
                byte[] tag = ReadExact(input, TagSize);
                byte[] ciphertext = ReadExact(input, len);
                byte[] plaintext = new byte[len];

                BinaryPrimitives.WriteInt64BigEndian(aad, counter++);
                try
                {
                    gcm.Decrypt(nonce, ciphertext, tag, plaintext, aad);
                }
                catch (CryptographicException)
                {
                    throw new InvalidDataException(
                        "Decryption failed — wrong passphrase, or the bundle is corrupted or has been tampered with.");
                }

                if (len == 0) break; // terminator verified → clean end of stream
                output.Write(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void WriteChunk(Stream output, AesGcm gcm, ReadOnlySpan<byte> plaintext, long counter)
    {
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var tag = new byte[TagSize];
        var ciphertext = new byte[plaintext.Length];

        Span<byte> aad = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(aad, counter);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag, aad);

        WriteInt32LE(output, plaintext.Length);
        output.Write(nonce);
        output.Write(tag);
        if (ciphertext.Length > 0) output.Write(ciphertext);
    }

    private static int ReadFull(Stream s, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int r = s.Read(buffer, offset + total, count - total);
            if (r == 0) break;
            total += r;
        }
        return total;
    }

    private static byte[] ReadExact(Stream s, int count)
    {
        var buffer = new byte[count];
        int total = 0;
        while (total < count)
        {
            int r = s.Read(buffer, total, count - total);
            if (r == 0) throw new EndOfStreamException("Unexpected end of bundle (truncated).");
            total += r;
        }
        return buffer;
    }

    private static void WriteInt32LE(Stream s, int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        s.Write(b);
    }

    private static int ReadInt32LE(Stream s)
    {
        byte[] b = ReadExact(s, 4);
        return BinaryPrimitives.ReadInt32LittleEndian(b);
    }
}
