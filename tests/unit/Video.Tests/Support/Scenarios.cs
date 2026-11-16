namespace Matgate.Display.Video.Tests;

// What happens on a remote desktop, played into a recording the way Matgate's session does it: a picture is
// encoded whenever something changed, and after the last change the same picture twice more, 300 ms apart, so
// that it can get sharp.
internal static class Scenarios
{
    public const double RefinePause = 300;

    // A page of text that appears and then stands still. Pictures: the key frame and the two repeats.
    public static void Still(Recording recording)
    {
        Pictures.TextPage(recording.Width, recording.Height).CopyTo(recording.Source, 0);
        recording.Show();
        recording.Encode();
        Refine(recording);
    }

    // The page is scrolled by a few rows with every picture; at the end it stands still.
    public static void Scroll(Recording recording, int pictures, int rowsPerPicture = 6, bool refine = true)
    {
        var page = Pictures.TextPage(recording.Width, recording.Height * 2);
        for (var i = 0; i < pictures; i++)
        {
            Pictures.Scroll(page, recording.Source, recording.Width, recording.Height, i * rowsPerPicture);
            recording.Show();
            recording.Encode();
        }

        if (refine)
        {
            Refine(recording);
        }
    }

    // A window is dragged across a desktop that otherwise does not change; only the part of the screen the
    // window left and the part it now covers are converted.
    public static void Window(Recording recording, int pictures, bool refine = true)
    {
        var width = recording.Width;
        var height = recording.Height;
        var background = Pictures.Desktop(width, height);
        var content = Pictures.TextPage(width, height, seed: 11);
        var windowWidth = width * 5 / 16 & ~1;
        var windowHeight = height * 3 / 8 & ~1;
        var rangeX = width - windowWidth - 8;
        var rangeY = height - windowHeight - 8;
        var lastLeft = 0;
        var lastTop = 0;
        for (var i = 0; i < pictures; i++)
        {
            // Back and forth, a little faster across than down.
            var left = 4 + Bounce(i * 7, rangeX);
            var top = 4 + Bounce(i * 5, rangeY);
            Pictures.Window(background, content, recording.Source, width, height, left, top, windowWidth, windowHeight);
            if (i == 0)
            {
                recording.Show();
            }
            else
            {
                var x = Math.Min(left, lastLeft);
                var y = Math.Min(top, lastTop);
                recording.Show(x, y, Math.Max(left, lastLeft) + windowWidth - x, Math.Max(top, lastTop) + windowHeight - y);
            }

            lastLeft = left;
            lastTop = top;
            recording.Encode();
        }

        if (refine)
        {
            Refine(recording);
        }
    }

    // Every pixel new in every picture.
    public static void Noise(Recording recording, int pictures, bool refine = true)
    {
        var random = new Random(3);
        for (var i = 0; i < pictures; i++)
        {
            Pictures.Noise(recording.Source, random);
            recording.Show();
            recording.Encode();
        }

        if (refine)
        {
            Refine(recording);
        }
    }

    // A page of text with a cursor that changes with every picture: the smallest change there is.
    public static void Cursor(Recording recording, int pictures)
    {
        Pictures.TextPage(recording.Width, recording.Height).CopyTo(recording.Source, 0);
        recording.Show();
        recording.Encode();
        var x = recording.Width / 2 & ~7;
        var y = recording.Height / 2 & ~15;
        for (var i = 0; i < pictures; i++)
        {
            Pictures.Cursor(recording.Source, recording.Width, x, y, i % 2 == 0);
            recording.Show(x, y, 2, 16);
            recording.Encode();
        }
    }

    // The same picture twice more, as the session does after the last change.
    public static void Refine(Recording recording, int repeats = 2)
    {
        for (var i = 0; i < repeats; i++)
        {
            recording.Encode(after: RefinePause);
        }
    }

    private static int Bounce(int position, int range)
    {
        var cycle = position % (2 * range);
        return cycle < range ? cycle : 2 * range - cycle;
    }
}
