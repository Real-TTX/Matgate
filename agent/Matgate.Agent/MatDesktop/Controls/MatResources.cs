// MatDesktop 0.2.0 – a copy from MatDesign, do not edit here
using Avalonia;
using Avalonia.Controls;

namespace MatDesktop;

/// <summary>Binds a property of a control to a resource of the theme, so that a switch between light and dark reaches it (the code form of <c>{DynamicResource Mat.Surface}</c>).</summary>
public static class MatResources
{
    /// <summary>Binds <paramref name="property"/> of <paramref name="control"/> to the resource <paramref name="key"/> and returns the control.</summary>
    public static T Use<T>(this T control, AvaloniaProperty property, string key)
        where T : Control
    {
        ArgumentNullException.ThrowIfNull(control);
        control[!property] = control.GetResourceObservable(key).ToBinding();
        return control;
    }

    /// <summary>Sets the control theme of <paramref name="control"/> to the theme <paramref name="key"/> of Controls.axaml (<c>MatButton.Primary</c>) and returns the control.</summary>
    public static T Themed<T>(this T control, string key)
        where T : Control
    {
        ArgumentNullException.ThrowIfNull(control);
        control.Use(StyledElement.ThemeProperty, key);
        return control;
    }
}
