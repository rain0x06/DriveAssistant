using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DriveAssistant.Avalonia.Core;
using DriveAssistant.Avalonia.ViewModels;
using FATXTools.Utilities;
using System.Globalization;

namespace DriveAssistant.Avalonia;

public sealed partial class MainWindow : Window
{
    private GridLength _partitionsPanelWidth = new(280);
    private GridLength _inspectorPanelWidth = new(320);
    private DetachedResultsWindow? _detachedResultsWindow;
    private readonly AppSettings _settings;
    private bool _closingMainWindow;

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;
    private ColumnDefinition PartitionsColumn => ContentGrid.ColumnDefinitions[0];
    private ColumnDefinition PartitionsSplitterColumn => ContentGrid.ColumnDefinitions[1];
    private ColumnDefinition InspectorSplitterColumn => ContentGrid.ColumnDefinitions[3];
    private ColumnDefinition InspectorColumn => ContentGrid.ColumnDefinitions[4];

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        DataContext = new MainWindowViewModel();
        ViewModel.KeyPath = _settings.KeyPath;
        ApplyWindowStatePadding();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled)
        {
            return;
        }

        if (TryExecuteShortcut(e))
        {
            e.Handled = true;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty)
        {
            ApplyWindowStatePadding();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closingMainWindow = true;
        if (_detachedResultsWindow != null)
        {
            DockResultsWindow();
        }

        ViewModel.Dispose();
        base.OnClosed(e);
    }

    private async void OpenImage_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Drive Assistant Image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Drive images")
                {
                    Patterns = ["*.img", "*.bin", "*.raw", "*.iso", "*.cso", "*.rvz", "*.wbfs", "*.vpk", "*.zip", "*.imgc", "*.nca", "*.xci", "*.nsp", "*.pbp", "*.gdi", "*.cue", "*.cdi", "*.cim", "*.sav", "*.dsk", "*.hdd", "*.nand"]
                },
                FilePickerFileTypes.All
            ]
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await ViewModel.OpenImageAsync(path);
        }
    }

    private async void SetKeyPath_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select optional key folder",
            AllowMultiple = false
        });

        ViewModel.KeyPath = folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private async void SetKeyFile_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select optional key file",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });

        ViewModel.KeyPath = files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async void RebuildFatxImageIncludeDeleted_Click(object? sender, RoutedEventArgs e)
    {
        await RebuildFatxImageFromJsonAsync(includeDeletedEntries: true);
    }

    private async void RebuildFatxImageExcludeDeleted_Click(object? sender, RoutedEventArgs e)
    {
        await RebuildFatxImageFromJsonAsync(includeDeletedEntries: false);
    }

    private async Task RebuildFatxImageFromJsonAsync(bool includeDeletedEntries)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        var snapshotFiles = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Drive Assistant database JSON",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Drive Assistant Database") { Patterns = ["*.json"] },
                FilePickerFileTypes.All
            ]
        });
        var snapshotPath = snapshotFiles.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(snapshotPath))
        {
            return;
        }

        var liveFolders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Optional: select folder with non-deleted files (cancel to skip)",
            AllowMultiple = false
        });
        var liveDirectory = liveFolders.FirstOrDefault()?.TryGetLocalPath() ?? string.Empty;

        var deletedFolders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Optional: select folder with deleted files (cancel to skip)",
            AllowMultiple = false
        });
        var deletedDirectory = deletedFolders.FirstOrDefault()?.TryGetLocalPath() ?? string.Empty;

        var suggestedName = ViewModel.SelectedPartition != null
            ? $"partition-{ViewModel.SelectedPartition.Index}-rebuilt.img"
            : "rebuilt-fatx.img";
        var outputFile = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save rebuilt FATX image",
            SuggestedFileName = suggestedName,
            FileTypeChoices =
            [
                new FilePickerFileType("Raw image") { Patterns = ["*.img"] },
                FilePickerFileTypes.All
            ]
        });
        var outputPath = outputFile?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        ViewModel.StatusText = "Rebuilding FATX image from JSON...";

        var args = new List<string>
        {
            "--output",
            outputPath,
            "--include-deleted",
            includeDeletedEntries ? "true" : "false",
            snapshotPath
        };

        if (!string.IsNullOrWhiteSpace(liveDirectory))
        {
            args.Add("--live-files");
            args.Add(liveDirectory);
        }

        if (!string.IsNullOrWhiteSpace(deletedDirectory))
        {
            args.Add("--deleted-files");
            args.Add(deletedDirectory);
        }

        if (ViewModel.SelectedPartition != null)
        {
            args.Add("--partition");
            args.Add(ViewModel.SelectedPartition.Index.ToString(CultureInfo.InvariantCulture));
        }

        using var rebuildCancellation = new CancellationTokenSource();
        var pauseFlag = 0;
        var progressDialog = new RebuildProgressDialog();
        progressDialog.CancelRequested += (_, _) =>
        {
            if (!rebuildCancellation.IsCancellationRequested)
            {
                rebuildCancellation.Cancel();
                ViewModel.StatusText = "Canceling FATX rebuild...";
            }
        };
        progressDialog.PauseChanged += (_, isPaused) =>
        {
            Interlocked.Exchange(ref pauseFlag, isPaused ? 1 : 0);
            ViewModel.StatusText = isPaused ? "FATX rebuild paused" : "Rebuilding FATX image from JSON...";
        };
        var progressDialogTask = progressDialog.ShowDialog(this);

        try
        {
            await Task.Run(() =>
            {
                var exitCode = DriveAssistant.Cli.FatxImageRebuildCommand.Run(args.ToArray(), new DriveAssistant.Cli.RebuildExecutionOptions
                {
                    CancellationToken = rebuildCancellation.Token,
                    IsPaused = () => Volatile.Read(ref pauseFlag) == 1,
                    PayloadWorkerCount = Math.Max(1, Environment.ProcessorCount - 1),
                    Progress = snapshot =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            progressDialog.Update(snapshot.Percent, snapshot.Stage, snapshot.Detail);
                            ViewModel.StatusText = Volatile.Read(ref pauseFlag) == 1
                                ? "FATX rebuild paused"
                                : $"Rebuilding FATX image from JSON... {snapshot.Percent:0}% - {snapshot.Stage}: {snapshot.Detail}";
                        });
                    }
                });
                if (exitCode != 0)
                {
                    if (rebuildCancellation.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(rebuildCancellation.Token);
                    }

                    throw new InvalidOperationException("FATX rebuild command failed. Check the selected JSON and source folders.");
                }
            }, rebuildCancellation.Token);

            ViewModel.StatusText = "Ready";
            await TextDialog.ShowAsync(this, "Rebuild FATX", $"Rebuilt FATX image:\n{outputPath}");
        }
        catch (OperationCanceledException)
        {
            ViewModel.StatusText = "Rebuild canceled";
            await TextDialog.ShowAsync(this, "Rebuild FATX canceled", "Rebuild canceled by user.");
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = "Rebuild failed";
            await TextDialog.ShowAsync(this, "Rebuild FATX failed", ex.Message);
        }
        finally
        {
            progressDialog.Close();
            await progressDialogTask;
        }
    }

    private async void SaveSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedFile == null)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        if (ViewModel.SelectedFile.IsDirectory)
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Export selected folder",
                AllowMultiple = false
            });
            var folderPath = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                await ViewModel.ExportSelectedAsync(Path.Combine(folderPath, ViewModel.SelectedFile.Name));
            }

            return;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export selected file",
            SuggestedFileName = ViewModel.SelectedFile.Name
        });
        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await ViewModel.ExportSelectedAsync(path);
        }
    }

    private void Search_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.FindNext();
    }

    private void MenuButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowMenuFlyout(sender);
    }

    private void MenuButton_PointerEntered(object? sender, PointerEventArgs e)
    {
        ShowMenuFlyout(sender);
    }

    private static void ShowMenuFlyout(object? sender)
    {
        if (sender is Control control)
        {
            FlyoutBase.ShowAttachedFlyout(control);
        }
    }

    private async void MetadataScan_Click(object? sender, RoutedEventArgs e)
    {
        await ViewModel.RunMetadataScanAsync();
    }

    private async void FileCarver_Click(object? sender, RoutedEventArgs e)
    {
        await ViewModel.RunFileCarverAsync();
    }

    private void CancelOperation_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.CancelOperation();
    }

    private void SearchResults_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.FindNext();
    }

    private void TogglePartitions_Click(object? sender, RoutedEventArgs e)
    {
        if (PartitionsPanel.IsVisible)
        {
            ClosePartitions_Click(sender, e);
        }
        else
        {
            RestorePartitions_Click(sender, e);
        }
    }

    private void ToggleInspector_Click(object? sender, RoutedEventArgs e)
    {
        if (InspectorPanel.IsVisible)
        {
            CloseInspector_Click(sender, e);
        }
        else
        {
            RestoreInspector_Click(sender, e);
        }
    }

    private void MinimizePartitions_Click(object? sender, RoutedEventArgs e)
    {
        SavePanelWidth(PartitionsColumn, ref _partitionsPanelWidth, 280);
        PartitionsPanel.IsVisible = false;
        PartitionsRail.IsVisible = true;
        ShowPartitionsButton.IsVisible = false;
        PartitionsColumn.Width = new GridLength(44);
        PartitionsSplitter.IsVisible = false;
        PartitionsSplitterColumn.Width = new GridLength(0);
    }

    private void ClosePartitions_Click(object? sender, RoutedEventArgs e)
    {
        SavePanelWidth(PartitionsColumn, ref _partitionsPanelWidth, 280);
        PartitionsPanel.IsVisible = false;
        PartitionsRail.IsVisible = false;
        ShowPartitionsButton.IsVisible = true;
        PartitionsColumn.Width = new GridLength(0);
        PartitionsSplitter.IsVisible = false;
        PartitionsSplitterColumn.Width = new GridLength(0);
    }

    private void RestorePartitions_Click(object? sender, RoutedEventArgs e)
    {
        PartitionsPanel.IsVisible = true;
        PartitionsRail.IsVisible = false;
        ShowPartitionsButton.IsVisible = false;
        PartitionsColumn.Width = EnsureUsableWidth(_partitionsPanelWidth, 280);
        PartitionsSplitter.IsVisible = true;
        PartitionsSplitterColumn.Width = new GridLength(6);
    }

    private void MinimizeInspector_Click(object? sender, RoutedEventArgs e)
    {
        SavePanelWidth(InspectorColumn, ref _inspectorPanelWidth, 320);
        InspectorPanel.IsVisible = false;
        InspectorRail.IsVisible = true;
        ShowInspectorButton.IsVisible = false;
        InspectorColumn.Width = new GridLength(44);
        InspectorSplitter.IsVisible = false;
        InspectorSplitterColumn.Width = new GridLength(0);
    }

    private void CloseInspector_Click(object? sender, RoutedEventArgs e)
    {
        SavePanelWidth(InspectorColumn, ref _inspectorPanelWidth, 320);
        InspectorPanel.IsVisible = false;
        InspectorRail.IsVisible = false;
        ShowInspectorButton.IsVisible = true;
        InspectorColumn.Width = new GridLength(0);
        InspectorSplitter.IsVisible = false;
        InspectorSplitterColumn.Width = new GridLength(0);
    }

    private void RestoreInspector_Click(object? sender, RoutedEventArgs e)
    {
        InspectorPanel.IsVisible = true;
        InspectorRail.IsVisible = false;
        ShowInspectorButton.IsVisible = false;
        InspectorColumn.Width = EnsureUsableWidth(_inspectorPanelWidth, 320);
        InspectorSplitter.IsVisible = true;
        InspectorSplitterColumn.Width = new GridLength(6);
    }

    private void ToggleResultsWindow_Click(object? sender, RoutedEventArgs e)
    {
        if (_detachedResultsWindow == null)
        {
            DetachResultsWindow();
        }
        else
        {
            DockResultsWindow();
        }
    }

    private void DetachResultsWindow()
    {
        if (_detachedResultsWindow != null)
        {
            _detachedResultsWindow.Activate();
            return;
        }

        _detachedResultsWindow = new DetachedResultsWindow
        {
            DataContext = ViewModel
        };
        _detachedResultsWindow.DockRequested += DetachedResultsWindow_DockRequested;
        _detachedResultsWindow.Closing += DetachedResultsWindow_Closing;

        RecoveryTab.IsVisible = false;
        CarverTab.IsVisible = false;
        ResultsTabs.SelectedItem = LogTab;
        ToggleResultsWindowText.Text = "Dock";
        ResultsPanelDetachText.Text = "Dock";

        _detachedResultsWindow.Show(this);
    }

    private void DetachedResultsWindow_DockRequested(object? sender, EventArgs e)
    {
        DockResultsWindow();
    }

    private void DetachedResultsWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingMainWindow)
        {
            return;
        }

        e.Cancel = true;
        DockResultsWindow();
    }

    private void DockResultsWindow()
    {
        if (_detachedResultsWindow == null)
        {
            return;
        }

        var window = _detachedResultsWindow;
        window.DockRequested -= DetachedResultsWindow_DockRequested;
        window.Closing -= DetachedResultsWindow_Closing;

        RecoveryTab.IsVisible = true;
        CarverTab.IsVisible = true;
        ResultsTabs.SelectedItem = RecoveryTab;
        ToggleResultsWindowText.Text = "Pop Out";
        ResultsPanelDetachText.Text = "Pop Out";
        _detachedResultsWindow = null;
        window.Close();
    }

    private static void SavePanelWidth(ColumnDefinition column, ref GridLength savedWidth, double fallback)
    {
        savedWidth = column.Width.Value >= 80
            ? new GridLength(column.Width.Value)
            : new GridLength(fallback);
    }

    private static GridLength EnsureUsableWidth(GridLength width, double fallback)
    {
        return width.Value >= 80 ? width : new GridLength(fallback);
    }

    private void UnmountPartition_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.UnmountSelectedPartition();
    }

    private async void AddCustomPartition_Click(object? sender, RoutedEventArgs e)
    {
        var request = await CustomPartitionDialog.ShowAsync(this);
        if (request == null)
        {
            return;
        }

        ViewModel.AddCustomPartition(request.Name, request.Offset, request.Length);
    }

    private void Files_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel.SelectedFile?.IsDirectory != true)
        {
            return;
        }

        var node = new DirectoryNodeViewModel(ViewModel.SelectedFile.Entry);
        ViewModel.SelectedDirectory = node;
    }

    private async void Settings_Click(object? sender, RoutedEventArgs e)
    {
        var result = await SettingsWindow.ShowAsync(this, _settings);
        if (result == null)
        {
            return;
        }

        ViewModel.KeyPath = result.KeyPath;
        ViewModel.StatusText = "Settings saved.";
    }

    private async void About_Click(object? sender, RoutedEventArgs e)
    {
        await TextDialog.ShowAsync(
            this,
            "About Drive Assistant",
            "Drive Assistant is a read-only disk recovery and storage inspection tool for HDD, SSD, flash, and console drive images.\n\n" +
            "Current focus: practical browsing, metadata recovery, file carving, and safe export workflows for general filesystems plus Xbox, PlayStation, and Nintendo storage.\n\n" +
            "Project: https://github.com/rain0x06/DriveAssistant\n" +
            "Original FATXTools codebase: https://github.com/aerosoul94/FATXTools\n\n" +
            $"Version: {BuildInfo.Version}\n" +
            $"Commit: {BuildInfo.CommitHash}\n" +
            $"Build date: {BuildInfo.BuildDate:yyyy-MM-dd HH:mm:ss}");
    }

    private bool TryExecuteShortcut(KeyEventArgs e)
    {
        var modifiers = e.KeyModifiers;
        if (modifiers == KeyModifiers.Control && e.Key == Key.O)
        {
            OpenImage_Click(this, new RoutedEventArgs());
            return true;
        }

        if (modifiers == KeyModifiers.Control && e.Key == Key.E)
        {
            SaveSelected_Click(this, new RoutedEventArgs());
            return true;
        }

        if (modifiers == KeyModifiers.Control && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            return true;
        }

        if (modifiers == KeyModifiers.None && e.Key == Key.F5)
        {
            MetadataScan_Click(this, new RoutedEventArgs());
            return true;
        }

        if (modifiers == KeyModifiers.Control && e.Key == Key.F5)
        {
            FileCarver_Click(this, new RoutedEventArgs());
            return true;
        }

        if (modifiers == KeyModifiers.Control && e.Key == Key.D1)
        {
            TogglePartitions_Click(this, new RoutedEventArgs());
            return true;
        }

        if (modifiers == KeyModifiers.Control && e.Key == Key.D2)
        {
            ToggleInspector_Click(this, new RoutedEventArgs());
            return true;
        }

        if (modifiers == KeyModifiers.Control && e.Key == Key.D4)
        {
            ToggleResultsWindow_Click(this, new RoutedEventArgs());
            return true;
        }

        if (modifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.A)
        {
            AddCustomPartition_Click(this, new RoutedEventArgs());
            return true;
        }

        if (modifiers == KeyModifiers.Control && e.Key == Key.U)
        {
            UnmountPartition_Click(this, new RoutedEventArgs());
            return true;
        }

        if (modifiers == KeyModifiers.Control && e.Key == Key.OemComma)
        {
            Settings_Click(this, new RoutedEventArgs());
            return true;
        }

        if (modifiers == KeyModifiers.None && e.Key == Key.F1)
        {
            About_Click(this, new RoutedEventArgs());
            return true;
        }

        return false;
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowMaximized();
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestore_Click(object? sender, RoutedEventArgs e)
    {
        ToggleWindowMaximized();
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleWindowMaximized()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void ApplyWindowStatePadding()
    {
        RootShell.Margin = WindowState == WindowState.Maximized
            ? new global::Avalonia.Thickness(6)
            : new global::Avalonia.Thickness(0);
    }

    private void ResizeNorth_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.North, e);

    private void ResizeSouth_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.South, e);

    private void ResizeWest_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.West, e);

    private void ResizeEast_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.East, e);

    private void ResizeNorthWest_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.NorthWest, e);

    private void ResizeNorthEast_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.NorthEast, e);

    private void ResizeSouthWest_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.SouthWest, e);

    private void ResizeSouthEast_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.SouthEast, e);

    private void BeginResize(WindowEdge edge, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginResizeDrag(edge, e);
    }
}

