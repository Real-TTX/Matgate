using Xunit;

namespace Matgate.Display.Video.Tests;

// What asking for fewer pictures rests on (see RateController): what a page of text costs that scrolls by so
// many rows from one picture to the next, with the quantiser fixed. The encoder follows a page that moves a
// little, and such a picture is small; one that moves far is coded almost from nothing, and then it hardly
// matters how far it moved. In between it finds a steady motion after a while - if the pictures keep coming.
// The numbers go into the report.
public sealed class ScrollCostTests
{
    private const int Width = 1920;
    private const int Height = 1080;

    [Fact]
    public void WhatTheEncoderDoesNotFollowCostsTheSameHoweverFarItMoved()
    {
        Assert.SkipUnless(H264Encoder.IsAvailable, "libopenh264 is not available: " + H264Encoder.UnavailableReason);
        var sizes = new Dictionary<(int Quantiser, int Rows), double>();
        var distances = new[] { 2, 6, 18, 30, 54, 90, 162 };
        foreach (var quantiser in new[] { 22, 34, 46 })
        {
            foreach (var rows in distances)
            {
                sizes[(quantiser, rows)] = Kilobits(Scroll(quantiser, rows, 12, new H264EncoderOptions()), 2, 10);
            }

            Report.Line($"scroll cost 1920x1080 at quantiser {quantiser}, kbit a picture: " + string.Join(", ", distances.Select(rows => $"{rows} rows {sizes[(quantiser, rows)]:F0}")));
        }

        foreach (var quantiser in new[] { 22, 34, 46 })
        {
            // Followed: a small part of what the lost page costs. Lost: the same at 90 rows and at 162, and
            // at 30 rows already most of it - so a third of the pictures takes far less than all of them.
            Assert.True(sizes[(quantiser, 6)] < 0.1 * sizes[(quantiser, 162)]);
            Assert.InRange(sizes[(quantiser, 90)], 0.9 * sizes[(quantiser, 162)], 1.1 * sizes[(quantiser, 162)]);
            Assert.True(sizes[(quantiser, 90)] < 2.5 * sizes[(quantiser, 30)], $"At {quantiser}, three times as far costs {sizes[(quantiser, 90)] / sizes[(quantiser, 30)]:F1} times as much.");
        }
    }

    [Fact]
    public void ASteadyScrollIsFoundAfterAWhileIfThePicturesKeepComing()
    {
        Assert.SkipUnless(H264Encoder.IsAvailable, "libopenh264 is not available: " + H264Encoder.UnavailableReason);

        // Fifty rows a picture - 3000 a second at sixty pictures a second - and three times that, which is
        // what the same scroll is at twenty.
        var near = Scroll(34, 50, 90, new H264EncoderOptions());
        var far = Scroll(34, 150, 90, new H264EncoderOptions());
        Report.Line(
            $"scroll cost 1920x1080 at quantiser 34 over 90 pictures, kbit a picture at the start and at the end: " +
            $"50 rows {Kilobits(near, 2, 8):F0} -> {Kilobits(near, 80, 10):F0}, 150 rows {Kilobits(far, 2, 8):F0} -> {Kilobits(far, 80, 10):F0}");
        Assert.True(Kilobits(near, 80, 10) < 0.5 * Kilobits(near, 2, 8), "The encoder did not find a scroll of 50 rows a picture.");
        Assert.True(Kilobits(far, 80, 10) > 0.8 * Kilobits(far, 2, 8), "The encoder found a scroll of 150 rows a picture.");
    }

    // The sizes of the pictures of a page scrolling by that many rows from each to the next.
    private static List<EncodedPicture> Scroll(int quantiser, int rows, int pictures, H264EncoderOptions options)
    {
        var page = Pictures.TextPage(Width, Height * 4);
        using var recording = new Recording(Width, Height, 60, 100000, options with { Quantisers = _ => quantiser }) { KeepReference = false };
        for (var i = 0; i < pictures; i++)
        {
            Pictures.Scroll(page, recording.Source, Width, Height, i * rows);
            recording.Show();
            recording.Encode();
        }

        return recording.Pictures;
    }

    private static double Kilobits(List<EncodedPicture> pictures, int first, int count) =>
        pictures.Skip(first).Take(count).Average(picture => picture.Bytes) * 8 / 1000;
}
