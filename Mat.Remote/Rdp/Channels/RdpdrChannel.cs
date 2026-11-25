using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Matgate.Display.Rdp.Channels;

// The device redirection channel "rdpdr" (MS-RDPEFS), with no devices at all. It only exists because Windows
// does not talk on "rdpsnd" to a client that does not announce "rdpdr" as well (MS-RDPEA <1>): it goes through
// the handshake - version and client id, the computer's name, capabilities, an empty device list - and then
// has nothing to say.
//
// Everything happens on the thread that calls Receive and Closed; the properties may be read from any thread.
public sealed class RdpdrChannel : StaticChannel
{
    public const string DefaultComputerName = "Matgate";

    // The longest computer name this client sends; Windows shows it next to redirected devices, of which there
    // are none, so there is no point in more.
    public const int MaxComputerNameLength = 255;

    private const ushort ComponentCore = 0x4472;
    private const ushort ComponentPrinter = 0x5052;

    private const ushort ServerAnnounce = 0x496E;
    private const ushort ClientIdConfirm = 0x4343;
    private const ushort ClientName = 0x434E;
    private const ushort DeviceListAnnounce = 0x4441;
    private const ushort DeviceReply = 0x6472;
    private const ushort DeviceIoRequest = 0x4952;
    private const ushort ServerCapability = 0x5350;
    private const ushort ClientCapability = 0x4350;
    private const ushort UserLoggedOn = 0x554C;

    private const ushort CapabilityGeneral = 1;
    private const uint GeneralVersion2 = 2;
    private const int GeneralLength = 44;

    // The minor versions a client may name (MS-RDPEFS 2.2.2.3), highest first: this one speaks 13, the
    // version of Windows Server 2016 and later, and goes down to what the server speaks.
    private static readonly ushort[] MinorVersions = [0x000D, 0x000C, 0x000A, 0x0005, 0x0002];

    // ioCode1: the bits marked "unused, always set", and the two security requests (MS-RDPEFS 2.2.2.7.1).
    private const uint AllIoCodes = 0x0000FFFF;

    // extendedPDU: device list remove, display name, user logged on - what FreeRDP and Windows clients say.
    private const uint DeviceRemovePdus = 0x00000001;
    private const uint ClientDisplayNamePdu = 0x00000002;
    private const uint UserLoggedOnPdu = 0x00000004;
    private const uint ExtendedPdus = DeviceRemovePdus | ClientDisplayNamePdu | UserLoggedOnPdu;

    private const uint EnableAsyncIo = 0x00000001;

    private readonly Lock _gate = new();
    private readonly byte[] _name;
    private readonly bool _announceEmptyList;

    // Where the handshake stands; Server Announce starts it over.
    private bool _announced;
    private ushort _minor;
    private uint _clientId;
    private bool _capabilities;
    private uint _serverIoCodes;
    private bool _serverSendsLogon;
    private bool _confirmed;
    private bool _listSent;

    // computerName is sent in UTF-16 with its terminator. announceEmptyDeviceList: when the server is ready for
    // devices, tell it that there are none (an empty Client Device List Announce Request). FreeRDP 3 and
    // IronRDP send nothing in that case; both are within the specification, which only says the client
    // announces "its devices" then.
    public RdpdrChannel(string computerName = DefaultComputerName, bool announceEmptyDeviceList = true)
    {
        ArgumentNullException.ThrowIfNull(computerName);
        if (computerName.Length is 0 or > MaxComputerNameLength || computerName.Contains('\0'))
        {
            throw new ArgumentException($"A computer name has 1 to {MaxComputerNameLength} characters and no NUL.", nameof(computerName));
        }

        ComputerName = computerName;
        _name = Encoding.Unicode.GetBytes(computerName + "\0");
        _announceEmptyList = announceEmptyDeviceList;
    }

    public override string Name => "rdpdr";

    public string ComputerName { get; }

    // The client id the server confirmed, 0 before it did.
    public uint ClientId
    {
        get
        {
            lock (_gate)
            {
                return _confirmed ? _clientId : 0;
            }
        }
    }

    // The minor version both sides speak, 0 before the server announced itself.
    public int VersionMinor
    {
        get
        {
            lock (_gate)
            {
                return _announced ? _minor : 0;
            }
        }
    }

