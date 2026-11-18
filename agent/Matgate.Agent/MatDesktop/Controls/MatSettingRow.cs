// MatDesktop 0.2.0 – a copy from MatDesign, do not edit here
using Avalonia;
using Avalonia.Controls;

namespace MatDesktop;

/// <summary>
/// One setting (DESK-04): a title and a sentence at the left, its control (a switch, a choice, a button) at the right, a line under it. The last row of a group sets
/// <c>BorderThickness="0"</c>. The look is the control theme in <c>Themes/Controls.axaml</c>.
/// </summary>
public sealed class MatSettingRow : ContentControl
{
    /// <summary>The title of the setting.</summary>
    public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<MatSettingRow, string>(nameof(Title), string.Empty);

    /// <summary>The sentence under the title (optional).</summary>
    public static readonly StyledProperty<string?> DescriptionProperty = AvaloniaProperty.Register<MatSettingRow, string?>(nameof(Description));

    /// <summary>The title of the setting.</summary>
    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>The sentence under the title.</summary>
    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}

/// <summary>A labelled field with a hint and an error line (the one form field of every window). The look is the control theme in <c>Themes/Controls.axaml</c>.</summary>
public sealed class MatFormField : ContentControl
{
    /// <summary>The label above the field.</summary>
    public static readonly StyledProperty<string> LabelProperty = AvaloniaProperty.Register<MatFormField, string>(nameof(Label), string.Empty);

    /// <summary>A hint under the field (optional).</summary>
    public static readonly StyledProperty<string?> HintProperty = AvaloniaProperty.Register<MatFormField, string?>(nameof(Hint));

    /// <summary>The error under the field (optional; in the colour of danger text, with the words of what is wrong).</summary>
    public static readonly StyledProperty<string?> ErrorProperty = AvaloniaProperty.Register<MatFormField, string?>(nameof(Error));

    /// <summary>The label above the field.</summary>
    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>A hint under the field.</summary>
    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>The error under the field.</summary>
    public string? Error
    {
        get => GetValue(ErrorProperty);
        set => SetValue(ErrorProperty, value);
    }
}
