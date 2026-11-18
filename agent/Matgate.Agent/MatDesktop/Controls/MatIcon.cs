// MatDesktop 0.2.0 – a copy from MatDesign, do not edit here
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MatDesktop;

/// <summary>
/// An icon of the toolkit's sprite (DESK-09): <see cref="Icon"/> is the name of the sprite (<c>search</c>, <c>settings</c>, the <c>x</c> of <c>mat-i-x</c>), drawn from the geometry
/// <c>MatIcon.&lt;name&gt;</c> of <c>Themes/MatIcons.axaml</c> on its 24 × 24 grid with a line of 2 units and round ends, scaled to <see cref="Size"/>, like the web strokes it; the parts
/// that the sprite fills are filled, and <see cref="IsFilled"/> fills the whole icon (a chosen star). The colour is the text colour (<see cref="Foreground"/>, inherited). Not <c>Name</c>: that is the name of an element in Avalonia.
/// </summary>
public sealed class MatIcon : Control
{
    /// <summary>The name of the icon in the sprite.</summary>
    public static readonly StyledProperty<string?> IconProperty = AvaloniaProperty.Register<MatIcon, string?>(nameof(Icon));

    /// <summary>The width and height in px (16 by default, the icon of a button).</summary>
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<MatIcon, double>(nameof(Size), 16);

    /// <summary>The colour, inherited from the text around it.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<MatIcon>();

    /// <summary>The width of the line on the 24 × 24 grid (2, like the web).</summary>
    public static readonly StyledProperty<double> StrokeWidthProperty = AvaloniaProperty.Register<MatIcon, double>(nameof(StrokeWidth), 2);

    /// <summary>True draws the icon filled: the same geometry, its inside filled and its line stroked (a chosen star, a pinned pin).</summary>
    public static readonly StyledProperty<bool> IsFilledProperty = AvaloniaProperty.Register<MatIcon, bool>(nameof(IsFilled));

    private Geometry? _stroke;
    private Geometry? _fill;

    static MatIcon()
    {
        AffectsRender<MatIcon>(IconProperty, ForegroundProperty, StrokeWidthProperty, IsFilledProperty);
        AffectsMeasure<MatIcon>(SizeProperty);
        IconProperty.Changed.AddClassHandler<MatIcon>((icon, _) => icon.Resolve());
        VerticalAlignmentProperty.OverrideDefaultValue<MatIcon>(Avalonia.Layout.VerticalAlignment.Center);
    }

    /// <summary>The name of the icon in the sprite.</summary>
    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>The width and height in px.</summary>
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>The colour of the icon.</summary>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>The width of the line on the 24 × 24 grid.</summary>
    public double StrokeWidth
    {
        get => GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    /// <summary>True draws the icon filled (the same geometry, the inside filled too): a chosen star, a pinned pin. The outline stays, so the filled and the open icon have the same size.</summary>
    public bool IsFilled
    {
        get => GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    /// <summary>True when the icon was found among the resources.</summary>
    public bool IsResolved => _stroke is not null;

    /// <summary>The geometry of an icon of the sprite from the resources of the application (null when there is no such icon or no theme yet).</summary>
    public static Geometry? Find(string name) => Application.Current?.TryFindResource("MatIcon." + name, out var value) == true ? value as Geometry : null;

    /// <inheritdoc />
    protected override void OnAttachedToLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        Resolve();
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        if (_stroke is null || Foreground is null)
        {
            return;
        }

        var side = Math.Min(Bounds.Width, Bounds.Height);
        var scale = side / 24.0;
        var offset = new Vector((Bounds.Width - side) / 2, (Bounds.Height - side) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset)))
        {
            context.DrawGeometry(IsFilled ? Foreground : null, new Pen(Foreground, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), _stroke);
            if (_fill is not null)
            {
                context.DrawGeometry(Foreground, null, _fill);
            }
        }
    }

    private void Resolve()
    {
        _stroke = _fill = null;
        if (!string.IsNullOrEmpty(Icon))
        {
            // in a window from the resources around it, outside of one (a bitmap of a menu) from the application's
            _stroke = this.TryFindResource("MatIcon." + Icon, out var stroke) ? stroke as Geometry : Find(Icon);
            _fill = this.TryFindResource("MatIcon." + Icon + ".Fill", out var fill) ? fill as Geometry : Find(Icon + ".Fill");
        }

        InvalidateVisual();
    }
}
