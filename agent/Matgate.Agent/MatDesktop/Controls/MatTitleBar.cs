// MatDesktop 0.2.0 – a copy from MatDesign, do not edit here
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace MatDesktop;

/// <summary>
/// The title bar of a main window (DESK-03): the logo (32 px) and the name or the word mark at the left, a slot in the middle for the search or the main actions (<see cref="Center"/>),
/// and on Windows and Linux the caption buttons at the right, drawn by the program in the shape and size of Windows 11 (46 × 32, the glyphs of the system, a red close button under the
/// pointer), each with a tooltip and a name. On macOS the traffic lights are the system's: the bar leaves room for them at the left and draws no buttons. The bar is dragged to move the
/// window and a double click maximises; the window keeps its <c>Title</c> for the task bar and screen readers. Call <see cref="MatWindow.UseOwnTitleBar"/> on the window.
/// </summary>
public sealed class MatTitleBar : Border
{
    /// <summary>The height of the bar (docs/DESKTOP.md, "Window sizes").</summary>
    public const double DefaultHeight = 68;

    /// <summary>
    /// The background of the close button under the pointer on Windows: the red of the system (Windows 11), not a colour of the tokens. It is the one literal colour of MatDesktop: the
    /// caption buttons are drawn in the shape of the system (DESK-03), and its close button is this red in every theme.
    /// </summary>
    public const string WindowsCloseHover = "#C42B1C";

    /// <summary>The glyph of the close button on that red: white, as the system draws it.</summary>
    public const string WindowsCloseHoverGlyph = "#FFFFFF";

    /// <summary>The room the traffic lights of macOS take at the left of the bar.</summary>
    public const double MacTrafficLights = 78;

    /// <summary>The glyphs of the caption buttons of Windows 11 on a 10 × 10 box, drawn with a line of 1 px (not icons of the sprite: they are the system's shapes).</summary>
    public static class Glyphs
    {
        /// <summary>Minimise: a line.</summary>
        public const string Minimise = "M0,5.5 H10";

        /// <summary>Maximise: a square with round corners.</summary>
        public const string Maximise = "M1.5,0.5 H8.5 A1,1 0 0 1 9.5,1.5 V8.5 A1,1 0 0 1 8.5,9.5 H1.5 A1,1 0 0 1 0.5,8.5 V1.5 A1,1 0 0 1 1.5,0.5 Z";

        /// <summary>Restore: two squares, the one behind showing its top and right edge.</summary>
        public const string Restore = "M1.5,2.5 H6.5 A1,1 0 0 1 7.5,3.5 V8.5 A1,1 0 0 1 6.5,9.5 H1.5 A1,1 0 0 1 0.5,8.5 V3.5 A1,1 0 0 1 1.5,2.5 Z M2.5,2.5 V1.5 A1,1 0 0 1 3.5,0.5 H8.5 A1,1 0 0 1 9.5,1.5 V6.5 A1,1 0 0 1 8.5,7.5 H7.5";

        /// <summary>Close: a cross.</summary>
        public const string Close = "M0.5,0.5 L9.5,9.5 M9.5,0.5 L0.5,9.5";
    }

    /// <summary>The name of the program at the left (or its word mark, <see cref="WordMark"/>).</summary>
    public static readonly StyledProperty<string> AppNameProperty = AvaloniaProperty.Register<MatTitleBar, string>(nameof(AppName), string.Empty);

    /// <summary>The logo at the left, 32 px.</summary>
    public static readonly StyledProperty<IImage?> LogoProperty = AvaloniaProperty.Register<MatTitleBar, IImage?>(nameof(Logo));

    /// <summary>A word mark that replaces the name (optional).</summary>
    public static readonly StyledProperty<Control?> WordMarkProperty = AvaloniaProperty.Register<MatTitleBar, Control?>(nameof(WordMark));

    /// <summary>What stands in the middle: the search, the main actions.</summary>
    public static readonly StyledProperty<Control?> CenterProperty = AvaloniaProperty.Register<MatTitleBar, Control?>(nameof(Center));

    /// <summary>The width of the part with the logo and the name; set it to the width of the menu so that the middle stands over the page (MatNav does it).</summary>
    public static readonly StyledProperty<double> BrandWidthProperty = AvaloniaProperty.Register<MatTitleBar, double>(nameof(BrandWidth), MatNav.OpenWidth);

