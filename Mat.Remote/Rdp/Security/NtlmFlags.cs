namespace Matgate.Display.Rdp.Security;

// The NegotiateFlags of MS-NLMP 2.2.2.5, as far as this client sets or reads them.
[Flags]
internal enum NtlmFlags : uint
{
    None = 0,
    Unicode = 0x00000001,
    Oem = 0x00000002,
    RequestTarget = 0x00000004,
    Sign = 0x00000010,
    Seal = 0x00000020,
    LmKey = 0x00000080,
    Ntlm = 0x00000200,
    AlwaysSign = 0x00008000,
    TargetTypeDomain = 0x00010000,
    TargetTypeServer = 0x00020000,
    ExtendedSessionSecurity = 0x00080000,
    TargetInfo = 0x00800000,
    Version = 0x02000000,
    Key128 = 0x20000000,
    KeyExchange = 0x40000000,
    Key56 = 0x80000000,
}

// The AvId values of MS-NLMP 2.2.2.1.
internal enum NtlmAvId : ushort
{
    EndOfList = 0,
    NetBiosComputerName = 1,
    NetBiosDomainName = 2,
    DnsComputerName = 3,
    DnsDomainName = 4,
    DnsTreeName = 5,
    Flags = 6,
    Timestamp = 7,
    SingleHost = 8,
    TargetName = 9,
    ChannelBindings = 10,
}
