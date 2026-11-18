// MatDesktop 0.2.0 – a copy from MatDesign, do not edit here
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MatDesktop;

/// <summary>A fact of <see cref="MatFacts"/>: a label and its value; <see cref="IsMono"/> writes the value in the monospaced font (a version, a port, a path, a fingerprint).</summary>
public sealed record MatFact(string Label, string Value, bool IsMono = false);

/// <summary>
/// The facts of an Info window or of About as rows (TRAY-05): the label at the left in the muted colour, the value at the right, selectable, a hairline between the rows.
/// </summary>
public sealed class MatFacts : Border
{
    /// <summary>The width of the column of the labels.</summary>
    public static readonly StyledProperty<double> LabelWidthProperty = AvaloniaProperty.Register<MatFacts, double>(nameof(LabelWidth), 170);

    private readonly Grid _grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
    private IReadOnlyList<MatFact> _facts = [];

    static MatFacts()
    {
        LabelWidthProperty.Changed.AddClassHandler<MatFacts>((facts, _) => facts.Rebuild());
    }

    /// <summary>An empty list of facts.</summary>
    public MatFacts()
    {
        Child = _grid;
    }

    /// <summary>The width of the column of the labels.</summary>
    public double LabelWidth
    {
        get => GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }

    /// <summary>The facts, in the order they are shown.</summary>
    public IReadOnlyList<MatFact> Facts
    {
        get => _facts;
        set
        {
            _facts = value ?? [];
            Rebuild();
        }
    }

    /// <summary>The value shown for a label (for tests and for copying), or null.</summary>
    public string? ValueOf(string label) => _facts.FirstOrDefault(f => f.Label == label)?.Value;

    private void Rebuild()
    {
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _grid.ColumnDefinitions[0].Width = new GridLength(LabelWidth);
        for (var i = 0; i < _facts.Count; i++)
        {
            var fact = _facts[i];
            _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var line = new Border { BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0) }.Use(Border.BorderBrushProperty, "Mat.Border");
            Grid.SetRow(line, i);
            Grid.SetColumnSpan(line, 2);
            _grid.Children.Add(line);

            var label = new TextBlock { Text = fact.Label, Margin = new Thickness(0, 9, 16, 9), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top }
                .Use(TextBlock.ForegroundProperty, "Mat.TextMuted");
            Grid.SetRow(label, i);
            _grid.Children.Add(label);

            var value = new SelectableTextBlock { Text = fact.Value, Margin = new Thickness(0, 9, 0, 9), TextWrapping = TextWrapping.Wrap };
            if (fact.IsMono)
            {
                value.Use(TextBlock.FontFamilyProperty, "Mat.Mono");
            }

            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            _grid.Children.Add(value);
        }
    }
}
