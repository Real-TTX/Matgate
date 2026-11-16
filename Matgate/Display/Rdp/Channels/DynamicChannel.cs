namespace Matgate.Display.Rdp.Channels;

// One dynamic virtual channel (MS-RDPEDYC): a named stream of messages inside the "drdynvc" static channel.
// The client does not open it - it listens under a name, and the server opens the channel when one of its
// components wants to talk to that listener. Register the listener with DynamicChannels before the session
// starts.
//
// Like a static channel, this is a state machine over bytes whose input is the remote server's: Receive drops
// and reports what does not hold together and never throws on it.
public abstract class DynamicChannel
{
    // The listener's name, as the server asks for it (ANSI characters, the case counts).
    public abstract string Name { get; }

    // Set by DynamicChannels when the server opens the channel, null again when it is closed. Takes one
    // complete message of any length; it is cut into the pieces the protocol allows on its way out.
    public Action<ReadOnlyMemory<byte>>? Send { get; set; }

    // Set by DynamicChannels (to its own log) unless it has been set before.
    public Action<string>? Log { get; set; }

    // One complete message from the server. The span is only good during the call.
    public abstract void Receive(ReadOnlySpan<byte> message);

    // The server opened the channel; Send works from here on.
    public virtual void Opened()
    {
    }

    // The channel is closed - by the server, by DynamicChannels.Close or because the session ended.
    public virtual void Closed()
    {
    }

    protected void Report(string message) => Guarded.Log(Log, Name + ": " + message);
}
