using System.Diagnostics;

namespace Matgate.Display.Video.Tests;

// Other programs the tests use: the compiler for the layout check, ffmpeg for decoding.
internal static class Tool
{
    // Runs a program to its end and returns what it wrote; a failure is an exception with its error output.
    public static string Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{program} did not start.");
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{program} failed with code {process.ExitCode}: {errors.Result.Trim()}");
        }

        return output;
    }

    public static bool Works(string program, string argument)
    {
        try
        {
            Run(program, argument);
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
