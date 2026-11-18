// MatDesktop 0.2.0 – a copy from MatDesign, do not edit here
using Avalonia;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Layout;
using Avalonia.Media;

namespace MatDesktop;

/// <summary>
/// An item of the menu (DESK-04): an icon of the sprite and a label; the current one is a tint of the accent behind the whole item with the icon in the accent colour, never a bar
/// (COLOR-08). In a folded menu only the icon shows and the tooltip names it. The look is the control theme in <c>Themes/Controls.axaml</c>.
/// </summary>
[PseudoClasses(":current", ":folded")]
public sealed class MatNavItem : Button
{
    /// <summary>The name of the icon in the sprite.</summary>
    public static readonly StyledProperty<string?> IconProperty = AvaloniaProperty.Register<MatNavItem, string?>(nameof(Icon));

    /// <summary>The label.</summary>
    public static readonly StyledProperty<string> LabelProperty = AvaloniaProperty.Register<MatNavItem, string>(nameof(Label), string.Empty);

    /// <summary>The shortcut that opens the page, named in the tooltip (<c>Ctrl+1</c>).</summary>
    public static readonly StyledProperty<string?> ShortcutProperty = AvaloniaProperty.Register<MatNavItem, string?>(nameof(Shortcut));

    /// <summary>True for the page that is shown.</summary>
    public static readonly StyledProperty<bool> IsCurrentProperty = AvaloniaProperty.Register<MatNavItem, bool>(nameof(IsCurrent));

    /// <summary>True while the menu is folded to a rail.</summary>
    public static readonly StyledProperty<bool> IsFoldedProperty = AvaloniaProperty.Register<MatNavItem, bool>(nameof(IsFolded));

    static MatNavItem()
    {
        IsCurrentProperty.Changed.AddClassHandler<MatNavItem>((item, _) => item.Refresh());
        IsFoldedProperty.Changed.AddClassHandler<MatNavItem>((item, _) => item.Refresh());
        LabelProperty.Changed.AddClassHandler<MatNavItem>((item, _) => item.Refresh());
        ShortcutProperty.Changed.AddClassHandler<MatNavItem>((item, _) => item.Refresh());
    }

    /// <summary>An item without a label.</summary>
    public MatNavItem()
    {
        Refresh();
    }

    /// <summary>An item with an icon and a label.</summary>
    public MatNavItem(string icon, string label, string? shortcut = null)
    {
        Icon = icon;
        Label = label;
        Shortcut = shortcut;
        Refresh();
    }

    /// <summary>The name of the icon in the sprite.</summary>
    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>The label.</summary>
    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>The shortcut, for the tooltip.</summary>
    public string? Shortcut
    {
        get => GetValue(ShortcutProperty);
        set => SetValue(ShortcutProperty, value);
    }

    /// <summary>True for the page that is shown.</summary>
    public bool IsCurrent
    {
        get => GetValue(IsCurrentProperty);
        set => SetValue(IsCurrentProperty, value);
    }

    /// <summary>True while the menu is folded.</summary>
    public bool IsFolded
    {
        get => GetValue(IsFoldedProperty);
        set => SetValue(IsFoldedProperty, value);
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(MatNavItem);

    private void Refresh()
    {
        PseudoClasses.Set(":current", IsCurrent);
        PseudoClasses.Set(":folded", IsFolded);
        AutomationProperties.SetName(this, Label);
        ToolTip.SetTip(this, string.IsNullOrEmpty(Shortcut) ? (IsFolded ? Label : null) : $"{Label} ({Shortcut})");
    }
}

/// <summary>
/// The menu of a main window (DESK-04, the menu of the web shell SHELL-01): the items with an icon and a label, a line, Settings as an item of its own below them, and at the foot the version
/// (<c>Version 1.4.2 · release</c>, SETUP-07) as a link that opens About (<see cref="AboutRequested"/>) and the button that folds the menu to a rail of 68 px and back
/// (<see cref="IsFolded"/>, <see cref="FoldChanged"/>: the program remembers it; <see cref="FoldShortcut"/> names its key). Above the version the program's own <see cref="Foot"/> and, while an update waits,
/// a link to it (<see cref="UpdateVersion"/>). The open menu is 248 px wide, 210 px in a window narrower than 900 px.
/// </summary>
public sealed class MatNav : Border
{
    /// <summary>The width of the open menu.</summary>
    public const double OpenWidth = 248;