internal sealed record CustomPartitionRequest(string Name, long Offset, long Length);

internal sealed class RebuildProgressDialog : Window
{
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _stageText;
    private readonly TextBlock _detailText;
    private readonly Button _pauseButton;
    private readonly ProgressEtaEstimator _etaEstimator = new();
    private bool _isPaused;

    public RebuildProgressDialog()
    {
        Title = "FATX Rebuild Progress";
        Width = 560;
        Height = 220;
        MinWidth = 500;
        MinHeight = 200;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        _stageText = new TextBlock
        {
            Text = "Preparing rebuild...",
            FontSize = 16,
            FontWeight = global::Avalonia.Media.FontWeight.SemiBold
        };
        _detailText = new TextBlock
        {
            Margin = new global::Avalonia.Thickness(0, 8, 0, 0),
            Foreground = global::Avalonia.Application.Current?.FindResource("MutedBrush") as global::Avalonia.Media.IBrush
        };
        _progressBar = new ProgressBar
        {
            Margin = new global::Avalonia.Thickness(0, 14, 0, 0),
            Minimum = 0,
            Maximum = 100,
            Height = 14
        };

        _pauseButton = new Button { Content = "Pause", MinWidth = 96 };
        _pauseButton.Click += (_, _) =>
        {
            _isPaused = !_isPaused;
            _pauseButton.Content = _isPaused ? "Resume" : "Pause";
            PauseChanged?.Invoke(this, _isPaused);
        };

        var cancelButton = new Button { Content = "Cancel", MinWidth = 96 };
        cancelButton.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);

