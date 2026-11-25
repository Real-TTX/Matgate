namespace Matgate.Display.Rdp;

// Why a server ended a session, in words (the codes of its "set error info" message, MS-RDPBCGR 2.2.5.1.1).
internal static class RdpErrorInfo
{
    public static string? Describe(uint code)
    {
        return code switch
        {
            0 => null,
            0x0001 => "The session was ended from the server.",
            0x0002 => "The user was signed out from the server.",
            0x0003 => "The session ended because nothing happened in it for too long.",
            0x0004 => "The session ended because it reached its time limit.",
            0x0005 => "Another connection took over this session.",
            0x0006 => "The server ran out of memory.",
            0x0007 => "The server denied the connection.",
            0x0009 => "This user is not allowed to sign in over Remote Desktop on this server.",
            0x000A => "The server does not accept saved credentials for this user.",
            0x000B => "The session was disconnected on the server.",
            0x000C => "You were signed out.",
            0x0101 => "The server found no licence server for Remote Desktop.",
            0x0102 => "The server has no free Remote Desktop licence for this connection.",
            >= 0x0100 and <= 0x010A => "The server ended the connection for licensing reasons.",
            >= 0x0400 and <= 0x0412 => "The connection broker of the server farm could not place this session.",
            // What is left are complaints about a message this client sent. The code is what a bug report needs.
            _ => $"The server ended the connection because of a protocol error (code 0x{code:X}).",
        };
    }
}
