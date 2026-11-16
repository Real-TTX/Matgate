namespace Matgate.Display.Video.Tests;

// ffmpeg as the independent judge: it decodes what the encoder wrote, the way a browser would.
internal static class Ffmpeg
{
    private static readonly Lazy<bool> Found = new(() => Tool.Works("ffmpeg", "-version") && Tool.Works("ffprobe", "-version"));

    public static bool IsAvailable => Found.Value;

    // The stream as raw I420 pictures, one after another, each plane without padding.
    public static void Decode(string stream, string pictures)
    {
        // passthrough: one picture out for every picture in - a raw stream has no time stamps to go by.
        Tool.Run("ffmpeg", "-v", "error", "-y", "-f", "h264", "-i", stream, "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "yuv420p", pictures);
    }

    // What the stream's header says: width, height, profile, level, pix_fmt, color_range, color_space, ...
    public static Dictionary<string, string> Probe(string stream)
    {
        var output = Tool.Run(
            "ffprobe", "-v", "error", "-f", "h264", "-select_streams", "v:0", "-show_entries",
            "stream=width,height,profile,level,pix_fmt,color_range,color_space,color_transfer,color_primaries,has_b_frames,refs",
            "-of", "default=noprint_wrappers=1", stream);
        var values = new Dictionary<string, string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = line.IndexOf('=');
            if (equals > 0)
            {
                values[line[..equals]] = line[(equals + 1)..];
            }
        }

        return values;
    }

    // The size of every access unit in the stream and whether it is a key frame, as ffmpeg's parser sees it.
    public static List<(int Bytes, bool IsKey)> Packets(string stream) =>
        Tool.Run("ffprobe", "-v", "error", "-f", "h264", "-show_entries", "packet=size,flags", "-of", "csv=p=0", stream)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => (int.Parse(line.Split(',')[0]), line.Contains('K')))
            .ToList();

    // The engine this replaces, for comparison: x264 as display/src/video.c set it up, through ffmpeg.
    public static void EncodeWithX264(string pictures, int width, int height, int framesPerSecond, int kilobitsPerSecond, string stream)
    {
        Tool.Run(
            "ffmpeg", "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "yuv420p", "-s", $"{width}x{height}", "-r", $"{framesPerSecond}", "-i", pictures,
            "-c:v", "libx264", "-preset", "superfast", "-tune", "zerolatency", "-profile:v", "baseline", "-crf", "22",
            "-maxrate", $"{kilobitsPerSecond}k", "-bufsize", $"{kilobitsPerSecond / 2}k", "-x264-params", "keyint=infinite:repeat-headers=1",
            "-f", "h264", stream);
    }
}
