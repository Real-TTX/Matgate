// MatDesktop 0.2.0 – a copy from MatDesign, do not edit here
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MatDesktop;

/// <summary>
/// The icons of a tray (TRAY-02): the mark of the app with the state as a dot at the bottom right, drawn at run time from the one mark (no second asset to keep in step). The dot stands
/// in a cut-out of the mark, a ring of transparency around it, so that it is seen on a light and on a dark task bar. On Windows the dot has the colour of the state (the status tokens of
/// light mode); a macOS template image has no colour (the system paints it), so there the state is a shape (<see cref="MatState.TemplateMark"/>): a ring, a cross, a pause sign, a dot or
/// nothing for Running. The words of the state stand in the menu and the tooltip.
/// </summary>
public static class MatTrayIcons
{
    /// <summary>The size the icons are drawn in (the system scales them down).</summary>
    public const int Size = 64;

    /// <summary>The mark with the dot of <paramref name="state"/>, <see cref="Size"/> px square: in colour, or as a template image for macOS (<paramref name="template"/>).</summary>
    public static RenderTargetBitmap Draw(Bitmap mark, MatState state, bool template, int size = Size)
    {
        ArgumentNullException.ThrowIfNull(mark);
        ArgumentNullException.ThrowIfNull(state);
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            var full = new Rect(0, 0, size, size);
            var dot = Dot(state, template, size);
            if (dot is null)
            {
                context.DrawImage(mark, full);
            }
            else
            {
                var center = new Point(size * 0.75, size * 0.75);
                var radius = size * 0.22;
                var gap = size * 0.08;
                var cut = new EllipseGeometry(new Rect(center.X - radius - gap, center.Y - radius - gap, (radius + gap) * 2, (radius + gap) * 2));
                using (context.PushGeometryClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), cut)))
                {
                    context.DrawImage(mark, full);
                }

                // a template image is read by its opacity only: any opaque colour of the tokens will do
                var colour = template ? MatTokens.Light.Text : MatPalette.For(MatTheme.Options?.Accent, dark: false, MatTheme.Options?.OnAccent)[state.TrayColorToken];
                context.DrawGeometry(new SolidColorBrush(MatTheme.ToColor(colour)), null, dot);
            }
        }

        return bitmap;
    }

    /// <summary>The same as a PNG.</summary>
    public static byte[] Png(Bitmap mark, MatState state, bool template, int size = Size)
    {
        using var bitmap = Draw(mark, state, template, size);
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    /// <summary>The icon for Avalonia's tray icon.</summary>
    public static WindowIcon Icon(Bitmap mark, MatState state, bool template) => new(new MemoryStream(Png(mark, state, template)));

    /// <summary>The dot of a state with its mark cut out, or null when nothing is drawn (Running on a template image).</summary>
    internal static Geometry? Dot(MatState state, bool template, int size)
    {
        var mark = template ? state.TemplateMark : state.Kind == MatStateKind.Custom ? state.TemplateMark : MatStateMark.Dot;
        if (mark == MatStateMark.None)
        {
            return template ? null : new EllipseGeometry(Circle(size, 1));
        }

        Geometry dot = new EllipseGeometry(Circle(size, 1));
        var c = size * 0.75;
        var r = size * 0.22;
        Geometry? hole = mark switch
        {
            MatStateMark.Ring => new EllipseGeometry(Circle(size, 0.5)),
            MatStateMark.Pause => new CombinedGeometry(
                GeometryCombineMode.Union,
                new RectangleGeometry(new Rect(c - r * 0.45, c - r * 0.5, r * 0.3, r)),
                new RectangleGeometry(new Rect(c + r * 0.15, c - r * 0.5, r * 0.3, r))),
            MatStateMark.Cross => Cross(c, r * 0.42, r * 0.14),
            _ => null,
        };
        return hole is null ? dot : new CombinedGeometry(GeometryCombineMode.Exclude, dot, hole);
    }

    private static Rect Circle(int size, double share)
    {
        var c = size * 0.75;
        var r = size * 0.22 * share;
        return new Rect(c - r, c - r, r * 2, r * 2);
    }

    private static StreamGeometry Cross(double c, double arm, double half)
    {
        // an X of two bars, as one outline
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(c - arm, c - arm + half), true);
            g.LineTo(new Point(c - arm + half, c - arm));
            g.LineTo(new Point(c, c - half));
            g.LineTo(new Point(c + arm - half, c - arm));
            g.LineTo(new Point(c + arm, c - arm + half));
            g.LineTo(new Point(c + half, c));
            g.LineTo(new Point(c + arm, c + arm - half));
            g.LineTo(new Point(c + arm - half, c + arm));
            g.LineTo(new Point(c, c + half));
            g.LineTo(new Point(c - arm + half, c + arm));
            g.LineTo(new Point(c - arm, c + arm - half));
            g.LineTo(new Point(c - half, c));
            g.EndFigure(true);
        }

        return geometry;
    }
}
