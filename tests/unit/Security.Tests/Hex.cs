namespace Matgate.Display.Rdp.Security.Tests;

// The specifications print their examples as hex dumps; the tests keep them in that shape.
internal static class Hex
{
    // Spaces and line breaks between the digits do not count.
    public static byte[] Bytes(string hex) =>
        Convert.FromHexString(string.Concat(hex.Where(character => !char.IsWhiteSpace(character))));

    public static string Text(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