    /// <summary>The width of the open menu in a narrow window.</summary>
    public const double NarrowWidth = 210;

    /// <summary>The width of the folded menu, a rail of icons (12 + 44 + 12).</summary>
    public const double RailWidth = 68;

    /// <summary>A window narrower than this narrows the open menu.</summary>
    public const double NarrowWindow = 900;

    /// <summary>True while the menu is folded to a rail of icons.</summary>
    public static readonly StyledProperty<bool> IsFoldedProperty = AvaloniaProperty.Register<MatNav, bool>(nameof(IsFolded));

    /// <summary>The name of the program, for "About &lt;App&gt;".</summary>
    public static readonly StyledProperty<string> AppNameProperty = AvaloniaProperty.Register<MatNav, string>(nameof(AppName), string.Empty);

    /// <summary>The version number.</summary>
    public static readonly StyledProperty<string> VersionProperty = AvaloniaProperty.Register<MatNav, string>(nameof(Version), "0.0.0");

    /// <summary>The channel (release, nightly, local).</summary>
    public static readonly StyledProperty<string> ChannelProperty = AvaloniaProperty.Register<MatNav, string>(nameof(Channel), MatVersion.Release);

    /// <summary>What the program shows at the foot above the version (a card, the state of a connection), or null.</summary>
    public static readonly StyledProperty<Control?> FootProperty = AvaloniaProperty.Register<MatNav, Control?>(nameof(Foot));

    /// <summary>The version of an update that waits (SETUP-06), or null: then a link above the version says so.</summary>
    public static readonly StyledProperty<string?> UpdateVersionProperty = AvaloniaProperty.Register<MatNav, string?>(nameof(UpdateVersion));

    /// <summary>The shortcut that folds and opens the menu (<c>Ctrl+B</c>), named in the tooltip of the fold button (DESK-07), or null.</summary>
    public static readonly StyledProperty<string?> FoldShortcutProperty = AvaloniaProperty.Register<MatNav, string?>(nameof(FoldShortcut));

