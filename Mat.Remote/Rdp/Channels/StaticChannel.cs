namespace Matgate.Display.Rdp.Channels;

// One static virtual channel of an RDP session (MS-RDPBCGR 1.3.3): a named, lossless stream of messages next to
// the picture. The core announces the channel while connecting, cuts what Send hands it into chunks and gives
// Receive what the server sent, put together again.
//
// A channel is a state machine over bytes - it never touches a socket. Everything it receives comes from the
// remote server and is treated as hostile: a message that does not hold together is dropped and reported
// through Log, it never throws out of Receive and never spoils the state for the message after it.
public abstract class StaticChannel
{
    // At most seven ASCII characters; the server finds its end of the channel by this name.
    public abstract string Name { get; }

    // What the core announces for this channel in its CHANNEL_DEF (MS-RDPBCGR 2.2.1.3.4.1). Compression is
    // never among it: the reassembler refuses compressed chunks.
    public virtual uint Options => StaticChannelOptions.Initialized | StaticChannelOptions.EncryptRdp;

    // Set by the core before the session starts. Takes one complete channel message; the core chunks it.
    // The channel never touches a buffer again after handing it over, and calls this without holding a lock.
    public Action<ReadOnlyMemory<byte>>? Send { get; set; }

    public Action<string>? Log { get; set; }

    // One complete, reassembled message from the server.
    public abstract void Receive(ReadOnlySpan<byte> message);

    // The session is active, the channel may talk.
    public virtual void Opened()
    {
    }

    // The session is over. A channel forgets what it negotiated, so the same object can serve the next session.
    public virtual void Closed()
    {
    }

    // The core hands every channel the same log, so a line says which channel it is about.
    protected void Report(string message) => Guarded.Log(Log, Name + ": " + message);
}

// The CHANNEL_DEF option flags (MS-RDPBCGR 2.2.1.3.4.1).
public static class StaticChannelOptions
{
    public const uint Initialized = 0x80000000;
    public const uint EncryptRdp = 0x40000000;
    public const uint EncryptServerToClient = 0x20000000;
    public const uint EncryptClientToServer = 0x10000000;
    public const uint PriorityHigh = 0x08000000;
    public const uint PriorityMedium = 0x04000000;
    public const uint PriorityLow = 0x02000000;
    public const uint CompressRdp = 0x00800000;
    public const uint Compress = 0x00400000;

    // The channel's endpoint on the server reads the chunk headers itself: every chunk of such a channel has
    // to carry CHANNEL_FLAG_SHOW_PROTOCOL (pass showProtocol to ChannelChunks.Split).
    public const uint ShowProtocol = 0x00200000;
    public const uint RemoteControlPersistent = 0x00100000;
}