        Content = new Border
        {
            Padding = new global::Avalonia.Thickness(18),
            Background = global::Avalonia.Application.Current?.FindResource("PanelBgBrush") as global::Avalonia.Media.IBrush,
            Child = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto)
                },
                Children =
                {
                    _stageText,
                    _detailText,
                    _progressBar,
                    new StackPanel
                    {
                        Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new global::Avalonia.Thickness(0, 12, 0, 0),
                        Children = { _pauseButton, cancelButton }
                    }
                }
            }
        };

        var grid = (Grid)((Border)Content).Child!;
        Grid.SetRow((Control)grid.Children[1], 1);
        Grid.SetRow((Control)grid.Children[2], 2);
        Grid.SetRow((Control)grid.Children[3], 3);
    }

    public event EventHandler? CancelRequested;

    public event EventHandler<bool>? PauseChanged;

    public void Update(int percent, string stage, string detail)
    {
        var clampedPercent = Math.Clamp(percent, 0, 100);
        _progressBar.Value = clampedPercent;
        _stageText.Text = $"{clampedPercent:0}% - {stage}";
        var etaText = _etaEstimator.BuildStatus(clampedPercent, stage);
        _detailText.Text = string.IsNullOrWhiteSpace(etaText)
            ? detail
            : $"{detail} | {etaText}";
    }
}