    private readonly StackPanel _items = new() { Spacing = 2 };
    private readonly Border _line = new() { Height = 1, Margin = new Thickness(12, 8, 12, 8) };
    private readonly ContentControl _settingsHost = new();
    private readonly ContentControl _footHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _versionText = new() { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
    private readonly Button _version = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _updateText = new() { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _update = new() { HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false, Margin = new Thickness(0, 0, 0, 6) };
    private readonly Button _fold = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _foot = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
    private TopLevel? _topLevel;
    private MatNavItem? _settings;
    private double _windowWidth = 1180;

    static MatNav()
    {
        IsFoldedProperty.Changed.AddClassHandler<MatNav>((nav, e) => nav.OnFolded(e));
        AppNameProperty.Changed.AddClassHandler<MatNav>((nav, _) => nav.Refresh());
        VersionProperty.Changed.AddClassHandler<MatNav>((nav, _) => nav.Refresh());
        ChannelProperty.Changed.AddClassHandler<MatNav>((nav, _) => nav.Refresh());
        UpdateVersionProperty.Changed.AddClassHandler<MatNav>((nav, _) => nav.Refresh());
        FoldShortcutProperty.Changed.AddClassHandler<MatNav>((nav, _) => nav.Refresh());
        FootProperty.Changed.AddClassHandler<MatNav>((nav, e) => nav._footHost.Content = e.NewValue);
    }

    /// <summary>An empty menu: add <see cref="Items"/> and <see cref="Settings"/>.</summary>
    public MatNav()
    {
        BorderThickness = new Thickness(0, 0, 1, 0);
        this.Use(BackgroundProperty, "Mat.NavBg");
        this.Use(BorderBrushProperty, "Mat.Border");
        _line.Use(Border.BackgroundProperty, "Mat.Border");
        _version.Themed("MatButton.Link");
        _update.Themed("MatButton.Link");
        _fold.Themed("MatButton.Icon");
        _version.Content = _versionText;
        _update.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { new MatIcon { Icon = "download", Size = 16 }, _updateText },
        };
        _version.Click += (_, _) => AboutRequested?.Invoke(this, EventArgs.Empty);
        _update.Click += (_, _) => UpdateRequested?.Invoke(this, EventArgs.Empty);
        _fold.Click += (_, _) => IsFolded = !IsFolded;
        Grid.SetColumn(_fold, 1);
        _foot.Children.Add(_version);
        _foot.Children.Add(_fold);

        var top = new StackPanel { Children = { _items, _line, _settingsHost } };
        var bottom = new StackPanel { Children = { _footHost, _update, _foot } };
        var dock = new DockPanel { Margin = new Thickness(12, 16, 12, 12) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(top);
        Child = dock;

        Items.CollectionChanged += (_, _) => RebuildItems();
        Refresh();
    }

    /// <summary>An item was chosen (by click, Enter or Space); it is the current one now.</summary>
    public event EventHandler<MatNavItem>? ItemInvoked;

    /// <summary>The version at the foot was chosen: open About (a page of the main window).</summary>
    public event EventHandler? AboutRequested;

    /// <summary>The link of the waiting update was chosen (<see cref="UpdateVersion"/>): show where it is installed (About or the settings of updates).</summary>
    public event EventHandler? UpdateRequested;

    /// <summary>
    /// What the program shows at the foot of the menu above the version: a card with a setting, the state of a connection. It stays while the menu is folded; the program shows
    /// the part of it that fits a rail of 68 px (<see cref="FoldChanged"/>).
    /// </summary>
    public Control? Foot
    {
        get => GetValue(FootProperty);
        set => SetValue(FootProperty, value);
    }

    /// <summary>
    /// The version of an update that waits (SETUP-06), or null. While it is set a link with the download icon stands above the version (<c>Update to 1.5.0</c>; folded the icon with
    /// the words as its tooltip) and raises <see cref="UpdateRequested"/>.
    /// </summary>
    public string? UpdateVersion
    {
        get => GetValue(UpdateVersionProperty);
        set => SetValue(UpdateVersionProperty, value);
    }

    /// <summary>The shortcut that folds and opens the menu (<c>Ctrl+B</c>, on macOS with the command key), named in the tooltip of the fold button (DESK-07); the program handles the key.</summary>
    public string? FoldShortcut
    {
        get => GetValue(FoldShortcutProperty);
        set => SetValue(FoldShortcutProperty, value);
    }

    /// <summary>The menu was folded or opened (the value is <see cref="IsFolded"/>): remember it for the next start.</summary>
    public event EventHandler<bool>? FoldChanged;

    /// <summary>The items of the menu above the line, in their order.</summary>
    public AvaloniaList<MatNavItem> Items { get; } = [];

    /// <summary>The item Settings below the line, or null (DESK-04: Settings is an item of its own below the others).</summary>
    public MatNavItem? Settings
    {
        get => _settings;
        set
        {
            if (_settings is not null)
            {
                _settings.Click -= OnItemClick;
            }

            _settings = value;
            if (_settings is not null)
            {
                _settings.Click += OnItemClick;
                _settings.IsFolded = IsFolded;
            }

            _settingsHost.Content = _settings;
            _line.IsVisible = _settings is not null && Items.Count > 0;
        }
    }

    /// <summary>The title bar whose brand follows the width of the menu (its middle stands over the page) and shows only the logo while the menu is folded.</summary>
    public MatTitleBar? TitleBar { get; set; }

    /// <summary>True while the menu is folded to a rail.</summary>
    public bool IsFolded
    {
        get => GetValue(IsFoldedProperty);
        set => SetValue(IsFoldedProperty, value);
    }

    /// <summary>The name of the program.</summary>
    public string AppName
    {
        get => GetValue(AppNameProperty);
        set => SetValue(AppNameProperty, value);
    }

    /// <summary>The version number.</summary>
    public string Version
    {
        get => GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    /// <summary>The channel.</summary>
    public string Channel
    {
        get => GetValue(ChannelProperty);
        set => SetValue(ChannelProperty, value);
    }

    /// <summary>The button with the version (for tests).</summary>
    public Button VersionButton => _version;

    /// <summary>The button that folds and opens the menu (for tests).</summary>
    public Button FoldButton => _fold;

    /// <summary>The link of a waiting update (for tests).</summary>
    public Button UpdateButton => _update;

    /// <summary>The text of the version as it is shown, <c>Version 1.4.2 · release</c> (trimmed with an ellipsis where the menu is too narrow; the tooltip has all of it).</summary>
    public string VersionText => MatVersion.Format(Version, Channel);

    /// <summary>The current item, or null.</summary>
    public MatNavItem? Current => Items.Append(_settings).FirstOrDefault(i => i?.IsCurrent == true);

    /// <summary>Makes <paramref name="item"/> the current item (the others are not).</summary>
    public void Select(MatNavItem? item)
    {
        foreach (var each in Items.Append(_settings).OfType<MatNavItem>())
        {
            each.IsCurrent = each == item;
        }
    }

    /// <summary>The width of the menu for a window of <paramref name="windowWidth"/>: the rail when folded, 210 in a narrow window, 248 otherwise.</summary>
    public static double WidthFor(bool folded, double windowWidth) => folded ? RailWidth : windowWidth < NarrowWindow ? NarrowWidth : OpenWidth;

    /// <summary>Sets the width for a window of this width (the menu follows its window by itself once it is in one).</summary>
    public void ApplyWindowWidth(double windowWidth)
    {
        _windowWidth = windowWidth;
        Width = WidthFor(IsFolded, windowWidth);
        if (TitleBar is not null)
        {
            TitleBar.BrandWidth = Width;
            TitleBar.IsNameVisible = !IsFolded || OperatingSystem.IsMacOS();
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            _topLevel.SizeChanged += OnTopLevelSizeChanged;
            ApplyWindowWidth(_topLevel.ClientSize.Width > 0 ? _topLevel.ClientSize.Width : _windowWidth);
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel is not null)
        {
            _topLevel.SizeChanged -= OnTopLevelSizeChanged;
            _topLevel = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnTopLevelSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyWindowWidth(e.NewSize.Width);

    private void OnFolded(AvaloniaPropertyChangedEventArgs e)
    {
        Refresh();
        if (!Equals(e.OldValue, e.NewValue))
        {
            FoldChanged?.Invoke(this, IsFolded);
        }
    }

    private void RebuildItems()
    {
        foreach (var item in _items.Children.OfType<MatNavItem>())
        {
            item.Click -= OnItemClick;
        }

        _items.Children.Clear();
        foreach (var item in Items)
        {
            item.Click += OnItemClick;
            item.IsFolded = IsFolded;
            _items.Children.Add(item);
        }

        _line.IsVisible = _settings is not null && Items.Count > 0;
    }

    private void OnItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is MatNavItem item)
        {
            Select(item);
            ItemInvoked?.Invoke(this, item);
        }
    }

    /// <summary>The foot: open, the version and the fold button side by side; folded, an info icon for About above the button that opens the menu.</summary>
    private void Refresh()
    {
        var folded = IsFolded;
        foreach (var item in Items.Append(_settings).OfType<MatNavItem>())
        {
            item.IsFolded = folded;
        }

        // the version: a long one (local-20261115 · local) is trimmed with an ellipsis, and the tooltip always has all of it
        var version = VersionText;
        var about = MatText.Format("nav.about", AppName);
        _versionText.Text = version;
        _version.Content = folded ? new MatIcon { Icon = "info", Size = 18 } : _versionText;
        ToolTip.SetTip(_version, $"{about} – {version}");
        AutomationProperties.SetName(_version, folded ? about : version);
        _version.HorizontalAlignment = folded ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        _version.Margin = folded ? new Thickness(0) : new Thickness(8, 0, 0, 0);
        Grid.SetColumnSpan(_version, folded ? 2 : 1);

        // an update that waits: a link above the version (the icon alone on the rail)
        var update = string.IsNullOrWhiteSpace(UpdateVersion) ? null : UpdateVersion.Trim();
        _update.IsVisible = update is not null;
        _updateText.Text = update is null ? string.Empty : MatText.Format("nav.update", update);
        _updateText.IsVisible = !folded;
        var available = update is null ? null : MatText.Format("nav.updateAvailable", update);
        ToolTip.SetTip(_update, available);
        AutomationProperties.SetName(_update, available ?? string.Empty);
        _update.HorizontalAlignment = folded ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        _update.Margin = folded ? new Thickness(0, 0, 0, 6) : new Thickness(8, 0, 0, 6);

        _fold.Content = new MatIcon { Icon = "sidebar", Size = 18 };
        var action = MatText.Get(folded ? "nav.unfold" : "nav.fold");
        ToolTip.SetTip(_fold, string.IsNullOrWhiteSpace(FoldShortcut) ? action : $"{action} ({FoldShortcut.Trim()})");
        AutomationProperties.SetName(_fold, action);
        Grid.SetRow(_fold, folded ? 1 : 0);
        Grid.SetColumn(_fold, folded ? 0 : 1);
        Grid.SetColumnSpan(_fold, folded ? 2 : 1);
        _fold.HorizontalAlignment = folded ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        _fold.Margin = folded ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        ApplyWindowWidth(_windowWidth);
    }
}
