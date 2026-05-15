using FATX.Analyzers;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace FATXTools.Wpf;

public partial class SettingsWindow : Window
{
    private static readonly BoolOption[] BoolOptions = [new(true), new(false)];

    private static readonly IntervalOption[] IntervalOptions =
    [
        new("Byte", "0x1", FileCarverInterval.Byte),
        new("Align", "0x10", FileCarverInterval.Align),
        new("Sector", "0x200", FileCarverInterval.Sector),
        new("Page", "0x1000", FileCarverInterval.Page),
        new("Cluster", "0x4000", FileCarverInterval.Cluster),
    ];

    private readonly AppSettings _settings;
    private readonly WorkerOption[] _workerOptions;
    private readonly ThemeOption[] _themeOptions = [new(WpfTheme.Dark), new(WpfTheme.Light)];
    private readonly ScanProfileOption[] _scanProfileOptions =
    [
        new(ScanProfile.Balanced, "Balanced", "custom signatures and bounded XVD/XVC nested scan"),
        new(ScanProfile.Fast, "Fast", "built-in signatures only; no nested container scan"),
        new(ScanProfile.Exhaustive, "Exhaustive", "custom signatures and deeper XVD/XVC nested scan")
    ];

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings.Clone();
        _workerOptions = Enumerable.Range(1, Math.Max(1, Environment.ProcessorCount))
            .Select(count => new WorkerOption(count))
            .ToArray();