internal sealed class TextDialog : Window
{
    private TextDialog(string title, string message)
    {
        Title = title;
        Width = 560;
        Height = 390;
        MinWidth = 460;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var closeButton = new Button { Content = "OK", Classes = { "primary" }, MinWidth = 86 };
        closeButton.Click += (_, _) => Close();

        Content = new Border
        {
            Padding = new global::Avalonia.Thickness(18),
            Background = global::Avalonia.Application.Current?.FindResource("PanelBgBrush") as global::Avalonia.Media.IBrush,
            Child = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(1, GridUnitType.Star),
                    new RowDefinition(GridLength.Auto)
                },
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 20,
                        FontWeight = global::Avalonia.Media.FontWeight.SemiBold,
                        Margin = new global::Avalonia.Thickness(0, 0, 0, 12)
                    },
                    new ScrollViewer
                    {
                        Content = new TextBlock { Text = message, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap }
                    },
                    new StackPanel
                    {
                        Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                        Margin = new global::Avalonia.Thickness(0, 14, 0, 0),
                        Children = { closeButton }
                    }
                }
            }
        };

        var grid = (Grid)((Border)Content).Child!;
        Grid.SetRow((Control)grid.Children[1], 1);
        Grid.SetRow((Control)grid.Children[2], 2);
    }

    public static Task ShowAsync(Window owner, string title, string message)
    {
        return new TextDialog(title, message).ShowDialog(owner);
    }
}

