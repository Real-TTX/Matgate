// MatDesktop 0.2.0 – a copy from MatDesign, do not edit here
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace MatDesktop;

/// <summary>
/// A value the user has to copy (AGENT-02, AGENT-03): its label, the value in the monospaced font, selectable, and a Copy button with the name of the value (<c>Copy password</c>). The
/// Finish step of an agent that others connect to shows host, port, password and fingerprint this way, each in one place only.
/// </summary>
public sealed class MatCopyField : Grid
{
    private readonly SelectableTextBlock _value;

    /// <summary>A field for <paramref name="label"/> and <paramref name="value"/>.</summary>
    public MatCopyField(string label, string value)
    {
        Label = label;
        Value = value;
        ColumnDefinitions = new ColumnDefinitions("140,*,Auto");
        Margin = new Thickness(0, 0, 0, 6);
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }.Use(TextBlock.ForegroundProperty, "Mat.TextMuted");
        _value = new SelectableTextBlock { Text = value, VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
        _value.Use(TextBlock.FontFamilyProperty, "Mat.Mono");
        CopyButton = new Button { Content = new MatIcon { Icon = "copy", Size = 16 } }.Themed("MatButton.Icon");
        var copy = MatText.Format("copy.named", label);
        ToolTip.SetTip(CopyButton, copy);
        AutomationProperties.SetName(CopyButton, copy);
        CopyButton.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(Value);
                ToolTip.SetTip(CopyButton, MatText.Get("copied"));
            }
        };
        SetColumn(_value, 1);
        SetColumn(CopyButton, 2);
        Children.Add(name);
        Children.Add(_value);
        Children.Add(CopyButton);
    }

    /// <summary>The label.</summary>
    public string Label { get; }

    /// <summary>The value.</summary>
    public string Value { get; }

    /// <summary>The Copy button.</summary>
    public Button CopyButton { get; }
}
