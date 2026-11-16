// Empty stand-ins for the parts of the engine that live in their own folders, so the connection sequence can
// be tried against a server before (or without) them - see the project file for the switches.
using System.Buffers.Binary;

#if STUB_CHANNELS
namespace Matgate.Display.Rdp.Channels
{
    public abstract class StaticChannel
    {
        public abstract string Name { get; }

        public virtual uint Options => 0xC0000000;

        public Action<ReadOnlyMemory<byte>>? Send { get; set; }

        public Action<string>? Log { get; set; }

        public abstract void Receive(ReadOnlySpan<byte> message);

        public virtual void Opened()
        {
        }

        public virtual void Closed()
        {
        }
    }

    public static class ChannelChunks
    {
        public static void Split(ReadOnlySpan<byte> message, int chunkSize, bool showProtocol, Action<ReadOnlySpan<byte>> emit)
        {
            var buffer = new byte[8 + chunkSize];
            var offset = 0;
            do
            {
                var count = Math.Min(chunkSize, message.Length - offset);
                uint flags = showProtocol ? 0x10u : 0u;
                if (offset == 0)
                {
                    flags |= 0x1;
                }

                if (offset + count == message.Length)
                {
                    flags |= 0x2;
                }

                BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)message.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), flags);
                message.Slice(offset, count).CopyTo(buffer.AsSpan(8));
                emit(buffer.AsSpan(0, 8 + count));
                offset += count;
            }
            while (offset < message.Length);
        }
    }

    public sealed class ChannelReassembler
    {
        public Action<string>? Log { get; set; }

        private readonly MemoryStream _buffer = new();

        public bool Add(ReadOnlySpan<byte> chunk, out ReadOnlyMemory<byte> message)
        {
            message = default;
            if (chunk.Length < 8)
            {
                return false;
            }

            var flags = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if ((flags & 0x1) != 0)
            {
                _buffer.SetLength(0);
            }

            _buffer.Write(chunk[8..]);
            if ((flags & 0x2) == 0)
            {
                return false;
            }

            message = _buffer.ToArray();
            _buffer.SetLength(0);
            return true;
        }
    }
}
#endif

#if STUB_SECURITY
namespace Matgate.Display.Rdp.Security
{
    public sealed class CredSspException(string message, bool isLogonFailure) : Exception(message)
    {
        public bool IsLogonFailure { get; } = isLogonFailure;
    }

    public sealed class CredSspClientOptions
    {
        public int MinimumServerVersion { get; init; } = 2;
    }

    public sealed class CredSspClient : IDisposable
    {
        public CredSspClient(string user, string domain, string password, string serverHost, ReadOnlyMemory<byte> serverPublicKey, CredSspClientOptions? options = null)
        {
        }

        public CredSspException ConnectionClosed(Exception? cause = null)
        {
            return new CredSspException("The connection closed while signing in.", true);
        }

        public void Dispose()
        {
        }

        public bool IsComplete => false;

        public byte[] Start()
        {
            throw new CredSspException("This build has no sign-in before the session (stubs).", false);
        }

        public byte[]? Next(ReadOnlySpan<byte> serverTsRequest)
        {
            return null;
        }
    }
}
#endif

#if STUB_GRAPHICS
namespace Matgate.Display.Rdp.Graphics
{
    public static class BitmapDecoder
    {
        public static long Rectangles;
        public static long Bytes;
        public static readonly Dictionary<string, int> Kinds = [];

        public static bool Decode(ReadOnlySpan<byte> data, int bitsPerPixel, bool compressed, bool hasCompressionHeader,
            int width, int height, Span<byte> framebuffer, int framebufferWidth, int framebufferHeight,
            int destLeft, int destTop, int destRight, int destBottom, ReadOnlySpan<uint> palette = default)
        {
            Rectangles++;
            Bytes += data.Length;
            var kind = bitsPerPixel + " bits" + (compressed ? ", compressed" : ", raw") + (hasCompressionHeader ? " with header" : "");
            lock (Kinds)
            {
                Kinds[kind] = Kinds.GetValueOrDefault(kind) + 1;
            }

            return true;
        }
    }

    public static class PointerDecoder
    {
        public static byte[]? Decode(int xorBitsPerPixel, int width, int height, ReadOnlySpan<byte> xorMask, ReadOnlySpan<byte> andMask,
            ReadOnlySpan<uint> palette = default)
        {
            return new byte[width * height * 4];
        }
    }
}
#endif