internal sealed class CustomPartitionDialog : Window
{
    private readonly TextBox _nameBox = new() { Text = "Custom Partition" };
    private readonly TextBox _offsetBox = new() { Watermark = "Offset, for example 0x80000" };
    private readonly TextBox _lengthBox = new() { Watermark = "Length, for example 0x1000000" };
    private CustomPartitionRequest? _request;

    private CustomPartitionDialog()
    {
        Title = "Add Custom Partition";
        Width = 420;
        Height = 250;
        MinWidth = 420;
        MinHeight = 250;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var addButton = new Button { Content = "Add", Classes = { "primary" }, MinWidth = 86 };
        addButton.Click += AddButton_Click;
        var cancelButton = new Button { Content = "Cancel", MinWidth = 86 };
        cancelButton.Click += (_, _) => Close();

        Content = new Border
        {
            Padding = new global::Avalonia.Thickness(18),
            Background = global::Avalonia.Application.Current?.FindResource("PanelBgBrush") as global::Avalonia.Media.IBrush,
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Name", FontWeight = global::Avalonia.Media.FontWeight.SemiBold },
                    _nameBox,
                    new TextBlock { Text = "Offset", FontWeight = global::Avalonia.Media.FontWeight.SemiBold },
                    _offsetBox,
                    new TextBlock { Text = "Length", FontWeight = global::Avalonia.Media.FontWeight.SemiBold },
                    _lengthBox,
                    new StackPanel
                    {
                        Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, addButton }
                    }
                }
            }
        };
    }

    public static async Task<CustomPartitionRequest?> ShowAsync(Window owner)
    {
        var dialog = new CustomPartitionDialog();
        await dialog.ShowDialog(owner);
        return dialog._request;
    }

    private void AddButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryParseNumber(_offsetBox.Text, out var offset) || !TryParseNumber(_lengthBox.Text, out var length) || length <= 0)
        {
            return;
        }

        _request = new CustomPartitionRequest(
            string.IsNullOrWhiteSpace(_nameBox.Text) ? "Custom Partition" : _nameBox.Text.Trim(),
            offset,
            length);
        Close();
    }

    private static bool TryParseNumber(string? text, out long value)
    {
        text = text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            value = 0;
            return false;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        }

        return long.TryParse(text, out value);
    }
}