        ScanProfileCombo.ItemsSource = _scanProfileOptions;
        ScanProfileCombo.SelectedItem = _scanProfileOptions.FirstOrDefault(option => option.Value == _settings.ScanProfile)
            ?? _scanProfileOptions.First(option => option.Value == ScanProfile.Balanced);
        FileCarverIntervalCombo.ItemsSource = IntervalOptions;
        FileCarverIntervalCombo.SelectedItem = IntervalOptions.FirstOrDefault(option => option.Value == _settings.FileCarverInterval)
            ?? IntervalOptions.First(option => option.Value == FileCarverInterval.Sector);
        MetadataIntervalTextBox.Text = Math.Max(1, _settings.MetadataIntervalClusters).ToString();
        MetadataWorkersCombo.ItemsSource = _workerOptions;
        MetadataWorkersCombo.SelectedItem = _workerOptions.FirstOrDefault(option => option.Count == ClampWorkerCount(_settings.MetadataParallelWorkers))
            ?? _workerOptions[0];
        ThemeCombo.ItemsSource = _themeOptions;
        ThemeCombo.SelectedItem = _themeOptions.First(option => option.Name == WpfTheme.NormalizeName(_settings.Theme));
        ZeroFillOverwrittenRecoveryClustersCombo.ItemsSource = BoolOptions;
        ZeroFillOverwrittenRecoveryClustersCombo.SelectedItem = BoolOptions.First(option => option.Value == _settings.ZeroFillOverwrittenRecoveryClusters);
        EnableFileLoggingCombo.ItemsSource = BoolOptions;
        EnableFileLoggingCombo.SelectedItem = BoolOptions.First(option => option.Value == _settings.EnableFileLogging);
        LogFileTextBox.Text = _settings.LogFile;
        CustomCarversTextBox.Text = AppSettings.NormalizeCustomCarversFile(_settings.CustomCarversFile);
        ResetShortcutRows(ShortcutCatalog.Normalize(_settings.Shortcuts));
        ApplyWindowStatePadding();
#if DEBUG
        Loaded += SettingsWindow_Loaded;
#endif
    }

    public AppSettings Result => _settings;

    public ObservableCollection<ShortcutEditorRow> ShortcutRows { get; } = new();

    private void BrowseLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Log files (*.txt;*.log)|*.txt;*.log|All files (*.*)|*.*",
            FileName = LogFileTextBox.Text
        };

        if (dialog.ShowDialog(this) == true)
        {
            LogFileTextBox.Text = dialog.FileName;
        }
    }

    private void BrowseCustomCarvers_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = CustomCarversTextBox.Text
        };

        if (dialog.ShowDialog(this) == true)
        {
            CustomCarversTextBox.Text = dialog.FileName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MetadataIntervalTextBox.Text, out var metadataInterval) || metadataInterval < 1)
        {
            MessageBox.Show(this, "Metadata interval must be a positive whole number.", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.FileCarverInterval = FileCarverIntervalCombo.SelectedItem is IntervalOption option
            ? option.Value
            : FileCarverInterval.Sector;
        _settings.ScanProfile = ScanProfileCombo.SelectedItem is ScanProfileOption profileOption
            ? profileOption.Value
            : ScanProfile.Balanced;
        _settings.MetadataIntervalClusters = metadataInterval;
        _settings.MetadataParallelWorkers = MetadataWorkersCombo.SelectedItem is WorkerOption workerOption
            ? ClampWorkerCount(workerOption.Count)
            : ClampWorkerCount(_settings.MetadataParallelWorkers);
        _settings.Theme = ThemeCombo.SelectedItem is ThemeOption themeOption
            ? WpfTheme.NormalizeName(themeOption.Name)
            : WpfTheme.Dark;
        _settings.ZeroFillOverwrittenRecoveryClusters = ZeroFillOverwrittenRecoveryClustersCombo.SelectedItem is BoolOption zeroFillOption
            ? zeroFillOption.Value
            : _settings.ZeroFillOverwrittenRecoveryClusters;
        _settings.EnableFileLogging = EnableFileLoggingCombo.SelectedItem is BoolOption loggingOption
            ? loggingOption.Value
            : _settings.EnableFileLogging;
        _settings.LogFile = LogFileTextBox.Text.Trim();
        _settings.CustomCarversFile = AppSettings.NormalizeCustomCarversFile(CustomCarversTextBox.Text);
        if (!TrySaveShortcuts())
        {
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void RestoreDefaultShortcuts_Click(object sender, RoutedEventArgs e)
    {
        ResetShortcutRows(ShortcutCatalog.DefaultMap());
    }

    private void ShortcutTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (e.Key is Key.Back or Key.Delete)
        {
            textBox.Text = string.Empty;
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        var modifiers = Keyboard.Modifiers;
        try
        {
            var gesture = new KeyGesture(key, modifiers);
            textBox.Text = ShortcutCatalog.FormatGesture(gesture);
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
        catch (NotSupportedException)
        {
            e.Handled = true;
        }
    }

    private void ShortcutTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.Dispatcher.BeginInvoke(new Action(textBox.SelectAll), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void ShortcutTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox textBox && !textBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            textBox.Focus();
        }
    }

    private void ResetShortcutRows(System.Collections.Generic.IDictionary<string, string> shortcuts)
    {
        ShortcutRows.Clear();
        foreach (var definition in ShortcutCatalog.All)
        {
            ShortcutRows.Add(new ShortcutEditorRow(
                definition.Id,
                definition.Category,
                definition.Name,
                ShortcutCatalog.GetGestureText(shortcuts, definition.Id)));
        }
    }

    private bool TrySaveShortcuts()
    {
        foreach (var row in ShortcutRows)
        {
            if (!ShortcutCatalog.TryParseGesture(row.Gesture, out var gesture))
            {
                MessageBox.Show(this, $"Shortcut '{row.Gesture}' for '{row.Name}' is not valid.", "Settings",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            row.Gesture = gesture == null ? string.Empty : ShortcutCatalog.FormatGesture(gesture);
        }

        var duplicates = ShortcutRows
            .Where(row => !string.IsNullOrWhiteSpace(row.Gesture))
            .GroupBy(row => row.Gesture.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicates != null)
        {
            MessageBox.Show(this, $"Shortcut '{duplicates.Key}' is assigned to more than one action.", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _settings.Shortcuts = ShortcutRows.ToDictionary(row => row.Id, row => row.Gesture.Trim(), StringComparer.OrdinalIgnoreCase);
        return true;
    }

    private static int ClampWorkerCount(int count)
    {
        return Math.Clamp(count, 1, Math.Max(1, Environment.ProcessorCount));
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowMaximized();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // DragMove can throw if the mouse state changes during the drag.
            }
        }
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreWindow_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowMaximized();
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleWindowMaximized()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        ApplyWindowStatePadding();
    }

    private void ApplyWindowStatePadding()
    {
        RootShell.Margin = WindowState == WindowState.Maximized
            ? new Thickness(6)
            : new Thickness(0);
    }

#if DEBUG
    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_debugInsetOverlayApplied)
        {
            return;
        }

        _debugInsetOverlayApplied = true;
        AddInsetDebugOverlay(ScanProfileCombo);
        AddInsetDebugOverlay(MetadataWorkersCombo);
        AddInsetDebugOverlay(MetadataIntervalTextBox);
        AddInsetDebugOverlay(FileCarverIntervalCombo);
        AddInsetDebugOverlay(ThemeCombo);
        AddInsetDebugOverlay(ZeroFillOverwrittenRecoveryClustersCombo);
        AddInsetDebugOverlay(EnableFileLoggingCombo);
        AddInsetDebugOverlay(LogFileTextBox);
        AddInsetDebugOverlay(CustomCarversTextBox);
    }

    private static void AddInsetDebugOverlay(Control control)
    {
        var layer = AdornerLayer.GetAdornerLayer(control);
        if (layer == null)
        {
            return;
        }

        layer.Add(new FieldInsetDebugAdorner(control));
    }
#endif

    private static double MeasureTextInset(Control control)
    {
        if (control is TextBox textBox)
        {
            var marker = FindTextBoxMarker(textBox);
            if (marker == null)
            {
                return 0;
            }

            try
            {
                var topLeft = marker.TransformToAncestor(textBox).Transform(new Point(0, 0));
                var textBoxViewMargin = string.Equals(marker.GetType().Name, "TextBoxView", StringComparison.Ordinal)
                    ? ((FrameworkElement)marker).Margin.Left
                    : 0;
                return Math.Round(Math.Max(0, topLeft.X + textBoxViewMargin), 2);
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }

        var comboMarker = control is ComboBox comboBox ? FindComboTextMarker(comboBox) : null;
        if (comboMarker == null)
        {
            return 0;
        }

        try
        {
            var topLeft = comboMarker.TransformToAncestor(control).Transform(new Point(0, 0));
            return Math.Round(Math.Max(0, topLeft.X), 2);
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static FrameworkElement? FindTextBoxMarker(TextBox textBox)
    {
        var textBoxView = FindVisualDescendant<FrameworkElement>(textBox, element => string.Equals(element.GetType().Name, "TextBoxView", StringComparison.Ordinal));
        if (textBoxView != null)
        {
            return textBoxView;
        }

        return textBox.Template?.FindName("PART_ContentHost", textBox) as FrameworkElement;
    }

    private static FrameworkElement? FindComboTextMarker(ComboBox comboBox)
    {
        var toggleButton = FindVisualDescendant<ToggleButton>(comboBox);
        if (toggleButton == null)
        {
            return null;
        }

        return FindVisualDescendant<TextBlock>(toggleButton, textBlock => textBlock.TextTrimming == TextTrimming.CharacterEllipsis);
    }

    private static T? FindVisualDescendant<T>(DependencyObject root, Func<T, bool>? match = null)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && (match == null || match(typed)))
            {
                return typed;
            }

            var nested = FindVisualDescendant(child, match);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    internal Dictionary<string, double> CollectInsetMeasurements()
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["combo.scan_profile"] = MeasureTextInset(ScanProfileCombo),
            ["combo.metadata_workers"] = MeasureTextInset(MetadataWorkersCombo),
            ["combo.file_carver_interval"] = MeasureTextInset(FileCarverIntervalCombo),
            ["combo.theme"] = MeasureTextInset(ThemeCombo),
            ["combo.zero_fill"] = MeasureTextInset(ZeroFillOverwrittenRecoveryClustersCombo),
            ["combo.log_to_file"] = MeasureTextInset(EnableFileLoggingCombo),
            ["textbox.metadata_interval_clusters"] = MeasureTextInset(MetadataIntervalTextBox),
            ["textbox.log_file"] = MeasureTextInset(LogFileTextBox),
            ["textbox.custom_carvers_file"] = MeasureTextInset(CustomCarversTextBox)
        };

        return result;
    }

#if DEBUG
    private bool _debugInsetOverlayApplied;

    private sealed class FieldInsetDebugAdorner : Adorner
    {
        public FieldInsetDebugAdorner(Control adornedElement)
            : base(adornedElement)
        {
            IsHitTestVisible = false;
            adornedElement.LayoutUpdated += (_, _) => InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (AdornedElement is not Control control || control.ActualWidth <= 1 || control.ActualHeight <= 1)
            {
                return;
            }

            Point controlTopLeft;
            try
            {
                controlTopLeft = control.TranslatePoint(new Point(0, 0), this);
            }
            catch (InvalidOperationException)
            {
                return;
            }

            var insetPx = Math.Min(control.ActualWidth - 1, MeasureTextInset(control));
            var y = controlTopLeft.Y + Math.Max(3, control.ActualHeight - 7);
            var lineStartX = controlTopLeft.X;
            var lineEndX = controlTopLeft.X + insetPx;
            var linePen = new Pen(Brushes.Red, 1);
            drawingContext.DrawLine(linePen, new Point(lineStartX, y), new Point(lineEndX, y));

            var label = $"{insetPx:0.##} px";
            var formattedText = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas"),
                10,
                Brushes.Red,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            drawingContext.DrawText(formattedText, new Point(lineStartX + 2, Math.Max(0, y - formattedText.Height - 1)));
        }
    }

#endif
}

public sealed record IntervalOption(string Name, string SizeText, FileCarverInterval Value)
{
    public override string ToString()
    {
        return $"{Name} ({SizeText})";
    }
}

public sealed record ScanProfileOption(ScanProfile Value, string Name, string Detail)
{
    public override string ToString()
    {
        return Name;
    }
}

public sealed record WorkerOption(int Count)
{
    public string Name => Count == 1 ? "1 worker" : $"{Count} workers";

    public string Detail => Count == Environment.ProcessorCount
        ? "uses all logical processors"
        : $"leaves {Environment.ProcessorCount - Count} processor(s) free";

    public override string ToString()
    {
        return Name;
    }
}

public sealed record ThemeOption(string Name)
{
    public override string ToString()
    {
        return Name;
    }
}

public sealed record BoolOption(bool Value)
{
    public override string ToString()
    {
        return Value ? "True" : "False";
    }
}

public sealed class ShortcutEditorRow : INotifyPropertyChanged
{
    private string _gesture;

    public ShortcutEditorRow(string id, string category, string name, string gesture)
    {
        Id = id;
        Category = category;
        Name = name;
        _gesture = gesture;
    }

    public string Id { get; }

    public string Category { get; }

    public string Name { get; }

    public string Gesture
    {
        get => _gesture;
        set
        {
            if (string.Equals(_gesture, value, StringComparison.Ordinal))
            {
                return;
            }

            _gesture = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
