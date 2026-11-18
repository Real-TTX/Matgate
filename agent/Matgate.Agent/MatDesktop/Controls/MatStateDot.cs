// MatDesktop 0.2.0 – a copy from MatDesign, do not edit here
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;

namespace MatDesktop;

/// <summary>
/// The state of a program as a dot in its colour with the word beside it (TRAY-02, COLOR-04: never the colour alone): green running, amber waiting, red stopped, grey not set up, or a
/// state of the program's own. Used in Info, in a menu, in About.
/// </summary>
public sealed class MatStateDot : StackPanel
{
    /// <summary>The state shown.</summary>
    public static readonly StyledProperty<MatState?> StateProperty = AvaloniaProperty.Register<MatStateDot, MatState?>(nameof(State));

    /// <summary>The diameter of the dot in px.</summary>
    public static readonly StyledProperty<double> DotSizeProperty = AvaloniaProperty.Register<MatStateDot, double>(nameof(DotSize), 10);

    private readonly Ellipse _dot = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _word = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = Avalonia.Media.FontWeight.SemiBold };

    static MatStateDot()
    {
        StateProperty.Changed.AddClassHandler<MatStateDot>((dot, _) => dot.Refresh());
        DotSizeProperty.Changed.AddClassHandler<MatStateDot>((dot, _) => dot.Refresh());
    }

    /// <summary>A dot without a state (it shows Running until one is set).</summary>
    public MatStateDot()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 8;
        Children.Add(_dot);
        Children.Add(_word);
        Refresh();
    }

    /// <summary>The state shown.</summary>
    public MatState? State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>The diameter of the dot in px.</summary>
    public double DotSize
    {
        get => GetValue(DotSizeProperty);
        set => SetValue(DotSizeProperty, value);
    }

    /// <summary>The word shown next to the dot.</summary>
    public string Word => _word.Text ?? string.Empty;

    private void Refresh()
    {
        var state = State ?? MatState.Running;
        _dot.Width = _dot.Height = DotSize;
        _dot.Use(Shape.FillProperty, MatTheme.KeyOf(state.ColorToken));
        _word.Text = state.Word;
        Avalonia.Automation.AutomationProperties.SetName(this, state.Word);
    }
}