    // The handshake is complete: capabilities exchanged, client id confirmed, and the (empty) device list
    // said as far as it is due.
    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _announced && _capabilities && _confirmed;
            }
        }
    }

    // Where a client id comes from when the client has to draw one; the tests bring their own.
    internal Func<uint> RandomId { get; set; } = () => BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));

    public override void Receive(ReadOnlySpan<byte> message)
    {
        var reader = new ByteReader(message);
        if (!reader.TryU16(out var component) || !reader.TryU16(out var packet))
        {
            Report("dropped a message shorter than its header");
            return;
        }

        if (component == ComponentPrinter)
        {
            Report($"ignored printer message {packet:X4}: there are no printers");
            return;
        }

        if (component != ComponentCore)
        {
            Report($"dropped a message of the unknown component {component:X4}");
            return;
        }

        var outgoing = new List<byte[]>();
        string? note;
        lock (_gate)
        {
            note = packet switch
            {
                ServerAnnounce => Announce(reader, outgoing),
                ServerCapability => Capabilities(reader, outgoing),
                ClientIdConfirm => Confirm(reader, outgoing),
                UserLoggedOn => LoggedOn(outgoing),
                DeviceReply => "ignored a device announce response: no device was announced",

                // A request for a device that was never announced is ignored (MS-RDPEFS 3.1.5.2).
                DeviceIoRequest => "ignored an I/O request: there are no devices",
                _ => $"dropped the unknown message {packet:X4}",
            };
        }

        if (note is not null)
        {
            Report(note);
        }

        foreach (var answer in outgoing)
        {
            Transmit(answer);
        }
    }

    public override void Closed()
    {
        lock (_gate)
        {
            _announced = false;
            _capabilities = false;
            _confirmed = false;
            _listSent = false;
        }
    }

    // ---- the server's messages (all under the lock) -----------------------------------------------------------

    private string? Announce(ByteReader reader, List<byte[]> outgoing)
    {
        if (!reader.TryU16(out var major) || !reader.TryU16(out var minor) || !reader.TryU32(out var serverId))
        {
            return "dropped a server announce that is too short";
        }

        // It may come again in the middle of a session: the server reconnected, and everything starts over
        // (MS-RDPEFS 3.2.5.1.2).
        _announced = true;
        _capabilities = false;
        _confirmed = false;
        _listSent = false;
        _serverIoCodes = AllIoCodes;
        _serverSendsLogon = true;
        _minor = MinorVersions.FirstOrDefault(version => version <= minor, MinorVersions[^1]);

        // From version 12 on the client takes the server's id; before that it draws one (MS-RDPEFS 3.2.5.1.3),
        // and not one the server could mistake for something else.
        _clientId = serverId;
        if (minor < 0x000C)
        {
            do
            {
                _clientId = RandomId();
            }
            while (_clientId is 0 or uint.MaxValue || _clientId == serverId);
        }

        outgoing.Add(Message(ClientIdConfirm, 8).U16(1).U16(_minor).U32(_clientId).ToArray());
        outgoing.Add(Message(ClientName, 12 + _name.Length).U32(1).U32(0).U32((uint)_name.Length).Bytes(_name).ToArray());
        return major == 1 ? null : $"the server announces major version {major}; answered with 1";
    }

    private string? Capabilities(ByteReader reader, List<byte[]> outgoing)
    {
        if (!_announced)
        {
            return "ignored capabilities before the server announced itself";
        }

        if (!reader.TryU16(out var count) || !reader.TrySkip(2))
        {
            return "dropped capabilities too short to count their sets";
        }

        uint ioCodes = AllIoCodes;
        var logon = true;
        for (var i = 0; i < count; i++)
        {
            if (!reader.TryU16(out var type) || !reader.TryU16(out var length) || length < 8 || !reader.TryBytes(length - 4, out var set))
            {
                return "dropped capabilities with a set that does not fit";
            }

            // The general set: its version, then osType, osVersion, the protocol version, ioCode1, ioCode2 and
            // extendedPDU. Version 1 has no SpecialTypeDeviceCap at the end; neither is needed here.
            if (type == CapabilityGeneral && set.Length >= 4 + 24)
            {
                ioCodes = BinaryPrimitives.ReadUInt32LittleEndian(set[16..]);
                logon = (BinaryPrimitives.ReadUInt32LittleEndian(set[24..]) & UserLoggedOnPdu) != 0;
            }
        }

        _serverIoCodes = ioCodes;
        _serverSendsLogon = logon;

        // Only the general set: this client supports no device type, so it lists none (MS-RDPEFS 3.2.5.1.8).
        outgoing.Add(Message(ClientCapability, 4 + GeneralLength)
            .U16(1)
            .U16(0)
            .U16(CapabilityGeneral)
            .U16(GeneralLength)
            .U32(GeneralVersion2)
            .U32(0)
            .U32(0)
            .U16(1)
            .U16(_minor)
            .U32(AllIoCodes & _serverIoCodes)
            .U32(0)
            .U32(ExtendedPdus)
            .U32(EnableAsyncIo)
            .U32(0)
            .U32(0)
            .ToArray());
        _capabilities = true;
        AnnounceIfDue(false, outgoing);
        return null;
    }

    private string? Confirm(ByteReader reader, List<byte[]> outgoing)
    {
        if (!_announced)
        {
            return "ignored a client id confirm before the server announced itself";
        }

        if (!reader.TryU16(out _) || !reader.TryU16(out _) || !reader.TryU32(out var id))
        {
            return "dropped a client id confirm that is too short";
        }

        // The server has the last word on the id (FreeRDP takes it the same way).
        string? note = id == _clientId ? null : $"the server confirms client id {id:X8} instead of {_clientId:X8}";
        _clientId = id;
        _confirmed = true;
        AnnounceIfDue(false, outgoing);
        return note;
    }

    private string? LoggedOn(List<byte[]> outgoing)
    {
        if (!_announced)
        {
            return "ignored a user logged on before the server announced itself";
        }

        AnnounceIfDue(true, outgoing);
        return null;
    }

    // Devices are announced once the user is logged on - or, with a server that never says so, as soon as
    // capabilities and client id are settled (MS-RDPEFS 3.2.5.1.5 and 3.2.5.1.9).
    private void AnnounceIfDue(bool loggedOn, List<byte[]> outgoing)
    {
        if (_listSent || !_announceEmptyList)
        {
            return;
        }

        var due = loggedOn || (_capabilities && _confirmed && (!_serverSendsLogon || _minor < 0x000A));
        if (!due)
        {
            return;
        }

        _listSent = true;
        outgoing.Add(Message(DeviceListAnnounce, 4).U32(0).ToArray());
    }

    private void Transmit(byte[] message)
    {
        var send = Send;
        if (send is null)
        {
            Report("a message is dropped, the channel has no way to send yet");
            return;
        }

        try
        {
            send(message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The connection is going down; the core notices that by itself.
            Report("sending failed: " + exception.Message);
        }
    }

    private static ByteWriter Message(ushort packet, int length) =>
        new ByteWriter(4 + length).U16(ComponentCore).U16(packet);
}
