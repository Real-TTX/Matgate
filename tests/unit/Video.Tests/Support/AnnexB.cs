namespace Matgate.Display.Video.Tests;

// What an access unit is made of, read from the bytes the encoder returned.
internal static class AnnexB
{
    public const int Slice = 1;
    public const int IdrSlice = 5;
    public const int Sps = 7;
    public const int Pps = 8;

    // The type of every NAL unit, in the order they come.
    public static List<int> NalTypes(ReadOnlySpan<byte> unit)
    {
        var types = new List<int>();
        foreach (var start in Starts(unit))
        {
            types.Add(unit[start] & 0x1F);
        }

        return types;
    }

    // Profile, constraint flags and level: the three bytes the browser builds its codec string from.
    public static (int Profile, int Constraints, int Level)? SpsHeader(ReadOnlySpan<byte> unit)
    {
        foreach (var start in Starts(unit))
        {
            if ((unit[start] & 0x1F) == Sps && start + 3 < unit.Length)
            {
                return (unit[start + 1], unit[start + 2], unit[start + 3]);
            }
        }

        return null;
    }

    // The bytes of the first NAL unit of a type, without its start code.
    public static byte[]? Nal(ReadOnlySpan<byte> unit, int type)
    {
        var starts = Starts(unit);
        for (var i = 0; i < starts.Count; i++)
        {
            if ((unit[starts[i]] & 0x1F) != type)
            {
                continue;
            }

            var end = i + 1 < starts.Count ? starts[i + 1] - 3 : unit.Length;
            while (end > starts[i] && unit[end - 1] == 0)
            {
                end--;
            }

            return unit[starts[i]..end].ToArray();
        }

        return null;
    }

    // The index of the first byte after every start code (00 00 01, with or without a leading zero).
    private static List<int> Starts(ReadOnlySpan<byte> unit)
    {
        var starts = new List<int>();
        for (var i = 0; i + 3 < unit.Length; i++)
        {
            if (unit[i] == 0 && unit[i + 1] == 0 && unit[i + 2] == 1)
            {
                starts.Add(i + 3);
                i += 2;
            }
        }

        return starts;
    }
}
