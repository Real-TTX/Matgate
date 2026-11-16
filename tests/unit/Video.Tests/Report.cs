using Xunit;

namespace Matgate.Display.Video.Tests;

// The numbers the tests measure - sizes, bit rates, sharpness, milliseconds - for whoever runs the suite: in
// the test's own output, and gathered in the file VIDEO_TESTS_REPORT names, if it names one.
internal static class Report
{
    private static readonly Lock Gate = new();
    private static readonly string? File = Environment.GetEnvironmentVariable("VIDEO_TESTS_REPORT");

    public static void Line(string text)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(text);
        if (string.IsNullOrEmpty(File))
        {
            return;
        }

        lock (Gate)
        {
            System.IO.File.AppendAllText(File, text + Environment.NewLine);
        }
    }
}