    /// <summary>Whether the bar draws the caption buttons: on Windows and Linux yes, on macOS no (the traffic lights are the system's).</summary>
    public static readonly StyledProperty<bool> ShowCaptionButtonsProperty = AvaloniaProperty.Register<MatTitleBar, bool>(nameof(ShowCaptionButtons), !OperatingSystem.IsMacOS());

    /// <summary>The key that also closes the window, named in the tooltip of the close button (<c>Esc</c>: "Close (Esc)").</summary>
    public static readonly StyledProperty<string?> CloseKeyProperty = AvaloniaProperty.Register<MatTitleBar, string?>(nameof(CloseKey));

    private readonly Grid _grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
    private readonly StackPanel _brand = new() { Orientation = Orientation.Horizontal, Spacing = 11, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly Image _logo = new() { Width = 32, Height = 32 };
    private readonly TextBlock _name = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, FontSize = MatTokens.Type.FsLg };
    private readonly ContentControl _center = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 20, 0) };
    private readonly StackPanel _captions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
    private readonly Button _minimise;
    private readonly Button _maximise;
    private readonly Button _close;
    private readonly Path _maximiseGlyph;
    private Window? _window;

    static MatTitleBar()
    {
        AppNameProperty.Changed.AddClassHandler<MatTitleBar>((bar, _) => bar.Refresh());
        LogoProperty.Changed.AddClassHandler<MatTitleBar>((bar, _) => bar.Refresh());
        WordMarkProperty.Changed.AddClassHandler<MatTitleBar>((bar, _) => bar.Refresh());
        CenterProperty.Changed.AddClassHandler<MatTitleBar>((bar, _) => bar._center.Content = bar.Center);
        BrandWidthProperty.Changed.AddClassHandler<MatTitleBar>((bar, _) => bar.Layout());
        ShowCaptionButtonsProperty.Changed.AddClassHandler<MatTitleBar>((bar, _) => bar.Layout());
        CloseKeyProperty.Changed.AddClassHandler<MatTitleBar>((bar, _) => bar.Refresh());
    }

    /// <summary>A bar without a name; set <see cref="AppName"/> and <see cref="Logo"/>.</summary>
    public MatTitleBar()
    {
        Height = DefaultHeight;
        BorderThickness = new Thickness(0, 0, 0, 1);
        this.Use(BackgroundProperty, "Mat.Surface");
        this.Use(BorderBrushProperty, "Mat.Border");

        _brand.Children.Add(_logo);
        _brand.Children.Add(_name);
        _minimise = Caption(Glyphs.Minimise, "MatButton.Caption", out _);
        _maximise = Caption(Glyphs.Maximise, "MatButton.Caption", out _maximiseGlyph);
        _close = Caption(Glyphs.Close, "MatButton.CaptionClose", out _);
        _minimise.Click += (_, _) => Act(w => w.WindowState = WindowState.Minimized);
        _maximise.Click += (_, _) => Act(ToggleMaximised);
        _close.Click += OnCloseClick;
        _captions.Children.Add(_minimise);
        _captions.Children.Add(_maximise);
        _captions.Children.Add(_close);

        Grid.SetColumn(_center, 1);
        Grid.SetColumn(_captions, 2);
        _grid.Children.Add(_brand);
        _grid.Children.Add(_center);
        _grid.Children.Add(_captions);
        Child = _grid;

        PointerPressed += OnPointerPressed;
        DoubleTapped += OnDoubleTapped;
        Refresh();
        Layout();
    }

    /// <summary>Raised by the close button. Without a handler the window closes; a program with a tray hides it instead (DESK-06) and sets <see cref="RoutedEventArgs.Handled"/>.</summary>
    public event EventHandler<RoutedEventArgs>? CloseRequested;

    /// <summary>The name of the program.</summary>
    public string AppName
    {
        get => GetValue(AppNameProperty);
        set => SetValue(AppNameProperty, value);
    }

    /// <summary>The logo.</summary>
    public IImage? Logo
    {
        get => GetValue(LogoProperty);
        set => SetValue(LogoProperty, value);
    }

    /// <summary>A word mark instead of the name.</summary>
    public Control? WordMark
    {
        get => GetValue(WordMarkProperty);
        set => SetValue(WordMarkProperty, value);
    }

    /// <summary>What stands in the middle.</summary>
    public Control? Center
    {
        get => GetValue(CenterProperty);
        set => SetValue(CenterProperty, value);
    }

    /// <summary>The width of the part with the logo and the name.</summary>
    public double BrandWidth
    {
        get => GetValue(BrandWidthProperty);
        set => SetValue(BrandWidthProperty, value);
    }

    /// <summary>Whether the caption buttons are drawn.</summary>
    public bool ShowCaptionButtons
    {
        get => GetValue(ShowCaptionButtonsProperty);
        set => SetValue(ShowCaptionButtonsProperty, value);
    }

    /// <summary>The key that also closes the window, for the tooltip.</summary>
    public string? CloseKey
    {
        get => GetValue(CloseKeyProperty);
        set => SetValue(CloseKeyProperty, value);
    }

    /// <summary>The caption buttons: minimise, maximise or restore, close (for tests and for an app that adds a tooltip with a shortcut).</summary>
    public IReadOnlyList<Button> CaptionButtons => [_minimise, _maximise, _close];

    /// <summary>Whether the name is shown next to the logo (a folded menu leaves only the logo).</summary>
    public bool IsNameVisible
    {
        get => _name.IsVisible || (WordMark?.IsVisible ?? false);
        set
        {
            _name.IsVisible = value && WordMark is null;
            if (WordMark is not null)
            {
                WordMark.IsVisible = value;
            }
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
        {
            _window.PropertyChanged += OnWindowPropertyChanged;
            Refresh();
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null)
        {
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private static Button Caption(string glyph, string theme, out Path path)
    {
        var button = new Button { Width = 46, Height = 32, Focusable = false }.Themed(theme);
        path = new Path { Data = StreamGeometry.Parse(glyph), Width = 10, Height = 10, StrokeThickness = 1, Stretch = Stretch.None };
        path.Bind(Shape.StrokeProperty, button.GetObservable(Button.ForegroundProperty));
        button.Content = path;
        return button;
    }

    private void Refresh()
    {
        _logo.Source = Logo;
        _logo.IsVisible = Logo is not null;
        _name.Text = AppName;
        if (_brand.Children.Count > 2)
        {
            _brand.Children.RemoveAt(2);
        }

        _name.IsVisible = WordMark is null;
        if (WordMark is not null)
        {
            _brand.Children.Add(WordMark);
        }

        var maximised = _window?.WindowState == WindowState.Maximized;
        _maximiseGlyph.Data = StreamGeometry.Parse(maximised ? Glyphs.Restore : Glyphs.Maximise);
        Name(_minimise, MatText.Get("caption.minimise"));
        Name(_maximise, MatText.Get(maximised ? "caption.restore" : "caption.maximise"));
        var close = MatText.Get("caption.close");
        AutomationProperties.SetName(_close, close);
        ToolTip.SetTip(_close, string.IsNullOrEmpty(CloseKey) ? close : $"{close} ({CloseKey})");

        static void Name(Control control, string text)
        {
            AutomationProperties.SetName(control, text);
            ToolTip.SetTip(control, text);
        }
    }

    private void Layout()
    {
        var mac = OperatingSystem.IsMacOS();
        _captions.IsVisible = ShowCaptionButtons;
        _grid.ColumnDefinitions[0].Width = new GridLength(Math.Max(0, BrandWidth - (mac ? 0 : 22)));
        _grid.Margin = new Thickness(mac ? MacTrafficLights : 22, 0, ShowCaptionButtons ? 0 : 18, 0);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            Refresh();
        }
    }

    private void Act(Action<Window> action)
    {
        if ((_window ?? TopLevel.GetTopLevel(this) as Window) is { } window)
        {
            action(window);
        }
    }

    private static void ToggleMaximised(Window window) =>
        window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        var args = new RoutedEventArgs();
        CloseRequested?.Invoke(this, args);
        if (!args.Handled)
        {
            Act(w => w.Close());
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 1 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && IsBarItself(e.Source) && _window is { } window)
        {
            window.BeginMoveDrag(e);
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsBarItself(e.Source))
        {
            Act(ToggleMaximised);
        }
    }

    /// <summary>True when a press lands on the bar or its brand, not on a control of the middle or a caption button.</summary>
    private bool IsBarItself(object? source) => source == this || source == _grid || source == _brand || source == _center;
}
