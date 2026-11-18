// MatDesktop 0.2.0 – a copy from MatDesign, do not edit here
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MatDesktop;

/// <summary>
/// The header and the footer of the windows of MatDesktop, as building blocks for a program's own dialogs: the Info window (TRAY-05) and the setup window (SETUP-01) are made of them, and so
/// is a dialog of the program (a pause, a confirmation) that wants the same look. AGENT-05 names the same chrome for the WinForms trays: a header on the surface with a line under it (the logo
/// of 44 px, the name or the word mark, the title of the window in the muted colour) and a footer in the colour of the menu with a line above it (the version at the left, the buttons at the
/// right, the primary action first).
/// </summary>
public static class MatDialogChrome
{
    /// <summary>
    /// A header on the surface with a line under it: the logo (44 px), the name of the program, or its word mark when one is given (a control such as the <c>Thix</c><c>Clip</c> of a title
    /// bar; the name is then its accessible name), and the title of the window (the mode of the setup, <c>Info</c>) in the muted colour. <paramref name="titleText"/> is the text block of
    /// the title when the caller wants to keep it.
    /// </summary>
    public static Border Header(IImage? logo, string name, string title, TextBlock? titleText = null, Control? wordMark = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        if (logo is not null)
        {
            row.Children.Add(new Image { Source = logo, Width = 44, Height = 44 });
        }

        if (wordMark is not null)
        {
            if (wordMark.Parent is Panel parent)
            {
                parent.Children.Remove(wordMark);
            }

            wordMark.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(wordMark, name);
            row.Children.Add(wordMark);
        }
        else
        {
            row.Children.Add(new TextBlock { Text = name, FontSize = MatTokens.Type.Fs2xl, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        }

        var label = titleText ?? new TextBlock();
        label.Text = title;
        label.FontSize = MatTokens.Type.FsLg;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 4, 0, 0);
        label.Use(TextBlock.ForegroundProperty, "Mat.TextMuted");
        row.Children.Add(label);
        return new Border { Padding = new Thickness(28, 18), BorderThickness = new Thickness(0, 0, 0, 1), Child = row }
            .Use(Border.BackgroundProperty, "Mat.Surface")
            .Use(Border.BorderBrushProperty, "Mat.Border");
    }

    /// <summary><c>Version 1.4.2 · release</c> in the muted colour, with the sentence of a local build as its tooltip (SETUP-07).</summary>
    public static TextBlock VersionText(string version, string channel)
    {
        var text = new TextBlock { Text = MatVersion.Format(version, channel), VerticalAlignment = VerticalAlignment.Center, FontSize = MatTokens.Type.FsSm, TextTrimming = TextTrimming.CharacterEllipsis }
            .Use(TextBlock.ForegroundProperty, "Mat.TextMuted");
        if (MatVersion.LocalNote(channel) is { } note)
        {
            ToolTip.SetTip(text, note);
        }

        return text;
    }

    /// <summary>
    /// A footer in the colour of the menu with a line above it: <paramref name="left"/> at the left (the version, or nothing in a small dialog) and the buttons at the right in the order given
    /// (the primary action first, Cancel or Close last).
    /// </summary>
    public static Border Footer(Control? left, params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in buttons)
        {
            row.Children.Add(button);
        }

        var dock = new DockPanel();
        DockPanel.SetDock(row, Dock.Right);
        dock.Children.Add(row);
        if (left is not null)
        {
            dock.Children.Add(left);
        }

        return new Border { Padding = new Thickness(28, 12), BorderThickness = new Thickness(0, 1, 0, 0), Child = dock, MinHeight = 58 }
            .Use(Border.BackgroundProperty, "Mat.NavBg")
            .Use(Border.BorderBrushProperty, "Mat.Border");
    }

    /// <summary>A window's content of the three: the header at the top, the footer at the bottom and the body between them, scrolled when it is taller than the window.</summary>
    public static DockPanel Compose(Control? header, Control body, Control? footer)
    {
        ArgumentNullException.ThrowIfNull(body);
        var dock = new DockPanel();
        if (header is not null)
        {
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);
        }

        if (footer is not null)
        {
            DockPanel.SetDock(footer, Dock.Bottom);
            dock.Children.Add(footer);
        }

        dock.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        return dock;
    }
}
