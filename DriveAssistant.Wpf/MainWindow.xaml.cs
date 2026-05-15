using FATX;
using FATX.Analyzers;
using FATX.Analyzers.Signatures;
using FATX.FileSystem;
using FATXTools.DiskTypes;
using FATXTools.Database;
using FATXTools.Utilities;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FATXTools.Wpf;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const string AppName = "Drive Assistant";
    private const int ClusterMapColumns = 32;

    private DriveReader? _drive;
    private XboxStorageImage? _xboxStorageImage;
    private PlayStationStorageImage? _playStationStorageImage;
    private GenericFileSystemImage? _genericFileSystemImage;
    private SwitchStorageImage? _switchStorageImage;
    private NintendoStorageImage? _nintendoStorageImage;
    private Ps2StorageImage? _ps2StorageImage;
    private LegacyConsoleStorageImage? _legacyConsoleStorageImage;
    private DriveDatabaseSnapshot? _databaseSnapshot;
    private PartitionModel? _selectedPartition;
    private FileRow? _selectedFile;
    private CarvedFileRow? _selectedCarvedFile;
    private RecoveryFileRow? _selectedRecoveryFile;
    private ClusterRow? _selectedCluster;
    private AppSettings _settings = AppSettings.Load();
    private bool _isBusy;
    private bool _isOpeningImage;
    private bool _isMetadataScanRunning;
    private bool _isFileCarverRunning;
    private bool _isExportRunning;
    private CancellationTokenSource? _metadataScanCancellation;
    private CancellationTokenSource? _fileCarverCancellation;
    private CancellationTokenSource? _exportCancellation;
    private string _statusText = "Ready";
    private string _logText = string.Empty;
    private string _inspectorTitle = "No file selected";
    private string _inspectorSubtitle = "Open an image and select a file.";
    private ImageSource? _inspectorPreviewImage;
    private bool _isInspectorPreviewVisible;
    private string _currentFileSystemTitle = "ORIGINAL FILESYSTEM";
    private string _currentDirectorySummary = string.Empty;
    private bool _isScanProgressVisible;
    private double _scanProgressValue;
    private string _scanProgressText = "0%";
    private string _scanProgressTitle = "Scan progress";
    private string _clusterViewerSummary = "Run Metadata Scan to populate cluster status.";
    private IReadOnlyList<ClusterMapRow> _clusterMapRows = Array.Empty<ClusterMapRow>();
    private string? _activeRawImagePath;
    private GridLength _partitionsPanelWidth = new(280);
    private GridLength _inspectorPanelWidth = new(320);
    private readonly Stack<object?> _directoryBackStack = new();
    private readonly Stack<object?> _directoryForwardStack = new();
    private object? _currentDirectory;
    private string? _openedImagePath;
    private bool _suppressTreeSelectionNavigation;
    private FileDatabase? _recoveryDatabase;
    private IntegrityAnalyzer? _recoveryIntegrity;
    private readonly List<string> _logLines = new();
    private readonly Dictionary<string, int> _liveLogLineIndexes = new();
    private readonly List<string> _temporaryScanFiles = new();
    private bool _suppressRecentImageSelection;
    private DetachedResultsWindow? _detachedResultsWindow;
    private bool _closingMainWindow;
    private Point? _resultsTabDragStart;
    private TabItem? _resultsTabDragItem;
    private readonly Dictionary<string, UiActionCommand> _shortcutCommands = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        BuildClusterMapColumns();
        DataContext = this;
        ConfigureShortcutBindings();
        AppLogger.Configure(_settings.LogFile, _settings.EnableFileLogging);
        AppLogger.LineWritten += line => Dispatcher.Invoke(() => AppendLog(line));
        RefreshRecentImages();
        RefreshSelectionState();
        SetResultsWindowToolTips(detached: false);
        ApplyWindowStatePadding();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void BuildClusterMapColumns()
    {
        for (var index = 0; index < ClusterMapColumns; index++)
        {
            var template = new DataTemplate();
            var cell = new FrameworkElementFactory(typeof(Border));
            cell.SetValue(FrameworkElement.WidthProperty, 20.0);
            cell.SetValue(FrameworkElement.HeightProperty, 20.0);
            cell.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 1, 2, 1));
            cell.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cell.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            cell.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
            cell.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            cell.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(21, 25, 34)));
            cell.SetValue(ToolTipService.InitialShowDelayProperty, 250);
            cell.SetValue(ToolTipService.ShowDurationProperty, 500000);
            cell.SetBinding(Border.BackgroundProperty, new Binding($"Cells[{index}].StatusBrush"));
            cell.SetBinding(FrameworkElement.ToolTipProperty, new Binding($"Cells[{index}].ToolTip"));
            cell.SetBinding(FrameworkElement.TagProperty, new Binding($"Cells[{index}]"));
            cell.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler(ClusterMapCell_MouseLeftButtonDown));
            template.VisualTree = cell;

            ClusterMapGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = (index + 1).ToString(),
                CellTemplate = template,
                Width = new DataGridLength(26),
                MinWidth = 26,
                CanUserResize = false,
                CanUserSort = false
            });
        }
    }

    public ObservableCollection<PartitionModel> Partitions { get; } = new();

    public ObservableCollection<DirectoryNode> DirectoryRoots { get; } = new();

    public ObservableCollection<FileRow> Files { get; } = new();

    public ObservableCollection<FileRow> MetadataResults { get; } = new();

    public ObservableCollection<CarvedFileRow> CarvedFiles { get; } = new();

    public ObservableCollection<RecoveryTreeNode> RecoveryTreeRoots { get; } = new();

    public ObservableCollection<RecoveryFileRow> RecoveryRows { get; } = new();

    public ObservableCollection<ClusterRow> ClusterRows { get; } = new();

    public ObservableCollection<ScanProgressRow> ScanProgressRows { get; } = new();

    public ObservableCollection<InspectorRow> InspectorRows { get; } = new();

    public ObservableCollection<string> RecentImages { get; } = new();

    public string CurrentFileSystemTitle
    {
        get => _currentFileSystemTitle;
        set => SetField(ref _currentFileSystemTitle, value);
    }

    public IReadOnlyList<ClusterMapRow> ClusterMapRows
    {
        get => _clusterMapRows;
        private set => SetField(ref _clusterMapRows, value);
    }

    public string CurrentDirectorySummary
    {
        get => _currentDirectorySummary;
        set => SetField(ref _currentDirectorySummary, value);
    }

    public PartitionModel? SelectedPartition
    {
        get => _selectedPartition;
        set
        {
            if (SetField(ref _selectedPartition, value))
            {
                LoadSelectedPartition();
                OnPropertyChanged(nameof(HasActiveVolume));
            }
        }
    }

    public FileRow? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetField(ref _selectedFile, value))
            {
                if (value != null)
                {
                    SelectedCarvedFile = null;
                    UpdateInspector(value);
                }
                RefreshSelectionState();
            }
        }
    }

    public CarvedFileRow? SelectedCarvedFile
    {
        get => _selectedCarvedFile;
        set
        {
            if (SetField(ref _selectedCarvedFile, value))
            {
                if (value != null)
                {
                    SelectedFile = null;
                    SelectedRecoveryFile = null;
                    UpdateInspector(value);
                }
                RefreshSelectionState();
            }
        }
    }

    public RecoveryFileRow? SelectedRecoveryFile
    {
        get => _selectedRecoveryFile;
        set
        {
            if (SetField(ref _selectedRecoveryFile, value))
            {
                if (value != null)
                {
                    SelectedFile = null;
                    SelectedCarvedFile = null;
                    UpdateInspector(value);
                }
                RefreshSelectionState();
            }
        }
    }

    public ClusterRow? SelectedCluster
    {
        get => _selectedCluster;
        set
        {
            if (SetField(ref _selectedCluster, value) && value != null)
            {
                UpdateInspector(value);
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(HasActiveVolume));
                OnPropertyChanged(nameof(CanRunMetadataScan));
                OnPropertyChanged(nameof(CanRunFileCarver));
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasOpenDatabase));
                OnPropertyChanged(nameof(CanCancelScan));
                OnPropertyChanged(nameof(CanCancelExport));
                OnPropertyChanged(nameof(CanAddCustomPartition));
                OnPropertyChanged(nameof(CanUnmountPartition));
            }
        }
    }

    public bool HasActiveVolume => SelectedPartition?.IsMounted == true && !_isOpeningImage;

    public bool CanRunMetadataScan => HasActiveVolume && (SelectedPartition?.FatxVolume != null || SelectedPartition?.NtfsVolume != null || SelectedPartition?.PlayStationVolume != null || SelectedPartition?.GenericVolume != null) && !_isMetadataScanRunning && !_isExportRunning;

    public bool CanRunFileCarver => HasActiveVolume && (SelectedPartition?.FatxVolume != null || SelectedPartition?.NtfsVolume != null || SelectedPartition?.PlayStationVolume != null || SelectedPartition?.GenericVolume != null) && !_isFileCarverRunning && !_isExportRunning;

    public bool CanCancelScan => _isMetadataScanRunning && _metadataScanCancellation?.IsCancellationRequested != true
                                 || _isFileCarverRunning && _fileCarverCancellation?.IsCancellationRequested != true;

    public bool CanCancelExport => _isExportRunning && _exportCancellation?.IsCancellationRequested != true;

    public bool HasSelection => (SelectedFile != null || SelectedCarvedFile != null || SelectedRecoveryFile != null) && !_isOpeningImage && !_isExportRunning;

    public bool HasOpenDatabase => HasLoadedImage && !_isOpeningImage;

    public bool CanAddCustomPartition => _drive != null && !_isOpeningImage && !_isMetadataScanRunning && !_isFileCarverRunning && !_isExportRunning;

    public bool CanUnmountPartition => SelectedPartition != null && !_isOpeningImage && !_isMetadataScanRunning && !_isFileCarverRunning && !_isExportRunning;

    private bool HasLoadedImage => Partitions.Count > 0 && (_drive != null || _xboxStorageImage != null || _playStationStorageImage != null || _genericFileSystemImage != null || _switchStorageImage != null || _nintendoStorageImage != null || _ps2StorageImage != null || _legacyConsoleStorageImage != null);

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string LogText
    {
        get => _logText;
        set => SetField(ref _logText, value);
    }

    public string InspectorTitle
    {
        get => _inspectorTitle;
        set => SetField(ref _inspectorTitle, value);
    }

    public string InspectorSubtitle
    {
        get => _inspectorSubtitle;
        set => SetField(ref _inspectorSubtitle, value);
    }

    public ImageSource? InspectorPreviewImage
    {
        get => _inspectorPreviewImage;
        set => SetField(ref _inspectorPreviewImage, value);
    }

    public bool IsInspectorPreviewVisible
    {
        get => _isInspectorPreviewVisible;
        set => SetField(ref _isInspectorPreviewVisible, value);
    }

    public bool IsScanProgressVisible
    {
        get => _isScanProgressVisible;
        set => SetField(ref _isScanProgressVisible, value);
    }

    public double ScanProgressValue
    {
        get => _scanProgressValue;
        set => SetField(ref _scanProgressValue, value);
    }

    public string ScanProgressText
    {
        get => _scanProgressText;
        set => SetField(ref _scanProgressText, value);
    }

    public string ScanProgressTitle
    {
        get => _scanProgressTitle;
        set => SetField(ref _scanProgressTitle, value);
    }

    public string ClusterViewerSummary
    {
        get => _clusterViewerSummary;
        set => SetField(ref _clusterViewerSummary, value);
    }

    private async void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConsoleImageOpenWindow
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await OpenConsoleImagePathAsync(dialog.ImagePath, dialog.ImageKind, dialog.KeyPath);
    }

    private async void RecentImagesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRecentImageSelection || RecentImagesCombo.SelectedItem is not string path)
        {
            return;
        }

        _suppressRecentImageSelection = true;
        RecentImagesCombo.SelectedIndex = -1;
        _suppressRecentImageSelection = false;

        if (!File.Exists(path))
        {
            StatusText = "Recent image no longer exists.";
            AppendLog($"Recent image missing: {path}");
            RemoveRecentImage(path);
            return;
        }

        await OpenConsoleImagePathAsync(path, ConsoleDriveImageKind.Auto, keyPath: null);
    }

    private void AddCustomPartition_Click(object sender, RoutedEventArgs e)
    {
        if (_drive == null)
        {
            StatusText = "Custom FATX partitions require an open FATX image.";
            return;
        }

        var dialog = new CustomPartitionWindow(_activeRawImagePath ?? _openedImagePath ?? string.Empty)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _drive.AddPartition(dialog.PartitionName, dialog.PartitionOffset, dialog.PartitionLength);
            var volume = _drive.Partitions.Last();
            string status;
            try
            {
                volume.Mount();
                status = $"Mounted, custom, {volume.GetRoot().Count:N0} root entries";
            }
            catch (Exception ex)
            {
                status = $"Mount failed: {ex.Message}";
            }

            var model = new PartitionModel(volume, status);
            Partitions.Add(model);
            SelectedPartition = model;
            AppendLog($"Added custom FATX partition: {dialog.PartitionName}, offset 0x{dialog.PartitionOffset:X}, length 0x{dialog.PartitionLength:X}.");
            RefreshSelectionState();
        }
        catch (Exception ex)
        {
            StatusText = "Failed";
            AppendLog($"Add custom partition failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UnmountPartition_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPartition == null)
        {
            return;
        }

        var partition = SelectedPartition;
        var index = Partitions.IndexOf(partition);
        if (index < 0)
        {
            return;
        }

        if (partition.FatxVolume != null && _drive != null)
        {
            var driveIndex = _drive.Partitions.FindIndex(volume => ReferenceEquals(volume, partition.FatxVolume));
            if (driveIndex >= 0)
            {
                _drive.RemovePartitionAt(driveIndex);
            }
        }

        Partitions.RemoveAt(index);
        SelectedPartition = Partitions.ElementAtOrDefault(Math.Min(index, Partitions.Count - 1));
        if (SelectedPartition == null)
        {
            DirectoryRoots.Clear();
            Files.Clear();
            CurrentFileSystemTitle = "ORIGINAL FILESYSTEM";
            CurrentDirectorySummary = "No mounted partition selected.";
        }

        AppendLog($"Unmounted partition from current session: {partition.Name}.");
        RefreshSelectionState();
    }

    private async void LoadDatabase_Click(object sender, RoutedEventArgs e)
    {
        if (!HasLoadedImage)
        {
            StatusText = "Open an image before loading a database.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Drive Assistant Database (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await LoadProgressDatabaseAsync(dialog.FileName);
    }

    private void SaveDatabase_Click(object sender, RoutedEventArgs e)
    {
        SaveProgressDatabase();
    }

    private async void RebuildFatxImageIncludeDeleted_Click(object sender, RoutedEventArgs e)
    {
        await RebuildFatxImageFromJsonAsync(includeDeletedEntries: true);
    }

    private async void RebuildFatxImageExcludeDeleted_Click(object sender, RoutedEventArgs e)
    {
        await RebuildFatxImageFromJsonAsync(includeDeletedEntries: false);
    }

    private async Task RebuildFatxImageFromJsonAsync(bool includeDeletedEntries)
    {
        var snapshotDialog = new OpenFileDialog
        {
            Filter = "Drive Assistant Database (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (snapshotDialog.ShowDialog(this) != true)
        {
            return;
        }

        string liveDirectory = string.Empty;
        var includeLiveFolder = MessageBox.Show(
            this,
            "Do you want to include a folder with non-deleted files?\nChoose 'No' to build a JSON-only skeleton image.",
            AppName,
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (includeLiveFolder == MessageBoxResult.Cancel)
        {
            return;
        }

        if (includeLiveFolder == MessageBoxResult.Yes)
        {
            liveDirectory = PickDestinationFolder("Select folder that contains non-deleted files") ?? string.Empty;
        }

        string deletedDirectory = string.Empty;
        var includeDeletedFolder = MessageBox.Show(
            this,
            "Do you want to include a folder with deleted files?\nChoose 'No' to skip deleted-source files.",
            AppName,
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (includeDeletedFolder == MessageBoxResult.Cancel)
        {
            return;
        }

        if (includeDeletedFolder == MessageBoxResult.Yes)
        {
            deletedDirectory = PickDestinationFolder("Select folder that contains deleted files") ?? string.Empty;
        }

        var suggestedName = !string.IsNullOrWhiteSpace(SelectedPartition?.Name)
            ? $"{SelectedPartition.Name}-rebuilt.img"
            : "rebuilt-fatx.img";
        var outputDialog = new SaveFileDialog
        {
            Filter = "Raw image (*.img)|*.img|All files (*.*)|*.*",
            FileName = suggestedName
        };
        if (outputDialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(outputDialog.FileName))
        {
            return;
        }

        StatusText = "Rebuilding FATX image from JSON...";
        AppendLog($"FATX rebuild requested ({(includeDeletedEntries ? "include deleted" : "exclude deleted")}): {snapshotDialog.FileName}");

        var args = new List<string>
        {
            "--output",
            outputDialog.FileName,
            "--include-deleted",
            includeDeletedEntries ? "true" : "false",
            snapshotDialog.FileName
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

        if (!string.IsNullOrWhiteSpace(SelectedPartition?.Name))
        {
            args.Add("--partition");
            args.Add(SelectedPartition.Name);
        }

        var rebuildCancellation = new CancellationTokenSource();
        var pauseFlag = 0;
        var progressDialog = new RebuildProgressDialog
        {
            Owner = this
        };
        progressDialog.CancelRequested += (_, _) =>
        {
            if (!rebuildCancellation.IsCancellationRequested)
            {
                rebuildCancellation.Cancel();
                StatusText = "Canceling FATX rebuild...";
                AppendLog("FATX rebuild cancellation requested.");
            }
        };
        progressDialog.PauseChanged += (_, isPaused) =>
        {
            Interlocked.Exchange(ref pauseFlag, isPaused ? 1 : 0);
            StatusText = isPaused ? "FATX rebuild paused" : "Rebuilding FATX image from JSON...";
            AppendLog(isPaused ? "FATX rebuild paused." : "FATX rebuild resumed.");
        };
        progressDialog.Show();

        try
        {
            await Task.Run(() =>
            {
                var exitCode = DriveAssistant.Cli.FatxImageRebuildCommand.Run(args.ToArray(), new DriveAssistant.Cli.RebuildExecutionOptions
                {
                    CancellationToken = rebuildCancellation.Token,
                    IsPaused = () => Volatile.Read(ref pauseFlag) == 1,
                    PayloadWorkerCount = Math.Max(1, _settings.MetadataParallelWorkers),
                    Progress = snapshot =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            progressDialog.Update(snapshot.Percent, snapshot.Stage, snapshot.Detail);
                            StatusText = Volatile.Read(ref pauseFlag) == 1
                                ? "FATX rebuild paused"
                                : $"Rebuilding FATX image from JSON... {snapshot.Percent:0}%";
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

            StatusText = "Ready";
            AppendLog($"FATX rebuild complete: {outputDialog.FileName}");
            MessageBox.Show(this, $"Rebuilt FATX image:\n{outputDialog.FileName}", AppName, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Rebuild canceled";
            AppendLog("FATX rebuild canceled.");
        }
        catch (Exception ex)
        {
            StatusText = "Failed";
            AppendLog($"FATX rebuild failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            progressDialog.Close();
            rebuildCancellation.Dispose();
        }
    }

    private async Task OpenImagePathAsync(string imagePath)
    {
        await OpenConsoleImagePathAsync(imagePath, ConsoleDriveImageKind.Auto, keyPath: null);
    }

    private async Task OpenConsoleImagePathAsync(string imagePath, ConsoleDriveImageKind imageKind, string? keyPath)
    {
        if (_isOpeningImage || _isMetadataScanRunning || _isFileCarverRunning)
        {
            StatusText = "Wait for active scans to finish before opening another image.";
            return;
        }

        if (!File.Exists(imagePath))
        {
            StatusText = "Dropped image file was not found.";
            return;
        }

        if (!IsSupportedImagePath(imagePath))
        {
            StatusText = "Drop a supported disk, NAND, ROM, save, disc, or devkit image file.";
            return;
        }

        if (imageKind is ConsoleDriveImageKind.Auto
            or ConsoleDriveImageKind.XboxOriginalFatx
            or ConsoleDriveImageKind.Xbox360Fatx)
        {
            var openedAsFatx = await OpenFatxImagePathAsync(imagePath);
            if (openedAsFatx
                || imageKind == ConsoleDriveImageKind.XboxOriginalFatx
                || imageKind == ConsoleDriveImageKind.Xbox360Fatx)
            {
                return;
            }
        }

        if (imageKind is ConsoleDriveImageKind.Auto or ConsoleDriveImageKind.XboxGptNtfs)
        {
            var openedAsXboxGpt = await OpenXboxGptNtfsImagePathAsync(imagePath);
            if (openedAsXboxGpt || imageKind == ConsoleDriveImageKind.XboxGptNtfs)
            {
                return;
            }
        }

        if (imageKind is ConsoleDriveImageKind.Auto or ConsoleDriveImageKind.NintendoSwitchNand)
        {
            var openedAsSwitch = await OpenSwitchImagePathAsync(imagePath, keyPath ?? string.Empty);
            if (openedAsSwitch || imageKind == ConsoleDriveImageKind.NintendoSwitchNand)
            {
                return;
            }
        }

        if (imageKind is ConsoleDriveImageKind.Auto
            or ConsoleDriveImageKind.NintendoWiiGameCube
            or ConsoleDriveImageKind.NintendoWiiUStorage
            or ConsoleDriveImageKind.NintendoDs3ds)
        {
            var openedAsNintendo = await OpenNintendoImagePathAsync(
                imagePath,
                allowRawWiiUCandidate: imageKind == ConsoleDriveImageKind.NintendoWiiUStorage,
                keyPath);
            if (openedAsNintendo
                || imageKind == ConsoleDriveImageKind.NintendoWiiGameCube
                || imageKind == ConsoleDriveImageKind.NintendoWiiUStorage
                || imageKind == ConsoleDriveImageKind.NintendoDs3ds)
            {
                return;
            }
        }

        if (imageKind is ConsoleDriveImageKind.Auto or ConsoleDriveImageKind.PlayStation2Hdd)
        {
            var openedAsPs2 = await OpenPs2ImagePathAsync(imagePath);
            if (openedAsPs2 || imageKind == ConsoleDriveImageKind.PlayStation2Hdd)
            {
                return;
            }
        }

        if (imageKind is ConsoleDriveImageKind.Auto or ConsoleDriveImageKind.GenericFileSystem)
        {
            var openedAsGeneric = await OpenGenericFileSystemImagePathAsync(imagePath);
            if (openedAsGeneric || imageKind == ConsoleDriveImageKind.GenericFileSystem)
            {
                return;
            }
        }

        if (imageKind is ConsoleDriveImageKind.Auto
            or ConsoleDriveImageKind.LegacyDevkitMedia
            or ConsoleDriveImageKind.PlayStation1Media)
        {
            var openedAsLegacy = await OpenLegacyConsoleImagePathAsync(imagePath);
            if (openedAsLegacy
                || imageKind == ConsoleDriveImageKind.LegacyDevkitMedia
                || imageKind == ConsoleDriveImageKind.PlayStation1Media)
            {
                return;
            }
        }

        if (imageKind == ConsoleDriveImageKind.Auto)
        {
            var openedAsPs3 = await OpenPlayStationImagePathAsync(
                imagePath,
                keyPath ?? string.Empty,
                ConsoleDriveImageKind.PlayStation3Hdd,
                showError: false);
            if (openedAsPs3)
            {
                return;
            }

            var openedAsPs4 = await OpenPlayStationImagePathAsync(
                imagePath,
                keyPath ?? string.Empty,
                ConsoleDriveImageKind.PlayStation4Hdd,
                showError: false);
            if (openedAsPs4)
            {
                return;
            }

            const string supportUrl = "https://github.com/rain0x06/DriveAssistant/issues";
            StatusText = $"Auto detect could not open this image. Submit a support request on GitHub: {supportUrl}";
            AppendLog($"Auto detect failed to open image: {imagePath}");
            MessageBox.Show(
                this,
                "Auto detect tried all supported filesystem handlers but could not open this image.\n\n" +
                $"Please submit a support request with sample details on GitHub:\n{supportUrl}",
                AppName,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (IsPlayStationImageKind(imageKind))
        {
            await OpenPlayStationImagePathAsync(imagePath, keyPath ?? string.Empty, imageKind);
            return;
        }

        StatusText = "Selected filesystem type could not open this image. Try another filesystem type.";
    }

    private async Task<bool> OpenFatxImagePathAsync(string imagePath)
    {
        return await RunUiTaskAsync("Opening FATX image...", () =>
        {
            DriveReader drive;
            string activeRawImagePath;
            if (Path.GetExtension(imagePath).Equals(".imgc", StringComparison.OrdinalIgnoreCase))
            {
                var compressedImage = new CompressedImage(imagePath, exhaustiveRawPartitionSearch: false);
                drive = compressedImage;
                activeRawImagePath = compressedImage.DecodedPath;
            }
            else
            {
                var rawImage = new RawImage(imagePath, exhaustiveRawPartitionSearch: false);
                drive = rawImage;
                activeRawImagePath = rawImage.SourcePath;
            }

            var partitions = new List<PartitionModel>();
            foreach (var volume in drive.Partitions)
            {
                string status;
                try
                {
                    volume.Mount();
                    status = $"Mounted, {volume.GetRoot().Count} root entries";
                }
                catch (Exception ex)
                {
                    status = $"Mount failed: {ex.Message}";
                }

                partitions.Add(new PartitionModel(volume, status));
            }

            if (!partitions.Any(partition => partition.IsMounted))
            {
                drive.Dispose();
                throw new InvalidDataException("No mountable FATX partitions were found.");
            }

            return (drive, partitions, fileName: imagePath, activeRawImagePath);
        },
        result =>
        {
            _xboxStorageImage?.Dispose();
            _xboxStorageImage = null;
            _playStationStorageImage?.Dispose();
            _playStationStorageImage = null;
            _genericFileSystemImage?.Dispose();
            _genericFileSystemImage = null;
            _switchStorageImage?.Dispose();
            _switchStorageImage = null;
            _nintendoStorageImage?.Dispose();
            _nintendoStorageImage = null;
            _ps2StorageImage?.Dispose();
            _ps2StorageImage = null;
            ClearTemporaryScanFiles();
            _databaseSnapshot = null;
            _drive?.Dispose();
            _drive = result.drive;
            _openedImagePath = result.fileName;
            _activeRawImagePath = result.activeRawImagePath;
            Title = $"{AppName} - {Path.GetFileName(result.fileName)}";
            Partitions.Clear();
            foreach (var partition in result.partitions)
            {
                Partitions.Add(partition);
            }

            MetadataResults.Clear();
            CarvedFiles.Clear();
            RecoveryTreeRoots.Clear();
            RecoveryRows.Clear();
            ClusterRows.Clear();
            _recoveryDatabase = null;
            _recoveryIntegrity = null;
            SelectedPartition = Partitions.FirstOrDefault(p => p.IsMounted) ?? Partitions.FirstOrDefault();
            AddRecentImage(result.fileName);
            AppendLog($"Opened image: {result.fileName}");
            if (!string.Equals(result.fileName, result.activeRawImagePath, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog($"Using decoded IMGC cache: {result.activeRawImagePath}");
            }
            AppendLog($"Detected partitions: {Partitions.Count}");
        },
        showError: false);
    }

    private async Task<bool> OpenXboxGptNtfsImagePathAsync(string imagePath)
    {
        return await RunUiTaskAsync("Opening Xbox GPT/NTFS image...", () =>
        {
            var activeRawImagePath = Path.GetExtension(imagePath).Equals(".imgc", StringComparison.OrdinalIgnoreCase)
                ? ImgcDecoder.DecodeToTempRawImage(imagePath)
                : imagePath;
            var image = XboxStorageImage.Open(activeRawImagePath);
            var partitions = image.Volumes
                .Select(volume => new PartitionModel(volume, $"Mounted, {volume.FamilyText}, {volume.GetRoot().Count:N0} root entries"))
                .Concat(image.BootFileSystems.Select(volume => new PartitionModel(volume, $"Mounted, {volume.FamilyText}, {volume.GetRoot().Count:N0} entries")))
                .OrderBy(partition => partition.Offset)
                .ToList();

            return (image, partitions, fileName: imagePath, activeRawImagePath);
        },
        result =>
        {
            _drive?.Dispose();
            _drive = null;
            _xboxStorageImage?.Dispose();
            _xboxStorageImage = result.image;
            _playStationStorageImage?.Dispose();
            _playStationStorageImage = null;
            _genericFileSystemImage?.Dispose();
            _genericFileSystemImage = null;
            _switchStorageImage?.Dispose();
            _switchStorageImage = null;
            _nintendoStorageImage?.Dispose();
            _nintendoStorageImage = null;
            _ps2StorageImage?.Dispose();
            _ps2StorageImage = null;
            ClearTemporaryScanFiles();
            _databaseSnapshot = null;
            _openedImagePath = result.fileName;
            _activeRawImagePath = result.activeRawImagePath;
            Title = $"{AppName} - {Path.GetFileName(result.fileName)}";
            Partitions.Clear();
            foreach (var partition in result.partitions)
            {
                Partitions.Add(partition);
            }

            MetadataResults.Clear();
            CarvedFiles.Clear();
            RecoveryTreeRoots.Clear();
            RecoveryRows.Clear();
            ClusterRows.Clear();
            _recoveryDatabase = null;
            _recoveryIntegrity = null;
            SelectedPartition = Partitions.FirstOrDefault();
            AddRecentImage(result.fileName);
            AppendLog($"Opened Xbox GPT/NTFS image: {result.fileName}");
            if (!string.Equals(result.fileName, result.activeRawImagePath, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog($"Using decoded IMGC cache: {result.activeRawImagePath}");
            }
            AppendLog($"Detected NTFS partitions: {Partitions.Count}");
        },
        showError: false);
    }

    private async Task<bool> OpenSwitchImagePathAsync(string imagePath, string keyPath)
    {
        if (!string.IsNullOrWhiteSpace(keyPath) && !File.Exists(keyPath))
        {
            StatusText = "Selected Switch key file was not found.";
            return false;
        }

        return await RunUiTaskAsync("Opening Nintendo Switch NAND image...", () =>
        {
            var activeRawImagePath = Path.GetExtension(imagePath).Equals(".imgc", StringComparison.OrdinalIgnoreCase)
                ? ImgcDecoder.DecodeToTempRawImage(imagePath)
                : imagePath;
            var image = SwitchStorageImage.Open(activeRawImagePath, keyPath);
            return (image, partitions: image.Partitions.ToList(), fileName: imagePath, activeRawImagePath);
        },
        result =>
        {
            _drive?.Dispose();
            _drive = null;
            _xboxStorageImage?.Dispose();
            _xboxStorageImage = null;
            _playStationStorageImage?.Dispose();
            _playStationStorageImage = null;
            _genericFileSystemImage?.Dispose();
            _genericFileSystemImage = null;
            _switchStorageImage?.Dispose();
            _switchStorageImage = result.image;
            _nintendoStorageImage?.Dispose();
            _nintendoStorageImage = null;
            _ps2StorageImage?.Dispose();
            _ps2StorageImage = null;
            ClearTemporaryScanFiles();
            _databaseSnapshot = null;
            _openedImagePath = result.fileName;
            _activeRawImagePath = result.activeRawImagePath;
            Title = $"{AppName} - {Path.GetFileName(result.fileName)}";
            Partitions.Clear();
            foreach (var partition in result.partitions)
            {
                Partitions.Add(partition);
            }

            MetadataResults.Clear();
            CarvedFiles.Clear();
            RecoveryTreeRoots.Clear();
            RecoveryRows.Clear();
            ClusterRows.Clear();
            _recoveryDatabase = null;
            _recoveryIntegrity = null;
            SelectedPartition = Partitions.FirstOrDefault(partition => partition.IsMounted) ?? Partitions.FirstOrDefault();
            AddRecentImage(result.fileName);
            AppendLog($"Opened Nintendo Switch NAND image: {result.fileName}");
            if (!string.Equals(result.fileName, result.activeRawImagePath, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog($"Using decoded IMGC cache: {result.activeRawImagePath}");
            }
            AppendLog($"Detected Switch NAND partitions: {Partitions.Count}");
        },
        showError: false);
    }

    private async Task<bool> OpenNintendoImagePathAsync(string imagePath, bool allowRawWiiUCandidate, string? keyPath)
    {
        return await RunUiTaskAsync("Opening Nintendo Wii/Wii U image...", () =>
        {
            var activeRawImagePath = Path.GetExtension(imagePath).Equals(".imgc", StringComparison.OrdinalIgnoreCase)
                ? ImgcDecoder.DecodeToTempRawImage(imagePath)
                : imagePath;
            var image = NintendoStorageImage.Open(activeRawImagePath, allowRawWiiUCandidate, keyPath);
            return (image, partitions: image.Partitions.ToList(), fileName: imagePath, activeRawImagePath: image.ActiveSourcePath);
        },
        result =>
        {
            _drive?.Dispose();
            _drive = null;
            _xboxStorageImage?.Dispose();
            _xboxStorageImage = null;
            _playStationStorageImage?.Dispose();
            _playStationStorageImage = null;
            _genericFileSystemImage?.Dispose();
            _genericFileSystemImage = null;
            _switchStorageImage?.Dispose();
            _switchStorageImage = null;
            _nintendoStorageImage?.Dispose();
            _nintendoStorageImage = result.image;
            _ps2StorageImage?.Dispose();
            _ps2StorageImage = null;
            ClearTemporaryScanFiles();
            _databaseSnapshot = null;
            _openedImagePath = result.fileName;
            _activeRawImagePath = result.activeRawImagePath;
            Title = $"{AppName} - {Path.GetFileName(result.fileName)}";
            Partitions.Clear();
            foreach (var partition in result.partitions)
            {
                Partitions.Add(partition);
            }

            MetadataResults.Clear();
            CarvedFiles.Clear();
            RecoveryTreeRoots.Clear();
            RecoveryRows.Clear();
            ClusterRows.Clear();
            _recoveryDatabase = null;
            _recoveryIntegrity = null;
            SelectedPartition = Partitions.FirstOrDefault();
            AddRecentImage(result.fileName);
            AppendLog($"Opened Nintendo Wii/Wii U image: {result.fileName}");
            if (!string.Equals(result.fileName, result.activeRawImagePath, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog($"Using Nintendo raw image source: {result.activeRawImagePath}");
            }
            AppendLog($"Detected Nintendo partitions/containers: {Partitions.Count}");
        },
        showError: false);
    }

    private async Task<bool> OpenPs2ImagePathAsync(string imagePath)
    {
        return await RunUiTaskAsync("Opening PlayStation 2 HDD image...", () =>
        {
            var activeRawImagePath = Path.GetExtension(imagePath).Equals(".imgc", StringComparison.OrdinalIgnoreCase)
                ? ImgcDecoder.DecodeToTempRawImage(imagePath)
                : imagePath;
            var image = Ps2StorageImage.Open(activeRawImagePath);
            return (image, partitions: image.Partitions.ToList(), fileName: imagePath, activeRawImagePath);
        },
        result =>
        {
            _drive?.Dispose();
            _drive = null;
            _xboxStorageImage?.Dispose();
            _xboxStorageImage = null;
            _playStationStorageImage?.Dispose();
            _playStationStorageImage = null;
            _genericFileSystemImage?.Dispose();
            _genericFileSystemImage = null;
            _switchStorageImage?.Dispose();
            _switchStorageImage = null;
            _nintendoStorageImage?.Dispose();
            _nintendoStorageImage = null;
            _ps2StorageImage?.Dispose();
            _ps2StorageImage = result.image;
            ClearTemporaryScanFiles();
            _databaseSnapshot = null;
            _openedImagePath = result.fileName;
            _activeRawImagePath = result.activeRawImagePath;
            Title = $"{AppName} - {Path.GetFileName(result.fileName)}";
            Partitions.Clear();
            foreach (var partition in result.partitions)
            {
                Partitions.Add(partition);
            }

            MetadataResults.Clear();
            CarvedFiles.Clear();
            RecoveryTreeRoots.Clear();
            RecoveryRows.Clear();
            ClusterRows.Clear();
            _recoveryDatabase = null;
            _recoveryIntegrity = null;
            SelectedPartition = Partitions.FirstOrDefault();
            AddRecentImage(result.fileName);
            AppendLog($"Opened PlayStation 2 HDD image: {result.fileName}");
            if (!string.Equals(result.fileName, result.activeRawImagePath, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog($"Using decoded IMGC cache: {result.activeRawImagePath}");
            }
            AppendLog($"Detected PlayStation 2 APA partitions: {Partitions.Count}");
        },
        showError: false);
    }

    private async Task<bool> OpenGenericFileSystemImagePathAsync(string imagePath)
    {
        return await RunUiTaskAsync("Opening generic filesystem image...", () =>
        {
            var activeRawImagePath = Path.GetExtension(imagePath).Equals(".imgc", StringComparison.OrdinalIgnoreCase)
                ? ImgcDecoder.DecodeToTempRawImage(imagePath)
                : imagePath;
            var image = GenericFileSystemImage.Open(activeRawImagePath);
            return (image, partitions: image.Partitions.ToList(), fileName: imagePath, activeRawImagePath);
        },
        result =>
        {
            _drive?.Dispose();
            _drive = null;
            _xboxStorageImage?.Dispose();
            _xboxStorageImage = null;
            _playStationStorageImage?.Dispose();
            _playStationStorageImage = null;
            _genericFileSystemImage?.Dispose();
            _genericFileSystemImage = result.image;
            _switchStorageImage?.Dispose();
            _switchStorageImage = null;
            _nintendoStorageImage?.Dispose();
            _nintendoStorageImage = null;
            _ps2StorageImage?.Dispose();
            _ps2StorageImage = null;
            ClearTemporaryScanFiles();
            _databaseSnapshot = null;
            _openedImagePath = result.fileName;
            _activeRawImagePath = result.activeRawImagePath;
            Title = $"{AppName} - {Path.GetFileName(result.fileName)}";
            Partitions.Clear();
            foreach (var partition in result.partitions)
            {
                Partitions.Add(partition);
            }

            MetadataResults.Clear();
            CarvedFiles.Clear();
            RecoveryTreeRoots.Clear();
            RecoveryRows.Clear();
            ClusterRows.Clear();
            _recoveryDatabase = null;
            _recoveryIntegrity = null;
            SelectedPartition = Partitions.FirstOrDefault();
            AddRecentImage(result.fileName);
            AppendLog($"Opened generic filesystem image: {result.fileName}");
            if (!string.Equals(result.fileName, result.activeRawImagePath, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog($"Using decoded IMGC cache: {result.activeRawImagePath}");
            }
            AppendLog($"Detected NTFS/FAT32/exFAT partitions: {Partitions.Count}");
        },
        showError: false);
    }

    private async Task<bool> OpenLegacyConsoleImagePathAsync(string imagePath)
    {
        return await RunUiTaskAsync("Opening legacy console/devkit media...", () =>
        {
            var activeRawImagePath = Path.GetExtension(imagePath).Equals(".imgc", StringComparison.OrdinalIgnoreCase)
                ? ImgcDecoder.DecodeToTempRawImage(imagePath)
                : imagePath;
            var image = LegacyConsoleStorageImage.Open(activeRawImagePath);
            return (image, partitions: image.Partitions.ToList(), fileName: imagePath, activeRawImagePath);
        },
        result =>
        {
            _drive?.Dispose();
            _drive = null;
            _xboxStorageImage?.Dispose();
            _xboxStorageImage = null;
            _playStationStorageImage?.Dispose();
            _playStationStorageImage = null;
            _genericFileSystemImage?.Dispose();
            _genericFileSystemImage = null;
            _switchStorageImage?.Dispose();
            _switchStorageImage = null;
            _nintendoStorageImage?.Dispose();
            _nintendoStorageImage = null;
            _ps2StorageImage?.Dispose();
            _ps2StorageImage = null;
            _legacyConsoleStorageImage?.Dispose();
            _legacyConsoleStorageImage = result.image;
            ClearTemporaryScanFiles();
            _databaseSnapshot = null;
            _openedImagePath = result.fileName;
            _activeRawImagePath = result.activeRawImagePath;
            Title = $"{AppName} - {Path.GetFileName(result.fileName)}";
            Partitions.Clear();
            foreach (var partition in result.partitions)
            {
                Partitions.Add(partition);
            }

            MetadataResults.Clear();
            CarvedFiles.Clear();
            RecoveryTreeRoots.Clear();
            RecoveryRows.Clear();
            ClusterRows.Clear();
            _recoveryDatabase = null;
            _recoveryIntegrity = null;
            SelectedPartition = Partitions.FirstOrDefault();
            AddRecentImage(result.fileName);
            AppendLog($"Opened legacy console/devkit media: {result.fileName}");
            if (!string.Equals(result.fileName, result.activeRawImagePath, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog($"Using decoded IMGC cache: {result.activeRawImagePath}");
            }
            AppendLog($"Detected legacy console/devkit regions: {Partitions.Count}");
        },
        showError: false);
    }

    private async Task<bool> OpenPlayStationImagePathAsync(
        string imagePath,
        string keyPath,
        ConsoleDriveImageKind imageKind,
        bool showError = true)
    {
        if (!string.IsNullOrWhiteSpace(keyPath) && !File.Exists(keyPath))
        {
            StatusText = "Selected PlayStation key file was not found.";
            return false;
        }

        var platformName = imageKind == ConsoleDriveImageKind.PlayStation3Hdd
            ? "PlayStation 3"
            : "PlayStation 4 / PlayStation 4 Pro";

        return await RunUiTaskAsync($"Opening {platformName} HDD image...", () =>
        {
            PlayStationStorageImage image;
            if (imageKind == ConsoleDriveImageKind.PlayStation3Hdd)
            {
                if (!ManagedPs3StorageImage.TryOpen(imagePath, keyPath, out image, out var ps3Error))
                {
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(ps3Error)
                            ? "The PlayStation 3 HDD image could not be opened by the managed PS3 reader."
                            : $"The PlayStation 3 HDD image could not be opened by the managed PS3 reader: {ps3Error}");
                }
            }
            else
            {
                image = PlayStationStorageImage.Open(imagePath, keyPath);
            }

            var partitions = image.Volumes
                .Select(volume =>
                {
                    var status = volume.IsLoaded
                        ? $"Mounted, {volume.FamilyText}, {volume.GetRoot().Count:N0} root entries"
                        : $"Mount failed: {volume.LoadError}";
                    return new PartitionModel(volume, status);
                })
                .ToList();

            return (image, partitions, fileName: imagePath);
        },
        result =>
        {
            _drive?.Dispose();
            _drive = null;
            _xboxStorageImage?.Dispose();
            _xboxStorageImage = null;
            _playStationStorageImage?.Dispose();
            _playStationStorageImage = result.image;
            _genericFileSystemImage?.Dispose();
            _genericFileSystemImage = null;
            _switchStorageImage?.Dispose();
            _switchStorageImage = null;
            _nintendoStorageImage?.Dispose();
            _nintendoStorageImage = null;
            _ps2StorageImage?.Dispose();
            _ps2StorageImage = null;
            ClearTemporaryScanFiles();
            _databaseSnapshot = null;
            _openedImagePath = result.fileName;
            _activeRawImagePath = result.fileName;
            Title = $"{AppName} - {Path.GetFileName(result.fileName)}";
            Partitions.Clear();
            foreach (var partition in result.partitions)
            {
                Partitions.Add(partition);
            }

            MetadataResults.Clear();
            CarvedFiles.Clear();
            RecoveryTreeRoots.Clear();
            RecoveryRows.Clear();
            ClusterRows.Clear();
            _recoveryDatabase = null;
            _recoveryIntegrity = null;
            SelectedPartition = Partitions.FirstOrDefault(p => p.IsMounted) ?? Partitions.FirstOrDefault();
            AddRecentImage(result.fileName);
            AppendLog($"Opened {platformName} HDD image: {result.fileName}");
            AppendLog($"Detected {platformName} partitions: {Partitions.Count}");
        },
        showError: showError);
    }

    private static bool IsPlayStationImageKind(ConsoleDriveImageKind imageKind)
    {
        return imageKind is ConsoleDriveImageKind.PlayStation3Hdd or ConsoleDriveImageKind.PlayStation4Hdd;
    }

    private static bool IsSupportedImagePath(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() is ".img" or ".imgc" or ".bin" or ".cue" or ".raw" or ".iso" or ".cso" or ".pbp" or ".vpk" or ".rvz" or ".wbfs" or ".zip" or ".wud" or ".wux" or ".gcm" or ".gdi" or ".cdi" or ".cim" or ".vmu" or ".vms" or ".dci" or ".hex" or ".mcr" or ".mcd" or ".psx" or ".ps2" or ".z64" or ".n64" or ".v64" or ".rom" or ".sra" or ".eep" or ".fla" or ".mpk" or ".gb" or ".gbc" or ".gba" or ".sav" or ".nds" or ".dsi" or ".3ds" or ".cci" or ".cxi" or ".cfa" or ".csu" or ".app";
    }

    private void AddRecentImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _settings.RecentImages.RemoveAll(item => item.Equals(path, StringComparison.OrdinalIgnoreCase));
        _settings.RecentImages.Insert(0, path);
        _settings.RecentImages = AppSettings.NormalizeRecentImages(_settings.RecentImages);
        _settings.Save();
        RefreshRecentImages();
    }

    private void RemoveRecentImage(string path)
    {
        _settings.RecentImages.RemoveAll(item => item.Equals(path, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        RefreshRecentImages();
    }

    private void RefreshRecentImages()
    {
        RecentImages.Clear();
        foreach (var path in AppSettings.NormalizeRecentImages(_settings.RecentImages))
        {
            RecentImages.Add(path);
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedImagePath(e) == null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        var imagePath = GetDroppedImagePath(e);
        if (imagePath == null)
        {
            StatusText = "Drop a single .img, .imgc, .bin, or .raw image file.";
            e.Handled = true;
            return;
        }

        e.Handled = true;
        await OpenImagePathAsync(imagePath);
    }

    private static string? GetDroppedImagePath(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
        {
            return null;
        }

        return files.FirstOrDefault(IsSupportedImagePath);
    }

    private async void MetadataScan_Click(object sender, RoutedEventArgs e)
    {
        var volume = SelectedPartition?.FatxVolume;
        if (volume != null)
        {
            await RunFatxMetadataScanAsync(volume);
            return;
        }

        var ntfsVolume = SelectedPartition?.NtfsVolume;
        if (ntfsVolume != null)
        {
            await RunNtfsMetadataScanAsync(ntfsVolume);
            return;
        }

        var playStationVolume = SelectedPartition?.PlayStationVolume;
        if (playStationVolume != null)
        {
            await RunPlayStationMetadataScanAsync(playStationVolume);
            return;
        }

        var genericVolume = SelectedPartition?.GenericVolume;
        if (genericVolume != null)
        {
            await RunGenericMetadataScanAsync(genericVolume);
        }
    }

    private async Task RunFatxMetadataScanAsync(Volume volume)
    {
        if (!volume.Mounted || _isMetadataScanRunning)
        {
            return;
        }

        var clusterMultiplier = Math.Max(1, _settings.MetadataIntervalClusters);
        var searchInterval = (long)volume.BytesPerCluster * clusterMultiplier;
        var scanLength = volume.FileAreaLength;
        var totalSteps = Math.Max(1L, scanLength / searchInterval);
        var progressWatch = Stopwatch.StartNew();
        var lastDisplayedPercent = -1;
        var metadataWorkerCount = Math.Clamp(
            _settings.MetadataParallelWorkers,
            1,
            Math.Max(1, Environment.ProcessorCount));
        const string progressLogKey = "metadata-scan-progress";

        _isMetadataScanRunning = true;
        _metadataScanCancellation = new CancellationTokenSource();
        var cancellationToken = _metadataScanCancellation.Token;
        UpdateTaskState();
        IsScanProgressVisible = true;
        var progressRow = GetOrCreateScanProgressRow("Metadata scan");
        progressRow.Update(0, $"0% - 0 / {totalSteps:N0}");
        SelectLogTab();
        StatusText = "Scanning metadata...";
        AppendLog($"Metadata scan started: {totalSteps:N0} scan steps, interval 0x{searchInterval:X}.");
        AppendLog($"Metadata scan using {metadataWorkerCount} parallel reader(s).");
        BeginLiveLog(progressLogKey, $"Metadata scan progress: 0% (0/{totalSteps:N0}, 00:00).");

        var progress = new Progress<int>(current =>
        {
            var clamped = Math.Clamp((long)current, 0, totalSteps);
            var percent = totalSteps == 0 ? 100 : (int)Math.Round(clamped * 100.0 / totalSteps);
            var value = Math.Clamp(percent, 0, 100);
            progressRow.Update(value, $"{value:0}% - {clamped:N0} / {totalSteps:N0}");
            StatusText = $"Scanning metadata... {value:0}%";

            if (value != lastDisplayedPercent)
            {
                lastDisplayedPercent = value;
                UpdateLiveLog(progressLogKey, $"Metadata scan progress: {value}% ({clamped:N0}/{totalSteps:N0}, {progressWatch.Elapsed:mm\\:ss}).");
            }
        });

        try
        {
            var rows = await Task.Run(() =>
            {
                var analyzer = new MetadataAnalyzer(volume, searchInterval, scanLength);
                var entries = !string.IsNullOrWhiteSpace(_activeRawImagePath) && File.Exists(_activeRawImagePath)
                    ? analyzer.AnalyzeParallel(
                        () => new PositionedClusterDataReader(_activeRawImagePath, volume),
                        cancellationToken,
                        progress,
                        metadataWorkerCount)
                    : analyzer.Analyze(cancellationToken, progress);
                cancellationToken.ThrowIfCancellationRequested();
                return new MetadataScanResult(
                    entries.Select(entry => new FileRow(entry, volume, source: "Recovered")).ToList(),
                    entries);
            });

            MetadataResults.Clear();
            foreach (var row in rows.Rows)
            {
                MetadataResults.Add(row);
            }

            ShowMetadataResultsInFileTable(
                $"{SelectedPartition?.Name ?? "FATX"} metadata",
                $"{SelectedPartition?.Name ?? "FATX"} metadata scan: {MetadataResults.Count:N0} entries found");
            BuildRecoveryViews(volume, rows.Entries);
            progressRow.Update(100, $"100% - {totalSteps:N0} / {totalSteps:N0}");
            StatusText = _isFileCarverRunning ? "File carver still running..." : "Ready";
            AppendLog($"Metadata scan complete: {MetadataResults.Count} entries found.");
            AppendLog($"Recovery View updated: {RecoveryTreeRoots.Count} cluster groups, {ClusterRows.Count} occupied clusters.");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Metadata scan canceled";
            progressRow.Update(progressRow.Value, "Canceled - metadata scan");
            UpdateLiveLog(progressLogKey, $"Metadata scan canceled ({progressWatch.Elapsed:mm\\:ss}).");
            AppendLog("Metadata scan canceled.");
        }
        catch (Exception ex)
        {
            StatusText = "Failed";
            AppendLog($"Metadata scan failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            progressWatch.Stop();
            _isMetadataScanRunning = false;
            _metadataScanCancellation?.Dispose();
            _metadataScanCancellation = null;
            UpdateTaskState();
            RefreshSelectionState();
        }
    }

    private async Task RunNtfsMetadataScanAsync(XboxNtfsVolume volume)
    {
        if (_isMetadataScanRunning)
        {
            return;
        }

        var progressWatch = Stopwatch.StartNew();
        var lastDisplayedPercent = -1;
        var lastTotal = 0;
        const string progressLogKey = "metadata-scan-progress";

        _isMetadataScanRunning = true;
        _metadataScanCancellation = new CancellationTokenSource();
        var cancellationToken = _metadataScanCancellation.Token;
        UpdateTaskState();
        IsScanProgressVisible = true;
        var progressRow = GetOrCreateScanProgressRow("Metadata scan");
        progressRow.Update(0, "Indexing NTFS MFT...");
        SelectLogTab();
        StatusText = "Scanning NTFS metadata...";
        AppendLog($"NTFS metadata scan started: {volume.Name}.");
        BeginLiveLog(progressLogKey, "NTFS metadata scan progress: indexing MFT records...");

        var progress = new Progress<NtfsMetadataScanProgress>(current =>
        {
            lastTotal = Math.Max(lastTotal, current.Total);
            var total = Math.Max(1, current.Total);
            var clamped = Math.Clamp(current.Current, 0, total);
            var percent = (int)Math.Round(clamped * 100.0 / total);
            var value = Math.Clamp(percent, 0, 100);
            progressRow.Update(value, $"{value:0}% - {clamped:N0} / {total:N0} MFT records");
            StatusText = $"Scanning NTFS metadata... {value:0}%";

            if (value != lastDisplayedPercent)
            {
                lastDisplayedPercent = value;
                UpdateLiveLog(progressLogKey, $"NTFS metadata scan progress: {value}% ({clamped:N0}/{total:N0}, {progressWatch.Elapsed:mm\\:ss}).");
            }
        });

        try
        {
            var rows = await Task.Run(() => volume.ScanMetadata(cancellationToken, progress), cancellationToken);

            MetadataResults.Clear();
            foreach (var row in rows.Select(entry => new FileRow(entry, source: "Metadata")))
            {
                MetadataResults.Add(row);
            }

            var deletedCount = MetadataResults.Count(row => row.IsDeleted);
            var metadataCount = MetadataResults.Count(row => row.NtfsEntry?.IsNtfsMetadata == true);
            ShowMetadataResultsInFileTable(
                $"{volume.Name} MFT metadata",
                $"{volume.Name} MFT metadata: {MetadataResults.Count:N0} entries, {deletedCount:N0} deleted, {metadataCount:N0} NTFS metadata records");
            RecoveryTreeRoots.Clear();
            RecoveryRows.Clear();
            if (ClusterViewerPanel.Visibility == Visibility.Visible)
            {
                PopulateClusterViewerForSelectedPartition();
            }

            _recoveryDatabase = null;
            _recoveryIntegrity = null;

            progressRow.Update(100, $"100% - {Math.Max(lastTotal, MetadataResults.Count):N0} MFT records");
            StatusText = _isFileCarverRunning ? "File carver still running..." : "Ready";
            AppendLog($"NTFS metadata scan complete: {MetadataResults.Count:N0} MFT entries, {deletedCount:N0} deleted, {metadataCount:N0} NTFS metadata records.");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Metadata scan canceled";
            progressRow.Update(progressRow.Value, $"Canceled - {volume.Name} MFT metadata");
            UpdateLiveLog(progressLogKey, $"NTFS metadata scan canceled ({progressWatch.Elapsed:mm\\:ss}).");
            AppendLog($"NTFS metadata scan canceled: {volume.Name}.");
        }
        catch (Exception ex)
        {
            StatusText = "Failed";
            AppendLog($"NTFS metadata scan failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            progressWatch.Stop();
            _isMetadataScanRunning = false;
            _metadataScanCancellation?.Dispose();
            _metadataScanCancellation = null;
            UpdateTaskState();
            RefreshSelectionState();
        }
    }

    private async Task RunPlayStationMetadataScanAsync(PlayStationVolume volume)
    {
        if (_isMetadataScanRunning)
        {
            return;
        }

        var progressWatch = Stopwatch.StartNew();
        var lastDisplayedPercent = -1;
        const string progressLogKey = "metadata-scan-progress";

        _isMetadataScanRunning = true;
        _metadataScanCancellation = new CancellationTokenSource();
        var cancellationToken = _metadataScanCancellation.Token;
        UpdateTaskState();
        IsScanProgressVisible = true;
        var progressRow = GetOrCreateScanProgressRow("Metadata scan");
        progressRow.Update(0, "Decrypting PlayStation partition...");
        SelectLogTab();
        StatusText = "Scanning PlayStation metadata...";
        AppendLog($"PlayStation metadata scan started: {volume.Name}.");
        BeginLiveLog(progressLogKey, "PlayStation metadata scan progress: decrypting partition...");

        var progress = new Progress<int>(current =>
        {
            var total = Math.Max(1L, volume.Length / 0x800);
            var clamped = Math.Clamp((long)current, 0, total);
            var percent = (int)Math.Round(clamped * 100.0 / total);
            var value = Math.Clamp(percent, 0, 100);
            progressRow.Update(value, $"{value:0}% - {clamped:N0} / {total:N0} blocks");
            StatusText = $"Scanning PlayStation metadata... {value:0}%";
            if (value != lastDisplayedPercent)
            {
                lastDisplayedPercent = value;
                UpdateLiveLog(progressLogKey, $"PlayStation metadata scan progress: {value}% ({clamped:N0}/{total:N0}, {progressWatch.Elapsed:mm\\:ss}).");
            }
        });

        try
        {
            var result = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var activeRows = volume.ScanMetadata()
                    .Select(entry => new FileRow(entry, "Metadata"))
                    .ToList();
                cancellationToken.ThrowIfCancellationRequested();

                var deletedCandidates = new List<FileRow>();
                try
                {
                    deletedCandidates.AddRange(volume.ScanDeletedInodes()
                        .Select(entry => new FileRow(entry, volume.Name, volume)));
                }
                catch
                {
                    // Some PlayStation partitions are FAT or otherwise not UFS2; active metadata remains useful.
                }

                var existingDecryptedPath = TryGetExistingPlayStationDecryptedPartition(volume);
                var hasDecryptedCache = !string.IsNullOrWhiteSpace(existingDecryptedPath);
                if (!string.IsNullOrWhiteSpace(existingDecryptedPath))
                {
                    var scanner = new Ps4UfsDirentScanner(existingDecryptedPath, volume.Offset);
                    deletedCandidates = scanner.Analyze(cancellationToken, progress)
                        .Where(entry => entry.IsDeleted)
                        .GroupBy(entry => $"{entry.Offset:X}:{entry.Name}", StringComparer.OrdinalIgnoreCase)
                        .Select(group => new FileRow(group.First(), volume.Name, volume))
                        .Concat(deletedCandidates)
                        .GroupBy(row => row.Identity, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First())
                        .ToList();
                }

                return new
                {
                    Rows = activeRows.Concat(deletedCandidates).ToList(),
                    ActiveCount = activeRows.Count,
                    DeletedCandidateCount = deletedCandidates.Count,
                    ScannedDeletedCandidates = deletedCandidates.Count > 0 || hasDecryptedCache || IsPlayStationFatDeletedCandidateScanRelevant(volume),
                    DeletedCandidateSkipReason = IsPlayStationUfsDeletedCandidateScanRelevant(volume)
                        ? "deleted UFS dirent slack scan requires a decrypted partition cache; run File Carver on this partition once to create the temporary cache"
                        : IsPlayStationFatDeletedCandidateScanRelevant(volume)
                            ? "no deleted FAT directory entries found"
                        : "deleted UFS dirent slack scan is not applicable to this non-UFS partition"
                };
            }, cancellationToken);

            MetadataResults.Clear();
            foreach (var row in result.Rows)
            {
                MetadataResults.Add(row);
            }

            ShowMetadataResultsInFileTable(
                $"{volume.Name} metadata",
                result.ScannedDeletedCandidates
                    ? $"{volume.Name} metadata scan: {result.ActiveCount:N0} active entries, {result.DeletedCandidateCount:N0} deleted metadata candidates"
                    : $"{volume.Name} metadata scan: {result.ActiveCount:N0} active entries; {result.DeletedCandidateSkipReason}");
            RecoveryTreeRoots.Clear();
            RecoveryRows.Clear();
            if (ClusterViewerPanel.Visibility == Visibility.Visible)
            {
                PopulateClusterViewerForSelectedPartition();
            }

            _recoveryDatabase = null;
            _recoveryIntegrity = null;

            progressRow.Update(100, "100%");
            StatusText = _isFileCarverRunning ? "File carver still running..." : "Ready";
            AppendLog(result.ScannedDeletedCandidates
                ? $"PlayStation metadata scan complete: {result.ActiveCount:N0} active entries, {result.DeletedCandidateCount:N0} deleted metadata candidates."
                : $"PlayStation metadata scan complete: {result.ActiveCount:N0} active entries. {result.DeletedCandidateSkipReason}.");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Metadata scan canceled";
            progressRow.Update(progressRow.Value, $"Canceled - {volume.Name} metadata");
            UpdateLiveLog(progressLogKey, $"PlayStation metadata scan canceled ({progressWatch.Elapsed:mm\\:ss}).");
            AppendLog($"PlayStation metadata scan canceled: {volume.Name}.");
        }
        catch (Exception ex)
        {
            StatusText = "Failed";
            AppendLog($"PlayStation metadata scan failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            progressWatch.Stop();
            _isMetadataScanRunning = false;
            _metadataScanCancellation?.Dispose();
            _metadataScanCancellation = null;
            UpdateTaskState();
            RefreshSelectionState();
        }
    }

    private async Task RunGenericMetadataScanAsync(GenericFileSystemVolume volume)
    {
        if (_isMetadataScanRunning)
        {
            return;
        }

        _isMetadataScanRunning = true;
        _metadataScanCancellation = new CancellationTokenSource();
        var cancellationToken = _metadataScanCancellation.Token;
        UpdateTaskState();
        IsScanProgressVisible = true;
        var progressRow = GetOrCreateScanProgressRow("Metadata scan");
        progressRow.Update(0, $"Scanning {volume.FamilyText} deleted entries...");
        SelectLogTab();
        StatusText = $"Scanning {volume.FamilyText} metadata...";
        AppendLog($"{volume.FamilyText} metadata scan started: {volume.Name}.");

        var progress = new Progress<int>(current =>
        {
            progressRow.Update(50, $"{current:N0} deleted entries found");
            StatusText = $"Scanning {volume.FamilyText} metadata... {current:N0} found";
        });

        try
        {
            var rows = await Task.Run(() => volume.ScanDeleted(cancellationToken, progress), cancellationToken);

            MetadataResults.Clear();
            foreach (var row in rows.Select(entry => new FileRow(entry, source: "Metadata")))
            {
                MetadataResults.Add(row);
            }

            ShowMetadataResultsInFileTable(
                $"{volume.Name} metadata",
                $"{volume.Name} metadata scan: {MetadataResults.Count:N0} deleted entries found");
            RecoveryTreeRoots.Clear();
            RecoveryRows.Clear();
            if (ClusterViewerPanel.Visibility == Visibility.Visible)
            {
                PopulateClusterViewerForSelectedPartition();
            }

            _recoveryDatabase = null;
            _recoveryIntegrity = null;
            progressRow.Update(100, $"100% - {MetadataResults.Count:N0} deleted entries");
            StatusText = _isFileCarverRunning ? "File carver still running..." : "Ready";
            AppendLog($"{volume.FamilyText} metadata scan complete: {MetadataResults.Count:N0} deleted entries found.");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Metadata scan canceled";
            progressRow.Update(progressRow.Value, $"Canceled - {volume.FamilyText} metadata");
            AppendLog($"{volume.FamilyText} metadata scan canceled: {volume.Name}.");
        }
        catch (Exception ex)
        {
            StatusText = "Failed";
            AppendLog($"{volume.FamilyText} metadata scan failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isMetadataScanRunning = false;
            _metadataScanCancellation?.Dispose();
            _metadataScanCancellation = null;
            UpdateTaskState();
            RefreshSelectionState();
        }
    }

    private async void FileCarver_Click(object sender, RoutedEventArgs e)
    {
        var volume = SelectedPartition?.FatxVolume;
        if (volume != null)
        {
            await RunFatxFileCarverAsync(volume);
            return;
        }

        var ntfsVolume = SelectedPartition?.NtfsVolume;
        if (ntfsVolume != null)
        {
            await RunGenericFileCarverAsync(
                "Xbox NTFS",
                ntfsVolume.Name,
                _activeRawImagePath ?? string.Empty,
                ntfsVolume.Offset,
                ntfsVolume.Length,
                ntfsVolume.Offset,
                requiresExistingPath: true);
            return;
        }

        var playStationVolume = SelectedPartition?.PlayStationVolume;
        if (playStationVolume != null)
        {
            await RunPlayStationFileCarverAsync(playStationVolume);
            return;
        }

        var genericVolume = SelectedPartition?.GenericVolume;
        if (genericVolume != null)
        {
            if (genericVolume is SwitchFat32Volume switchVolume)
            {
                await RunSwitchFileCarverAsync(switchVolume);
                return;
            }

            await RunGenericFileCarverAsync(
                genericVolume.FamilyText,
                genericVolume.Name,
                _activeRawImagePath ?? genericVolume.SourcePath,
                genericVolume.Offset,
                genericVolume.Length,
                genericVolume.Offset,
                requiresExistingPath: true);
        }
    }

    private async Task RunSwitchFileCarverAsync(SwitchFat32Volume volume)
    {
        if (_isFileCarverRunning)
        {
            return;
        }

        _isFileCarverRunning = true;
        _fileCarverCancellation = new CancellationTokenSource();
        var cancellationToken = _fileCarverCancellation.Token;
        var progressWatch = Stopwatch.StartNew();
        const string progressLogKey = "file-carver-progress";
        var totalBytes = Math.Max(1L, volume.Length);
        var lastDisplayedPercent = -1;

        UpdateTaskState();
        IsScanProgressVisible = true;
        var progressRow = GetOrCreateScanProgressRow("File carver");
        progressRow.Update(0, $"Decrypting 0% - 0 / {MainWindowFormat.Bytes(totalBytes)}");
        SelectLogTab();
        StatusText = $"Decrypting Switch partition for carving: {volume.Name}...";
        AppendLog($"Decrypting Switch partition for file carver: {volume.Name}, {MainWindowFormat.Bytes(totalBytes)}.");
        BeginLiveLog(progressLogKey, $"Switch decrypt progress: 0% (0 / {MainWindowFormat.Bytes(totalBytes)}, 00:00).");

        var progress = new Progress<long>(bytes =>
        {
            var clamped = Math.Clamp(bytes, 0, totalBytes);
            var percent = (int)Math.Round(clamped * 100.0 / totalBytes);
            var value = Math.Clamp(percent, 0, 100);
            progressRow.Update(value, $"Decrypting {value:0}% - {MainWindowFormat.Bytes(clamped)} / {MainWindowFormat.Bytes(totalBytes)}");
            StatusText = $"Decrypting Switch partition for carving... {value:0}%";

            if (value != lastDisplayedPercent)
            {
                lastDisplayedPercent = value;
                UpdateLiveLog(progressLogKey, $"Switch decrypt progress: {value}% ({MainWindowFormat.Bytes(clamped)} / {MainWindowFormat.Bytes(totalBytes)}, {progressWatch.Elapsed:mm\\:ss}).");
            }
        });

        string decryptedPath;
        try
        {
            decryptedPath = await Task.Run(() => volume.CreateDecryptedPartitionImage(cancellationToken, progress), cancellationToken);
            _temporaryScanFiles.Add(decryptedPath);
            progressRow.Update(100, $"Decrypting 100% - {MainWindowFormat.Bytes(totalBytes)} / {MainWindowFormat.Bytes(totalBytes)}");
            UpdateLiveLog(progressLogKey, $"Switch decrypt progress: 100% ({MainWindowFormat.Bytes(totalBytes)} / {MainWindowFormat.Bytes(totalBytes)}, {progressWatch.Elapsed:mm\\:ss}).");
        }
        catch (OperationCanceledException)
        {
            StatusText = "File carver canceled";
            progressRow.Update(progressRow.Value, $"Canceled - {volume.Name} decrypt");
            UpdateLiveLog(progressLogKey, $"Switch decrypt canceled ({progressWatch.Elapsed:mm\\:ss}).");
            AppendLog($"Switch file carver canceled while decrypting partition: {volume.Name}.");
            _isFileCarverRunning = false;
            _fileCarverCancellation?.Dispose();
            _fileCarverCancellation = null;
            UpdateTaskState();
            RefreshSelectionState();
            return;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or UnauthorizedAccessException)
        {
            StatusText = "Failed";
            AppendLog($"Switch partition decrypt failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            _isFileCarverRunning = false;
            _fileCarverCancellation?.Dispose();
            _fileCarverCancellation = null;
            UpdateTaskState();
            RefreshSelectionState();
            return;
        }
        finally
        {
            progressWatch.Stop();
        }

        _isFileCarverRunning = false;
        _fileCarverCancellation?.Dispose();
        _fileCarverCancellation = null;
        UpdateTaskState();

        await RunGenericFileCarverAsync(
            volume.FamilyText,
            volume.Name,
            decryptedPath,
            0,
            volume.Length,
            volume.Offset,
            requiresExistingPath: true);
    }

    private async Task RunFatxFileCarverAsync(Volume volume)
    {
        if (!volume.Mounted || _isFileCarverRunning)
        {
            return;
        }

        var scanLength = volume.FileAreaLength;
        var interval = Math.Max(1L, (long)_settings.FileCarverInterval);
        var totalSteps = Math.Max(1L, scanLength / interval);
        var progressWatch = Stopwatch.StartNew();
        var lastDisplayedPercent = -1;
        const string progressLogKey = "file-carver-progress";

        _isFileCarverRunning = true;
        _fileCarverCancellation = new CancellationTokenSource();
        var cancellationToken = _fileCarverCancellation.Token;
        UpdateTaskState();
        IsScanProgressVisible = true;
        var progressRow = GetOrCreateScanProgressRow("File carver");
        progressRow.Update(0, $"0% - 0 / {totalSteps:N0}");
        SelectLogTab();
        StatusText = "Carving files...";
        AppendLog($"File carver started: {totalSteps:N0} scan steps, interval 0x{interval:X}, profile {_settings.ScanProfile}. Profile selection does not change the selected interval.");
        BeginLiveLog(progressLogKey, $"File carver progress: 0% (0/{totalSteps:N0}, 00:00).");

        var progress = new Progress<int>(current =>
        {
            var clamped = Math.Clamp((long)current, 0, totalSteps);
            var percent = totalSteps == 0 ? 100 : (int)Math.Round(clamped * 100.0 / totalSteps);
            var value = Math.Clamp(percent, 0, 100);
            progressRow.Update(value, $"{value:0}% - {clamped:N0} / {totalSteps:N0}");
            StatusText = $"Carving files... {value:0}%";

            if (value != lastDisplayedPercent)
            {
                lastDisplayedPercent = value;
                UpdateLiveLog(progressLogKey, $"File carver progress: {value}% ({clamped:N0}/{totalSteps:N0}, {progressWatch.Elapsed:mm\\:ss}).");
            }
        });

        try
        {
            var activeRawImagePath = _activeRawImagePath;
            var result = await Task.Run(() =>
            {
                if (!string.IsNullOrWhiteSpace(activeRawImagePath) && File.Exists(activeRawImagePath))
                {
                    var sourceOffset = volume.Offset + volume.FileAreaByteOffset;
                    var fastCarver = new GenericFileCarver(activeRawImagePath, sourceOffset, scanLength, sourceOffset, interval, $"FATX {volume.Name}", _settings.ScanProfile);
                    fastCarver.SetCustomSignatures(LoadCustomCarversForProfile());
                    var fastRows = fastCarver.Analyze(cancellationToken, progress)
                        .Select(file => new CarvedFileRow(file))
                        .ToList();

                    return (rows: fastRows, badOffsetCount: 0, errorCount: 0);
                }

                var carver = new FileCarver(volume, _settings.FileCarverInterval, scanLength);
                carver.SetCustomSignatures(LoadCustomCarversForProfile());
                var rows = carver.Analyze(cancellationToken, progress)
                    .Select(signature => new CarvedFileRow(signature, volume))
                    .ToList();

                return (rows, badOffsetCount: carver.BadOffsets.Count, errorCount: carver.Errors.Count);
            });

            CarvedFiles.Clear();
            foreach (var row in result.rows)
            {
                CarvedFiles.Add(row);
            }

            progressRow.Update(100, $"100% - {totalSteps:N0} / {totalSteps:N0}");
            StatusText = _isMetadataScanRunning ? "Metadata scan still running..." : "Ready";
            AppendLog($"File carver complete: {CarvedFiles.Count} files found, {result.badOffsetCount} bad offsets, {result.errorCount} errors.");
        }
        catch (OperationCanceledException)
        {
            StatusText = "File carver canceled";
            progressRow.Update(progressRow.Value, "Canceled - file carver");
            UpdateLiveLog(progressLogKey, $"File carver canceled ({progressWatch.Elapsed:mm\\:ss}).");
            AppendLog("File carver canceled.");
        }
        catch (Exception ex)
        {
            StatusText = "Failed";
            AppendLog($"File carver failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            progressWatch.Stop();
            _isFileCarverRunning = false;
            _fileCarverCancellation?.Dispose();
            _fileCarverCancellation = null;
            UpdateTaskState();
            RefreshSelectionState();
        }
    }

    private async Task RunPlayStationFileCarverAsync(PlayStationVolume volume)
    {
        if (_isFileCarverRunning)
        {
            return;
        }

        _isFileCarverRunning = true;
        _fileCarverCancellation = new CancellationTokenSource();
        var cancellationToken = _fileCarverCancellation.Token;
        UpdateTaskState();
        IsScanProgressVisible = true;
        var progressRow = GetOrCreateScanProgressRow("File carver");
        progressRow.Update(0, "Decrypting PlayStation partition...");
        SelectLogTab();
        StatusText = "Preparing PlayStation file carver...";
        AppendLog($"Decrypting PlayStation partition for file carver: {volume.Name}.");
        string decryptedPath;
        try
        {
            decryptedPath = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = EnsurePlayStationDecryptedPartition(volume);
                cancellationToken.ThrowIfCancellationRequested();
                return path;
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            StatusText = "File carver canceled";
            progressRow.Update(progressRow.Value, $"Canceled - {volume.Name} file carver");
            AppendLog($"PlayStation file carver canceled while preparing partition: {volume.Name}.");
            _isFileCarverRunning = false;
            _fileCarverCancellation?.Dispose();
            _fileCarverCancellation = null;
            UpdateTaskState();
            RefreshSelectionState();
            return;
        }
        catch (Exception ex)
        {
            StatusText = "Failed";
            AppendLog($"PlayStation partition decrypt failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            _isFileCarverRunning = false;
            _fileCarverCancellation?.Dispose();
            _fileCarverCancellation = null;
            UpdateTaskState();
            RefreshSelectionState();
            return;
        }

        _isFileCarverRunning = false;
        _fileCarverCancellation?.Dispose();
        _fileCarverCancellation = null;
        UpdateTaskState();
        await RunGenericFileCarverAsync(
            "PlayStation",
            volume.Name,
            decryptedPath,
            0,
            volume.Length,
            volume.Offset,
            requiresExistingPath: true);
    }

    private async Task RunGenericFileCarverAsync(
        string family,
        string partitionName,
        string sourcePath,
        long sourceOffset,
        long scanLength,
        long displayBaseOffset,
        bool requiresExistingPath)
    {
        if (_isFileCarverRunning)
        {
            return;
        }

        if (requiresExistingPath && (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)))
        {
            StatusText = "No readable image source is available for this partition.";
            return;
        }

        var interval = Math.Max(1L, (long)_settings.FileCarverInterval);
        var totalSteps = Math.Max(1L, scanLength / interval);
        var progressWatch = Stopwatch.StartNew();
        var lastDisplayedPercent = -1;
        const string progressLogKey = "file-carver-progress";

        _isFileCarverRunning = true;
        _fileCarverCancellation = new CancellationTokenSource();
        var cancellationToken = _fileCarverCancellation.Token;
        UpdateTaskState();
        IsScanProgressVisible = true;
        var progressRow = GetOrCreateScanProgressRow("File carver");
        progressRow.Update(0, $"0% - 0 / {totalSteps:N0}");
        SelectLogTab();
        StatusText = $"Carving {family} files...";
        AppendLog($"{family} file carver started: {partitionName}, {totalSteps:N0} scan steps, interval 0x{interval:X}, profile {_settings.ScanProfile}. Profile selection does not change the selected interval.");
        BeginLiveLog(progressLogKey, $"{family} file carver progress: 0% (0/{totalSteps:N0}, 00:00).");

        var progress = new Progress<int>(current =>
        {
            var clamped = Math.Clamp((long)current, 0, totalSteps);
            var percent = totalSteps == 0 ? 100 : (int)Math.Round(clamped * 100.0 / totalSteps);
            var value = Math.Clamp(percent, 0, 100);
            progressRow.Update(value, $"{value:0}% - {clamped:N0} / {totalSteps:N0}");
            StatusText = $"Carving {family} files... {value:0}%";

            if (value != lastDisplayedPercent)
            {
                lastDisplayedPercent = value;
                UpdateLiveLog(progressLogKey, $"{family} file carver progress: {value}% ({clamped:N0}/{totalSteps:N0}, {progressWatch.Elapsed:mm\\:ss}).");
            }
        });

        try
        {
            var rows = await Task.Run(() =>
            {
                var carver = new GenericFileCarver(sourcePath, sourceOffset, scanLength, displayBaseOffset, interval, $"{family} {partitionName}", _settings.ScanProfile);
                carver.SetCustomSignatures(LoadCustomCarversForProfile());
                return carver.Analyze(cancellationToken, progress)
                    .Select(file => new CarvedFileRow(file))
                    .ToList();
            }, cancellationToken);

            CarvedFiles.Clear();
            foreach (var row in rows)
            {
                CarvedFiles.Add(row);
            }

            progressRow.Update(100, $"100% - {totalSteps:N0} / {totalSteps:N0}");
            StatusText = _isMetadataScanRunning ? "Metadata scan still running..." : "Ready";
            AppendLog($"{family} file carver complete: {CarvedFiles.Count:N0} files found.");
        }
        catch (OperationCanceledException)
        {
            StatusText = "File carver canceled";
            progressRow.Update(progressRow.Value, $"Canceled - {family} file carver");
            UpdateLiveLog(progressLogKey, $"{family} file carver canceled ({progressWatch.Elapsed:mm\\:ss}).");
            AppendLog($"{family} file carver canceled: {partitionName}.");
        }
        catch (Exception ex)
        {
            StatusText = "Failed";
            AppendLog($"{family} file carver failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            progressWatch.Stop();
            _isFileCarverRunning = false;
            _fileCarverCancellation?.Dispose();
            _fileCarverCancellation = null;
            UpdateTaskState();
            RefreshSelectionState();
        }
    }

    private IReadOnlyList<CustomSignatureDefinition> LoadCustomCarversForProfile()
    {
        return _settings.ScanProfile == ScanProfile.Fast
            ? Array.Empty<CustomSignatureDefinition>()
            : CustomSignatureLoader.Load(_settings.CustomCarversFile);
    }

    private async void SaveSelected_Click(object sender, RoutedEventArgs e)
    {
        var fileRows = GetSelectedFileRows();
        if (fileRows.Count > 0)
        {
            await SaveFileRowsAsync(fileRows);
            return;
        }

        var recoveryRows = GetSelectedRecoveryRows();
        if (recoveryRows.Count > 0)
        {
            await SaveRecoveryRowsAsync(recoveryRows);
            return;
        }

        var carvedRows = GetSelectedCarvedRows();
        if (carvedRows.Count > 0)
        {
            await SaveCarvedRowsAsync(carvedRows);
            return;
        }

        StatusText = "Select files or folders to save.";
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        SearchCurrentResults();
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

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1)
        {
            NavigateBackDirectory();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.XButton2)
        {
            NavigateForwardDirectory();
            e.Handled = true;
        }

        var focusedTextBox = FindVisualParent<TextBox>(e.OriginalSource as DependencyObject);
        if (SearchBox.IsKeyboardFocused && focusedTextBox != SearchBox)
        {
            Keyboard.ClearFocus();
            UpdateSearchPlaceholderVisibility();
        }
    }

    private void ToggleWindowMaximized()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        ApplyWindowStatePadding();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExportRunning)
        {
            MessageBox.Show(this, "Wait for the current export to finish before closing Drive Assistant.", AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            e.Cancel = true;
            return;
        }

        _closingMainWindow = true;
        if (_drive == null && _xboxStorageImage == null && _playStationStorageImage == null && _genericFileSystemImage == null && _switchStorageImage == null && _nintendoStorageImage == null && _ps2StorageImage == null && _legacyConsoleStorageImage == null && _databaseSnapshot == null)
        {
            if (_detachedResultsWindow != null)
            {
                DockResultsTabs();
            }

            return;
        }

        var result = MessageBox.Show(
            this,
            "Would you like to save progress before closing?",
            "Save Progress",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Cancel)
        {
            _closingMainWindow = false;
            e.Cancel = true;
            return;
        }

        if (result == MessageBoxResult.Yes && !SaveProgressDatabase())
        {
            _closingMainWindow = false;
            e.Cancel = true;
            return;
        }

        if (_detachedResultsWindow != null)
        {
            DockResultsTabs();
        }

        ClearTemporaryScanFiles();
        _drive?.Dispose();
        _xboxStorageImage?.Dispose();
        _playStationStorageImage?.Dispose();
        _genericFileSystemImage?.Dispose();
        _switchStorageImage?.Dispose();
        _nintendoStorageImage?.Dispose();
        _ps2StorageImage?.Dispose();
        _legacyConsoleStorageImage?.Dispose();
    }

    private void ApplyWindowStatePadding()
    {
        RootShell.Margin = WindowState == WindowState.Maximized
            ? new Thickness(6)
            : new Thickness(0);
    }

    private void MinimizePartitions_Click(object sender, RoutedEventArgs e)
    {
        SavePanelWidth(PartitionsColumn, ref _partitionsPanelWidth);
        PartitionsPanel.Visibility = Visibility.Collapsed;
        PartitionsRail.Visibility = Visibility.Visible;
        ShowPartitionsButton.Visibility = Visibility.Collapsed;
        PartitionsColumn.Width = new GridLength(44);
        PartitionsSplitter.Visibility = Visibility.Collapsed;
        PartitionsSplitterColumn.Width = new GridLength(0);
    }

    private void ClosePartitions_Click(object sender, RoutedEventArgs e)
    {
        SavePanelWidth(PartitionsColumn, ref _partitionsPanelWidth);
        PartitionsPanel.Visibility = Visibility.Collapsed;
        PartitionsRail.Visibility = Visibility.Collapsed;
        ShowPartitionsButton.Visibility = Visibility.Visible;
        PartitionsColumn.Width = new GridLength(0);
        PartitionsSplitter.Visibility = Visibility.Collapsed;
        PartitionsSplitterColumn.Width = new GridLength(0);
    }

    private void RestorePartitions_Click(object sender, RoutedEventArgs e)
    {
        PartitionsPanel.Visibility = Visibility.Visible;
        PartitionsRail.Visibility = Visibility.Collapsed;
        ShowPartitionsButton.Visibility = Visibility.Collapsed;
        PartitionsColumn.Width = EnsureUsableWidth(_partitionsPanelWidth, 280);
        PartitionsSplitter.Visibility = Visibility.Visible;
        PartitionsSplitterColumn.Width = new GridLength(6);
    }

    private void MinimizeInspector_Click(object sender, RoutedEventArgs e)
    {
        SavePanelWidth(InspectorColumn, ref _inspectorPanelWidth);
        InspectorPanel.Visibility = Visibility.Collapsed;
        InspectorRail.Visibility = Visibility.Visible;
        ShowInspectorButton.Visibility = Visibility.Collapsed;
        InspectorColumn.Width = new GridLength(44);
        InspectorSplitter.Visibility = Visibility.Collapsed;
        InspectorSplitterColumn.Width = new GridLength(0);
    }

    private void CloseInspector_Click(object sender, RoutedEventArgs e)
    {
        SavePanelWidth(InspectorColumn, ref _inspectorPanelWidth);
        InspectorPanel.Visibility = Visibility.Collapsed;
        InspectorRail.Visibility = Visibility.Collapsed;
        ShowInspectorButton.Visibility = Visibility.Visible;
        InspectorColumn.Width = new GridLength(0);
        InspectorSplitter.Visibility = Visibility.Collapsed;
        InspectorSplitterColumn.Width = new GridLength(0);
    }

    private void RestoreInspector_Click(object sender, RoutedEventArgs e)
    {
        InspectorPanel.Visibility = Visibility.Visible;
        InspectorRail.Visibility = Visibility.Collapsed;
        ShowInspectorButton.Visibility = Visibility.Collapsed;
        InspectorColumn.Width = EnsureUsableWidth(_inspectorPanelWidth, 320);
        InspectorSplitter.Visibility = Visibility.Visible;
        InspectorSplitterColumn.Width = new GridLength(6);
    }

    private void ToggleClusterViewer_Click(object sender, RoutedEventArgs e)
    {
        SetClusterViewerVisible(ClusterViewerPanel.Visibility != Visibility.Visible);
    }

    private void TogglePartitionsShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (PartitionsPanel.Visibility == Visibility.Visible)
        {
            ClosePartitions_Click(sender, e);
        }
        else
        {
            RestorePartitions_Click(sender, e);
        }
    }

    private void ToggleInspectorShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (InspectorPanel.Visibility == Visibility.Visible)
        {
            CloseInspector_Click(sender, e);
        }
        else
        {
            RestoreInspector_Click(sender, e);
        }
    }

    private void SearchShortcut_Click(object sender, RoutedEventArgs e)
    {
        ExecuteShortcut(ShortcutCatalog.Search);
    }

    private void ToggleResultsWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_detachedResultsWindow == null)
        {
            DetachResultsTabs();
        }
        else
        {
            DockResultsTabs();
        }
    }

    private void ResultsTabs_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _resultsTabDragStart = null;
        _resultsTabDragItem = null;

        if (_detachedResultsWindow != null ||
            e.GetPosition(ResultsTabs).Y > 36 ||
            FindVisualParent<TabItem>(e.OriginalSource as DependencyObject) is not { } tab ||
            (tab != RecoveryTab && tab != CarverTab))
        {
            return;
        }

        _resultsTabDragStart = e.GetPosition(this);
        _resultsTabDragItem = tab;
    }

    private void ResultsTabs_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _resultsTabDragStart = null;
        _resultsTabDragItem = null;
    }

    private void ResultsTabs_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_detachedResultsWindow != null ||
            e.LeftButton != MouseButtonState.Pressed ||
            _resultsTabDragStart is not { } start ||
            _resultsTabDragItem == null)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var draggedTab = _resultsTabDragItem;
        _resultsTabDragStart = null;
        _resultsTabDragItem = null;
        ResultsTabs.SelectedItem = draggedTab;
        DetachResultsTabs(draggedTab, PointToScreen(current));
        e.Handled = true;
    }

    private void DetachResultsTabs(TabItem? selectedTab = null, Point? screenLocation = null)
    {
        if (_detachedResultsWindow != null)
        {
            _detachedResultsWindow.Activate();
            return;
        }

        _detachedResultsWindow = new DetachedResultsWindow
        {
            Owner = this,
            DataContext = this
        };
        _detachedResultsWindow.DockRequested += DetachedResultsWindow_DockRequested;
        _detachedResultsWindow.Closing += DetachedResultsWindow_Closing;

        ResultsTabs.Items.Remove(RecoveryTab);
        ResultsTabs.Items.Remove(CarverTab);
        _detachedResultsWindow.DetachedTabs.Items.Add(RecoveryTab);
        _detachedResultsWindow.DetachedTabs.Items.Add(CarverTab);
        _detachedResultsWindow.DetachedTabs.SelectedItem = selectedTab == CarverTab ? CarverTab : RecoveryTab;
        ResultsTabs.SelectedItem = LogTab;
        ToggleResultsWindowText.Text = "Dock Results";
        ResultsPanelDetachText.Text = "Dock";
        SetResultsWindowToolTips(detached: true);
        _detachedResultsWindow.Show();
        if (screenLocation is { } location)
        {
            _detachedResultsWindow.Left = Math.Max(0, location.X - 64);
            _detachedResultsWindow.Top = Math.Max(0, location.Y - 18);
        }
    }

    private void DetachedResultsWindow_DockRequested(object? sender, EventArgs e)
    {
        DockResultsTabs();
    }

    private void DetachedResultsWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingMainWindow)
        {
            return;
        }

        e.Cancel = true;
        DockResultsTabs();
    }

    private void DockResultsTabs()
    {
        if (_detachedResultsWindow == null)
        {
            return;
        }

        var window = _detachedResultsWindow;
        window.DockRequested -= DetachedResultsWindow_DockRequested;
        window.Closing -= DetachedResultsWindow_Closing;

        window.DetachedTabs.Items.Remove(RecoveryTab);
        window.DetachedTabs.Items.Remove(CarverTab);
        var logIndex = ResultsTabs.Items.IndexOf(LogTab);
        if (logIndex < 0)
        {
            logIndex = ResultsTabs.Items.Count;
        }

        ResultsTabs.Items.Insert(logIndex, RecoveryTab);
        ResultsTabs.Items.Insert(logIndex + 1, CarverTab);
        ResultsTabs.SelectedItem = RecoveryTab;
        ToggleResultsWindowText.Text = "Pop Out Results";
        ResultsPanelDetachText.Text = "Pop Out";
        SetResultsWindowToolTips(detached: false);
        _detachedResultsWindow = null;
        window.Close();
    }

    private void SetResultsWindowToolTips(bool detached)
    {
        var title = detached ? "Dock Results" : "Pop Out Results";
        var description = detached
            ? "Move Recovery View and Carved Files back into the main window."
            : "Move Recovery View and Carved Files into a resizable window.";

        ToggleResultsWindowButton.ToolTip = CreateResultsWindowToolTip(title, description, PlacementMode.Bottom);
        ResultsPanelDetachButton.ToolTip = CreateResultsWindowToolTip(title, description, PlacementMode.Left);
    }

    private static ToolTip CreateResultsWindowToolTip(string title, string description, PlacementMode placement)
    {
        var body = new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

        var content = new StackPanel
        {
            MaxWidth = 260
        };
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold
        });
        content.Children.Add(body);

        return new ToolTip
        {
            Content = content,
            MaxWidth = 280,
            Placement = placement
        };
    }

    private void SetClusterViewerVisible(bool visible)
    {
        if (visible)
        {
            PopulateClusterViewerForSelectedPartition();
        }

        ClusterViewerPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        FileExplorerPanel.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        ClusterViewerToggleText.Text = visible ? "File Explorer" : "Cluster Viewer";
        ClusterViewerToggleButton.ToolTip = visible ? "Show filesystem browser" : "Show cluster viewer";
    }

    private void ClusterMapCell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ClusterMapCell { IsInRange: true } cell })
        {
            return;
        }

        var row = new ClusterRow(cell.AddressSpace, cell.Cluster, cell.Occupants);
        SelectedCluster = row;
        if (cell.RecoveryFiles.Count > 0)
        {
            PopulateRecoveryRows(cell.RecoveryFiles);
        }

        StatusText = $"Cluster {cell.Cluster}: {cell.Status}";
        e.Handled = true;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            _settings = dialog.Result;
            _settings.Save();
            WpfTheme.Apply(_settings.Theme);
            AppLogger.Configure(_settings.LogFile, _settings.EnableFileLogging);
            ConfigureShortcutBindings();
            AppendLog("Settings saved.");
        }
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.DataContext = DataContext;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ConfigureShortcutBindings()
    {
        InputBindings.Clear();
        _shortcutCommands.Clear();
        foreach (var definition in ShortcutCatalog.All)
        {
            var command = new UiActionCommand(() => ExecuteShortcut(definition.Id), () => CanExecuteShortcut(definition.Id));
            _shortcutCommands[definition.Id] = command;
            var gestureText = ShortcutCatalog.GetGestureText(_settings.Shortcuts, definition.Id);
            if (ShortcutCatalog.TryParseGesture(gestureText, out var gesture) && gesture != null)
            {
                InputBindings.Add(new KeyBinding(command, gesture));
            }
        }

        UpdateShortcutMenuText();
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateShortcutMenuText()
    {
        OpenImageMenuItem.InputGestureText = ShortcutCatalog.GetGestureText(_settings.Shortcuts, ShortcutCatalog.OpenImage);
        LoadDatabaseMenuItem.InputGestureText = ShortcutCatalog.GetGestureText(_settings.Shortcuts, ShortcutCatalog.LoadDatabase);
        SaveDatabaseMenuItem.InputGestureText = ShortcutCatalog.GetGestureText(_settings.Shortcuts, ShortcutCatalog.SaveDatabase);
        SaveSelectedMenuItem.InputGestureText = ShortcutCatalog.GetGestureText(_settings.Shortcuts, ShortcutCatalog.SaveSelected);
        SearchMenuItem.InputGestureText = ShortcutCatalog.GetGestureText(_settings.Shortcuts, ShortcutCatalog.Search);
        MetadataScanMenuItem.InputGestureText = ShortcutCatalog.GetGestureText(_settings.Shortcuts, ShortcutCatalog.MetadataScan);
        FileCarverMenuItem.InputGestureText = ShortcutCatalog.GetGestureText(_settings.Shortcuts, ShortcutCatalog.FileCarver);
        TogglePartitionsMenuItem.InputGestureText = ShortcutCatalog.GetGestureText(_settings.Shortcuts, ShortcutCatalog.TogglePartitions);
        ToggleInspectorMenuItem.InputGestureText = ShortcutCatalog.GetGestureText(_settings.Shortcuts, ShortcutCatalog.ToggleInspector);
        ToggleClusterViewerMenuItem.InputGestureText = ShortcutCatalog.GetGestureText(_settings.Shortcuts, ShortcutCatalog.ToggleClusterViewer);
        ToggleResultsWindowMenuItem.InputGestureText = ShortcutCatalog.GetGestureText(_settings.Shortcuts, ShortcutCatalog.ToggleResultsWindow);
        AddPartitionMenuItem.InputGestureText = ShortcutCatalog.GetGestureText(_settings.Shortcuts, ShortcutCatalog.AddPartition);
        UnmountPartitionMenuItem.InputGestureText = ShortcutCatalog.GetGestureText(_settings.Shortcuts, ShortcutCatalog.UnmountPartition);
        SettingsMenuItem.InputGestureText = ShortcutCatalog.GetGestureText(_settings.Shortcuts, ShortcutCatalog.Settings);
        AboutMenuItem.InputGestureText = ShortcutCatalog.GetGestureText(_settings.Shortcuts, ShortcutCatalog.About);
    }

    private bool CanExecuteShortcut(string id)
    {
        return id switch
        {
            ShortcutCatalog.LoadDatabase or ShortcutCatalog.SaveDatabase => HasOpenDatabase,
            ShortcutCatalog.SaveSelected => HasSelection,
            ShortcutCatalog.MetadataScan => CanRunMetadataScan,
            ShortcutCatalog.FileCarver => CanRunFileCarver,
            ShortcutCatalog.AddPartition => CanAddCustomPartition,
            ShortcutCatalog.UnmountPartition => CanUnmountPartition,
            _ => true
        };
    }

    private void ExecuteShortcut(string id)
    {
        var routed = new RoutedEventArgs();
        switch (id)
        {
            case ShortcutCatalog.OpenImage:
                OpenImage_Click(this, routed);
                break;
            case ShortcutCatalog.LoadDatabase:
                LoadDatabase_Click(this, routed);
                break;
            case ShortcutCatalog.SaveDatabase:
                SaveDatabase_Click(this, routed);
                break;
            case ShortcutCatalog.SaveSelected:
                SaveSelected_Click(this, routed);
                break;
            case ShortcutCatalog.Search:
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case ShortcutCatalog.MetadataScan:
                MetadataScan_Click(this, routed);
                break;
            case ShortcutCatalog.FileCarver:
                FileCarver_Click(this, routed);
                break;
            case ShortcutCatalog.AddPartition:
                AddCustomPartition_Click(this, routed);
                break;
            case ShortcutCatalog.UnmountPartition:
                UnmountPartition_Click(this, routed);
                break;
            case ShortcutCatalog.TogglePartitions:
                TogglePartitionsShortcut_Click(this, routed);
                break;
            case ShortcutCatalog.ToggleInspector:
                ToggleInspectorShortcut_Click(this, routed);
                break;
            case ShortcutCatalog.ToggleClusterViewer:
                ToggleClusterViewer_Click(this, routed);
                break;
            case ShortcutCatalog.ToggleResultsWindow:
                ToggleResultsWindow_Click(this, routed);
                break;
            case ShortcutCatalog.Settings:
                Settings_Click(this, routed);
                break;
            case ShortcutCatalog.About:
                About_Click(this, routed);
                break;
        }
    }

    private void About_Click(object sender, RoutedEventArgs e) 
    { 
        var aboutWindow = new AboutWindow
        {
            Owner = this
        };
        aboutWindow.ShowDialog();
    } 

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Keyboard.ClearFocus();
            UpdateSearchPlaceholderVisibility();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            SearchCurrentResults();
            e.Handled = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchPlaceholderVisibility();
    }

    private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        UpdateSearchPlaceholderVisibility();
    }

    private void SearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        UpdateSearchPlaceholderVisibility();
    }

    private void UpdateSearchPlaceholderVisibility()
    {
        SearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text) && !SearchBox.IsKeyboardFocused
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void PartitionList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindVisualParent<ListBoxItem>((DependencyObject)e.OriginalSource);
        if (item?.DataContext is not PartitionModel partition)
        {
            e.Handled = true;
            return;
        }

        PartitionList.SelectedItem = partition;
        SelectedPartition = partition;
        RefreshSelectionState();
    }

    private void PartitionList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (SelectedPartition?.IsMounted != true)
        {
            e.Handled = true;
        }
    }

    private void PartitionMetadataScan_Click(object sender, RoutedEventArgs e)
    {
        MetadataScan_Click(sender, e);
    }

    private void PartitionFileCarver_Click(object sender, RoutedEventArgs e)
    {
        FileCarver_Click(sender, e);
    }

    private void PartitionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PartitionList.SelectedItem is PartitionModel partition && !ReferenceEquals(partition, SelectedPartition))
        {
            SelectedPartition = partition;
        }
    }

    private void DirectoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeSelectionNavigation)
        {
            return;
        }

        if (e.NewValue is DirectoryNode node)
        {
            if (node.BrowseEntries != null)
            {
                PopulateFiles(node);
                _currentDirectory = null;
            }
            else
            {
                NavigateToDirectory(node.NavigationEntry, addHistory: true, syncTree: false);
            }

            UpdateInspector(node);
        }
    }

    private void FilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid && grid.SelectedItem is FileRow row)
        {
            SelectedFile = row;
        }

        RefreshSelectionState();
    }

    private void CarverGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CarverGrid.SelectedItem is CarvedFileRow row)
        {
            SelectedCarvedFile = row;
        }

        RefreshSelectionState();
    }

    private void DirectoryTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!HasActiveVolume)
        {
            e.Handled = true;
            return;
        }

        if (FindVisualParent<TreeViewItem>(e.OriginalSource as DependencyObject) is { } item)
        {
            item.IsSelected = true;
            item.Focus();
            return;
        }

        e.Handled = true;
    }

    private void DataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!HasActiveVolume || sender is not DataGrid grid)
        {
            e.Handled = true;
            return;
        }

        if (FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject) is not { } row)
        {
            e.Handled = true;
            return;
        }

        row.Focus();
        if (!row.IsSelected)
        {
            grid.SelectedItems.Clear();
            row.IsSelected = true;
            grid.SelectedItem = row.Item;
        }
    }

    private void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!HasActiveVolume ||
            FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject) is not { Item: FileRow { IsFolder: true } row })
        {
            return;
        }

        FilesGrid.SelectedItem = row;
        SelectedFile = row;
        NavigateToDirectory(row.NavigationEntry, addHistory: true, syncTree: true);
        UpdateInspector(row);
        StatusText = $"Opened folder: {row.Name}";
        e.Handled = true;
    }

    private void DirectoryTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!HasActiveVolume || DirectoryTree.SelectedItem is not DirectoryNode)
        {
            e.Handled = true;
        }
    }

    private void FileGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!HasActiveVolume ||
            sender is not DataGrid grid ||
            grid.SelectedItems.OfType<FileRow>().Any() == false)
        {
            e.Handled = true;
        }
    }

    private void CarverGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!HasActiveVolume ||
            sender is not DataGrid grid ||
            grid.SelectedItems.OfType<CarvedFileRow>().Any() == false)
        {
            e.Handled = true;
        }
    }

    private void RecoveryGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!HasActiveVolume ||
            sender is not DataGrid grid ||
            grid.SelectedItems.OfType<RecoveryFileRow>().Any() == false)
        {
            e.Handled = true;
        }
    }

    private async void SaveDirectoryTreeSelection_Click(object sender, RoutedEventArgs e)
    {
        if (DirectoryTree.SelectedItem is DirectoryNode node)
        {
            await SaveDirectoryNodeAsync(node);
        }
    }

    private async void SaveFileGridSelection_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuGrid(sender, out var grid))
        {
            await SaveFileRowsAsync(grid.SelectedItems.OfType<FileRow>().ToList());
        }
    }

    private async void SaveCarverGridSelection_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuGrid(sender, out var grid))
        {
            await SaveCarvedRowsAsync(grid.SelectedItems.OfType<CarvedFileRow>().ToList());
        }
    }

    private async void SaveRecoveryGridSelection_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuGrid(sender, out var grid))
        {
            await SaveRecoveryRowsAsync(grid.SelectedItems.OfType<RecoveryFileRow>().ToList());
        }
    }

    private void CopyFileGridOffset_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuGrid(sender, out var grid))
        {
            CopyRowsToClipboard(grid.SelectedItems.OfType<FileRow>().Select(row => $"{row.Name}\t{row.OffsetText}"), "Copied file offsets.");
        }
    }

    private void CopyFileGridFirstCluster_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuGrid(sender, out var grid))
        {
            CopyRowsToClipboard(
                grid.SelectedItems.OfType<FileRow>().Select(row => $"{row.Name}\t{FormatFileRowFirstCluster(row)}"),
                "Copied file first clusters.");
        }
    }

    private void CopyFileGridAddressDetails_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuGrid(sender, out var grid))
        {
            CopyRowsToClipboard(grid.SelectedItems.OfType<FileRow>().Select(FormatFileRowAddressDetails), "Copied file address details.");
        }
    }

    private void CopyRecoveryGridOffset_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuGrid(sender, out var grid))
        {
            CopyRowsToClipboard(grid.SelectedItems.OfType<RecoveryFileRow>().Select(row => $"{row.Name}\t{row.OffsetText}"), "Copied recovery offsets.");
        }
    }

    private void CopyRecoveryGridFirstCluster_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuGrid(sender, out var grid))
        {
            CopyRowsToClipboard(
                grid.SelectedItems.OfType<RecoveryFileRow>().Select(row => $"{row.Name}\t{FormatRecoveryRowFirstCluster(row)}"),
                "Copied recovery first clusters.");
        }
    }

    private void CopyRecoveryGridAddressDetails_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuGrid(sender, out var grid))
        {
            CopyRowsToClipboard(grid.SelectedItems.OfType<RecoveryFileRow>().Select(FormatRecoveryRowAddressDetails), "Copied recovery address details.");
        }
    }

    private void CopyCarverGridOffset_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuGrid(sender, out var grid))
        {
            CopyRowsToClipboard(grid.SelectedItems.OfType<CarvedFileRow>().Select(row => $"{row.Name}\t{row.OffsetText}"), "Copied carved file offsets.");
        }
    }

    private void CopyCarverGridRelativeOffset_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuGrid(sender, out var grid))
        {
            CopyRowsToClipboard(grid.SelectedItems.OfType<CarvedFileRow>().Select(row => $"{row.Name}\t{row.RelativeOffsetText}"), "Copied carved file relative offsets.");
        }
    }

    private void CopyCarverGridAddressDetails_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuGrid(sender, out var grid))
        {
            CopyRowsToClipboard(grid.SelectedItems.OfType<CarvedFileRow>().Select(FormatCarvedRowAddressDetails), "Copied carved file address details.");
        }
    }

    private void LoadSelectedPartition()
    {
        DirectoryRoots.Clear();
        Files.Clear();
        SelectedFile = null;
        SelectedCarvedFile = null;
        ClusterMapRows = Array.Empty<ClusterMapRow>();
        ClusterViewerSummary = "Open Cluster Viewer to inspect allocation units for the selected partition.";

        if (SelectedPartition is not { IsMounted: true } partition)
        {
            DirectoryTree.ItemsSource = DirectoryRoots;
            StatusText = SelectedPartition == null ? "Ready" : SelectedPartition.Status;
            CurrentFileSystemTitle = "ORIGINAL FILESYSTEM";
            CurrentDirectorySummary = "No mounted filesystem selected.";
            return;
        }

        var root = partition.FatxVolume != null
            ? DirectoryNode.CreateRoot(partition.FatxVolume)
            : partition.NtfsVolume != null
                ? DirectoryNode.CreateRoot(partition.NtfsVolume)
                : partition.PlayStationVolume != null
                    ? DirectoryNode.CreateRoot(partition.PlayStationVolume)
                    : partition.GenericVolume != null
                        ? DirectoryNode.CreateRoot(partition.GenericVolume)
                        : DirectoryNode.CreateRoot(partition.SnapshotPartition!);
        DirectoryRoots.Add(root);
        DirectoryTree.ItemsSource = DirectoryRoots;
        _directoryBackStack.Clear();
        _directoryForwardStack.Clear();
        _currentDirectory = null;
        NavigateToDirectory(null, addHistory: false, syncTree: true);
        StatusText = $"{partition.Name}: {FormatBytes(partition.UsedSpace)} used of {FormatBytes(partition.TotalSpace)}";
        if (SelectedPartition != null)
        {
            UpdateInspector(SelectedPartition);
        }

        if (ClusterViewerPanel.Visibility == Visibility.Visible)
        {
            PopulateClusterViewerForSelectedPartition();
        }
    }

    private void NavigateToDirectory(object? directory, bool addHistory, bool syncTree)
    {
        if (addHistory && !IsSameDirectory(_currentDirectory, directory))
        {
            _directoryBackStack.Push(_currentDirectory);
            _directoryForwardStack.Clear();
        }

        _currentDirectory = directory;
        PopulateFiles(directory);

        if (syncTree)
        {
            SelectDirectoryInTree(directory);
        }
    }

    private void NavigateBackDirectory()
    {
        if (!HasActiveVolume)
        {
            return;
        }

        if (_directoryBackStack.Count == 0)
        {
            StatusText = "No previous folder.";
            return;
        }

        var previousDirectory = _directoryBackStack.Pop();
        _directoryForwardStack.Push(_currentDirectory);
        NavigateToDirectory(previousDirectory, addHistory: false, syncTree: true);
        StatusText = $"Back: {GetDirectoryDisplayName(previousDirectory)}";
    }

    private void NavigateForwardDirectory()
    {
        if (!HasActiveVolume)
        {
            return;
        }

        if (_directoryForwardStack.Count == 0)
        {
            StatusText = "No next folder.";
            return;
        }

        var nextDirectory = _directoryForwardStack.Pop();
        _directoryBackStack.Push(_currentDirectory);
        NavigateToDirectory(nextDirectory, addHistory: false, syncTree: true);
        StatusText = $"Forward: {GetDirectoryDisplayName(nextDirectory)}";
    }

    private void PopulateFiles(object? directory)
    {
        Files.Clear();
        if (SelectedPartition is not { IsMounted: true } partition)
        {
            return;
        }

        var folderCount = 0;
        var fileCount = 0;
        if (partition.FatxVolume != null)
        {
            var fatxDirectory = directory as DirectoryEntry;
            var entries = fatxDirectory?.Children ?? partition.FatxVolume.GetRoot();
            foreach (var entry in entries)
            {
                if (entry.IsDirectory())
                {
                    folderCount++;
                }
                else
                {
                    fileCount++;
                }

                Files.Add(new FileRow(entry, partition.FatxVolume, source: "Active"));
            }
        }
        else if (partition.NtfsVolume != null)
        {
            var ntfsDirectory = directory as XboxFileEntry;
            foreach (var entry in partition.NtfsVolume.GetChildren(ntfsDirectory))
            {
                if (entry.IsDirectory)
                {
                    folderCount++;
                }
                else
                {
                    fileCount++;
                }

                Files.Add(new FileRow(entry, source: "Active"));
            }
        }
        else if (partition.PlayStationVolume != null)
        {
            var playStationDirectory = directory as PlayStationFileEntry;
            foreach (var entry in partition.PlayStationVolume.GetChildren(playStationDirectory))
            {
                if (entry.IsDirectory)
                {
                    folderCount++;
                }
                else
                {
                    fileCount++;
                }

                Files.Add(new FileRow(entry, source: "Active"));
            }
        }
        else if (partition.GenericVolume != null)
        {
            var genericDirectory = directory as GenericFileSystemEntry;
            foreach (var entry in partition.GenericVolume.GetChildren(genericDirectory))
            {
                if (entry.IsDirectory)
                {
                    folderCount++;
                }
                else
                {
                    fileCount++;
                }

                Files.Add(new FileRow(entry, source: "Active"));
            }
        }
        else if (partition.SnapshotPartition != null)
        {
            var snapshotDirectory = directory as SnapshotFileEntry;
            foreach (var entry in partition.SnapshotPartition.GetChildren(snapshotDirectory))
            {
                if (entry.IsDirectory)
                {
                    folderCount++;
                }
                else
                {
                    fileCount++;
                }

                Files.Add(new FileRow(entry, source: "Active"));
            }
        }

        var directoryName = GetDirectoryDisplayName(directory);
        CurrentFileSystemTitle = $"{partition.Name.ToUpperInvariant()} FILESYSTEM";
        CurrentDirectorySummary = $"{partition.Name} / {directoryName}: {folderCount} folders, {fileCount} files";
    }

    private void ShowMetadataResultsInFileTable(string title, string summary)
    {
        Files.Clear();
        foreach (var row in MetadataResults)
        {
            Files.Add(row);
        }

        FilesGrid.SelectedItem = null;
        SelectedFile = null;
        CurrentFileSystemTitle = title.ToUpperInvariant();
        CurrentDirectorySummary = summary;
    }

    private void PopulateFiles(DirectoryNode node)
    {
        Files.Clear();
        var volume = SelectedPartition?.FatxVolume;
        if (volume == null || node.BrowseEntries == null)
        {
            return;
        }

        var folderCount = 0;
        var fileCount = 0;
        foreach (var entry in node.BrowseEntries.OrderBy(entry => entry.IsDirectory() ? 0 : 1).ThenBy(entry => entry.FileName))
        {
            if (entry.IsDirectory())
            {
                folderCount++;
            }
            else
            {
                fileCount++;
            }

            Files.Add(new FileRow(entry, volume, source: "Recovered", _recoveryDatabase?.GetFile(entry)));
        }

        CurrentDirectorySummary = $"{node.Name}: {folderCount} recovered folders, {fileCount} recovered files";
    }

    private void BuildRecoveryViews(Volume volume, IReadOnlyCollection<DirectoryEntry> recoveredEntries)
    {
        var database = new FileDatabase(volume);
        database.Reset();
        foreach (var entry in recoveredEntries)
        {
            database.AddFile(entry, deleted: true);
        }

        database.Update();
        var integrity = new IntegrityAnalyzer(volume, database);
        integrity.Update();

        _recoveryDatabase = database;
        _recoveryIntegrity = integrity;

        PopulateRecoveryTree(database.GetRootFiles());
        PopulateClusterViewer(volume, database, integrity);
        AddRecoveryClustersToDirectoryTree(database.GetRootFiles());

        RecoveryRows.Clear();
        if (RecoveryTreeRoots.FirstOrDefault() is { } firstRoot)
        {
            PopulateRecoveryRows(firstRoot.Files);
        }
    }

    private void AddRecoveryClustersToDirectoryTree(IReadOnlyCollection<DatabaseFile> rootFiles)
    {
        var root = DirectoryRoots.FirstOrDefault(node => node.IsRoot);
        if (root == null)
        {
            return;
        }

        foreach (var existing in root.Children.Where(node => node.IsRecoveredClusterGroup).ToList())
        {
            root.Children.Remove(existing);
        }

        foreach (var group in rootFiles.GroupBy(file => file.Cluster).OrderBy(group => group.Key))
        {
            var entries = group.Select(file => file.GetDirent()).ToList();
            if (entries.Count > 0)
            {
                root.Children.Add(DirectoryNode.CreateRecoveryCluster(group.Key, entries, root));
            }
        }
    }

    private void PopulateRecoveryTree(IReadOnlyCollection<DatabaseFile> rootFiles)
    {
        RecoveryTreeRoots.Clear();
        foreach (var group in rootFiles.GroupBy(file => file.Cluster).OrderBy(group => group.Key))
        {
            var files = group.ToList();
            var clusterNode = RecoveryTreeNode.CreateCluster(group.Key, files);
            foreach (var file in files.Where(file => file.IsDirectory()).OrderBy(file => file.FileName))
            {
                AddRecoveryTreeFile(clusterNode, file);
            }

            RecoveryTreeRoots.Add(clusterNode);
        }
    }

    private static void AddRecoveryTreeFile(RecoveryTreeNode parent, DatabaseFile file)
    {
        var node = RecoveryTreeNode.CreateFile(file);
        parent.Children.Add(node);
        foreach (var child in file.Children.Where(child => child.IsDirectory()).OrderBy(child => child.FileName))
        {
            AddRecoveryTreeFile(node, child);
        }
    }

    private void PopulateRecoveryRows(IEnumerable<DatabaseFile> files)
    {
        RecoveryRows.Clear();
        foreach (var file in files.OrderBy(file => file.IsDirectory() ? 0 : 1).ThenBy(file => file.FileName))
        {
            var volume = _recoveryDatabase?.GetVolume();
            if (volume != null)
            {
                RecoveryRows.Add(new RecoveryFileRow(file, volume));
            }
        }
    }

    private void PopulateClusterViewer(Volume volume, FileDatabase database, IntegrityAnalyzer integrity)
    {
        var occupancy = BuildFatxClusterOccupancy(database);
        var addressSpace = ClusterMapAddressSpace.CreateFatx(volume);
        PopulateClusterViewer(addressSpace, occupancy);
    }

    private void PopulateClusterViewerForSelectedPartition()
    {
        if (SelectedPartition is not { IsMounted: true } partition)
        {
            ClusterRows.Clear();
            ClusterMapRows = Array.Empty<ClusterMapRow>();
            ClusterViewerSummary = "No mounted filesystem selected.";
            return;
        }

        if (partition.FatxVolume != null)
        {
            var volume = partition.FatxVolume;
            if (_recoveryDatabase?.GetVolume() == volume && _recoveryIntegrity != null)
            {
                PopulateClusterViewer(volume, _recoveryDatabase, _recoveryIntegrity);
                return;
            }

            var database = new FileDatabase(volume);
            database.Update();
            var integrity = new IntegrityAnalyzer(volume, database);
            integrity.Update();
            PopulateClusterViewer(volume, database, integrity);
            return;
        }

        if (partition.NtfsVolume != null)
        {
            PopulateClusterViewer(
                ClusterMapAddressSpace.CreateNtfs(partition.NtfsVolume),
                BuildNtfsClusterOccupancy(partition.NtfsVolume));
            return;
        }

        if (partition.PlayStationVolume != null)
        {
            PopulateClusterViewer(
                ClusterMapAddressSpace.CreatePlayStation(partition.PlayStationVolume),
                BuildPlayStationClusterOccupancy(partition.PlayStationVolume));
            return;
        }

        if (partition.GenericVolume != null)
        {
            PopulateClusterViewer(
                ClusterMapAddressSpace.CreateGeneric(partition.GenericVolume),
                BuildGenericClusterOccupancy(partition.GenericVolume));
            return;
        }

        if (partition.SnapshotPartition != null)
        {
            PopulateClusterViewer(
                ClusterMapAddressSpace.CreateSnapshot(partition.SnapshotPartition),
                BuildSnapshotClusterOccupancy(partition.SnapshotPartition));
        }
    }

    private void PopulateClusterViewer(ClusterMapAddressSpace addressSpace, ClusterOccupancyMap occupancy)
    {
        ClusterRows.Clear();
        var clusters = occupancy.DisplayClusters
            .Append(addressSpace.RootCluster ?? uint.MaxValue)
            .Where(cluster => addressSpace.Contains(cluster))
            .Distinct()
            .OrderBy(cluster => cluster);

        foreach (var cluster in clusters)
        {
            ClusterRows.Add(new ClusterRow(addressSpace, cluster, occupancy.GetOccupants(cluster)));
        }

        var fullRowCount = (int)Math.Ceiling(addressSpace.ClusterCount / (double)ClusterMapColumns);
        var displayRowStarts = occupancy.GetDisplayRowStartClusters(addressSpace, ClusterMapColumns);
        if (displayRowStarts.Count == 0 && fullRowCount > 0)
        {
            displayRowStarts = Enumerable.Range(0, Math.Min(fullRowCount, 256))
                .Select(row => addressSpace.FirstCluster + (uint)(row * ClusterMapColumns))
                .ToList();
        }

        ClusterMapRows = displayRowStarts
            .Select(startCluster => new ClusterMapRow(addressSpace, startCluster, ClusterMapColumns, occupancy))
            .ToList();
        var rowSummary = ClusterMapRows.Count < fullRowCount
            ? $"{ClusterMapRows.Count:N0}/{fullRowCount:N0} map rows shown; empty rows omitted, large extents sampled"
            : $"{ClusterMapRows.Count:N0} map rows shown";
        var colorSummary = "green=allocated/active file data, yellow=deleted/recovered, red=overlap, gray=free/unknown";
        ClusterViewerSummary = $"{addressSpace.Name}: {addressSpace.ClusterCount:N0} {addressSpace.UnitName.ToLowerInvariant()}s, {ClusterRows.Count:N0} listed units. {rowSummary}; {colorSummary}.";
    }

    private static ClusterOccupancyMap BuildFatxClusterOccupancy(FileDatabase database)
    {
        var occupancy = new ClusterOccupancyMap();
        foreach (var file in database.GetFiles().Values)
        {
            var clusters = file.ClusterChain?.Count > 0 ? file.ClusterChain : [file.Cluster];
            var occupant = ClusterOccupant.FromFatx(file);
            foreach (var cluster in clusters.Where(cluster => cluster > 0))
            {
                occupancy.Add(cluster, occupant);
            }
        }

        return occupancy;
    }

    private ClusterOccupancyMap BuildNtfsClusterOccupancy(XboxNtfsVolume volume)
    {
        var occupancy = new ClusterOccupancyMap();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddNtfsBitmapOccupancy(volume, occupancy);

        foreach (var entry in WalkNtfs(volume, null))
        {
            AddNtfsEntryOccupancy(volume, entry, occupancy, seen);
        }

        foreach (var row in MetadataResults.Where(row => row.NtfsEntry != null && ReferenceEquals(row.NtfsEntry.Volume, volume)))
        {
            AddNtfsEntryOccupancy(volume, row.NtfsEntry!, occupancy, seen);
        }

        return occupancy;
    }

    private static void AddNtfsBitmapOccupancy(XboxNtfsVolume volume, ClusterOccupancyMap occupancy)
    {
        var occupant = ClusterOccupant.FromNtfsAllocationBitmap(volume);
        foreach (var run in volume.ReadAllocationRuns())
        {
            if (run.StartCluster < 0 || run.ClusterCount <= 0 || run.StartCluster > uint.MaxValue)
            {
                continue;
            }

            var maxRunCount = uint.MaxValue - run.StartCluster + 1;
            var endCluster = run.ClusterCount >= maxRunCount
                ? uint.MaxValue
                : run.StartCluster + run.ClusterCount - 1;
            occupancy.AddRangeCompact((uint)run.StartCluster, (uint)endCluster, occupant);
        }
    }

    private static void AddNtfsEntryOccupancy(
        XboxNtfsVolume volume,
        XboxFileEntry entry,
        ClusterOccupancyMap occupancy,
        HashSet<string> seen)
    {
        if (entry.IsDirectory || entry.Extents.Count == 0 || volume.ClusterSize <= 0)
        {
            return;
        }

        var identity = $"{entry.MftRecordIndex}:{entry.SequenceNumber}:{entry.Path}:{entry.IsDeleted}";
        if (!seen.Add(identity))
        {
            return;
        }

        var occupant = ClusterOccupant.FromNtfs(entry);
        foreach (var extent in entry.Extents)
        {
            AddExtentOccupancy(occupancy, occupant, extent.Offset, extent.Length, volume.Offset, volume.ClusterSize);
        }
    }

    private static IEnumerable<XboxFileEntry> WalkNtfs(XboxNtfsVolume volume, XboxFileEntry? directory)
    {
        IReadOnlyList<XboxFileEntry> children;
        try
        {
            children = volume.GetChildren(directory);
        }
        catch
        {
            yield break;
        }

        foreach (var child in children)
        {
            yield return child;
            if (!child.IsDirectory)
            {
                continue;
            }

            foreach (var nested in WalkNtfs(volume, child))
            {
                yield return nested;
            }
        }
    }

    private static ClusterOccupancyMap BuildPlayStationClusterOccupancy(PlayStationVolume volume)
    {
        var occupancy = new ClusterOccupancyMap();
        foreach (var entry in WalkPlayStation(volume, null))
        {
            if (entry.IsDirectory || entry.Length <= 0)
            {
                continue;
            }

            var occupant = ClusterOccupant.FromPlayStation(entry);
            foreach (var extent in GetPlayStationExtents(entry))
            {
                AddExtentOccupancy(occupancy, occupant, extent.Offset, extent.Length, volume.Offset, ClusterMapAddressSpace.PlayStationUnitSize);
            }
        }

        return occupancy;
    }

    private static IEnumerable<PlayStationFileEntry> WalkPlayStation(PlayStationVolume volume, PlayStationFileEntry? directory)
    {
        IReadOnlyList<PlayStationFileEntry> children;
        try
        {
            children = volume.GetChildren(directory);
        }
        catch
        {
            yield break;
        }

        foreach (var child in children)
        {
            yield return child;
            if (!child.IsDirectory)
            {
                continue;
            }

            foreach (var nested in WalkPlayStation(volume, child))
            {
                yield return nested;
            }
        }
    }

    private static ClusterOccupancyMap BuildGenericClusterOccupancy(GenericFileSystemVolume volume)
    {
        var occupancy = new ClusterOccupancyMap();
        foreach (var entry in WalkGeneric(volume, null))
        {
            if (entry.IsDirectory || entry.Extents.Count == 0)
            {
                continue;
            }

            var occupant = ClusterOccupant.FromGeneric(entry);
            foreach (var extent in entry.Extents)
            {
                AddExtentOccupancy(occupancy, occupant, extent.Offset, extent.Length, volume.Offset, volume.ClusterSize);
            }
        }

        return occupancy;
    }

    private static IEnumerable<GenericFileSystemEntry> WalkGeneric(GenericFileSystemVolume volume, GenericFileSystemEntry? directory)
    {
        IReadOnlyList<GenericFileSystemEntry> children;
        try
        {
            children = volume.GetChildren(directory);
        }
        catch
        {
            yield break;
        }

        foreach (var child in children)
        {
            yield return child;
            if (!child.IsDirectory)
            {
                continue;
            }

            foreach (var nested in WalkGeneric(volume, child))
            {
                yield return nested;
            }
        }
    }

    private static IReadOnlyList<FileExtent> GetPlayStationExtents(PlayStationFileEntry entry)
    {
        var extents = ParseExtentText(entry.DataRanges, entry.Length);
        if (extents.Count > 0)
        {
            return extents;
        }

        var unitSize = entry.EstimatedAllocationUnit > 0
            ? entry.EstimatedAllocationUnit
            : ClusterMapAddressSpace.PlayStationUnitSize;
        extents = ParseOffsetList(entry.DataOffsets, unitSize);
        if (extents.Count > 0)
        {
            return extents;
        }

        return entry.Offset > 0 ? [new FileExtent(entry.Offset, entry.Length)] : [];
    }

    private static ClusterOccupancyMap BuildSnapshotClusterOccupancy(SnapshotPartition partition)
    {
        var occupancy = new ClusterOccupancyMap();
        foreach (var entry in WalkSnapshot(partition.GetRoot()))
        {
            if (entry.IsDirectory || entry.Size <= 0)
            {
                continue;
            }

            var occupant = ClusterOccupant.FromSnapshot(entry);
            var extents = ParseExtentText(entry.Extents, entry.Size);
            if (extents.Count == 0 && entry.Offset > 0)
            {
                extents = [new FileExtent(entry.Offset, entry.Size)];
            }

            foreach (var extent in extents)
            {
                AddExtentOccupancy(occupancy, occupant, extent.Offset, extent.Length, partition.Offset, ClusterMapAddressSpace.DefaultUnitSize);
            }
        }

        return occupancy;
    }

    private static IEnumerable<SnapshotFileEntry> WalkSnapshot(IEnumerable<SnapshotFileEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
            foreach (var nested in WalkSnapshot(entry.Children))
            {
                yield return nested;
            }
        }
    }

    private static List<FileExtent> ParseExtentText(string text, long fallbackLength)
    {
        var extents = new List<FileExtent>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return extents;
        }

        foreach (var segment in text.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var values = ParseHexNumbers(segment).ToList();
            if (values.Count == 0)
            {
                continue;
            }

            if (values.Count == 1)
            {
                extents.Add(new FileExtent(values[0], Math.Max(1, fallbackLength)));
                continue;
            }

            var start = values[0];
            var second = values[1];
            var length = segment.Contains('-', StringComparison.Ordinal) && second > start
                ? second - start + 1
                : second;
            if (length > 0)
            {
                extents.Add(new FileExtent(start, length));
            }
        }

        return extents;
    }

    private static List<FileExtent> ParseOffsetList(string text, long unitSize)
    {
        return ParseHexNumbers(text)
            .Select(offset => new FileExtent(offset, Math.Max(1, unitSize)))
            .ToList();
    }

    private static IEnumerable<long> ParseHexNumbers(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match match in Regex.Matches(text, @"(?:0x)?[0-9a-fA-F]+"))
        {
            var value = match.Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? match.Value[2..]
                : match.Value;
            if (long.TryParse(value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                yield return parsed;
            }
        }
    }

    private static void AddExtentOccupancy(
        ClusterOccupancyMap occupancy,
        ClusterOccupant occupant,
        long extentOffset,
        long extentLength,
        long partitionOffset,
        long unitSize)
    {
        if (extentLength <= 0 || unitSize <= 0)
        {
            return;
        }

        var absoluteOffset = extentOffset >= partitionOffset ? extentOffset : partitionOffset + extentOffset;
        var relativeStart = Math.Max(0, absoluteOffset - partitionOffset);
        var startCluster = relativeStart / unitSize;
        var endCluster = (relativeStart + extentLength - 1) / unitSize;
        if (startCluster > uint.MaxValue)
        {
            return;
        }

        occupancy.AddRange((uint)startCluster, (uint)Math.Min(endCluster, uint.MaxValue), occupant);
    }

    private void RecoveryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not RecoveryTreeNode node)
        {
            return;
        }

        PopulateRecoveryRows(node.Files);
        if (node.File != null)
        {
            var volume = _recoveryDatabase?.GetVolume();
            if (volume != null)
            {
                UpdateInspector(new RecoveryFileRow(node.File, volume));
            }
        }
        else
        {
            InspectorTitle = node.Name;
            InspectorSubtitle = "Recovery cluster group";
            InspectorRows.Clear();
            InspectorRows.Add(new InspectorRow("Cluster", node.Cluster?.ToString() ?? string.Empty));
            InspectorRows.Add(new InspectorRow("Entries", node.Files.Count.ToString()));
        }
    }

    private void RecoveryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecoveryGrid.SelectedItem is RecoveryFileRow row)
        {
            SelectedRecoveryFile = row;
        }

        RefreshSelectionState();
    }

    private void RecoveryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject) is not { Item: RecoveryFileRow { IsFolder: true } row })
        {
            return;
        }

        PopulateRecoveryRows(row.File.Children);
        SelectedRecoveryFile = row;
        UpdateInspector(row);
        StatusText = $"Opened recovered folder: {row.Name}";
        e.Handled = true;
    }

    private void SelectDirectoryInTree(object? directory)
    {
        var node = FindDirectoryNode(directory);
        if (node == null)
        {
            return;
        }

        ExpandAndSelectDirectoryNode(node);
    }

    private DirectoryNode? FindDirectoryNode(object? directory)
    {
        foreach (var root in DirectoryRoots)
        {
            var match = FindDirectoryNode(root, directory);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static DirectoryNode? FindDirectoryNode(DirectoryNode node, object? directory)
    {
        if (directory == null && !node.IsRoot)
        {
            return null;
        }

        if (IsSameDirectory(node.NavigationEntry, directory))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var match = FindDirectoryNode(child, directory);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private void ExpandAndSelectDirectoryNode(DirectoryNode node)
    {
        var path = new Stack<DirectoryNode>();
        for (var current = node; current != null; current = current.Parent)
        {
            path.Push(current);
        }

        ItemsControl parent = DirectoryTree;
        TreeViewItem? item = null;
        _suppressTreeSelectionNavigation = true;
        try
        {
            while (path.Count > 0)
            {
                var current = path.Pop();
                parent.UpdateLayout();
                item = parent.ItemContainerGenerator.ContainerFromItem(current) as TreeViewItem;
                if (item == null)
                {
                    return;
                }

                item.IsExpanded = true;
                parent = item;
            }

            if (item != null)
            {
                item.IsSelected = true;
                item.BringIntoView();
                item.Focus();
            }
        }
        finally
        {
            _suppressTreeSelectionNavigation = false;
        }
    }

    private void SearchCurrentResults()
    {
        var query = SearchBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            StatusText = "Type a file or result name to search.";
            SearchBox.Focus();
            return;
        }

        var fileMatch = Files.FirstOrDefault(row => row.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            ?? MetadataResults.FirstOrDefault(row => row.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (fileMatch != null)
        {
            SelectedFile = fileMatch;
            if (Files.Contains(fileMatch))
            {
                FilesGrid.SelectedItem = fileMatch;
                FilesGrid.ScrollIntoView(fileMatch);
            }
            else
            {
                FilesGrid.SelectedItem = null;
            }

            StatusText = $"Found: {fileMatch.Name}";
            AppendLog($"Search matched file: {fileMatch.Name}");
            return;
        }

        var carvedMatch = CarvedFiles.FirstOrDefault(row => row.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (carvedMatch != null)
        {
            SelectedCarvedFile = carvedMatch;
            CarverGrid.SelectedItem = carvedMatch;
            CarverGrid.ScrollIntoView(carvedMatch);
            StatusText = $"Found: {carvedMatch.Name}";
            AppendLog($"Search matched carved file: {carvedMatch.Name}");
            return;
        }

        StatusText = $"No match for: {query}";
        AppendLog($"Search found no match: {query}");
    }

    private async Task SaveDirectoryNodeAsync(DirectoryNode node)
    {
        if (SelectedPartition is not { IsMounted: true } partition)
        {
            StatusText = "No mounted filesystem selected.";
            return;
        }

        var destination = PickDestinationFolder($"Save {node.Name}");
        if (destination == null)
        {
            return;
        }

        var targetPath = GetUniqueDirectoryPath(Path.Combine(destination, SanitizeFileName(node.Name)));

        var totalBytes = EstimateDirectoryNodeBytes(node, partition);
        var totalItems = CountDirectoryNodeItems(node, partition);
        await RunExportAsync(
            $"Save folder {node.Name}",
            totalBytes,
            totalItems,
            state =>
            {
                Directory.CreateDirectory(targetPath);
                if (node.BrowseEntries != null)
                {
                    var volume = partition.FatxVolume;
                    if (volume == null)
                    {
                        return;
                    }

                    foreach (var entry in node.BrowseEntries)
                    {
                        state.ThrowIfCancellationRequested();
                        WriteEntryToDirectory(volume, entry, targetPath, state);
                    }
                }
                else if (node.Entry == null && node.NtfsEntry == null && node.PlayStationEntry == null && node.GenericEntry == null && node.SnapshotEntry == null)
                {
                    if (partition.FatxVolume != null)
                    {
                        foreach (var entry in partition.FatxVolume.GetRoot())
                        {
                            state.ThrowIfCancellationRequested();
                            WriteEntryToDirectory(partition.FatxVolume, entry, targetPath, state);
                        }
                    }
                    else if (partition.NtfsVolume != null)
                    {
                        foreach (var entry in partition.NtfsVolume.GetRoot())
                        {
                            state.ThrowIfCancellationRequested();
                            WriteEntryToDirectory(entry, targetPath, state);
                        }
                    }
                    else if (partition.PlayStationVolume != null)
                    {
                        foreach (var entry in partition.PlayStationVolume.GetRoot())
                        {
                            state.ThrowIfCancellationRequested();
                            WriteEntryToDirectory(entry, targetPath, state);
                        }
                    }
                    else if (partition.GenericVolume != null)
                    {
                        foreach (var entry in partition.GenericVolume.GetRoot())
                        {
                            state.ThrowIfCancellationRequested();
                            WriteEntryToDirectory(entry, targetPath, state);
                        }
                    }
                    else if (partition.SnapshotPartition != null)
                    {
                        throw new InvalidOperationException("Loaded database snapshots do not contain file data to export.");
                    }
                }
                else if (node.Entry != null && partition.FatxVolume != null)
                {
                    WriteDirectoryTree(partition.FatxVolume, node.Entry, targetPath, state);
                }
                else if (node.NtfsEntry != null)
                {
                    WriteDirectoryTree(node.NtfsEntry, targetPath, state);
                }
                else if (node.PlayStationEntry != null)
                {
                    WriteDirectoryTree(node.PlayStationEntry, targetPath, state);
                }
                else if (node.GenericEntry != null)
                {
                    WriteDirectoryTree(node.GenericEntry, targetPath, state);
                }
                else if (node.SnapshotEntry != null)
                {
                    throw new InvalidOperationException("Loaded database snapshots do not contain file data to export.");
                }
            },
            $"Saved folder: {targetPath}",
            targetPath);
    }

    private async Task SaveFileRowsAsync(IReadOnlyCollection<FileRow> rows)
    {
        if (rows.Count == 0)
        {
            StatusText = "Select files or folders to save.";
            return;
        }

        if (rows.Count == 1)
        {
            var row = rows.First();
            if (row.IsFolder)
            {
                await SaveDirectoryRowAsync(row);
            }
            else
            {
                await SaveFileRowAsync(row);
            }

            return;
        }

        var destination = PickDestinationFolder("Save selected files and folders");
        if (destination == null)
        {
            return;
        }

        await RunExportAsync(
            $"Save {rows.Count:N0} filesystem item(s)",
            EstimateFileRowsBytes(rows),
            CountFileRows(rows),
            state =>
            {
                foreach (var row in rows)
                {
                    state.ThrowIfCancellationRequested();
                    WriteEntryToDirectory(row, destination, state);
                }
            },
            $"Saved {rows.Count:N0} selected filesystem item(s) to: {destination}",
            destination);
    }

    private async Task SaveCarvedRowsAsync(IReadOnlyCollection<CarvedFileRow> rows)
    {
        if (rows.Count == 0)
        {
            StatusText = "Select carved files to save.";
            return;
        }

        if (rows.Count == 1)
        {
            await SaveCarvedFileAsync(rows.First());
            return;
        }

        var destination = PickDestinationFolder("Save selected carved files");
        if (destination == null)
        {
            return;
        }

        await RunExportAsync(
            $"Save {rows.Count:N0} carved file(s)",
            rows.Sum(row => Math.Max(0, row.SizeBytes)),
            rows.Count,
            state =>
            {
                foreach (var row in rows)
                {
                    state.ThrowIfCancellationRequested();
                    if (!row.HasFileData)
                    {
                        throw new InvalidOperationException("Loaded database snapshots do not contain carved file data to export.");
                    }

                    var path = GetUniqueFilePath(Path.Combine(destination, SanitizeFileName(row.Name)));
                    WriteCarvedFile(row, path, state);
                }
            },
            $"Saved {rows.Count:N0} carved file(s) to: {destination}",
            destination);
    }

    private async Task SaveRecoveryRowsAsync(IReadOnlyCollection<RecoveryFileRow> rows)
    {
        if (rows.Count == 0)
        {
            StatusText = "Select recovered files or folders to save.";
            return;
        }

        if (rows.Count == 1 && !rows.First().IsFolder)
        {
            await SaveRecoveryFileAsync(rows.First());
            return;
        }

        var destination = PickDestinationFolder("Save recovered files");
        if (destination == null)
        {
            return;
        }

        await RunExportAsync(
            $"Save {rows.Count:N0} recovered item(s)",
            EstimateRecoveryRowsBytes(rows),
            CountRecoveryRows(rows),
            state =>
            {
                foreach (var row in rows)
                {
                    state.ThrowIfCancellationRequested();
                    WriteRecoveryFileToDirectory(row, destination, _settings.ZeroFillOverwrittenRecoveryClusters, state);
                }
            },
            $"Saved {rows.Count:N0} recovered item(s) to: {destination}",
            destination);
    }

    private async Task SaveRecoveryFileAsync(RecoveryFileRow row)
    {
        var dialog = new SaveFileDialog
        {
            FileName = SanitizeFileName(row.Name),
            Filter = "All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunExportAsync(
            $"Save recovered file {row.Name}",
            Math.Max(0, row.SizeBytes),
            1,
            state => WriteRecoveryFile(row, dialog.FileName, _settings.ZeroFillOverwrittenRecoveryClusters, state),
            $"Saved recovered file: {dialog.FileName}",
            dialog.FileName);
    }

    private async Task SaveDirectoryRowAsync(FileRow row)
    {
        var destination = PickDestinationFolder($"Save {row.Name}");
        if (destination == null)
        {
            return;
        }

        await RunExportAsync(
            $"Save folder {row.Name}",
            EstimateFileRowBytes(row),
            CountFileRowItems(row),
            state => WriteEntryToDirectory(row, destination, state),
            $"Saved folder: {Path.Combine(destination, row.Name)}",
            destination);
    }

    private async Task SaveFileRowAsync(FileRow row)
    {
        if (row.IsFolder)
        {
            MessageBox.Show(this, "Select a file row to save.", "Save Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = SanitizeFileName(row.Name),
            Filter = "All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunExportAsync(
            $"Save file {row.Name}",
            Math.Max(0, row.SizeBytes),
            1,
            state => WriteDirectoryEntry(row, dialog.FileName, state),
            $"Saved file: {dialog.FileName}",
            dialog.FileName);
    }

    private async Task SaveCarvedFileAsync(CarvedFileRow row)
    {
        var dialog = new SaveFileDialog
        {
            FileName = SanitizeFileName(row.Name),
            Filter = "All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunExportAsync(
            $"Save carved file {row.Name}",
            Math.Max(0, row.SizeBytes),
            1,
            state =>
            {
                if (!row.HasFileData)
                {
                    throw new InvalidOperationException("Loaded database snapshots do not contain carved file data to export.");
                }

                WriteCarvedFile(row, dialog.FileName, state);
            },
            $"Saved carved file: {dialog.FileName}",
            dialog.FileName);
    }

    private async Task RunExportAsync(string title, long totalBytes, int totalItems, Action<ExportProgressState> export, string successLog, string? destinationPath = null)
    {
        if (_isExportRunning)
        {
            StatusText = "Another export is already running.";
            return;
        }

        if (!EnsureEnoughDiskSpaceForExport(destinationPath, totalBytes))
        {
            return;
        }

        var progressWatch = Stopwatch.StartNew();
        var lastDisplayedPercent = -1;
        const string progressLogKey = "export-progress";

        _isExportRunning = true;
        _exportCancellation = new CancellationTokenSource();
        var cancellationToken = _exportCancellation.Token;
        UpdateTaskState();
        IsScanProgressVisible = true;
        var progressRow = GetOrCreateScanProgressRow("Export");
        progressRow.Update(0, "Preparing export...");
        SelectLogTab();
        StatusText = $"Exporting: {title}";
        AppendLog($"Export started: {title}.");
        BeginLiveLog(progressLogKey, "Export progress: 0%.");

        var progress = new Progress<ExportProgressSnapshot>(snapshot =>
        {
            var percent = CalculateExportPercent(snapshot);
            var byteText = snapshot.TotalBytes > 0
                ? $"{FormatBytes(snapshot.BytesWritten)} / {FormatBytes(snapshot.TotalBytes)}"
                : FormatBytes(snapshot.BytesWritten);
            var itemText = snapshot.TotalItems > 0
                ? $"{snapshot.ItemsCompleted:N0} / {snapshot.TotalItems:N0} items"
                : $"{snapshot.ItemsCompleted:N0} items";
            var current = string.IsNullOrWhiteSpace(snapshot.CurrentName) ? string.Empty : $" - {snapshot.CurrentName}";
            progressRow.Update(percent, $"{percent:0}% - {byteText}, {itemText}{current}");
            StatusText = $"Exporting... {percent:0}%";

            if (percent != lastDisplayedPercent)
            {
                lastDisplayedPercent = percent;
                UpdateLiveLog(progressLogKey, $"Export progress: {percent:0}% ({byteText}, {itemText}, {progressWatch.Elapsed:mm\\:ss}).");
            }
        });

        try
        {
            var state = new ExportProgressState(Math.Max(0, totalBytes), Math.Max(0, totalItems), progress, cancellationToken);
            await Task.Run(() => export(state), cancellationToken);
            state.ReportComplete();
            progressRow.Update(100, $"100% - {FormatBytes(state.BytesWritten)}, {state.ItemsCompleted:N0} / {Math.Max(1, state.TotalItems):N0} items");
            StatusText = "Ready";
            AppendLog(successLog);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Export canceled";
            progressRow.Update(progressRow.Value, $"Canceled - {title}");
            UpdateLiveLog(progressLogKey, $"Export canceled ({progressWatch.Elapsed:mm\\:ss}).");
            AppendLog($"Export canceled: {title}.");
        }
        catch (Exception ex)
        {
            StatusText = "Export failed";
            AppendLog($"Export failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Save Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            progressWatch.Stop();
            _isExportRunning = false;
            _exportCancellation?.Dispose();
            _exportCancellation = null;
            UpdateTaskState();
            RefreshSelectionState();
        }
    }

    private bool EnsureEnoughDiskSpaceForExport(string? destinationPath, long estimatedBytes)
    {
        return EnsureEnoughDiskSpace(destinationPath, estimatedBytes, "export");
    }

    private bool EnsureEnoughDiskSpace(string? destinationPath, long estimatedBytes, string operationName)
    {
        if (string.IsNullOrWhiteSpace(destinationPath) || estimatedBytes <= 0)
        {
            return true;
        }

        try
        {
            var fullPath = Path.GetFullPath(destinationPath);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                return true;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                AppendLog($"{operationName} disk-space check skipped because destination drive is not ready: {root}");
                return true;
            }

            var reserveBytes = Math.Max(64L * 1024 * 1024, estimatedBytes / 20);
            var requiredBytes = estimatedBytes > long.MaxValue - reserveBytes
                ? long.MaxValue
                : estimatedBytes + reserveBytes;
            if (drive.AvailableFreeSpace >= requiredBytes)
            {
                AppendLog($"{operationName} disk-space check passed: need about {FormatBytes(requiredBytes)}, available {FormatBytes(drive.AvailableFreeSpace)} on {root}.");
                return true;
            }

            var message =
                $"The {operationName} is estimated to need {FormatBytes(estimatedBytes)} plus a safety reserve ({FormatBytes(requiredBytes)} total), but {root} only has {FormatBytes(drive.AvailableFreeSpace)} free.";
            StatusText = $"Not enough free space for {operationName}.";
            AppendLog($"{operationName} blocked by disk-space preflight: {message}");
            MessageBox.Show(this, message, "Not Enough Free Space", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        catch (Exception ex)
        {
            AppendLog($"{operationName} disk-space check could not read destination capacity: {ex.Message}");
            return true;
        }
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e)
    {
        if (!_isExportRunning || _exportCancellation == null || _exportCancellation.IsCancellationRequested)
        {
            return;
        }

        _exportCancellation.Cancel();
        StatusText = "Canceling export...";
        AppendLog("Export cancellation requested.");
        OnPropertyChanged(nameof(CanCancelExport));
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e)
    {
        var requested = false;
        if (_isMetadataScanRunning && _metadataScanCancellation is { IsCancellationRequested: false } metadataCancellation)
        {
            metadataCancellation.Cancel();
            requested = true;
        }

        if (_isFileCarverRunning && _fileCarverCancellation is { IsCancellationRequested: false } fileCarverCancellation)
        {
            fileCarverCancellation.Cancel();
            requested = true;
        }

        if (!requested)
        {
            return;
        }

        StatusText = "Canceling scan...";
        AppendLog("Scan cancellation requested.");
        OnPropertyChanged(nameof(CanCancelScan));
    }

    private static int CalculateExportPercent(ExportProgressSnapshot snapshot)
    {
        if (snapshot.TotalBytes > 0)
        {
            return Math.Clamp((int)Math.Round(snapshot.BytesWritten * 100.0 / snapshot.TotalBytes), 0, 100);
        }

        if (snapshot.TotalItems > 0)
        {
            return Math.Clamp((int)Math.Round(snapshot.ItemsCompleted * 100.0 / snapshot.TotalItems), 0, 100);
        }

        return 0;
    }

    private string? PickDestinationFolder(string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private static long EstimateFileRowsBytes(IEnumerable<FileRow> rows)
    {
        return rows.Sum(EstimateFileRowBytes);
    }

    private static long EstimateDirectoryNodeBytes(DirectoryNode node, PartitionModel partition)
    {
        if (node.BrowseEntries != null)
        {
            return node.BrowseEntries
                .Select(entry => new FileRow(entry, partition.FatxVolume!, "Active"))
                .Sum(EstimateFileRowBytes);
        }

        if (node.Entry == null && node.NtfsEntry == null && node.PlayStationEntry == null && node.GenericEntry == null && node.SnapshotEntry == null)
        {
            return partition.FatxVolume != null
                ? partition.FatxVolume.GetRoot().Select(entry => new FileRow(entry, partition.FatxVolume, "Active")).Sum(EstimateFileRowBytes)
                : partition.NtfsVolume != null
                    ? partition.NtfsVolume.GetRoot().Select(entry => new FileRow(entry, "Active")).Sum(EstimateFileRowBytes)
                    : partition.PlayStationVolume != null
                        ? partition.PlayStationVolume.GetRoot().Select(entry => new FileRow(entry, "Active")).Sum(EstimateFileRowBytes)
                        : partition.GenericVolume != null
                            ? partition.GenericVolume.GetRoot().Select(entry => new FileRow(entry, "Active")).Sum(EstimateFileRowBytes)
                            : 0;
        }

        return node.Entry != null && partition.FatxVolume != null
            ? EstimateFileRowBytes(new FileRow(node.Entry, partition.FatxVolume, "Active"))
            : node.NtfsEntry != null
                ? EstimateFileRowBytes(new FileRow(node.NtfsEntry, "Active"))
                : node.PlayStationEntry != null
                    ? EstimateFileRowBytes(new FileRow(node.PlayStationEntry, "Active"))
                    : node.GenericEntry != null
                        ? EstimateFileRowBytes(new FileRow(node.GenericEntry, "Active"))
                        : 0;
    }

    private static int CountDirectoryNodeItems(DirectoryNode node, PartitionModel partition)
    {
        if (node.BrowseEntries != null)
        {
            return Math.Max(1, node.BrowseEntries
                .Select(entry => new FileRow(entry, partition.FatxVolume!, "Active"))
                .Sum(CountFileRowItems));
        }

        if (node.Entry == null && node.NtfsEntry == null && node.PlayStationEntry == null && node.GenericEntry == null && node.SnapshotEntry == null)
        {
            var count = partition.FatxVolume != null
                ? partition.FatxVolume.GetRoot().Select(entry => new FileRow(entry, partition.FatxVolume, "Active")).Sum(CountFileRowItems)
                : partition.NtfsVolume != null
                    ? partition.NtfsVolume.GetRoot().Select(entry => new FileRow(entry, "Active")).Sum(CountFileRowItems)
                    : partition.PlayStationVolume != null
                        ? partition.PlayStationVolume.GetRoot().Select(entry => new FileRow(entry, "Active")).Sum(CountFileRowItems)
                        : partition.GenericVolume != null
                            ? partition.GenericVolume.GetRoot().Select(entry => new FileRow(entry, "Active")).Sum(CountFileRowItems)
                            : 0;
            return Math.Max(1, count);
        }

        var nodeCount = node.Entry != null && partition.FatxVolume != null
            ? CountFileRowItems(new FileRow(node.Entry, partition.FatxVolume, "Active"))
            : node.NtfsEntry != null
                ? CountFileRowItems(new FileRow(node.NtfsEntry, "Active"))
                : node.PlayStationEntry != null
                    ? CountFileRowItems(new FileRow(node.PlayStationEntry, "Active"))
                    : node.GenericEntry != null
                        ? CountFileRowItems(new FileRow(node.GenericEntry, "Active"))
                        : 0;
        return Math.Max(1, nodeCount);
    }

    private static long EstimateFileRowBytes(FileRow row)
    {
        if (!row.IsFolder)
        {
            return Math.Max(0, row.SizeBytes);
        }

        return row.Entry != null
            ? WalkFatx(row.Entry).Where(entry => !entry.IsDirectory()).Sum(entry => (long)Math.Max(0, entry.FileSize))
            : row.NtfsEntry != null
                ? WalkXbox(row.NtfsEntry).Where(entry => !entry.IsDirectory).Sum(entry => Math.Max(0, entry.Length))
                : row.PlayStationEntry != null
                    ? WalkPlayStation(row.PlayStationEntry).Where(entry => !entry.IsDirectory).Sum(entry => Math.Max(0, entry.Length))
                    : row.GenericEntry != null
                        ? WalkGeneric(row.GenericEntry).Where(entry => !entry.IsDirectory).Sum(entry => Math.Max(0, entry.Length))
                        : 0;
    }

    private static int CountFileRows(IEnumerable<FileRow> rows)
    {
        return rows.Sum(CountFileRowItems);
    }

    private static int CountFileRowItems(FileRow row)
    {
        if (!row.IsFolder)
        {
            return 1;
        }

        var count = row.Entry != null
            ? WalkFatx(row.Entry).Count(entry => !entry.IsDirectory())
            : row.NtfsEntry != null
                ? WalkXbox(row.NtfsEntry).Count(entry => !entry.IsDirectory)
                : row.PlayStationEntry != null
                    ? WalkPlayStation(row.PlayStationEntry).Count(entry => !entry.IsDirectory)
                    : row.GenericEntry != null
                        ? WalkGeneric(row.GenericEntry).Count(entry => !entry.IsDirectory)
                        : 0;
        return Math.Max(1, count);
    }

    private static long EstimateRecoveryRowsBytes(IEnumerable<RecoveryFileRow> rows)
    {
        return rows.Sum(row => row.IsFolder
            ? WalkRecovery(row.File).Where(file => !file.IsDirectory()).Sum(file => (long)Math.Max(0, file.FileSize))
            : Math.Max(0, row.SizeBytes));
    }

    private static int CountRecoveryRows(IEnumerable<RecoveryFileRow> rows)
    {
        return rows.Sum(row =>
        {
            var count = row.IsFolder
                ? WalkRecovery(row.File).Count(file => !file.IsDirectory())
                : 1;
            return Math.Max(1, count);
        });
    }

    private static IEnumerable<DirectoryEntry> WalkFatx(DirectoryEntry entry)
    {
        yield return entry;
        foreach (var child in entry.Children)
        {
            foreach (var nested in WalkFatx(child))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<XboxFileEntry> WalkXbox(XboxFileEntry entry)
    {
        yield return entry;
        if (!entry.IsDirectory)
        {
            yield break;
        }

        foreach (var child in entry.Volume.GetChildren(entry))
        {
            foreach (var nested in WalkXbox(child))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<PlayStationFileEntry> WalkPlayStation(PlayStationFileEntry entry)
    {
        yield return entry;
        foreach (var child in entry.Children)
        {
            foreach (var nested in WalkPlayStation(child))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<GenericFileSystemEntry> WalkGeneric(GenericFileSystemEntry entry)
    {
        yield return entry;
        foreach (var child in entry.Children)
        {
            foreach (var nested in WalkGeneric(child))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<DatabaseFile> WalkRecovery(DatabaseFile file)
    {
        yield return file;
        foreach (var child in file.Children)
        {
            foreach (var nested in WalkRecovery(child))
            {
                yield return nested;
            }
        }
    }

    private static void WriteDirectoryEntry(Volume volume, DirectoryEntry entry, string path)
    {
        WriteDirectoryEntry(volume, entry, path, null);
    }

    private static void WriteDirectoryEntry(Volume volume, DirectoryEntry entry, string path, ExportProgressState? progress)
    {
        var clusterChain = BuildClusterChain(volume, entry);
        uint bytesLeft = entry.FileSize;

        using var output = File.Create(path);
        progress?.StartItem(entry.FileName);
        foreach (var cluster in clusterChain)
        {
            progress?.ThrowIfCancellationRequested();
            if (bytesLeft == 0)
            {
                break;
            }

            var data = volume.ReadCluster(cluster);
            var writeSize = (int)Math.Min(bytesLeft, volume.BytesPerCluster);
            output.Write(data, 0, writeSize);
            bytesLeft -= (uint)writeSize;
            progress?.AddBytes(writeSize);
        }

        progress?.CompleteItem();
    }

    private static void WriteDirectoryEntry(FileRow row, string path)
    {
        WriteDirectoryEntry(row, path, null);
    }

    private static void WriteDirectoryEntry(FileRow row, string path, ExportProgressState? progress)
    {
        if (row.Entry != null && row.Volume != null)
        {
            WriteDirectoryEntry(row.Volume, row.Entry, path, progress);
            return;
        }

        if (row.NtfsEntry != null)
        {
            WriteXboxFile(row.NtfsEntry, path, progress);
            return;
        }

        if (row.PlayStationEntry != null)
        {
            progress?.StartItem(row.PlayStationEntry.Name);
            row.PlayStationEntry.Volume.CopyFile(row.PlayStationEntry, path);
            progress?.AddBytes(row.PlayStationEntry.Length);
            progress?.CompleteItem();
            return;
        }

        if (row.PsMetadataEntry != null && row.PsMetadataVolume != null && row.PsMetadataEntry.Inode > 0)
        {
            progress?.StartItem(row.PsMetadataEntry.Name);
            row.PsMetadataVolume.ExportDeletedInode(row.PsMetadataEntry.Inode, path);
            progress?.AddBytes(row.PsMetadataEntry.Size);
            progress?.CompleteItem();
            return;
        }

        if (row.GenericEntry != null)
        {
            WriteGenericFile(row.GenericEntry, path, progress);
            return;
        }

        if (row.SnapshotEntry != null)
        {
            throw new InvalidOperationException("Loaded database snapshots do not contain file data to export.");
        }

        throw new InvalidOperationException("Unsupported file row.");
    }

    private static void WriteXboxFile(XboxFileEntry entry, string path, ExportProgressState? progress)
    {
        progress?.StartItem(entry.Name);
        if (entry.Length == 0)
        {
            File.Create(path).Dispose();
            progress?.CompleteItem();
            return;
        }

        if (entry.ResidentData is { Length: > 0 } residentData)
        {
            File.WriteAllBytes(path, residentData);
            progress?.AddBytes(residentData.Length);
            progress?.CompleteItem();
            return;
        }

        entry.Volume.CopyFile(
            entry,
            path,
            progress == null ? null : new Action<long>(progress.AddBytes),
            progress?.CancellationToken ?? CancellationToken.None);
        progress?.CompleteItem();
    }

    private static void WriteGenericFile(GenericFileSystemEntry entry, string path, ExportProgressState? progress)
    {
        progress?.StartItem(entry.Name);
        if (entry.Length == 0)
        {
            File.Create(path).Dispose();
            progress?.CompleteItem();
            return;
        }

        if (entry.Extents.Count > 0)
        {
            CopyRawExtents(entry.Volume.SourcePath, entry.Extents, entry.Length, path, progress);
            progress?.CompleteItem();
            return;
        }

        entry.Volume.CopyFile(
            entry,
            path,
            progress == null ? null : new Action<long>(progress.AddBytes),
            progress?.CancellationToken ?? CancellationToken.None);
        progress?.CompleteItem();
    }

    private static void CopyRawExtents(string sourcePath, IReadOnlyList<FileExtent> extents, long length, string destinationPath, ExportProgressState? progress)
    {
        const int bufferSize = 0x400000;
        var remaining = length;
        var buffer = new byte[bufferSize];

        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, FileOptions.RandomAccess);
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize, FileOptions.SequentialScan);
        foreach (var extent in extents)
        {
            progress?.ThrowIfCancellationRequested();
            if (remaining <= 0)
            {
                break;
            }

            var offset = Math.Max(0, extent.Offset);
            var readable = Math.Min(extent.Length, remaining);
            if (offset >= input.Length || readable <= 0)
            {
                break;
            }

            input.Position = offset;
            readable = Math.Min(readable, input.Length - offset);
            while (readable > 0 && remaining > 0)
            {
                progress?.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, Math.Min(readable, remaining)));
                if (read == 0)
                {
                    return;
                }

                output.Write(buffer, 0, read);
                readable -= read;
                remaining -= read;
                progress?.AddBytes(read);
            }
        }
    }

    private static void WriteEntryToDirectory(FileRow row, string destinationDirectory)
    {
        WriteEntryToDirectory(row, destinationDirectory, null);
    }

    private static void WriteEntryToDirectory(FileRow row, string destinationDirectory, ExportProgressState? progress)
    {
        if (row.Entry != null && row.Volume != null)
        {
            WriteEntryToDirectory(row.Volume, row.Entry, destinationDirectory, progress);
            return;
        }

        if (row.NtfsEntry != null)
        {
            WriteEntryToDirectory(row.NtfsEntry, destinationDirectory, progress);
            return;
        }

        if (row.PlayStationEntry != null)
        {
            WriteEntryToDirectory(row.PlayStationEntry, destinationDirectory, progress);
            return;
        }

        if (row.GenericEntry != null)
        {
            WriteEntryToDirectory(row.GenericEntry, destinationDirectory, progress);
            return;
        }

        if (row.SnapshotEntry != null)
        {
            throw new InvalidOperationException("Loaded database snapshots do not contain file data to export.");
        }

        throw new InvalidOperationException("Unsupported file row.");
    }

    private static void WriteEntryToDirectory(Volume volume, DirectoryEntry entry, string destinationDirectory)
    {
        WriteEntryToDirectory(volume, entry, destinationDirectory, null);
    }

    private static void WriteEntryToDirectory(Volume volume, DirectoryEntry entry, string destinationDirectory, ExportProgressState? progress)
    {
        if (entry.IsDirectory())
        {
            var targetDirectory = GetUniqueDirectoryPath(Path.Combine(destinationDirectory, SanitizeFileName(entry.FileName)));
            WriteDirectoryTree(volume, entry, targetDirectory, progress);
            return;
        }

        var targetFile = GetUniqueFilePath(Path.Combine(destinationDirectory, SanitizeFileName(entry.FileName)));
        WriteDirectoryEntry(volume, entry, targetFile, progress);
    }

    private static void WriteRecoveryFileToDirectory(RecoveryFileRow row, string destinationDirectory)
    {
        WriteRecoveryFileToDirectory(row, destinationDirectory, zeroFillOverwrittenClusters: false, null);
    }

    private static void WriteRecoveryFileToDirectory(RecoveryFileRow row, string destinationDirectory, bool zeroFillOverwrittenClusters, ExportProgressState? progress)
    {
        if (row.IsFolder)
        {
            var targetDirectory = GetUniqueDirectoryPath(Path.Combine(destinationDirectory, SanitizeFileName(row.Name)));
            WriteRecoveryDirectory(row.Volume, row.File, targetDirectory, zeroFillOverwrittenClusters, progress);
            return;
        }

        var targetFile = GetUniqueFilePath(Path.Combine(destinationDirectory, SanitizeFileName(row.Name)));
        WriteRecoveryFile(row, targetFile, zeroFillOverwrittenClusters, progress);
    }

    private static void WriteRecoveryDirectory(Volume volume, DatabaseFile directory, string targetDirectory)
    {
        WriteRecoveryDirectory(volume, directory, targetDirectory, zeroFillOverwrittenClusters: false, null);
    }

    private static void WriteRecoveryDirectory(Volume volume, DatabaseFile directory, string targetDirectory, bool zeroFillOverwrittenClusters, ExportProgressState? progress)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var child in directory.Children)
        {
            progress?.ThrowIfCancellationRequested();
            if (child.IsDirectory())
            {
                var childDirectory = GetUniqueDirectoryPath(Path.Combine(targetDirectory, SanitizeFileName(child.FileName)));
                WriteRecoveryDirectory(volume, child, childDirectory, zeroFillOverwrittenClusters, progress);
                continue;
            }

            var childFile = GetUniqueFilePath(Path.Combine(targetDirectory, SanitizeFileName(child.FileName)));
            WriteRecoveryFile(volume, child, childFile, zeroFillOverwrittenClusters, progress);
        }
    }

    private static void WriteRecoveryFile(RecoveryFileRow row, string path)
    {
        WriteRecoveryFile(row, path, zeroFillOverwrittenClusters: false, null);
    }

    private static void WriteRecoveryFile(RecoveryFileRow row, string path, bool zeroFillOverwrittenClusters, ExportProgressState? progress)
    {
        WriteRecoveryFile(row.Volume, row.File, path, zeroFillOverwrittenClusters, progress);
    }

    private static void WriteRecoveryFile(Volume volume, DatabaseFile file, string path)
    {
        WriteRecoveryFile(volume, file, path, zeroFillOverwrittenClusters: false, null);
    }

    private static void WriteRecoveryFile(Volume volume, DatabaseFile file, string path, bool zeroFillOverwrittenClusters, ExportProgressState? progress)
    {
        if (file.IsDirectory())
        {
            throw new InvalidOperationException("Select a recovered file to export, or save the folder to a directory.");
        }

        var clusters = file.ClusterChain ?? [];
        if (file.FileSize > 0 && clusters.Count == 0)
        {
            clusters = BuildClusterChain(volume, file.GetDirent());
        }

        progress?.StartItem(file.FileName);
        IReadOnlyCollection<uint>? overwritten = null;
        if (zeroFillOverwrittenClusters)
        {
            overwritten = file.GetCollisions();
        }

        WriteClustersToFile(volume, clusters, file.FileSize, path, overwritten, progress);
        progress?.CompleteItem();
    }

    private static void WriteEntryToDirectory(XboxFileEntry entry, string destinationDirectory)
    {
        WriteEntryToDirectory(entry, destinationDirectory, null);
    }

    private static void WriteEntryToDirectory(XboxFileEntry entry, string destinationDirectory, ExportProgressState? progress)
    {
        if (entry.IsDirectory)
        {
            var targetDirectory = GetUniqueDirectoryPath(Path.Combine(destinationDirectory, SanitizeFileName(entry.Name)));
            WriteDirectoryTree(entry, targetDirectory, progress);
            return;
        }

        var targetFile = GetUniqueFilePath(Path.Combine(destinationDirectory, SanitizeFileName(entry.Name)));
        WriteXboxFile(entry, targetFile, progress);
    }

    private static void WriteEntryToDirectory(PlayStationFileEntry entry, string destinationDirectory)
    {
        WriteEntryToDirectory(entry, destinationDirectory, null);
    }

    private static void WriteEntryToDirectory(PlayStationFileEntry entry, string destinationDirectory, ExportProgressState? progress)
    {
        if (entry.IsDirectory)
        {
            var targetDirectory = GetUniqueDirectoryPath(Path.Combine(destinationDirectory, SanitizeFileName(entry.Name)));
            WriteDirectoryTree(entry, targetDirectory, progress);
            return;
        }

        var targetFile = GetUniqueFilePath(Path.Combine(destinationDirectory, SanitizeFileName(entry.Name)));
        progress?.StartItem(entry.Name);
        entry.Volume.CopyFile(entry, targetFile);
        progress?.AddBytes(entry.Length);
        progress?.CompleteItem();
    }

    private static void WriteEntryToDirectory(GenericFileSystemEntry entry, string destinationDirectory)
    {
        WriteEntryToDirectory(entry, destinationDirectory, null);
    }

    private static void WriteEntryToDirectory(GenericFileSystemEntry entry, string destinationDirectory, ExportProgressState? progress)
    {
        if (entry.IsDirectory)
        {
            var targetDirectory = GetUniqueDirectoryPath(Path.Combine(destinationDirectory, SanitizeFileName(entry.Name)));
            WriteDirectoryTree(entry, targetDirectory, progress);
            return;
        }

        var targetFile = GetUniqueFilePath(Path.Combine(destinationDirectory, SanitizeFileName(entry.Name)));
        WriteGenericFile(entry, targetFile, progress);
    }

    private static void WriteDirectoryTree(Volume volume, DirectoryEntry directory, string targetDirectory)
    {
        WriteDirectoryTree(volume, directory, targetDirectory, null);
    }

    private static void WriteDirectoryTree(Volume volume, DirectoryEntry directory, string targetDirectory, ExportProgressState? progress)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var child in directory.Children)
        {
            progress?.ThrowIfCancellationRequested();
            WriteEntryToDirectory(volume, child, targetDirectory, progress);
        }
    }

    private static void WriteDirectoryTree(XboxFileEntry directory, string targetDirectory)
    {
        WriteDirectoryTree(directory, targetDirectory, null);
    }

    private static void WriteDirectoryTree(XboxFileEntry directory, string targetDirectory, ExportProgressState? progress)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var child in directory.Volume.GetChildren(directory))
        {
            progress?.ThrowIfCancellationRequested();
            WriteEntryToDirectory(child, targetDirectory, progress);
        }
    }

    private static void WriteDirectoryTree(PlayStationFileEntry directory, string targetDirectory)
    {
        WriteDirectoryTree(directory, targetDirectory, null);
    }

    private static void WriteDirectoryTree(PlayStationFileEntry directory, string targetDirectory, ExportProgressState? progress)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var child in directory.Volume.GetChildren(directory))
        {
            progress?.ThrowIfCancellationRequested();
            WriteEntryToDirectory(child, targetDirectory, progress);
        }
    }

    private static void WriteDirectoryTree(GenericFileSystemEntry directory, string targetDirectory)
    {
        WriteDirectoryTree(directory, targetDirectory, null);
    }

    private static void WriteDirectoryTree(GenericFileSystemEntry directory, string targetDirectory, ExportProgressState? progress)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var child in directory.Volume.GetChildren(directory))
        {
            progress?.ThrowIfCancellationRequested();
            WriteEntryToDirectory(child, targetDirectory, progress);
        }
    }

    private static List<uint> BuildClusterChain(Volume volume, DirectoryEntry entry)
    {
        if (!entry.IsDeleted())
        {
            return volume.GetClusterChain(entry);
        }

        var clusterCount = (int)(((entry.FileSize + (volume.BytesPerCluster - 1)) &
                                  ~(volume.BytesPerCluster - 1)) / volume.BytesPerCluster);
        return Enumerable.Range((int)entry.FirstCluster, Math.Max(1, clusterCount))
            .Select(i => (uint)i)
            .Where(cluster => cluster > 0 && cluster < volume.MaxClusters)
            .ToList();
    }

    private static void WriteClustersToFile(Volume volume, IReadOnlyList<uint> clusters, long fileSize, string path)
    {
        WriteClustersToFile(volume, clusters, fileSize, path, null, null);
    }

    private static void WriteClustersToFile(Volume volume, IReadOnlyList<uint> clusters, long fileSize, string path, ExportProgressState? progress)
    {
        WriteClustersToFile(volume, clusters, fileSize, path, null, progress);
    }

    private static void WriteClustersToFile(
        Volume volume,
        IReadOnlyList<uint> clusters,
        long fileSize,
        string path,
        IReadOnlyCollection<uint>? clustersToZeroFill,
        ExportProgressState? progress)
    {
        var remaining = fileSize;
        var bufferSize = (int)Math.Min(int.MaxValue, Math.Max(0x100000L, volume.BytesPerCluster));
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize, FileOptions.SequentialScan);
        HashSet<uint>? zeroFillSet = null;
        byte[]? zeroBuffer = null;
        if (clustersToZeroFill is { Count: > 0 })
        {
            zeroFillSet = new HashSet<uint>(clustersToZeroFill);
            zeroBuffer = new byte[Math.Max(1, (int)volume.BytesPerCluster)];
        }

        foreach (var cluster in clusters)
        {
            progress?.ThrowIfCancellationRequested();
            if (remaining <= 0)
            {
                break;
            }

            var writeSize = (int)Math.Min(remaining, volume.BytesPerCluster);
            if (zeroFillSet != null && zeroFillSet.Contains(cluster))
            {
                output.Write(zeroBuffer!, 0, writeSize);
                remaining -= writeSize;
                progress?.AddBytes(writeSize);
                continue;
            }

            if (cluster == 0 || cluster >= volume.MaxClusters)
            {
                break;
            }

            var data = volume.ReadCluster(cluster);
            output.Write(data, 0, writeSize);
            remaining -= writeSize;
            progress?.AddBytes(writeSize);
        }
    }

    private static void WriteCarvedFile(Volume volume, FileSignature signature, string path)
    {
        WriteCarvedFile(volume, signature, path, null);
    }

    private static void WriteCarvedFile(Volume volume, FileSignature signature, string path, ExportProgressState? progress)
    {
        const int bufferSize = 0x400000;
        var remaining = signature.FileSize;
        volume.SeekFileArea(signature.Offset);

        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize, FileOptions.SequentialScan);
        while (remaining > 0)
        {
            progress?.ThrowIfCancellationRequested();
            var read = (int)Math.Min(remaining, bufferSize);
            var buffer = volume.GetReader().ReadBytes(read);
            output.Write(buffer, 0, buffer.Length);
            remaining -= buffer.Length;
            progress?.AddBytes(buffer.Length);
        }
    }

    private static void WriteCarvedFile(CarvedFileRow row, string path)
    {
        WriteCarvedFile(row, path, null);
    }

    private static void WriteCarvedFile(CarvedFileRow row, string path, ExportProgressState? progress)
    {
        progress?.StartItem(row.Name);
        if (row.Signature != null && row.Volume != null)
        {
            WriteCarvedFile(row.Volume, row.Signature, path, progress);
            progress?.CompleteItem();
            return;
        }

        if (row.GenericFile == null || !row.GenericFile.HasFileData)
        {
            throw new InvalidOperationException("Loaded database snapshots do not contain carved file data to export.");
        }

        const int bufferSize = 0x400000;
        var remaining = row.GenericFile.Size;
        using var input = new FileStream(row.GenericFile.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, FileOptions.SequentialScan);
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize, FileOptions.SequentialScan);
        input.Position = row.GenericFile.SourceOffset;
        var buffer = new byte[bufferSize];
        while (remaining > 0)
        {
            progress?.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            remaining -= read;
            progress?.AddBytes(read);
        }

        progress?.CompleteItem();
    }

    private bool SaveProgressDatabase()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON File (*.json)|*.json",
            FileName = BuildDefaultDatabaseFileName()
        };

        if (dialog.ShowDialog(this) != true)
        {
            return true;
        }

        try
        {
            SaveProgressDatabase(dialog.FileName);
            AppendLog($"Finished saving database: {dialog.FileName}");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save Database Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            AppendLog($"Save database failed: {ex.Message}");
            return false;
        }
    }

    private string BuildDefaultDatabaseFileName()
    {
        var name = !string.IsNullOrWhiteSpace(_openedImagePath)
            ? Path.GetFileNameWithoutExtension(_openedImagePath)
            : AppName;

        return $"{SanitizeFileName(name)}.json";
    }

    private void SaveProgressDatabase(string path)
    {
        var databaseObject = CreateDriveDatabaseSnapshot();
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(databaseObject, options));
    }

    private async Task LoadProgressDatabaseAsync(string path)
    {
        var progressRow = GetOrCreateScanProgressRow("Database load");
        progressRow.Update(0, "Reading JSON...");
        IsScanProgressVisible = true;
        StatusText = "Loading database...";
        AppendLog($"Loading database: {path}");

        try
        {
            var snapshot = await Task.Run(() => LoadDatabaseSnapshotFromFile(path));
            progressRow.Update(75, "Applying database...");
            ApplyProgressDatabase(path, snapshot);
            progressRow.Update(100, "100%");
        }
        catch (Exception ex)
        {
            StatusText = "Failed";
            progressRow.Update(progressRow.Value, "Failed - database load");
            AppendLog($"Load database failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Load Database Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private DriveDatabaseSnapshot CreateDriveDatabaseSnapshot()
    {
        return new DriveDatabaseSnapshot
        {
            Version = 3,
            Application = AppName,
            SavedAtUtc = DateTime.UtcNow,
            SourceImage = _openedImagePath ?? _databaseSnapshot?.SourceImage ?? string.Empty,
            ActivePartitionName = SelectedPartition?.Name ?? string.Empty,
            Partitions = Partitions.Select(CreatePartitionDatabaseObject).ToList()
        };
    }

    private PartitionDatabaseSnapshot CreatePartitionDatabaseObject(PartitionModel partition)
    {
        var originalFilesystem = GetPartitionRootEntries(partition).Select(CreateSnapshotEntry).ToList();
        var metadataAnalyzer = MetadataResults
            .Where(row => BelongsToPartition(row, partition))
            .Select(row => CreateSnapshotEntry(row, includeChildren: false))
            .ToList();
        StampSnapshotPartition(originalFilesystem, partition.Name);
        StampSnapshotPartition(metadataAnalyzer, partition.Name);

        return new PartitionDatabaseSnapshot
        {
            Name = partition.Name,
            Offset = partition.Offset,
            Length = partition.Length,
            Family = partition.FamilyText,
            Status = partition.Status,
            UsedSpace = partition.UsedSpace,
            FreeSpace = partition.FreeSpace,
            TotalSpace = partition.TotalSpace,
            OriginalFilesystem = originalFilesystem,
            Analysis = new PartitionAnalysisSnapshot
            {
                MetadataAnalyzer = metadataAnalyzer,
                FileCarver = CarvedFiles
                    .Where(row => ReferenceEquals(row.Volume, partition.FatxVolume)
                        || row.GenericFile != null && IsGenericCarvedFileInPartition(row.GenericFile, partition)
                        || row.Snapshot != null && string.Equals(row.Snapshot.PartitionName, partition.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(row => new CarvedFileSnapshot
                    {
                        Name = row.Name,
                        Kind = row.Kind,
                        Offset = row.Offset,
                        SourceOffset = GetCarvedSourceOffset(row, partition),
                        SourcePath = GetCarvedSourcePath(row),
                        Size = row.SizeBytes,
                        PartitionName = partition.Name,
                        Source = row.GenericFile?.Source ?? row.Snapshot?.Source ?? string.Empty,
                        Detail = row.GenericFile?.Detail ?? row.Snapshot?.Detail ?? string.Empty,
                        Fragmentation = row.GenericFile?.EffectiveFragmentationStatus ?? row.Snapshot?.Fragmentation ?? string.Empty,
                        Extents = row.GenericFile?.EffectiveExtentSummary ?? row.Snapshot?.Extents ?? string.Empty
                    })
                    .ToList()
            }
        };
    }

    private static bool IsGenericCarvedFileInPartition(GenericCarvedFile file, PartitionModel partition)
    {
        return file.DisplayOffset >= partition.Offset && file.DisplayOffset < partition.Offset + partition.Length;
    }

    private static IEnumerable<object> GetPartitionRootEntries(PartitionModel partition)
    {
        if (partition.FatxVolume != null)
        {
            return partition.FatxVolume.GetRoot().Cast<object>();
        }

        if (partition.NtfsVolume != null)
        {
            return partition.NtfsVolume.GetRoot().Cast<object>();
        }

        if (partition.PlayStationVolume != null)
        {
            return partition.PlayStationVolume.GetRoot().Cast<object>();
        }

        if (partition.GenericVolume != null)
        {
            return partition.GenericVolume.GetRoot().Cast<object>();
        }

        if (partition.SnapshotPartition != null)
        {
            return partition.SnapshotPartition.GetRoot().Cast<object>();
        }

        return [];
    }

    private static bool BelongsToPartition(FileRow row, PartitionModel partition)
    {
        return row.Entry != null && ReferenceEquals(row.Volume, partition.FatxVolume)
               || row.NtfsEntry != null && ReferenceEquals(row.NtfsEntry.Volume, partition.NtfsVolume)
               || row.PlayStationEntry != null && ReferenceEquals(row.PlayStationEntry.Volume, partition.PlayStationVolume)
               || row.GenericEntry != null && ReferenceEquals(row.GenericEntry.Volume, partition.GenericVolume)
               || row.Ps3Entry != null && string.Equals(row.Ps3PartitionName, partition.Name, StringComparison.OrdinalIgnoreCase)
               || row.SnapshotEntry != null && (partition.SnapshotPartition?.Contains(row.SnapshotEntry) == true
                   || string.Equals(row.SnapshotEntry.PartitionName, partition.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static SnapshotFileEntry CreateSnapshotEntry(object entry)
    {
        return entry switch
        {
            DirectoryEntry fatx => CreateSnapshotEntry(fatx),
            XboxFileEntry ntfs => CreateSnapshotEntry(ntfs),
            PlayStationFileEntry playStation => CreateSnapshotEntry(playStation),
            GenericFileSystemEntry generic => CreateSnapshotEntry(generic),
            Ps3DirectoryEntry ps3 => CreateSnapshotEntry(ps3),
            SnapshotFileEntry snapshot => snapshot.Clone(),
            FileRow row => CreateSnapshotEntry(row, includeChildren: false),
            _ => new SnapshotFileEntry()
        };
    }

    private static SnapshotFileEntry CreateSnapshotEntry(DirectoryEntry entry)
    {
        return new SnapshotFileEntry
        {
            Path = string.Empty,
            Name = entry.FileName,
            Kind = entry.IsDirectory() ? "Folder" : "File",
            IsDirectory = entry.IsDirectory(),
            Size = entry.IsDirectory() ? -1 : entry.FileSize,
            Created = entry.CreationTime.AsDateTime(),
            Modified = entry.LastWriteTime.AsDateTime(),
            Accessed = entry.LastAccessTime.AsDateTime(),
            Offset = entry.Offset,
            Cluster = entry.Cluster,
            IsDeleted = entry.IsDeleted(),
            Attributes = entry.FileAttributes.ToString(),
            FirstCluster = entry.FirstCluster,
            Fragmentation = string.Empty,
            MetadataStatus = entry.IsDeleted() ? "Deleted FATX entry" : "Active FATX entry",
            Children = entry.IsDirectory() ? entry.Children.Select(CreateSnapshotEntry).ToList() : []
        };
    }

    private static SnapshotFileEntry CreateSnapshotEntry(XboxFileEntry entry)
    {
        return new SnapshotFileEntry
        {
            Path = entry.Path,
            Name = entry.Name,
            Kind = entry.IsDirectory ? "Folder" : "File",
            IsDirectory = entry.IsDirectory,
            Size = entry.IsDirectory ? -1 : entry.Length,
            Created = entry.Created,
            Modified = entry.Modified,
            Accessed = entry.Accessed,
            Offset = entry.Offset,
            Cluster = entry.Cluster,
            IsDeleted = entry.IsDeleted,
            Attributes = entry.Attributes.ToString(),
            Fragmentation = entry.FragmentationStatus,
            Extents = entry.ExtentSummary,
            MetadataStatus = entry.MetadataStatus,
            MftRecordIndex = entry.MftRecordIndex,
            MftSequenceNumber = entry.SequenceNumber,
            ParentMftRecordIndex = entry.ParentMftRecordIndex,
            AlternateDataStreamCount = entry.AlternateDataStreamCount,
            Children = entry.IsDirectory ? entry.Volume.GetChildren(entry).Select(CreateSnapshotEntry).ToList() : []
        };
    }

    private static SnapshotFileEntry CreateSnapshotEntry(PlayStationFileEntry entry)
    {
        return new SnapshotFileEntry
        {
            Path = entry.Path,
            Name = entry.Name,
            Kind = entry.IsDirectory ? "Folder" : "File",
            IsDirectory = entry.IsDirectory,
            Size = entry.IsDirectory ? -1 : entry.Length,
            Created = entry.Created,
            Modified = entry.Modified,
            Accessed = entry.Accessed,
            Offset = entry.Offset,
            Cluster = 0,
            IsDeleted = false,
            Attributes = entry.Type,
            Fragmentation = entry.RecoveryDisplayStatus,
            Extents = entry.ExtentSummary,
            MetadataStatus = entry.RecoveryDisplayStatus,
            Children = entry.IsDirectory ? entry.Volume.GetChildren(entry).Select(CreateSnapshotEntry).ToList() : []
        };
    }

    private static SnapshotFileEntry CreateSnapshotEntry(GenericFileSystemEntry entry)
    {
        return new SnapshotFileEntry
        {
            Path = entry.Path,
            Name = entry.Name,
            Kind = entry.IsDirectory ? "Folder" : "File",
            IsDirectory = entry.IsDirectory,
            Size = entry.IsDirectory ? -1 : entry.Length,
            Created = entry.Created,
            Modified = entry.Modified,
            Accessed = entry.Accessed,
            Offset = entry.Offset,
            Cluster = entry.Cluster,
            IsDeleted = entry.IsDeleted,
            Attributes = entry.Attributes,
            Fragmentation = entry.FragmentationStatus,
            Extents = entry.ExtentSummary,
            MetadataStatus = entry.MetadataStatus,
            Children = entry.IsDirectory ? entry.Volume.GetChildren(entry).Select(CreateSnapshotEntry).ToList() : []
        };
    }

    private static SnapshotFileEntry CreateSnapshotEntry(Ps3DirectoryEntry entry)
    {
        return new SnapshotFileEntry
        {
            Path = string.Empty,
            Name = entry.Name,
            Kind = entry.Kind,
            IsDirectory = entry.Kind.Equals("Directory", StringComparison.OrdinalIgnoreCase),
            Size = -1,
            Offset = entry.Offset,
            Cluster = entry.Inode,
            Attributes = $"dirent type {entry.FileType}, reclen {entry.RecordLength}, namlen {entry.NameLength}",
            Fragmentation = string.Empty,
            Extents = string.Empty,
            MetadataStatus = "PS3 directory entry"
        };
    }

    private static SnapshotFileEntry CreateSnapshotEntry(FileRow row, bool includeChildren)
    {
        var snapshot = new SnapshotFileEntry
        {
            Path = row.NtfsEntry?.Path ?? row.PlayStationEntry?.Path ?? row.GenericEntry?.Path ?? row.SnapshotEntry?.Path ?? string.Empty,
            Name = row.Name,
            Kind = row.Kind,
            IsDirectory = row.IsFolder,
            Size = row.SizeBytes,
            Created = row.Created,
            Modified = row.Modified,
            Accessed = row.Accessed,
            Offset = row.Offset,
            Cluster = row.ClusterNumber,
            IsDeleted = row.IsDeleted,
            Attributes = row.NtfsEntry?.Attributes.ToString() ?? row.PlayStationEntry?.Type ?? row.GenericEntry?.Attributes ?? row.SnapshotEntry?.Attributes ?? row.Entry?.FileAttributes.ToString() ?? row.Ps3Entry?.Kind ?? row.PsMetadataEntry?.Kind ?? string.Empty,
            Fragmentation = row.EffectiveFragmentationText,
            Extents = row.NtfsEntry?.ExtentSummary ?? row.PlayStationEntry?.ExtentSummary ?? row.GenericEntry?.ExtentSummary ?? row.SnapshotEntry?.Extents ?? string.Empty,
            MetadataStatus = row.NtfsEntry?.MetadataStatus ?? row.PlayStationEntry?.RecoveryDisplayStatus ?? row.GenericEntry?.MetadataStatus ?? row.SnapshotEntry?.MetadataStatus ?? row.PsMetadataEntry?.MetadataStatus ?? (row.Ps3Entry != null ? "PS3 directory entry" : row.RecoveryStatusName),
            MftRecordIndex = row.NtfsEntry?.MftRecordIndex ?? row.SnapshotEntry?.MftRecordIndex ?? -1,
            MftSequenceNumber = row.NtfsEntry?.SequenceNumber ?? row.SnapshotEntry?.MftSequenceNumber ?? 0,
            ParentMftRecordIndex = row.NtfsEntry?.ParentMftRecordIndex ?? row.SnapshotEntry?.ParentMftRecordIndex ?? -1,
            AlternateDataStreamCount = row.NtfsEntry?.AlternateDataStreamCount ?? row.SnapshotEntry?.AlternateDataStreamCount ?? 0
        };

        if (includeChildren)
        {
            snapshot.Children = row.NavigationEntry switch
            {
                DirectoryEntry fatx => fatx.Children.Select(CreateSnapshotEntry).ToList(),
                XboxFileEntry ntfs => ntfs.Volume.GetChildren(ntfs).Select(CreateSnapshotEntry).ToList(),
                PlayStationFileEntry playStation => playStation.Volume.GetChildren(playStation).Select(CreateSnapshotEntry).ToList(),
                GenericFileSystemEntry generic => generic.Volume.GetChildren(generic).Select(CreateSnapshotEntry).ToList(),
                SnapshotFileEntry loaded => loaded.Children.Select(child => child.Clone()).ToList(),
                _ => []
            };
        }

        return snapshot;
    }

    private static DriveDatabaseSnapshot LoadDatabaseSnapshotFromFile(string path)
    {
        var json = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize<DriveDatabaseSnapshot>(json)
            ?? throw new InvalidDataException("Database JSON was empty or invalid.");
        if (snapshot.Partitions.Count == 0)
        {
            snapshot = TryConvertLegacyFatxDatabase(json)
                ?? throw new InvalidDataException("Database did not contain any partitions.");
        }

        return snapshot;
    }

    private void LoadProgressDatabase(string path)
    {
        ApplyProgressDatabase(path, LoadDatabaseSnapshotFromFile(path));
    }

    private void ApplyProgressDatabase(string path, DriveDatabaseSnapshot snapshot)
    {
        if (!HasLoadedImage)
        {
            throw new InvalidOperationException("Open the matching drive image before loading a database.");
        }

        var partitionMatches = ValidateDatabaseForCurrentImage(snapshot);
        _databaseSnapshot = snapshot;

        MetadataResults.Clear();
        CarvedFiles.Clear();
        RecoveryTreeRoots.Clear();
        RecoveryRows.Clear();
        ClusterRows.Clear();
        _recoveryDatabase = null;
        _recoveryIntegrity = null;

        var metadataTopLevelRows = 0;
        var metadataTotalRows = 0;
        var metadataFileRows = 0;
        foreach (var (livePartition, partitionSnapshot) in partitionMatches)
        {
            StampSnapshotPartition(partitionSnapshot.OriginalFilesystem, partitionSnapshot.Name);
            StampSnapshotPartition(partitionSnapshot.Analysis.MetadataAnalyzer, livePartition.Name);
            metadataTopLevelRows += partitionSnapshot.Analysis.MetadataAnalyzer.Count;
            metadataTotalRows += CountSnapshotEntriesRecursive(partitionSnapshot.Analysis.MetadataAnalyzer);
            metadataFileRows += CountSnapshotFilesRecursive(partitionSnapshot.Analysis.MetadataAnalyzer);
            foreach (var row in partitionSnapshot.Analysis.MetadataAnalyzer.Select(entry => new FileRow(entry, "Metadata")))
            {
                MetadataResults.Add(row);
            }

            foreach (var carvedFile in partitionSnapshot.Analysis.FileCarver)
            {
                if (string.IsNullOrWhiteSpace(carvedFile.PartitionName))
                {
                    carvedFile.PartitionName = livePartition.Name;
                }

                NormalizeImportedCarvedFile(carvedFile, livePartition);
                CarvedFiles.Add(CreateCarvedRowFromSnapshot(carvedFile));
            }
        }

        StatusText = "Ready";
        AppendLog($"Loaded database: {path}");
        AppendLog($"Database validation passed: matched {partitionMatches.Count:N0} partition(s) against the open image.");
        AppendLog($"Restored database analysis rows: metadata roots {MetadataResults.Count:N0}, metadata recursive rows {metadataTotalRows:N0}, metadata recursive files {metadataFileRows:N0}, carved files {CarvedFiles.Count:N0}.");
        if (metadataTopLevelRows != metadataTotalRows)
        {
            AppendLog("Legacy JSON note: metadata root-row count differs from recursive file count. Compare recursive files for parity with legacy file totals.");
        }
        if (!string.IsNullOrWhiteSpace(snapshot.ActivePartitionName))
        {
            SelectedPartition = Partitions.FirstOrDefault(partition => string.Equals(partition.Name, snapshot.ActivePartitionName, StringComparison.OrdinalIgnoreCase))
                ?? SelectedPartition;
        }

        LoadSelectedPartition();
        if (MetadataResults.Count > 0)
        {
            ShowMetadataResultsInFileTable(
                $"{SelectedPartition?.Name ?? "FATX"} metadata",
                $"{SelectedPartition?.Name ?? "FATX"} restored metadata: {MetadataResults.Count:N0} entries");
        }
        RefreshSelectionState();
    }

    private static DriveDatabaseSnapshot? TryConvertLegacyFatxDatabase(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Drive", out var drive) ||
            !drive.TryGetProperty("Partitions", out var partitionsElement) ||
            partitionsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var snapshot = new DriveDatabaseSnapshot
        {
            Version = 1,
            Application = "FATXTools legacy",
            SavedAtUtc = DateTime.UtcNow,
            SourceImage = GetJsonString(drive, "FileName")
        };

        foreach (var partitionElement in partitionsElement.EnumerateArray())
        {
            var partition = new PartitionDatabaseSnapshot
            {
                Name = GetJsonString(partitionElement, "Name"),
                Offset = GetJsonInt64(partitionElement, "Offset"),
                Length = GetJsonInt64(partitionElement, "Length"),
                Family = "FATX",
                Status = "Loaded from legacy FATXTools database",
                TotalSpace = GetJsonInt64(partitionElement, "Length")
            };

            if (partitionElement.TryGetProperty("Analysis", out var analysis))
            {
                if (analysis.TryGetProperty("MetadataAnalyzer", out var metadata) &&
                    metadata.ValueKind == JsonValueKind.Array)
                {
                    partition.Analysis.MetadataAnalyzer = metadata
                        .EnumerateArray()
                        .Select(entry => ConvertLegacyDirectoryEntry(entry, partition.Name))
                        .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                        .ToList();
                }

                if (analysis.TryGetProperty("FileCarver", out var carver) &&
                    carver.ValueKind == JsonValueKind.Array)
                {
                    partition.Analysis.FileCarver = carver
                        .EnumerateArray()
                        .Select(entry => new CarvedFileSnapshot
                        {
                            Name = GetJsonString(entry, "Name"),
                            Kind = GetLegacyCarvedKind(entry),
                            Offset = partition.Offset + GetJsonInt64(entry, "Offset"),
                            SourceOffset = GetJsonInt64(entry, "Offset"),
                            Size = GetJsonInt64(entry, "Size"),
                            PartitionName = partition.Name,
                            Source = "Legacy FATXTools database",
                            Detail = $"Imported legacy FATXTools file-carver row; legacy FATX file-area offset 0x{GetJsonInt64(entry, "Offset"):X}",
                            Fragmentation = "Legacy FATXTools carve row",
                            Extents = string.Empty
                        })
                        .ToList();
                }
            }

            snapshot.Partitions.Add(partition);
        }

        return snapshot.Partitions.Count > 0 ? snapshot : null;
    }

    private static SnapshotFileEntry ConvertLegacyDirectoryEntry(JsonElement entry, string partitionName, string parentPath = "")
    {
        var name = GetJsonString(entry, "FileName");
        var attributes = GetJsonInt32(entry, "FileAttributes");
        var isDirectory = (attributes & 0x10) != 0;
        var path = string.IsNullOrWhiteSpace(parentPath)
            ? name
            : $"{parentPath}/{name}";
        var snapshot = new SnapshotFileEntry
        {
            Name = name,
            Path = path,
            PartitionName = partitionName,
            Kind = isDirectory ? "Folder" : "File",
            IsDirectory = isDirectory,
            Size = isDirectory ? -1 : GetJsonInt64(entry, "FileSize"),
            Offset = GetJsonInt64(entry, "Offset"),
            Cluster = (uint)Math.Clamp(GetJsonInt64(entry, "Cluster"), 0, uint.MaxValue),
            FirstCluster = (uint)Math.Clamp(GetJsonInt64(entry, "FirstCluster"), 0, uint.MaxValue),
            IsDeleted = true,
            Attributes = $"0x{attributes:X2}",
            Created = ReadLegacyFatxTimestamp(entry, "CreationTime"),
            Modified = ReadLegacyFatxTimestamp(entry, "LastWriteTime"),
            Accessed = ReadLegacyFatxTimestamp(entry, "LastAccessTime"),
            MetadataStatus = "Imported legacy FATXTools metadata",
            Fragmentation = "Legacy FATXTools metadata",
            Extents = FormatLegacyClusters(entry)
        };

        if (entry.TryGetProperty("Children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            snapshot.Children = children
                .EnumerateArray()
                .Select(child => ConvertLegacyDirectoryEntry(child, partitionName, path))
                .Where(child => !string.IsNullOrWhiteSpace(child.Name))
                .ToList();
        }

        return snapshot;
    }

    private static void NormalizeImportedCarvedFile(CarvedFileSnapshot carvedFile, PartitionModel livePartition)
    {
        if (!carvedFile.Source.Equals("Legacy FATXTools database", StringComparison.OrdinalIgnoreCase)
            || carvedFile.SourceOffset < 0)
        {
            return;
        }

        var relativeOffset = carvedFile.SourceOffset;
        var absoluteOffset = livePartition.FatxVolume != null
            ? livePartition.FatxVolume.Offset + livePartition.FatxVolume.FileAreaByteOffset + relativeOffset
            : livePartition.Offset + relativeOffset;

        carvedFile.Offset = absoluteOffset;
        carvedFile.SourceOffset = absoluteOffset;
    }

    private static string GetLegacyCarvedKind(JsonElement entry)
    {
        var extension = Path.GetExtension(GetJsonString(entry, "Name")).TrimStart('.');
        return string.IsNullOrWhiteSpace(extension)
            ? "File"
            : extension.ToUpperInvariant();
    }

    private static string FormatLegacyClusters(JsonElement entry)
    {
        if (!entry.TryGetProperty("Clusters", out var clustersElement) ||
            clustersElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var clusters = new List<uint>();
        foreach (var cluster in clustersElement.EnumerateArray())
        {
            if (cluster.ValueKind == JsonValueKind.Number && cluster.TryGetUInt32(out var value))
            {
                clusters.Add(value);
            }
        }

        return ClusterChainMetrics.FormatRanges(clusters, maxRanges: 8);
    }

    private static DateTime ReadLegacyFatxTimestamp(JsonElement entry, string propertyName)
    {
        if (!entry.TryGetProperty(propertyName, out var value))
        {
            return default;
        }

        uint raw;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number))
        {
            raw = number;
        }
        else if (!uint.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out raw))
        {
            return default;
        }

        return new X360TimeStamp(raw).AsDateTime();
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : string.Empty;
    }

    private static long GetJsonInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static int GetJsonInt32(JsonElement element, string propertyName)
    {
        return (int)Math.Clamp(GetJsonInt64(element, propertyName), int.MinValue, int.MaxValue);
    }

    private string GetCarvedSourcePath(CarvedFileRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.GenericFile?.SourcePath))
        {
            return row.GenericFile.SourcePath;
        }

        if (!string.IsNullOrWhiteSpace(row.Snapshot?.SourcePath))
        {
            return row.Snapshot.SourcePath;
        }

        return row.Signature != null ? _activeRawImagePath ?? string.Empty : string.Empty;
    }

    private static long GetCarvedSourceOffset(CarvedFileRow row, PartitionModel partition)
    {
        if (row.GenericFile != null)
        {
            return row.GenericFile.SourceOffset;
        }

        if (row.Signature != null && partition.FatxVolume != null)
        {
            return partition.FatxVolume.Offset + partition.FatxVolume.FileAreaByteOffset + row.Signature.Offset;
        }

        return row.Snapshot?.SourceOffset ?? 0;
    }

    private static CarvedFileRow CreateCarvedRowFromSnapshot(CarvedFileSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.SourcePath)
            && snapshot.Size > 0
            && snapshot.SourceOffset >= 0
            && File.Exists(snapshot.SourcePath))
        {
            return new CarvedFileRow(new GenericCarvedFile(
                snapshot.Name,
                snapshot.Kind,
                snapshot.SourcePath,
                snapshot.SourceOffset,
                snapshot.Offset,
                snapshot.Size,
                snapshot.Source,
                snapshot.Detail,
                Extents: ParseExtentText(snapshot.Extents, snapshot.Size),
                FragmentationStatus: snapshot.Fragmentation,
                ExtentSummary: snapshot.Extents));
        }

        return new CarvedFileRow(snapshot);
    }

    private List<(PartitionModel LivePartition, PartitionDatabaseSnapshot DatabasePartition)> ValidateDatabaseForCurrentImage(DriveDatabaseSnapshot snapshot)
    {
        var unmatchedLivePartitions = Partitions.ToList();
        var matches = new List<(PartitionModel LivePartition, PartitionDatabaseSnapshot DatabasePartition)>();
        var mismatches = new List<string>();

        foreach (var databasePartition in snapshot.Partitions)
        {
            var match = unmatchedLivePartitions.FirstOrDefault(partition => IsSamePartitionIdentity(partition, databasePartition));
            if (match == null)
            {
                mismatches.Add($"{databasePartition.Name} @ 0x{databasePartition.Offset:X}, length 0x{databasePartition.Length:X}, {databasePartition.Family}");
                continue;
            }

            unmatchedLivePartitions.Remove(match);
            matches.Add((match, databasePartition));
        }

        if (mismatches.Count > 0)
        {
            var detail = string.Join(Environment.NewLine, mismatches.Take(8));
            var suffix = mismatches.Count > 8 ? $"{Environment.NewLine}+{mismatches.Count - 8:N0} more mismatch(es)" : string.Empty;
            throw new InvalidDataException($"Database does not match the currently open image. Unmatched database partition(s):{Environment.NewLine}{detail}{suffix}");
        }

        if (matches.Count == 0)
        {
            throw new InvalidDataException("Database did not match any partition in the currently open image.");
        }

        var sourceFileName = Path.GetFileName(snapshot.SourceImage);
        var openFileName = Path.GetFileName(_openedImagePath);
        if (!string.IsNullOrWhiteSpace(sourceFileName)
            && !string.IsNullOrWhiteSpace(openFileName)
            && !string.Equals(sourceFileName, openFileName, StringComparison.OrdinalIgnoreCase))
        {
            AppendLog($"Database source name differs from open image name: database '{sourceFileName}', open image '{openFileName}'. Partition scan still matched.");
        }

        return matches;
    }

    private static bool IsSamePartitionIdentity(PartitionModel livePartition, PartitionDatabaseSnapshot databasePartition)
    {
        return livePartition.Offset == databasePartition.Offset
               && livePartition.Length == databasePartition.Length
               && string.Equals(NormalizePartitionFamily(livePartition.FamilyText), NormalizePartitionFamily(databasePartition.Family), StringComparison.OrdinalIgnoreCase)
               && (string.IsNullOrWhiteSpace(databasePartition.Name)
                   || string.Equals(livePartition.Name, databasePartition.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePartitionFamily(string value)
    {
        var normalized = value.Trim();
        return normalized.Contains("FATX", StringComparison.OrdinalIgnoreCase)
            ? "FATX"
            : normalized;
    }

    private static void StampSnapshotPartition(IEnumerable<SnapshotFileEntry> entries, string partitionName)
    {
        foreach (var entry in entries)
        {
            entry.PartitionName = partitionName;
            StampSnapshotPartition(entry.Children, partitionName);
        }
    }

    private static int CountSnapshotEntriesRecursive(IEnumerable<SnapshotFileEntry> entries)
    {
        var total = 0;
        foreach (var entry in entries)
        {
            total++;
            total += CountSnapshotEntriesRecursive(entry.Children);
        }

        return total;
    }

    private static int CountSnapshotFilesRecursive(IEnumerable<SnapshotFileEntry> entries)
    {
        var total = 0;
        foreach (var entry in entries)
        {
            if (!entry.IsDirectory)
            {
                total++;
            }

            total += CountSnapshotFilesRecursive(entry.Children);
        }

        return total;
    }

    private async Task<bool> RunUiTaskAsync<T>(string busyText, Func<T> worker, Action<T> completed, bool showError = true)
    {
        try
        {
            _isOpeningImage = true;
            UpdateTaskState();
            StatusText = busyText;
            AppendLog(busyText);
            var result = await Task.Run(worker);
            completed(result);
            StatusText = "Ready";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = "Failed";
            AppendLog($"{busyText} failed: {ex.Message}");
            if (showError)
            {
                MessageBox.Show(this, ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return false;
        }
        finally
        {
            _isOpeningImage = false;
            UpdateTaskState();
            RefreshSelectionState();
        }
    }

    private void SetInspectorPreview(ImageSource? image)
    {
        InspectorPreviewImage = image;
        IsInspectorPreviewVisible = image != null;
    }

    private static ImageSource? TryCreateFileImagePreview(FileRow row)
    {
        const long maxPreviewBytes = 128L * 1024 * 1024;
        if (row.IsFolder
            || !LooksLikePreviewableImage(row.Name, row.Kind)
            || row.SizeBytes <= 0
            || row.SizeBytes > maxPreviewBytes
            || row.SnapshotEntry != null
            || row.Ps3Entry != null)
        {
            return null;
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"DriveAssistantPreview-{Guid.NewGuid():N}.tmp");
        try
        {
            WriteDirectoryEntry(row, tempPath);
            using var input = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            return TryDecodeImagePreview(input);
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Preview generation must not block normal file inspection.
            }
        }
    }

    private static ImageSource? TryCreateCarvedImagePreview(CarvedFileRow row)
    {
        const long maxPreviewBytes = 128L * 1024 * 1024;
        if (!LooksLikePreviewableImage(row.Name, row.Kind)
            || row.SizeBytes <= 0
            || row.SizeBytes > maxPreviewBytes
            || row.GenericFile?.HasFileData != true)
        {
            return null;
        }

        try
        {
            using var input = new FileStream(row.GenericFile.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
            if (row.GenericFile.SourceOffset < 0 || row.GenericFile.SourceOffset >= input.Length)
            {
                return null;
            }

            var available = input.Length - row.GenericFile.SourceOffset;
            var length = Math.Min(row.GenericFile.Size, available);
            if (length <= 0 || length > maxPreviewBytes)
            {
                return null;
            }

            input.Position = row.GenericFile.SourceOffset;
            using var buffer = new MemoryStream((int)Math.Min(length, 8 * 1024 * 1024));
            var copyBuffer = new byte[1024 * 1024];
            var remaining = length;
            while (remaining > 0)
            {
                var read = input.Read(copyBuffer, 0, (int)Math.Min(copyBuffer.Length, remaining));
                if (read == 0)
                {
                    break;
                }

                buffer.Write(copyBuffer, 0, read);
                remaining -= read;
            }

            if (buffer.Length == 0)
            {
                return null;
            }

            return TryDecodeImagePreview(buffer);
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryDecodeImagePreview(Stream stream)
    {
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.DecodePixelWidth = 300;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static bool LooksLikePreviewableImage(string name, string kind)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff")
        {
            return true;
        }

        return kind.Equals("PNG", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("JPEG", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("JPG", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("BMP", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("GIF", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("TIFF", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateInspector(PartitionModel partition)
    {
        SetInspectorPreview(null);
        InspectorTitle = partition.Name;
        InspectorSubtitle = partition.Status;
        InspectorRows.Clear();
        InspectorRows.Add(new InspectorRow("Offset", partition.OffsetText));
        InspectorRows.Add(new InspectorRow("Length", partition.LengthText));
        InspectorRows.Add(new InspectorRow("Family", partition.FamilyText));
        if (partition.NtfsVolume != null)
        {
            InspectorRows.Add(new InspectorRow("Partition GUID", partition.NtfsVolume.PartitionTypeGuid.ToString()));
            if (!string.IsNullOrWhiteSpace(partition.NtfsVolume.PartitionLabel))
            {
                InspectorRows.Add(new InspectorRow("GPT Label", partition.NtfsVolume.PartitionLabel));
            }
            InspectorRows.Add(new InspectorRow("Cluster Size", FormatBytes(partition.NtfsVolume.ClusterSize)));
        }
        else if (partition.PlayStationVolume != null && !string.IsNullOrWhiteSpace(partition.PlayStationVolume.LoadError))
        {
            InspectorRows.Add(new InspectorRow("Load Error", partition.PlayStationVolume.LoadError));
        }

        if (partition.IsMounted)
        {
            InspectorRows.Add(new InspectorRow("Used", FormatBytes(partition.UsedSpace)));
            InspectorRows.Add(new InspectorRow("Free", FormatBytes(partition.FreeSpace)));
            InspectorRows.Add(new InspectorRow("Total", FormatBytes(partition.TotalSpace)));
        }
    }

    private void UpdateInspector(FileRow row)
    {
        SetInspectorPreview(TryCreateFileImagePreview(row));
        InspectorTitle = row.Name;
        InspectorSubtitle = $"{row.Source} {row.Kind}";
        InspectorRows.Clear();
        if (row.IsFolder)
        {
            InspectorRows.Add(new InspectorRow("Contents", row.DirectorySummary));
        }
        InspectorRows.Add(new InspectorRow("Size", row.SizeText));
        InspectorRows.Add(new InspectorRow("Offset", row.OffsetText));
        InspectorRows.Add(new InspectorRow("Cluster", row.ClusterText));
        InspectorRows.Add(new InspectorRow("Created", row.CreatedText));
        InspectorRows.Add(new InspectorRow("Modified", row.ModifiedText));
        InspectorRows.Add(new InspectorRow("Accessed", row.AccessedText));
        InspectorRows.Add(new InspectorRow("Deleted", row.IsDeleted ? "Yes" : "No"));
        if (row.NtfsEntry != null)
        {
            InspectorRows.Add(new InspectorRow("MFT Record", row.NtfsEntry.MftRecordIndex >= 0 ? row.NtfsEntry.MftRecordIndex.ToString("N0") : string.Empty));
            InspectorRows.Add(new InspectorRow("MFT Sequence", row.NtfsEntry.SequenceNumber.ToString("N0")));
            InspectorRows.Add(new InspectorRow("Parent MFT", row.NtfsEntry.ParentMftRecordIndex >= 0 ? row.NtfsEntry.ParentMftRecordIndex.ToString("N0") : string.Empty));
            InspectorRows.Add(new InspectorRow("Metadata Status", row.NtfsEntry.MetadataStatus));
            InspectorRows.Add(new InspectorRow("Attributes", row.NtfsEntry.Attributes.ToString()));
            InspectorRows.Add(new InspectorRow("Alternate Streams", row.NtfsEntry.AlternateDataStreamCount.ToString("N0")));
            InspectorRows.Add(new InspectorRow("Extents", row.NtfsEntry.ExtentSummary));
        }
        else if (row.PlayStationEntry != null)
        {
            InspectorRows.Add(new InspectorRow("Recovery", row.PlayStationEntry.RecoveryDisplayStatus));
            InspectorRows.Add(new InspectorRow("Fragment Runs", row.PlayStationEntry.DataRunCount.ToString("N0")));
            InspectorRows.Add(new InspectorRow("Data Offsets", row.PlayStationEntry.DataOffsetCount.ToString("N0")));
            InspectorRows.Add(new InspectorRow("Largest Run", FormatBytes(row.PlayStationEntry.LargestRunBytes)));
            InspectorRows.Add(new InspectorRow("Extents", row.PlayStationEntry.ExtentSummary));
        }
        else if (row.GenericEntry != null)
        {
            InspectorRows.Add(new InspectorRow("Filesystem", row.GenericEntry.Volume.FamilyText));
            InspectorRows.Add(new InspectorRow("Metadata Status", row.GenericEntry.MetadataStatus));
            InspectorRows.Add(new InspectorRow("Attributes", row.GenericEntry.Attributes));
            InspectorRows.Add(new InspectorRow("Recovery", row.GenericEntry.FragmentationStatus));
            InspectorRows.Add(new InspectorRow("Extents", row.GenericEntry.ExtentSummary));
        }
        else if (row.SnapshotEntry != null)
        {
            InspectorRows.Add(new InspectorRow("MFT Record", row.SnapshotEntry.MftRecordIndex >= 0 ? row.SnapshotEntry.MftRecordIndex.ToString("N0") : string.Empty));
            InspectorRows.Add(new InspectorRow("MFT Sequence", row.SnapshotEntry.MftSequenceNumber.ToString("N0")));
            InspectorRows.Add(new InspectorRow("Parent MFT", row.SnapshotEntry.ParentMftRecordIndex >= 0 ? row.SnapshotEntry.ParentMftRecordIndex.ToString("N0") : string.Empty));
            InspectorRows.Add(new InspectorRow("Metadata Status", row.SnapshotEntry.MetadataStatus));
            InspectorRows.Add(new InspectorRow("Attributes", row.SnapshotEntry.Attributes));
            InspectorRows.Add(new InspectorRow("Alternate Streams", row.SnapshotEntry.AlternateDataStreamCount.ToString("N0")));
            InspectorRows.Add(new InspectorRow("Extents", row.SnapshotEntry.Extents));
        }
        else if (row.Ps3Entry != null)
        {
            InspectorRows.Add(new InspectorRow("Partition", row.Ps3PartitionName ?? string.Empty));
            InspectorRows.Add(new InspectorRow("Inode", row.Ps3Entry.Inode.ToString("N0")));
            InspectorRows.Add(new InspectorRow("Record Length", row.Ps3Entry.RecordLength.ToString("N0")));
            InspectorRows.Add(new InspectorRow("Dirent Type", $"{row.Ps3Entry.FileType}"));
            InspectorRows.Add(new InspectorRow("Name Length", row.Ps3Entry.NameLength.ToString("N0")));
            InspectorRows.Add(new InspectorRow("Metadata Status", "PS3 directory entry"));
        }
        else if (row.PsMetadataEntry != null)
        {
            InspectorRows.Add(new InspectorRow("Partition", row.Ps3PartitionName ?? string.Empty));
            InspectorRows.Add(new InspectorRow("Inode", row.PsMetadataEntry.Inode.ToString("N0")));
            InspectorRows.Add(new InspectorRow("Record Length", row.PsMetadataEntry.RecordLength.ToString("N0")));
            InspectorRows.Add(new InspectorRow("Dirent Type", $"{row.PsMetadataEntry.FileType}"));
            InspectorRows.Add(new InspectorRow("Name Length", row.PsMetadataEntry.NameLength.ToString("N0")));
            InspectorRows.Add(new InspectorRow("Metadata Status", row.PsMetadataEntry.MetadataStatus));
            InspectorRows.Add(new InspectorRow("Data Offsets", row.PsMetadataEntry.DataOffsetCount.ToString("N0")));
            InspectorRows.Add(new InspectorRow("Fragment Runs", row.PsMetadataEntry.DataRunCount.ToString("N0")));
            InspectorRows.Add(new InspectorRow("Largest Run", FormatBytes(row.PsMetadataEntry.LargestRunBytes)));
            InspectorRows.Add(new InspectorRow("Extents", row.BuildPlayStationMetadataExtentSummary() ?? string.Empty));
        }
        if (row.HasRecoveryStatus)
        {
            InspectorRows.Add(new InspectorRow("Recovery", row.FragmentationText));
            InspectorRows.Add(new InspectorRow("Recovery Detail", row.RecoveryStatusText));
            InspectorRows.Add(new InspectorRow("Clusters", row.ClusterCountText));
            InspectorRows.Add(new InspectorRow("Fragment Runs", row.FragmentRunText));
            InspectorRows.Add(new InspectorRow("Largest Run", row.LargestRunText));
            InspectorRows.Add(new InspectorRow("Collisions", row.CollisionText));
            InspectorRows.Add(new InspectorRow("Collision Percent", row.CollisionPercentText));
            InspectorRows.Add(new InspectorRow("Cluster Ranges", row.ClusterRangesText));
        }
    }

    private void UpdateInspector(RecoveryFileRow row)
    {
        SetInspectorPreview(null);
        InspectorTitle = row.Name;
        InspectorSubtitle = $"Recovery View {row.Kind}";
        InspectorRows.Clear();
        if (row.IsFolder)
        {
            InspectorRows.Add(new InspectorRow("Contents", row.DirectorySummary));
        }
        InspectorRows.Add(new InspectorRow("Score", row.ScoreText));
        InspectorRows.Add(new InspectorRow("Status", row.Status));
        InspectorRows.Add(new InspectorRow("Size", row.SizeText));
        InspectorRows.Add(new InspectorRow("Offset", row.OffsetText));
        InspectorRows.Add(new InspectorRow("Cluster", row.ClusterText));
        InspectorRows.Add(new InspectorRow("Created", row.CreatedText));
        InspectorRows.Add(new InspectorRow("Modified", row.ModifiedText));
        InspectorRows.Add(new InspectorRow("Accessed", row.AccessedText));
        InspectorRows.Add(new InspectorRow("Recovered", row.File.IsDeleted ? "Yes" : "No"));
        InspectorRows.Add(new InspectorRow("Collisions", row.File.GetCollisions()?.Count.ToString() ?? "0"));
        InspectorRows.Add(new InspectorRow("Clusters", row.ClusterCountText));
        InspectorRows.Add(new InspectorRow("Fragment Runs", row.FragmentRunText));
        InspectorRows.Add(new InspectorRow("Largest Run", row.LargestRunText));
        InspectorRows.Add(new InspectorRow("Collision Percent", row.CollisionPercentText));
        InspectorRows.Add(new InspectorRow("Cluster Ranges", row.ClusterRangesText));
    }

    private void UpdateInspector(ClusterRow row)
    {
        SetInspectorPreview(null);
        InspectorTitle = $"Cluster {row.Cluster}";
        InspectorSubtitle = row.Status;
        InspectorRows.Clear();
        InspectorRows.Add(new InspectorRow("Offset", row.OffsetText));
        InspectorRows.Add(new InspectorRow("Occupants", row.OccupantCount.ToString()));
        InspectorRows.Add(new InspectorRow("Files", row.OccupantsText));
    }

    private void UpdateInspector(CarvedFileRow row)
    {
        SetInspectorPreview(TryCreateCarvedImagePreview(row));
        InspectorTitle = row.Name;
        InspectorSubtitle = "Carved file";
        InspectorRows.Clear();
        InspectorRows.Add(new InspectorRow("Kind", row.Kind));
        InspectorRows.Add(new InspectorRow("Size", row.SizeText));
        InspectorRows.Add(new InspectorRow("Offset", row.OffsetText));
        InspectorRows.Add(new InspectorRow("Relative", row.RelativeOffsetText));
        if (!string.IsNullOrWhiteSpace(row.Source))
        {
            InspectorRows.Add(new InspectorRow("Source", row.Source));
        }

        if (!string.IsNullOrWhiteSpace(row.Detail))
        {
            InspectorRows.Add(new InspectorRow("Detail", row.Detail));
        }

        if (!row.HasFileData)
        {
            InspectorRows.Add(new InspectorRow("Source", "Loaded database metadata only"));
        }
    }

    private void UpdateInspector(DirectoryNode node)
    {
        SetInspectorPreview(null);
        InspectorTitle = node.Name;
        InspectorSubtitle = node.IsRecoveredClusterGroup
            ? "Recovered cluster group"
            : node.NavigationEntry == null ? "Original filesystem root" : "Active Folder";
        InspectorRows.Clear();
        InspectorRows.Add(new InspectorRow("Contents", node.SummaryText));
        if (node.Entry != null)
        {
            InspectorRows.Add(new InspectorRow("Offset", $"0x{node.Entry.Offset:X}"));
            InspectorRows.Add(new InspectorRow("Cluster", node.Entry.Cluster.ToString()));
            InspectorRows.Add(new InspectorRow("Created", node.Entry.CreationTime.AsDateTime().ToString("yyyy-MM-dd HH:mm:ss")));
            InspectorRows.Add(new InspectorRow("Modified", node.Entry.LastWriteTime.AsDateTime().ToString("yyyy-MM-dd HH:mm:ss")));
        }
        else if (node.NtfsEntry != null)
        {
            InspectorRows.Add(new InspectorRow("Offset", $"0x{node.NtfsEntry.Offset:X}"));
            InspectorRows.Add(new InspectorRow("Cluster", node.NtfsEntry.Cluster.ToString()));
            InspectorRows.Add(new InspectorRow("Created", node.NtfsEntry.Created.ToString("yyyy-MM-dd HH:mm:ss")));
            InspectorRows.Add(new InspectorRow("Modified", node.NtfsEntry.Modified.ToString("yyyy-MM-dd HH:mm:ss")));
        }
        else if (node.PlayStationEntry != null)
        {
            InspectorRows.Add(new InspectorRow("Offset", $"0x{node.PlayStationEntry.Offset:X}"));
            InspectorRows.Add(new InspectorRow("Created", node.PlayStationEntry.Created.ToString("yyyy-MM-dd HH:mm:ss")));
            InspectorRows.Add(new InspectorRow("Modified", node.PlayStationEntry.Modified.ToString("yyyy-MM-dd HH:mm:ss")));
            InspectorRows.Add(new InspectorRow("Recovery", node.PlayStationEntry.RecoveryDisplayStatus));
        }
        else if (node.GenericEntry != null)
        {
            InspectorRows.Add(new InspectorRow("Offset", $"0x{node.GenericEntry.Offset:X}"));
            InspectorRows.Add(new InspectorRow("Cluster", node.GenericEntry.Cluster == 0 ? string.Empty : node.GenericEntry.Cluster.ToString()));
            InspectorRows.Add(new InspectorRow("Created", node.GenericEntry.Created.ToString("yyyy-MM-dd HH:mm:ss")));
            InspectorRows.Add(new InspectorRow("Modified", node.GenericEntry.Modified.ToString("yyyy-MM-dd HH:mm:ss")));
            InspectorRows.Add(new InspectorRow("Metadata Status", node.GenericEntry.MetadataStatus));
        }
        else if (node.SnapshotEntry != null)
        {
            InspectorRows.Add(new InspectorRow("Offset", $"0x{node.SnapshotEntry.Offset:X}"));
            InspectorRows.Add(new InspectorRow("Cluster", node.SnapshotEntry.Cluster == 0 ? string.Empty : node.SnapshotEntry.Cluster.ToString()));
            InspectorRows.Add(new InspectorRow("Created", node.SnapshotEntry.Created.ToString("yyyy-MM-dd HH:mm:ss")));
            InspectorRows.Add(new InspectorRow("Modified", node.SnapshotEntry.Modified.ToString("yyyy-MM-dd HH:mm:ss")));
            InspectorRows.Add(new InspectorRow("Metadata Status", node.SnapshotEntry.MetadataStatus));
        }
    }

    private void RefreshSelectionState()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasActiveVolume));
        OnPropertyChanged(nameof(CanRunMetadataScan));
        OnPropertyChanged(nameof(CanRunFileCarver));
        OnPropertyChanged(nameof(HasOpenDatabase));
        OnPropertyChanged(nameof(CanCancelScan));
        OnPropertyChanged(nameof(CanCancelExport));
        OnPropertyChanged(nameof(CanAddCustomPartition));
        OnPropertyChanged(nameof(CanUnmountPartition));
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateTaskState()
    {
        IsBusy = _isOpeningImage || _isMetadataScanRunning || _isFileCarverRunning || _isExportRunning;
        RefreshSelectionState();
    }

    private ScanProgressRow GetOrCreateScanProgressRow(string title)
    {
        var row = ScanProgressRows.FirstOrDefault(item => item.Title == title);
        if (row != null)
        {
            return row;
        }

        row = new ScanProgressRow(title);
        ScanProgressRows.Add(row);
        IsScanProgressVisible = true;
        return row;
    }

    private void SelectLogTab()
    {
        ResultsTabs.SelectedItem = LogTab;
    }

    private void AppendLog(string line)
    {
        _logLines.Add(FormatLogLine(line));
        RefreshLogText();
    }

    private string EnsurePlayStationDecryptedPartition(PlayStationVolume volume)
    {
        var existing = TryGetExistingPlayStationDecryptedPartition(volume);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var path = Path.Combine(
            Path.GetTempPath(),
            $"drive_assistant_ps_{SanitizeFileName(volume.Name)}_{DateTime.UtcNow.Ticks}.img");
        if (!EnsureEnoughDiskSpace(path, volume.Length, "temporary PlayStation decrypt"))
        {
            throw new IOException("Not enough free space to create the temporary decrypted PlayStation partition image.");
        }

        volume.DecryptToFile(path);
        _temporaryScanFiles.Add(path);
        return path;
    }

    private string? TryGetExistingPlayStationDecryptedPartition(PlayStationVolume volume)
    {
        return _temporaryScanFiles.FirstOrDefault(path =>
            Path.GetFileName(path).Contains(SanitizeFileName(volume.Name), StringComparison.OrdinalIgnoreCase) &&
            File.Exists(path));
    }

    private static bool IsPlayStationUfsDeletedCandidateScanRelevant(PlayStationVolume volume)
    {
        return volume.Name.Equals("user", StringComparison.OrdinalIgnoreCase)
               || volume.Name.Equals("eap_user", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlayStationFatDeletedCandidateScanRelevant(PlayStationVolume volume)
    {
        return volume.Name.Equals("dev_hdd1", StringComparison.OrdinalIgnoreCase)
               || volume.Name.Equals("dev_flash1", StringComparison.OrdinalIgnoreCase)
               || volume.Name.Equals("dev_flash2", StringComparison.OrdinalIgnoreCase)
               || volume.Name.Equals("dev_flash3", StringComparison.OrdinalIgnoreCase)
               || volume.Name.Equals("eap_vsh", StringComparison.OrdinalIgnoreCase)
               || volume.Name.Equals("system_ex", StringComparison.OrdinalIgnoreCase);
    }

    private void ClearTemporaryScanFiles()
    {
        foreach (var path in _temporaryScanFiles.ToList())
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort cleanup; recovery scans should not fail because a temp file is locked.
            }
        }

        _temporaryScanFiles.Clear();
    }

    private void BeginLiveLog(string key, string line)
    {
        _liveLogLineIndexes.Remove(key);
        UpdateLiveLog(key, line);
    }

    private void UpdateLiveLog(string key, string line)
    {
        var formatted = FormatLogLine(line);
        if (_liveLogLineIndexes.TryGetValue(key, out var index) && index >= 0 && index < _logLines.Count)
        {
            _logLines[index] = formatted;
        }
        else
        {
            _liveLogLineIndexes[key] = _logLines.Count;
            _logLines.Add(formatted);
        }

        RefreshLogText();
    }

    private void RefreshLogText()
    {
        var logBox = LogTextBox;

        LogText = _logLines.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, _logLines) + Environment.NewLine;

        logBox?.Dispatcher.BeginInvoke(new Action(() =>
        {
            logBox.ScrollToEnd();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private static string FormatLogLine(string line)
    {
        return $"[{DateTime.Now:HH:mm:ss}] {line}";
    }

    private static string FormatBytes(long value)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        int suffix = 0;
        while (size >= 1024 && suffix < suffixes.Length - 1)
        {
            size /= 1024;
            suffix++;
        }

        return $"{size:0.##} {suffixes[suffix]}";
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(invalid.Contains(c) ? '_' : c);
        }

        var result = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? "recovered.bin" : result;
    }

    private static string GetUniqueFilePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 1; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{fileName} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Could not find a unique file name for {path}.");
    }

    private static string GetUniqueDirectoryPath(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return path;
        }

        var parent = Path.GetDirectoryName(path) ?? string.Empty;
        var folderName = Path.GetFileName(path);
        for (var index = 1; index < 1000; index++)
        {
            var candidate = Path.Combine(parent, $"{folderName} ({index})");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Could not find a unique folder name for {path}.");
    }

    private static bool IsSameDirectory(DirectoryEntry? left, DirectoryEntry? right)
    {
        if (left == null || right == null)
        {
            return left == null && right == null;
        }

        return ReferenceEquals(left, right) || left.Offset == right.Offset;
    }

    private static bool IsSameDirectory(object? left, object? right)
    {
        if (left is DirectoryEntry leftFatx && right is DirectoryEntry rightFatx)
        {
            return IsSameDirectory(leftFatx, rightFatx);
        }

        if (left is XboxFileEntry leftNtfs && right is XboxFileEntry rightNtfs)
        {
            return ReferenceEquals(leftNtfs, rightNtfs)
                   || ReferenceEquals(leftNtfs.Volume, rightNtfs.Volume)
                   && leftNtfs.Path.Equals(rightNtfs.Path, StringComparison.OrdinalIgnoreCase);
        }

        if (left is PlayStationFileEntry leftPlayStation && right is PlayStationFileEntry rightPlayStation)
        {
            return ReferenceEquals(leftPlayStation, rightPlayStation)
                   || ReferenceEquals(leftPlayStation.Volume, rightPlayStation.Volume)
                   && leftPlayStation.Path.Equals(rightPlayStation.Path, StringComparison.OrdinalIgnoreCase);
        }

        if (left is GenericFileSystemEntry leftGeneric && right is GenericFileSystemEntry rightGeneric)
        {
            return ReferenceEquals(leftGeneric, rightGeneric)
                   || ReferenceEquals(leftGeneric.Volume, rightGeneric.Volume)
                   && leftGeneric.Path.Equals(rightGeneric.Path, StringComparison.OrdinalIgnoreCase);
        }

        return left == null && right == null;
    }

    private static string GetDirectoryDisplayName(object? directory)
    {
        return directory switch
        {
            DirectoryEntry entry => entry.FileName,
            XboxFileEntry entry => entry.Name,
            PlayStationFileEntry entry => entry.Name,
            GenericFileSystemEntry entry => entry.Name,
            SnapshotFileEntry entry => entry.Name,
            _ => "Root"
        };
    }

    private List<FileRow> GetSelectedFileRows()
    {
        return FilesGrid.SelectedItems
            .OfType<FileRow>()
            .GroupBy(row => row.Identity)
            .Select(group => group.First())
            .ToList();
    }

    private List<RecoveryFileRow> GetSelectedRecoveryRows()
    {
        return RecoveryGrid.SelectedItems
            .OfType<RecoveryFileRow>()
            .GroupBy(row => (row.Volume, row.File.Offset, row.Name))
            .Select(group => group.First())
            .ToList();
    }

    private List<CarvedFileRow> GetSelectedCarvedRows()
    {
        return CarverGrid.SelectedItems
            .OfType<CarvedFileRow>()
            .GroupBy(row => (row.Volume, row.Offset, row.Name))
            .Select(group => group.First())
            .ToList();
    }

    private void CopyRowsToClipboard(IEnumerable<string> rows, string successMessage)
    {
        var text = string.Join(Environment.NewLine, rows.Where(row => !string.IsNullOrWhiteSpace(row)));
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText = "No address details available for the selection.";
            return;
        }

        try
        {
            Clipboard.SetText(text);
            StatusText = successMessage;
            AppendLog(successMessage);
        }
        catch (Exception ex)
        {
            StatusText = "Clipboard copy failed.";
            AppendLog($"Clipboard copy failed: {ex.Message}");
        }
    }

    private static string FormatFileRowFirstCluster(FileRow row)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.ClusterText))
        {
            parts.Add($"cluster {row.ClusterText}");
        }

        if (TryGetFatxClusterAddress(row.Volume, row.ClusterNumber, out var address))
        {
            parts.Add($"address 0x{address:X}");
        }

        return parts.Count == 0 ? "n/a" : string.Join(", ", parts);
    }

    private static string FormatRecoveryRowFirstCluster(RecoveryFileRow row)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.ClusterText))
        {
            parts.Add($"cluster {row.ClusterText}");
        }

        if (TryGetFatxClusterAddress(row.Volume, row.ClusterNumber, out var address))
        {
            parts.Add($"address 0x{address:X}");
        }

        return parts.Count == 0 ? "n/a" : string.Join(", ", parts);
    }

    private static string FormatFileRowAddressDetails(FileRow row)
    {
        return string.Join(Environment.NewLine, new[]
        {
            row.Name,
            $"Source: {row.Source}",
            $"Kind: {row.Kind}",
            $"Offset: {row.OffsetText}",
            $"First cluster: {FormatFileRowFirstCluster(row)}",
            !string.IsNullOrWhiteSpace(row.ClusterRangesText) ? $"Cluster ranges: {row.ClusterRangesText}" : string.Empty
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string FormatRecoveryRowAddressDetails(RecoveryFileRow row)
    {
        return string.Join(Environment.NewLine, new[]
        {
            row.Name,
            $"Kind: {row.Kind}",
            $"Dirent offset: {row.OffsetText}",
            $"First cluster: {FormatRecoveryRowFirstCluster(row)}",
            !string.IsNullOrWhiteSpace(row.ClusterRangesText) ? $"Cluster ranges: {row.ClusterRangesText}" : string.Empty
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string FormatCarvedRowAddressDetails(CarvedFileRow row)
    {
        return string.Join(Environment.NewLine, new[]
        {
            row.Name,
            $"Kind: {row.Kind}",
            $"Offset: {row.OffsetText}",
            !string.IsNullOrWhiteSpace(row.RelativeOffsetText) ? $"Relative offset: {row.RelativeOffsetText}" : string.Empty,
            !string.IsNullOrWhiteSpace(row.Source) ? $"Source: {row.Source}" : string.Empty,
            !string.IsNullOrWhiteSpace(row.Detail) ? $"Detail: {row.Detail}" : string.Empty
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static bool TryGetFatxClusterAddress(Volume? volume, long cluster, out long address)
    {
        address = 0;
        if (volume == null || cluster <= 0 || cluster > uint.MaxValue)
        {
            return false;
        }

        try
        {
            address = volume.ClusterToPhysicalOffset((uint)cluster);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetContextMenuGrid(object sender, out DataGrid grid)
    {
        grid = null!;
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: DataGrid dataGrid } })
        {
            grid = dataGrid;
            return true;
        }

        return false;
    }

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static void SavePanelWidth(ColumnDefinition column, ref GridLength storedWidth)
    {
        if (column.ActualWidth >= 120)
        {
            storedWidth = new GridLength(column.ActualWidth);
        }
    }

    private static GridLength EnsureUsableWidth(GridLength storedWidth, double fallback)
    {
        var value = storedWidth.Value >= 120 ? storedWidth.Value : fallback;
        return new GridLength(value);
    }
}

internal sealed class RebuildProgressDialog : Window
{
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _stageText;
    private readonly TextBlock _detailText;
    private readonly Button _pauseButton;
    private bool _isPaused;

    public RebuildProgressDialog()
    {
        Title = "FATX Rebuild Progress";
        Width = 520;
        Height = 200;
        MinWidth = 480;
        MinHeight = 180;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        _stageText = new TextBlock
        {
            Text = "Preparing rebuild...",
            FontWeight = FontWeights.SemiBold
        };
        _detailText = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(149, 164, 184)),
            Text = string.Empty
        };
        _progressBar = new ProgressBar
        {
            Margin = new Thickness(0, 14, 0, 0),
            Minimum = 0,
            Maximum = 100,
            Height = 16,
            Value = 0
        };

        _pauseButton = new Button
        {
            MinWidth = 92,
            Content = "Pause",
            Margin = new Thickness(0, 12, 8, 0)
        };
        _pauseButton.Click += (_, _) =>
        {
            _isPaused = !_isPaused;
            _pauseButton.Content = _isPaused ? "Resume" : "Pause";
            PauseChanged?.Invoke(this, _isPaused);
        };

        var cancelButton = new Button
        {
            MinWidth = 92,
            Content = "Cancel",
            Margin = new Thickness(0, 12, 0, 0)
        };
        cancelButton.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _pauseButton, cancelButton }
        };

        var layoutRoot = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto }
            },
            Children =
            {
                _stageText,
                _detailText,
                _progressBar,
                buttons
            }
        };
        Grid.SetRow(_detailText, 1);
        Grid.SetRow(_progressBar, 2);
        Grid.SetRow(buttons, 3);

        Content = new Border
        {
            Padding = new Thickness(16),
            Child = layoutRoot
        };
    }

    public event EventHandler? CancelRequested;

    public event EventHandler<bool>? PauseChanged;

    public void Update(int percent, string stage, string detail)
    {
        _progressBar.Value = Math.Clamp(percent, 0, 100);
        _stageText.Text = $"{Math.Clamp(percent, 0, 100):0}% - {stage}";
        _detailText.Text = detail;
    }
}

public sealed class DriveDatabaseSnapshot
{
    public int Version { get; set; }

    public string Application { get; set; } = string.Empty;

    public DateTime SavedAtUtc { get; set; }

    public string SourceImage { get; set; } = string.Empty;

    public string ActivePartitionName { get; set; } = string.Empty;

    public List<PartitionDatabaseSnapshot> Partitions { get; set; } = [];
}

public sealed class PartitionDatabaseSnapshot
{
    public string Name { get; set; } = string.Empty;

    public long Offset { get; set; }

    public long Length { get; set; }

    public string Family { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public long UsedSpace { get; set; }

    public long FreeSpace { get; set; }

    public long TotalSpace { get; set; }

    public List<SnapshotFileEntry> OriginalFilesystem { get; set; } = [];

    public PartitionAnalysisSnapshot Analysis { get; set; } = new();
}

public sealed class PartitionAnalysisSnapshot
{
    public List<SnapshotFileEntry> MetadataAnalyzer { get; set; } = [];

    public List<CarvedFileSnapshot> FileCarver { get; set; } = [];
}

public sealed class CarvedFileSnapshot
{
    public string Name { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string PartitionName { get; set; } = string.Empty;

    public long Offset { get; set; }

    public long SourceOffset { get; set; }

    public string SourcePath { get; set; } = string.Empty;

    public long Size { get; set; }

    public string Source { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public string Fragmentation { get; set; } = string.Empty;

    public string Extents { get; set; } = string.Empty;

    public int FragmentRunCount
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Extents))
            {
                return 0;
            }

            return Extents.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        }
    }
}

public sealed class SnapshotPartition
{
    private readonly HashSet<SnapshotFileEntry> _entries = [];

    public SnapshotPartition(PartitionDatabaseSnapshot snapshot)
    {
        Snapshot = snapshot;
        RegisterEntries(snapshot.OriginalFilesystem);
        RegisterEntries(snapshot.Analysis.MetadataAnalyzer);
    }

    public PartitionDatabaseSnapshot Snapshot { get; }

    public string Name => Snapshot.Name;

    public long Offset => Snapshot.Offset;

    public long Length => Snapshot.Length;

    public string Family => Snapshot.Family;

    public long UsedSpace => Snapshot.UsedSpace;

    public long FreeSpace => Snapshot.FreeSpace;

    public long TotalSpace => Snapshot.TotalSpace;

    public IReadOnlyList<SnapshotFileEntry> MetadataEntries => Snapshot.Analysis.MetadataAnalyzer;

    public IReadOnlyList<SnapshotFileEntry> GetRoot() => Snapshot.OriginalFilesystem;

    public IReadOnlyList<SnapshotFileEntry> GetChildren(SnapshotFileEntry? directory) => directory?.Children ?? Snapshot.OriginalFilesystem;

    public bool Contains(SnapshotFileEntry entry) => _entries.Contains(entry);

    private void RegisterEntries(IEnumerable<SnapshotFileEntry> entries)
    {
        foreach (var entry in entries)
        {
            _entries.Add(entry);
            RegisterEntries(entry.Children);
        }
    }
}

public sealed class SnapshotFileEntry
{
    public string Path { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string PartitionName { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public long Size { get; set; }

    public DateTime Created { get; set; }

    public DateTime Modified { get; set; }

    public DateTime Accessed { get; set; }

    public long Offset { get; set; }

    public long Cluster { get; set; }

    public bool IsDeleted { get; set; }

    public string Attributes { get; set; } = string.Empty;

    public uint FirstCluster { get; set; }

    public string Fragmentation { get; set; } = string.Empty;

    public string Extents { get; set; } = string.Empty;

    public string MetadataStatus { get; set; } = string.Empty;

    public long MftRecordIndex { get; set; } = -1;

    public int MftSequenceNumber { get; set; }

    public long ParentMftRecordIndex { get; set; } = -1;

    public int AlternateDataStreamCount { get; set; }

    public List<SnapshotFileEntry> Children { get; set; } = [];

    public int FragmentRunCount
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Extents))
            {
                return 0;
            }

            return Extents.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        }
    }

    public SnapshotFileEntry Clone()
    {
        return new SnapshotFileEntry
        {
            Path = Path,
            Name = Name,
            PartitionName = PartitionName,
            Kind = Kind,
            IsDirectory = IsDirectory,
            Size = Size,
            Created = Created,
            Modified = Modified,
            Accessed = Accessed,
            Offset = Offset,
            Cluster = Cluster,
            IsDeleted = IsDeleted,
            Attributes = Attributes,
            FirstCluster = FirstCluster,
            Fragmentation = Fragmentation,
            Extents = Extents,
            MetadataStatus = MetadataStatus,
            MftRecordIndex = MftRecordIndex,
            MftSequenceNumber = MftSequenceNumber,
            ParentMftRecordIndex = ParentMftRecordIndex,
            AlternateDataStreamCount = AlternateDataStreamCount,
            Children = Children.Select(child => child.Clone()).ToList()
        };
    }
}

public sealed class PartitionModel
{
    public PartitionModel(Volume volume, string status)
    {
        FatxVolume = volume;
        Status = status;
    }

    public PartitionModel(XboxNtfsVolume volume, string status)
    {
        NtfsVolume = volume;
        Status = status;
    }

    public PartitionModel(PlayStationVolume volume, string status)
    {
        PlayStationVolume = volume;
        Status = status;
    }

    public PartitionModel(GenericFileSystemVolume volume, string status)
    {
        GenericVolume = volume;
        Status = status;
    }

    public PartitionModel(SnapshotPartition partition, string status)
    {
        SnapshotPartition = partition;
        Status = status;
    }

    public Volume? FatxVolume { get; }

    public XboxNtfsVolume? NtfsVolume { get; }

    public PlayStationVolume? PlayStationVolume { get; }

    public GenericFileSystemVolume? GenericVolume { get; }

    public SnapshotPartition? SnapshotPartition { get; }

    public bool IsMounted => FatxVolume?.Mounted == true || NtfsVolume != null || PlayStationVolume?.IsLoaded == true || GenericVolume != null || SnapshotPartition != null;

    public string Name => FatxVolume?.Name ?? NtfsVolume?.Name ?? PlayStationVolume?.Name ?? GenericVolume?.Name ?? SnapshotPartition?.Name ?? "Partition";

    public string Status { get; }

    public string StatusDisplay
    {
        get
        {
            var value = Status
                .Replace("PlayStation 4 HDD (managed)", "PS4 managed", StringComparison.OrdinalIgnoreCase)
                .Replace("Original Xbox FATX", "Xbox FATX", StringComparison.OrdinalIgnoreCase)
                .Replace("Xbox 360 FATX", "360 FATX", StringComparison.OrdinalIgnoreCase);
            return value.Length <= 44 ? value : value[..41] + "...";
        }
    }

    public string OffsetText => $"0x{Offset:X}";

    public string LengthText => $"0x{Length:X}";

    public long Offset => FatxVolume?.Offset ?? NtfsVolume?.Offset ?? PlayStationVolume?.Offset ?? GenericVolume?.Offset ?? SnapshotPartition?.Offset ?? 0;

    public long Length => FatxVolume?.Length ?? NtfsVolume?.Length ?? PlayStationVolume?.Length ?? GenericVolume?.Length ?? SnapshotPartition?.Length ?? 0;

    public string FamilyText => FatxVolume != null
        ? (FatxVolume.Platform == Platform.Xbox ? "Original Xbox FATX" : "Xbox 360 FATX")
        : NtfsVolume?.FamilyText ?? PlayStationVolume?.FamilyText ?? GenericVolume?.FamilyText ?? SnapshotPartition?.Family ?? "Unknown";

    public long UsedSpace => FatxVolume?.GetUsedSpace() ?? NtfsVolume?.UsedSpace ?? PlayStationVolume?.UsedSpace ?? GenericVolume?.UsedSpace ?? SnapshotPartition?.UsedSpace ?? 0;

    public long FreeSpace => FatxVolume?.GetFreeSpace() ?? NtfsVolume?.FreeSpace ?? PlayStationVolume?.FreeSpace ?? GenericVolume?.FreeSpace ?? SnapshotPartition?.FreeSpace ?? 0;

    public long TotalSpace => FatxVolume?.GetTotalSpace() ?? NtfsVolume?.TotalSpace ?? PlayStationVolume?.TotalSpace ?? GenericVolume?.TotalSpace ?? SnapshotPartition?.TotalSpace ?? 0;
}

internal sealed record MetadataScanResult(List<FileRow> Rows, List<DirectoryEntry> Entries);

internal sealed class PositionedClusterDataReader : IClusterDataReader
{
    private readonly string _imagePath;
    private readonly Volume _volume;
    private readonly FileStream _stream;

    public PositionedClusterDataReader(string imagePath, Volume volume)
    {
        _imagePath = imagePath;
        _volume = volume;
        _stream = new FileStream(
            _imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            options: FileOptions.RandomAccess);
    }

    public byte[] ReadCluster(uint cluster)
    {
        var buffer = new byte[_volume.BytesPerCluster];
        _stream.Position = _volume.ClusterToPhysicalOffset(cluster);

        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = _stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException($"Unexpected end of image while reading cluster {cluster}.");
            }

            offset += read;
        }

        return buffer;
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}

public sealed class DirectoryNode
{
    private DirectoryNode(
        string name,
        DirectoryEntry? entry,
        DirectoryNode? parent = null,
        IReadOnlyList<DirectoryEntry>? browseEntries = null,
        XboxFileEntry? ntfsEntry = null,
        PlayStationFileEntry? playStationEntry = null,
        GenericFileSystemEntry? genericEntry = null,
        SnapshotFileEntry? snapshotEntry = null,
        bool isRoot = false,
        bool isRecoveredClusterGroup = false)
    {
        Name = name;
        Entry = entry;
        NtfsEntry = ntfsEntry;
        PlayStationEntry = playStationEntry;
        GenericEntry = genericEntry;
        SnapshotEntry = snapshotEntry;
        Parent = parent;
        BrowseEntries = browseEntries;
        IsRoot = isRoot;
        IsRecoveredClusterGroup = isRecoveredClusterGroup;
    }

    public string Name { get; }

    public DirectoryEntry? Entry { get; }

    public XboxFileEntry? NtfsEntry { get; }

    public PlayStationFileEntry? PlayStationEntry { get; }

    public GenericFileSystemEntry? GenericEntry { get; }

    public SnapshotFileEntry? SnapshotEntry { get; }

    public object? NavigationEntry => Entry ?? (object?)NtfsEntry ?? PlayStationEntry ?? (object?)GenericEntry ?? SnapshotEntry;

    public DirectoryNode? Parent { get; }

    public IReadOnlyList<DirectoryEntry>? BrowseEntries { get; }

    public bool IsRoot { get; }

    public bool IsRecoveredClusterGroup { get; }

    public ObservableCollection<DirectoryNode> Children { get; } = new();

    public int FolderCount { get; private set; }

    public int FileCount { get; private set; }

    public string SummaryText => $"{FolderCount} folders, {FileCount} files";

    public string Glyph => IsRecoveredClusterGroup ? "\uE7C3" : "\uE8B7";

    public Brush GlyphColor => IsRecoveredClusterGroup
        ? new SolidColorBrush(Color.FromRgb(225, 182, 68))
        : new SolidColorBrush(Color.FromRgb(115, 183, 255));

    public static DirectoryNode CreateRoot(Volume volume)
    {
        var root = new DirectoryNode("Root", null, isRoot: true);
        root.FolderCount = volume.GetRoot().Count(entry => entry.IsDirectory());
        root.FileCount = volume.GetRoot().Count(entry => !entry.IsDirectory());
        AddChildren(root, volume.GetRoot());
        return root;
    }

    public static DirectoryNode CreateRoot(XboxNtfsVolume volume)
    {
        var rootEntries = volume.GetRoot();
        var root = new DirectoryNode("Root", null, isRoot: true);
        root.FolderCount = rootEntries.Count(entry => entry.IsDirectory);
        root.FileCount = rootEntries.Count(entry => !entry.IsDirectory);
        AddChildren(root, rootEntries);
        return root;
    }

    public static DirectoryNode CreateRoot(PlayStationVolume volume)
    {
        var rootEntries = volume.GetRoot();
        var root = new DirectoryNode("Root", null, isRoot: true);
        root.FolderCount = rootEntries.Count(entry => entry.IsDirectory);
        root.FileCount = rootEntries.Count(entry => !entry.IsDirectory);
        AddChildren(root, rootEntries);
        return root;
    }

    public static DirectoryNode CreateRoot(GenericFileSystemVolume volume)
    {
        var rootEntries = volume.GetRoot();
        var root = new DirectoryNode("Root", null, isRoot: true);
        root.FolderCount = rootEntries.Count(entry => entry.IsDirectory);
        root.FileCount = rootEntries.Count(entry => !entry.IsDirectory);
        AddChildren(root, rootEntries);
        return root;
    }

    public static DirectoryNode CreateRoot(SnapshotPartition partition)
    {
        var rootEntries = partition.GetRoot();
        var root = new DirectoryNode("Root", null, isRoot: true);
        root.FolderCount = rootEntries.Count(entry => entry.IsDirectory);
        root.FileCount = rootEntries.Count(entry => !entry.IsDirectory);
        AddChildren(root, rootEntries);
        return root;
    }

    public static DirectoryNode CreateRecoveryCluster(uint cluster, IReadOnlyList<DirectoryEntry> entries, DirectoryNode parent)
    {
        var node = new DirectoryNode($"Cluster {cluster}", null, parent, entries, isRecoveredClusterGroup: true);
        node.FolderCount = entries.Count(entry => entry.IsDirectory());
        node.FileCount = entries.Count(entry => !entry.IsDirectory());
        AddChildren(node, entries, includeDeletedChildren: true);
        return node;
    }

    private static void AddChildren(DirectoryNode parent, IEnumerable<DirectoryEntry> entries, bool includeDeletedChildren = false)
    {
        foreach (var entry in entries.Where(entry => entry.IsDirectory()))
        {
            var node = new DirectoryNode(entry.FileName, entry, parent);
            node.FolderCount = entry.Children.Count(child => child.IsDirectory());
            node.FileCount = entry.Children.Count(child => !child.IsDirectory());
            parent.Children.Add(node);
            if (includeDeletedChildren || !entry.IsDeleted())
            {
                AddChildren(node, entry.Children, includeDeletedChildren);
            }
        }
    }

    private static void AddChildren(DirectoryNode parent, IEnumerable<XboxFileEntry> entries)
    {
        foreach (var entry in entries.Where(entry => entry.IsDirectory))
        {
            var node = new DirectoryNode(entry.Name, null, parent, ntfsEntry: entry);
            node.FolderCount = entry.FolderCount;
            node.FileCount = entry.FileCount;
            parent.Children.Add(node);
            AddChildren(node, entry.Volume.GetChildren(entry));
        }
    }

    private static void AddChildren(DirectoryNode parent, IEnumerable<PlayStationFileEntry> entries)
    {
        foreach (var entry in entries.Where(entry => entry.IsDirectory))
        {
            var node = new DirectoryNode(entry.Name, null, parent, playStationEntry: entry);
            node.FolderCount = entry.FolderCount;
            node.FileCount = entry.FileCount;
            parent.Children.Add(node);
            AddChildren(node, entry.Volume.GetChildren(entry));
        }
    }

    private static void AddChildren(DirectoryNode parent, IEnumerable<GenericFileSystemEntry> entries)
    {
        foreach (var entry in entries.Where(entry => entry.IsDirectory))
        {
            var node = new DirectoryNode(entry.Name, null, parent, genericEntry: entry);
            node.FolderCount = entry.FolderCount;
            node.FileCount = entry.FileCount;
            parent.Children.Add(node);
            AddChildren(node, entry.Volume.GetChildren(entry));
        }
    }

    private static void AddChildren(DirectoryNode parent, IEnumerable<SnapshotFileEntry> entries)
    {
        foreach (var entry in entries.Where(entry => entry.IsDirectory))
        {
            var node = new DirectoryNode(entry.Name, null, parent, snapshotEntry: entry);
            node.FolderCount = entry.Children.Count(child => child.IsDirectory);
            node.FileCount = entry.Children.Count(child => !child.IsDirectory);
            parent.Children.Add(node);
            AddChildren(node, entry.Children);
        }
    }
}

public sealed class FileRow
{
    private bool _fatxActiveClusterChainLoaded;
    private IReadOnlyList<uint> _fatxActiveClusterChain = [];

    public FileRow(DirectoryEntry entry, Volume volume, string source, DatabaseFile? recoveryFile = null)
    {
        Entry = entry;
        Volume = volume;
        Source = source;
        RecoveryFile = recoveryFile;
    }

    public FileRow(XboxFileEntry entry, string source)
    {
        NtfsEntry = entry;
        Source = source;
    }

    public FileRow(PlayStationFileEntry entry, string source)
    {
        PlayStationEntry = entry;
        Source = source;
    }

    public FileRow(GenericFileSystemEntry entry, string source)
    {
        GenericEntry = entry;
        Source = source;
    }

    public FileRow(SnapshotFileEntry entry, string source)
    {
        SnapshotEntry = entry;
        Source = source;
    }

    public FileRow(Ps3DirectoryEntry entry, string partitionName)
    {
        Ps3Entry = entry;
        Source = "Metadata";
        Ps3PartitionName = partitionName;
    }

    public FileRow(PlayStationMetadataEntry entry, string partitionName, PlayStationVolume? volume = null)
    {
        PsMetadataEntry = entry;
        PsMetadataVolume = volume;
        Source = "Metadata";
        Ps3PartitionName = partitionName;
    }

    public DirectoryEntry? Entry { get; }

    public XboxFileEntry? NtfsEntry { get; }

    public PlayStationFileEntry? PlayStationEntry { get; }

    public GenericFileSystemEntry? GenericEntry { get; }

    public SnapshotFileEntry? SnapshotEntry { get; }

    public Ps3DirectoryEntry? Ps3Entry { get; }

    public PlayStationMetadataEntry? PsMetadataEntry { get; }

    public PlayStationVolume? PsMetadataVolume { get; }

    public string? Ps3PartitionName { get; }

    public object? NavigationEntry => Entry ?? (object?)NtfsEntry ?? PlayStationEntry ?? (object?)GenericEntry ?? SnapshotEntry;

    public Volume? Volume { get; }

    public string Source { get; }

    public DatabaseFile? RecoveryFile { get; }

    public string Identity => Entry != null && Volume != null
        ? $"fatx:{RuntimeHelpers.GetHashCode(Volume)}:{Entry.Offset:X}:{Name}"
        : NtfsEntry != null
            ? $"ntfs:{RuntimeHelpers.GetHashCode(NtfsEntry.Volume)}:{NtfsEntry.Path}"
                : PlayStationEntry != null
                    ? $"ps:{RuntimeHelpers.GetHashCode(PlayStationEntry.Volume)}:{PlayStationEntry.Path}"
                    : GenericEntry != null
                        ? $"generic:{RuntimeHelpers.GetHashCode(GenericEntry.Volume)}:{GenericEntry.Path}:{GenericEntry.IsDeleted}"
                        : SnapshotEntry != null
                            ? $"snapshot:{SnapshotEntry.Path}:{SnapshotEntry.Name}:{SnapshotEntry.Offset:X}"
                            : Ps3Entry != null
                                ? $"ps3dirent:{Ps3PartitionName}:{Ps3Entry.Offset:X}:{Ps3Entry.Name}"
                                : PsMetadataEntry != null
                                    ? $"psdirent:{Ps3PartitionName}:{PsMetadataEntry.Offset:X}:{PsMetadataEntry.Name}:{PsMetadataEntry.IsDeleted}"
                                    : Name;

    public string Name => Entry?.FileName ?? NtfsEntry?.Name ?? PlayStationEntry?.Name ?? GenericEntry?.Name ?? SnapshotEntry?.Name ?? Ps3Entry?.Name ?? PsMetadataEntry?.Name ?? string.Empty;

    public string Kind => PsMetadataEntry?.Kind ?? Ps3Entry?.Kind ?? GenericEntry?.Kind ?? (IsFolder ? "Folder" : "File");

    public int KindSort => IsFolder ? 0 : 1;

    public bool IsFolder => Entry?.IsDirectory() ?? NtfsEntry?.IsDirectory ?? PlayStationEntry?.IsDirectory ?? GenericEntry?.IsDirectory ?? SnapshotEntry?.IsDirectory ?? Ps3Entry?.Kind.Equals("Directory", StringComparison.OrdinalIgnoreCase) ?? PsMetadataEntry?.Kind.Equals("Directory", StringComparison.OrdinalIgnoreCase) ?? false;

    public string KindGlyph => IsFolder ? "\uE8B7" : "\uE8A5";

    public Brush KindColor => IsFolder
        ? new SolidColorBrush(Color.FromRgb(115, 183, 255))
        : new SolidColorBrush(Color.FromRgb(202, 214, 232));

    public string SizeText => IsFolder ? string.Empty : MainWindowFormat.Bytes(SizeBytes);

    public long SizeBytes => IsFolder ? -1 : Entry?.FileSize ?? NtfsEntry?.Length ?? PlayStationEntry?.Length ?? GenericEntry?.Length ?? SnapshotEntry?.Size ?? PsMetadataEntry?.Size ?? 0;

    public DateTime Created => Entry?.CreationTime.AsDateTime() ?? NtfsEntry?.Created ?? PlayStationEntry?.Created ?? GenericEntry?.Created ?? SnapshotEntry?.Created ?? DateTime.MinValue;

    public string CreatedText => Created.ToString("yyyy-MM-dd HH:mm:ss");

    public DateTime Modified => Entry?.LastWriteTime.AsDateTime() ?? NtfsEntry?.Modified ?? PlayStationEntry?.Modified ?? GenericEntry?.Modified ?? SnapshotEntry?.Modified ?? DateTime.MinValue;

    public string ModifiedText => Modified.ToString("yyyy-MM-dd HH:mm:ss");

    public DateTime Accessed => Entry?.LastAccessTime.AsDateTime() ?? NtfsEntry?.Accessed ?? PlayStationEntry?.Accessed ?? GenericEntry?.Accessed ?? SnapshotEntry?.Accessed ?? DateTime.MinValue;

    public string AccessedText => Accessed.ToString("yyyy-MM-dd HH:mm:ss");

    public string OffsetText => $"0x{Offset:X}";

    public long Offset => Entry?.Offset ?? NtfsEntry?.Offset ?? PlayStationEntry?.Offset ?? GenericEntry?.Offset ?? SnapshotEntry?.Offset ?? Ps3Entry?.Offset ?? PsMetadataEntry?.Offset ?? 0;

    public string ClusterText => ClusterNumber == 0 && (NtfsEntry != null || PlayStationEntry != null || GenericEntry != null || SnapshotEntry != null) ? string.Empty : ClusterNumber.ToString();

    public long ClusterNumber => Entry?.Cluster ?? NtfsEntry?.Cluster ?? GenericEntry?.Cluster ?? SnapshotEntry?.Cluster ?? 0;

    public bool IsDeleted => Entry?.IsDeleted() ?? NtfsEntry?.IsDeleted ?? GenericEntry?.IsDeleted ?? SnapshotEntry?.IsDeleted ?? PsMetadataEntry?.IsDeleted ?? false;

    public bool HasRecoveryStatus => RecoveryFile != null && Source.Equals("Recovered", StringComparison.OrdinalIgnoreCase);

    public bool HasFragmentationStatus => !IsFolder && !string.IsNullOrWhiteSpace(EffectiveFragmentationText);

    public bool HasRowStatus => HasRecoveryStatus || HasFragmentationStatus || IsDeleted;

    public int RecoveryScore => RecoveryFile?.GetRanking() ?? -1;

    public string RecoveryStatusName => RecoveryScore switch
    {
        0 => "Active filesystem entry",
        1 => "Perfect recovered entry",
        2 => "Likely intact with collisions",
        3 => "Partially overwritten",
        4 => "Fully overwritten",
        _ => "Recovered entry"
    };

    public string RecoveryStatusText => RecoveryFile?.GetRankingReason() ?? string.Empty;

    public string RecoveryStatusTooltip => string.IsNullOrWhiteSpace(RecoveryStatusText)
        ? FragmentationTooltip
        : $"{FragmentationTooltip}\n{RecoveryStatusText}";

    public Brush RecoveryStatusBrush => RecoveryScore switch
    {
        1 => new SolidColorBrush(Color.FromArgb(88, 49, 118, 64)),
        2 => new SolidColorBrush(Color.FromArgb(78, 135, 106, 42)),
        3 => new SolidColorBrush(Color.FromArgb(82, 155, 91, 46)),
        4 => new SolidColorBrush(Color.FromArgb(88, 139, 55, 62)),
        0 => new SolidColorBrush(Color.FromArgb(58, 49, 118, 64)),
        _ => new SolidColorBrush(Color.FromArgb(56, 77, 88, 109))
    };

    public Brush RowStatusBrush => HasRecoveryStatus
        ? RecoveryStatusBrush
        : GetFragmentationStatusBrush(EffectiveFragmentationText, IsDeleted);

    public string RowStatusTooltip => HasRecoveryStatus
        ? RecoveryStatusTooltip
        : BuildFragmentationStatusTooltip();

    public IReadOnlyList<uint> ClusterChain => HasRecoveryStatus
        ? RecoveryFile?.ClusterChain ?? []
        : FatxActiveClusterChain;

    public IReadOnlyList<uint> Collisions => RecoveryFile?.GetCollisions() ?? [];

    public int ClusterCount => ClusterChain.Count;

    public string ClusterCountText => HasRecoveryStatus ? ClusterCount.ToString("N0") : string.Empty;

    public int FragmentRunCount => HasRecoveryStatus
        ? ClusterChainMetrics.CountRuns(ClusterChain)
        : Entry != null
            ? ClusterChainMetrics.CountRuns(FatxActiveClusterChain)
            : NtfsEntry?.Extents.Count ?? PlayStationEntry?.DataRunCount ?? PsMetadataEntry?.DataRunCount ?? GenericEntry?.Extents.Count ?? SnapshotEntry?.FragmentRunCount ?? 0;

    public int FragmentationSort => HasRecoveryStatus
        ? RecoveryScore * 100000 + FragmentRunCount
        : Entry == null && NtfsEntry == null && PlayStationEntry == null && GenericEntry == null && SnapshotEntry == null && PsMetadataEntry == null ? int.MaxValue : FragmentRunCount;

    public string FragmentRunText => HasRecoveryStatus
        ? $"{FragmentRunCount:N0} {(FragmentRunCount == 1 ? "run" : "runs")}"
        : Entry != null && !IsFolder || NtfsEntry is { IsDirectory: false } || PlayStationEntry is { IsDirectory: false } || GenericEntry is { IsDirectory: false } || SnapshotEntry is { IsDirectory: false } || PsMetadataEntry is { Kind: not "Directory" }
            ? $"{FragmentRunCount:N0} {(FragmentRunCount == 1 ? "run" : "runs")}"
            : string.Empty;

    public int LargestRunLength => ClusterChainMetrics.GetLargestRunLength(ClusterChain);

    public string LargestRunText => HasRecoveryStatus ? $"{LargestRunLength:N0} clusters" : string.Empty;

    public int CollisionCount => Collisions.Count;

    public string CollisionText => HasRecoveryStatus ? CollisionCount.ToString("N0") : string.Empty;

    public string CollisionPercentText => HasRecoveryStatus && ClusterCount > 0
        ? $"{CollisionCount * 100.0 / ClusterCount:0.0}%"
        : string.Empty;

    public string ClusterRangesText => HasRecoveryStatus
        ? ClusterChainMetrics.FormatRanges(ClusterChain, maxRanges: 8)
        : Entry != null
            ? ClusterChainMetrics.FormatRanges(FatxActiveClusterChain, maxRanges: 8)
        : string.Empty;

    public string FragmentationText
    {
        get
        {
            if (!HasRecoveryStatus)
            {
                return string.Empty;
            }

            if (RecoveryScore == 4)
            {
                return "Fully overwritten";
            }

            if (CollisionCount > 0)
            {
                return FragmentRunCount <= 1
                    ? "Contiguous, has collisions"
                    : $"Fragmented, {CollisionCount:N0} collisions";
            }

            return FragmentRunCount <= 1
                ? "Contiguous, no collisions"
                : $"Fragmented into {FragmentRunCount:N0} runs";
        }
    }

    public string FragmentationTooltip =>
        $"{RecoveryStatusName}\n" +
        $"Clusters: {ClusterCount:N0}\n" +
        $"Fragment runs: {FragmentRunCount:N0}\n" +
        $"Largest run: {LargestRunLength:N0} clusters\n" +
        $"Collisions: {CollisionCount:N0} ({CollisionPercentText})\n" +
        $"Ranges: {ClusterRangesText}";

    public string DirectorySummary
    {
        get
        {
            if (!IsFolder)
            {
                return string.Empty;
            }

            if (Entry != null)
            {
                var folderCount = Entry.Children.Count(child => child.IsDirectory());
                var fileCount = Entry.Children.Count(child => !child.IsDirectory());
                return $"{folderCount} folders, {fileCount} files";
            }

            if (NtfsEntry != null)
            {
                return $"{NtfsEntry.FolderCount} folders, {NtfsEntry.FileCount} files";
            }

            if (PlayStationEntry != null)
            {
                return $"{PlayStationEntry.FolderCount} folders, {PlayStationEntry.FileCount} files";
            }

            if (GenericEntry != null)
            {
                return $"{GenericEntry.FolderCount} folders, {GenericEntry.FileCount} files";
            }

            if (SnapshotEntry != null)
            {
                return $"{SnapshotEntry.Children.Count(child => child.IsDirectory)} folders, {SnapshotEntry.Children.Count(child => !child.IsDirectory)} files";
            }

            return string.Empty;
        }
    }

    public string NtfsFragmentationText => NtfsEntry?.FragmentationStatus ?? string.Empty;

    public string FatxFragmentationText
    {
        get
        {
            if (Entry == null || IsFolder || Entry.IsDeleted())
            {
                return string.Empty;
            }

            if (SizeBytes == 0)
            {
                return "Empty";
            }

            if (FatxActiveClusterChain.Count == 0)
            {
                return "Unknown";
            }

            var runs = ClusterChainMetrics.CountRuns(FatxActiveClusterChain);
            return runs <= 1 ? "Contiguous" : $"Fragmented into {runs:N0} runs";
        }
    }

    public string PlayStationFragmentationText => PlayStationEntry?.RecoveryDisplayStatus ?? string.Empty;

    public string PlayStationMetadataFragmentationText => PsMetadataEntry?.FragmentationStatus ?? string.Empty;

    public string GenericFragmentationText => GenericEntry?.FragmentationStatus ?? string.Empty;

    public string SnapshotFragmentationText => SnapshotEntry?.Fragmentation ?? string.Empty;

    public string EffectiveFragmentationText => HasRecoveryStatus
        ? FragmentationText
        : FatxFragmentationText.Length > 0
            ? FatxFragmentationText
            : NtfsFragmentationText.Length > 0
                ? NtfsFragmentationText
                : PlayStationFragmentationText.Length > 0
                    ? PlayStationFragmentationText
                    : PlayStationMetadataFragmentationText.Length > 0
                        ? PlayStationMetadataFragmentationText
                    : GenericFragmentationText.Length > 0
                        ? GenericFragmentationText
                        : SnapshotFragmentationText;

    public string DisplayStatusText => Source.Equals("Metadata", StringComparison.OrdinalIgnoreCase)
        ? NtfsEntry?.MetadataStatus
          ?? GenericEntry?.MetadataStatus
          ?? SnapshotEntry?.MetadataStatus
          ?? PsMetadataEntry?.MetadataStatus
          ?? (Ps3Entry != null ? "PS3 directory entry" : EffectiveFragmentationText)
        : EffectiveFragmentationText;

    public int DisplayStatusSort => Source.Equals("Metadata", StringComparison.OrdinalIgnoreCase)
        ? IsDeleted ? 2 : NtfsEntry?.IsNtfsMetadata == true ? 1 : 0
        : FragmentationSort;

    private string BuildFragmentationStatusTooltip()
    {
        var builder = new StringBuilder();
        if (IsDeleted)
        {
            builder.AppendLine("Deleted filesystem entry");
        }

        if (!string.IsNullOrWhiteSpace(EffectiveFragmentationText))
        {
            builder.AppendLine(EffectiveFragmentationText);
        }

        if (FragmentRunCount > 0)
        {
            builder.AppendLine($"Fragment runs: {FragmentRunCount:N0}");
        }

        var extents = Entry != null
            ? ClusterRangesText
            : NtfsEntry?.ExtentSummary ?? PlayStationEntry?.ExtentSummary ?? BuildPlayStationMetadataExtentSummary() ?? GenericEntry?.ExtentSummary ?? SnapshotEntry?.Extents ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(extents))
        {
            builder.AppendLine(extents);
        }

        return builder.ToString().Trim();
    }

    public string? BuildPlayStationMetadataExtentSummary()
    {
        if (PsMetadataEntry == null)
        {
            return null;
        }

        return string.Join(Environment.NewLine, new[]
        {
            PsMetadataEntry.MetadataStatus,
            PsMetadataEntry.DataOffsetCount > 0 ? $"Data offsets: {PsMetadataEntry.DataOffsetCount:N0}" : string.Empty,
            PsMetadataEntry.DataRunCount > 0 ? $"Estimated runs: {PsMetadataEntry.DataRunCount:N0}" : string.Empty,
            PsMetadataEntry.LargestRunBytes > 0 ? $"Largest run: {PsMetadataEntry.LargestRunBytes:N0} bytes" : string.Empty,
            !string.IsNullOrWhiteSpace(PsMetadataEntry.DataRanges) ? $"Ranges: {PsMetadataEntry.DataRanges}" : string.Empty,
            !string.IsNullOrWhiteSpace(PsMetadataEntry.DataOffsets) ? $"Offsets: {PsMetadataEntry.DataOffsets}" : string.Empty
        }.Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static Brush GetFragmentationStatusBrush(string status, bool isDeleted)
    {
        if (isDeleted)
        {
            return new SolidColorBrush(Color.FromArgb(88, 139, 55, 62));
        }

        if (string.IsNullOrWhiteSpace(status))
        {
            return new SolidColorBrush(Color.FromArgb(56, 77, 88, 109));
        }

        if (status.Contains("unrecoverable", StringComparison.OrdinalIgnoreCase)
            || status.Contains("fully overwritten", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Color.FromArgb(88, 139, 55, 62));
        }

        if (status.Contains("fragmented", StringComparison.OrdinalIgnoreCase)
            || status.Contains("collision", StringComparison.OrdinalIgnoreCase)
            || status.Contains("partial", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Color.FromArgb(82, 155, 91, 46));
        }

        if (status.Contains("contiguous", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Color.FromArgb(88, 49, 118, 64));
        }

        if (status.Contains("resident", StringComparison.OrdinalIgnoreCase)
            || status.Contains("sparse", StringComparison.OrdinalIgnoreCase)
            || status.Contains("empty", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Color.FromArgb(62, 63, 91, 126));
        }

        return new SolidColorBrush(Color.FromArgb(56, 77, 88, 109));
    }

    private IReadOnlyList<uint> FatxActiveClusterChain
    {
        get
        {
            if (_fatxActiveClusterChainLoaded)
            {
                return _fatxActiveClusterChain;
            }

            _fatxActiveClusterChainLoaded = true;
            if (Entry == null || Volume == null || Entry.IsDirectory() || Entry.IsDeleted())
            {
                _fatxActiveClusterChain = [];
                return _fatxActiveClusterChain;
            }

            try
            {
                _fatxActiveClusterChain = Volume.GetClusterChain(Entry);
            }
            catch
            {
                _fatxActiveClusterChain = [];
            }

            return _fatxActiveClusterChain;
        }
    }
}

public sealed class RecoveryTreeNode
{
    private RecoveryTreeNode(string name, uint? cluster, DatabaseFile? file, IReadOnlyList<DatabaseFile> files)
    {
        Name = name;
        Cluster = cluster;
        File = file;
        Files = files;
    }

    public string Name { get; }

    public uint? Cluster { get; }

    public DatabaseFile? File { get; }

    public IReadOnlyList<DatabaseFile> Files { get; }

    public ObservableCollection<RecoveryTreeNode> Children { get; } = new();

    public string Glyph => File == null ? "\uE7C3" : "\uE8B7";

    public Brush GlyphColor => File == null
        ? new SolidColorBrush(Color.FromRgb(74, 158, 255))
        : new SolidColorBrush(Color.FromRgb(115, 183, 255));

    public static RecoveryTreeNode CreateCluster(uint cluster, IReadOnlyList<DatabaseFile> files)
    {
        return new RecoveryTreeNode($"Cluster {cluster}", cluster, null, files);
    }

    public static RecoveryTreeNode CreateFile(DatabaseFile file)
    {
        return new RecoveryTreeNode(file.FileName, null, file, file.Children);
    }
}

public sealed class RecoveryFileRow
{
    public RecoveryFileRow(DatabaseFile file, Volume volume)
    {
        File = file;
        Volume = volume;
    }

    public DatabaseFile File { get; }

    public Volume Volume { get; }

    public string Name => File.FileName;

    public string Kind => File.IsDirectory() ? "Folder" : "File";

    public int KindSort => File.IsDirectory() ? 0 : 1;

    public bool IsFolder => File.IsDirectory();

    public string KindGlyph => IsFolder ? "\uE8B7" : "\uE8A5";

    public Brush KindColor => IsFolder
        ? new SolidColorBrush(Color.FromRgb(115, 183, 255))
        : new SolidColorBrush(Color.FromRgb(202, 214, 232));

    public string SizeText => File.IsDirectory() ? string.Empty : MainWindowFormat.Bytes(File.FileSize);

    public long SizeBytes => File.IsDirectory() ? -1 : File.FileSize;

    public DateTime Created => File.CreationTime.AsDateTime();

    public string CreatedText => Created.ToString("yyyy-MM-dd HH:mm:ss");

    public DateTime Modified => File.LastWriteTime.AsDateTime();

    public string ModifiedText => Modified.ToString("yyyy-MM-dd HH:mm:ss");

    public DateTime Accessed => File.LastAccessTime.AsDateTime();

    public string AccessedText => Accessed.ToString("yyyy-MM-dd HH:mm:ss");

    public string OffsetText => $"0x{File.Offset:X}";

    public long Offset => File.Offset;

    public string ClusterText => File.Cluster.ToString();

    public uint ClusterNumber => File.Cluster;

    public int Score => File.GetRanking();

    public string ScoreText => (Score + 1).ToString();

    public string Status => File.GetRankingReason() ?? string.Empty;

    public IReadOnlyList<uint> ClusterChain => File.ClusterChain ?? [];

    public IReadOnlyList<uint> Collisions => File.GetCollisions() ?? [];

    public int ClusterCount => ClusterChain.Count;

    public string ClusterCountText => ClusterCount.ToString("N0");

    public int FragmentRunCount => ClusterChainMetrics.CountRuns(ClusterChain);

    public string FragmentRunText => $"{FragmentRunCount:N0} {(FragmentRunCount == 1 ? "run" : "runs")}";

    public int LargestRunLength => ClusterChainMetrics.GetLargestRunLength(ClusterChain);

    public string LargestRunText => $"{LargestRunLength:N0} clusters";

    public int CollisionCount => Collisions.Count;

    public string CollisionText => CollisionCount.ToString("N0");

    public string CollisionPercentText => ClusterCount > 0
        ? $"{CollisionCount * 100.0 / ClusterCount:0.0}%"
        : string.Empty;

    public string ClusterRangesText => ClusterChainMetrics.FormatRanges(ClusterChain, maxRanges: 8);

    public string DirectorySummary => File.IsDirectory()
        ? $"{File.Children.Count(child => child.IsDirectory())} folders, {File.Children.Count(child => !child.IsDirectory())} files"
        : string.Empty;
}

internal static class ClusterChainMetrics
{
    public static int CountRuns(IReadOnlyList<uint> clusters)
    {
        if (clusters.Count == 0)
        {
            return 0;
        }

        var runs = 1;
        for (var index = 1; index < clusters.Count; index++)
        {
            if (clusters[index] != clusters[index - 1] + 1)
            {
                runs++;
            }
        }

        return runs;
    }

    public static int GetLargestRunLength(IReadOnlyList<uint> clusters)
    {
        if (clusters.Count == 0)
        {
            return 0;
        }

        var current = 1;
        var largest = 1;
        for (var index = 1; index < clusters.Count; index++)
        {
            if (clusters[index] == clusters[index - 1] + 1)
            {
                current++;
            }
            else
            {
                largest = Math.Max(largest, current);
                current = 1;
            }
        }

        return Math.Max(largest, current);
    }

    public static string FormatRanges(IReadOnlyList<uint> clusters, int maxRanges)
    {
        if (clusters.Count == 0)
        {
            return string.Empty;
        }

        var ranges = new List<string>();
        var start = clusters[0];
        var previous = clusters[0];

        for (var index = 1; index < clusters.Count; index++)
        {
            var cluster = clusters[index];
            if (cluster == previous + 1)
            {
                previous = cluster;
                continue;
            }

            ranges.Add(FormatRange(start, previous));
            start = previous = cluster;
        }

        ranges.Add(FormatRange(start, previous));

        var visibleRanges = ranges.Take(maxRanges).ToList();
        var suffix = ranges.Count > maxRanges ? $" (+{ranges.Count - maxRanges:N0} more)" : string.Empty;
        return string.Join(", ", visibleRanges) + suffix;
    }

    private static string FormatRange(uint start, uint end)
    {
        return start == end ? start.ToString() : $"{start}-{end}";
    }
}

public sealed class ClusterRow
{
    public ClusterRow(ClusterMapAddressSpace addressSpace, uint cluster, IReadOnlyList<ClusterOccupant> occupants)
    {
        AddressSpace = addressSpace;
        Cluster = cluster;
        Occupants = occupants;
    }

    public ClusterMapAddressSpace AddressSpace { get; }

    public uint Cluster { get; }

    public IReadOnlyList<ClusterOccupant> Occupants { get; }

    public string ClusterText => Cluster.ToString();

    public long Offset => AddressSpace.GetOffset(Cluster);

    public string OffsetText => $"0x{Offset:X}";

    public int OccupantCount => Occupants.Count;

    public string OccupantsText => OccupantCount == 0
        ? AddressSpace.RootCluster == Cluster ? "Root directory" : "Empty"
        : string.Join(", ", Occupants.Take(8).Select(file => file.Name)) +
          (Occupants.Count > 8 ? $" (+{Occupants.Count - 8} more)" : string.Empty);

    public string Status
    {
        get
        {
            if (AddressSpace.RootCluster == Cluster)
            {
                return "Root";
            }

            if (Occupants.Count > 1)
            {
                return Occupants.Any(file => !file.IsDeleted) ? "Active" : "Collision";
            }

            return Occupants.FirstOrDefault()?.IsDeleted == true ? "Recovered" : "Active";
        }
    }

    public int StatusSort => Status switch
    {
        "Root" => 0,
        "Active" => 1,
        "Recovered" => 2,
        "Collision" => 3,
        _ => 9
    };

    public Brush StatusBrush => Status switch
    {
        "Root" => new SolidColorBrush(Color.FromRgb(141, 96, 233)),
        "Active" => new SolidColorBrush(Color.FromRgb(65, 178, 67)),
        "Recovered" => new SolidColorBrush(Color.FromRgb(225, 182, 68)),
        "Collision" => new SolidColorBrush(Color.FromRgb(210, 92, 92)),
        _ => new SolidColorBrush(Color.FromRgb(76, 89, 109))
    };
}

public sealed class ClusterMapRow
{
    private readonly ClusterMapAddressSpace _addressSpace;
    private readonly uint _startCluster;
    private readonly int _columns;
    private readonly ClusterOccupancyMap _occupancy;
    private IReadOnlyList<ClusterMapCell>? _cells;

    public ClusterMapRow(
        ClusterMapAddressSpace addressSpace,
        uint startCluster,
        int columns,
        ClusterOccupancyMap occupancy)
    {
        _addressSpace = addressSpace;
        _startCluster = startCluster;
        _columns = columns;
        _occupancy = occupancy;
    }

    public int RowNumber => (int)((_startCluster - _addressSpace.FirstCluster) / _columns) + 1;

    public string OffsetText => $"0x{_addressSpace.GetOffset(_startCluster):X16}";

    public IReadOnlyList<ClusterMapCell> Cells => _cells ??= Enumerable.Range(0, _columns)
        .Select(index => ClusterMapCell.Create(_addressSpace, _startCluster + (uint)index, _occupancy))
        .ToList();
}

public sealed class ClusterMapCell
{
    private static readonly Brush EmptyBrush = new SolidColorBrush(Color.FromRgb(42, 48, 60));
    private static readonly Brush ActiveBrush = new SolidColorBrush(Color.FromRgb(65, 178, 67));
    private static readonly Brush RecoveredBrush = new SolidColorBrush(Color.FromRgb(225, 182, 68));
    private static readonly Brush CollisionBrush = new SolidColorBrush(Color.FromRgb(210, 92, 92));
    private static readonly Brush RootBrush = new SolidColorBrush(Color.FromRgb(141, 96, 233));
    private static readonly Brush OutOfRangeBrush = new SolidColorBrush(Color.FromRgb(18, 23, 35));

    private ClusterMapCell(ClusterMapAddressSpace addressSpace, uint cluster, IReadOnlyList<ClusterOccupant> occupants, bool isInRange)
    {
        AddressSpace = addressSpace;
        Cluster = cluster;
        Occupants = occupants;
        IsInRange = isInRange;
    }

    public ClusterMapAddressSpace AddressSpace { get; }

    public uint Cluster { get; }

    public IReadOnlyList<ClusterOccupant> Occupants { get; }

    public IReadOnlyList<DatabaseFile> RecoveryFiles => Occupants
        .Select(occupant => occupant.RecoveryFile)
        .Where(file => file != null)
        .Cast<DatabaseFile>()
        .ToList();

    public bool IsInRange { get; }

    public string Status
    {
        get
        {
            if (!IsInRange)
            {
                return "Out of range";
            }

            if (AddressSpace.RootCluster == Cluster)
            {
                return "Root";
            }

            if (Occupants.Count == 0)
            {
                return "Empty";
            }

            if (Occupants.Count > 1)
            {
                return Occupants.Any(file => !file.IsDeleted) ? "Active" : "Collision";
            }

            return Occupants[0].IsDeleted ? "Recovered" : "Active";
        }
    }

    public Brush StatusBrush => Status switch
    {
        "Root" => RootBrush,
        "Active" => ActiveBrush,
        "Recovered" => RecoveredBrush,
        "Collision" => CollisionBrush,
        "Out of range" => OutOfRangeBrush,
        _ => EmptyBrush
    };

    public string ToolTip
    {
        get
        {
            if (!IsInRange)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            builder.AppendLine($"{AddressSpace.UnitName} Information");
            builder.AppendLine($"{AddressSpace.UnitName} Index: {Cluster}");
            builder.AppendLine($"{AddressSpace.UnitName} Address: 0x{AddressSpace.GetOffset(Cluster):X}");

            if (AddressSpace.RootCluster == Cluster)
            {
                builder.AppendLine();
                builder.AppendLine("Type: Root Directory");
            }

            foreach (var occupant in Occupants.Take(8))
            {
                builder.AppendLine();
                builder.AppendLine($"Occupant: {occupant.Name}");
                builder.AppendLine($"Type: {occupant.Kind}");
                builder.AppendLine($"File Size: 0x{occupant.Size:X8}");
                if (!string.IsNullOrWhiteSpace(occupant.Detail))
                {
                    builder.AppendLine($"Detail: {occupant.Detail}");
                }

                builder.AppendLine($"Deleted: {occupant.IsDeleted}");
            }

            if (Occupants.Count > 8)
            {
                builder.AppendLine();
                builder.AppendLine($"+{Occupants.Count - 8} more occupants");
            }

            return builder.ToString().TrimEnd();
        }
    }

    public static ClusterMapCell Create(
        ClusterMapAddressSpace addressSpace,
        uint cluster,
        ClusterOccupancyMap occupancy)
    {
        if (!addressSpace.Contains(cluster))
        {
            return new ClusterMapCell(addressSpace, cluster, Array.Empty<ClusterOccupant>(), isInRange: false);
        }

        var occupants = occupancy.GetOccupants(cluster);
        return new ClusterMapCell(addressSpace, cluster, occupants, isInRange: true);
    }
}

public sealed class ClusterMapAddressSpace
{
    public const long DefaultUnitSize = 4096;
    public const long PlayStationUnitSize = 0x1000;
    private readonly Func<uint, long> _offsetResolver;

    private ClusterMapAddressSpace(
        string name,
        string unitName,
        long unitSize,
        uint firstCluster,
        uint clusterCount,
        uint? rootCluster,
        Func<uint, long> offsetResolver)
    {
        Name = name;
        UnitName = unitName;
        UnitSize = unitSize;
        FirstCluster = firstCluster;
        ClusterCount = clusterCount;
        RootCluster = rootCluster;
        _offsetResolver = offsetResolver;
    }

    public string Name { get; }

    public string UnitName { get; }

    public long UnitSize { get; }

    public uint FirstCluster { get; }

    public uint ClusterCount { get; }

    public uint? RootCluster { get; }

    public long GetOffset(uint cluster) => _offsetResolver(cluster);

    public bool Contains(uint cluster)
    {
        if (cluster < FirstCluster)
        {
            return false;
        }

        return cluster - FirstCluster < ClusterCount;
    }

    public static ClusterMapAddressSpace CreateFatx(Volume volume)
    {
        var count = volume.MaxClusters <= 1 ? 0 : volume.MaxClusters - 1;
        return new ClusterMapAddressSpace(
            volume.Name,
            "Cluster",
            volume.BytesPerCluster,
            1,
            count,
            volume.RootDirFirstCluster,
            volume.ClusterToPhysicalOffset);
    }

    public static ClusterMapAddressSpace CreateNtfs(XboxNtfsVolume volume)
    {
        var clusterSize = Math.Max(1, volume.ClusterSize);
        return new ClusterMapAddressSpace(
            volume.Name,
            "Cluster",
            clusterSize,
            0,
            CountUnits(volume.Length, clusterSize),
            null,
            cluster => volume.Offset + cluster * clusterSize);
    }

    public static ClusterMapAddressSpace CreatePlayStation(PlayStationVolume volume)
    {
        return new ClusterMapAddressSpace(
            volume.Name,
            "Block",
            PlayStationUnitSize,
            0,
            CountUnits(volume.Length, PlayStationUnitSize),
            null,
            cluster => volume.Offset + cluster * PlayStationUnitSize);
    }

    public static ClusterMapAddressSpace CreateGeneric(GenericFileSystemVolume volume)
    {
        var unitSize = Math.Max(1, volume.ClusterSize);
        return new ClusterMapAddressSpace(
            volume.Name,
            "Cluster",
            unitSize,
            0,
            CountUnits(volume.Length, unitSize),
            null,
            cluster => volume.Offset + cluster * unitSize);
    }

    public static ClusterMapAddressSpace CreateSnapshot(SnapshotPartition partition)
    {
        return new ClusterMapAddressSpace(
            partition.Name,
            "Unit",
            DefaultUnitSize,
            0,
            CountUnits(partition.Length, DefaultUnitSize),
            null,
            cluster => partition.Offset + cluster * DefaultUnitSize);
    }

    private static uint CountUnits(long length, long unitSize)
    {
        if (length <= 0 || unitSize <= 0)
        {
            return 0;
        }

        var count = (length + unitSize - 1) / unitSize;
        return (uint)Math.Min(uint.MaxValue, count);
    }
}

public sealed record ClusterOccupant(
    string Identity,
    string Name,
    string Kind,
    long Size,
    bool IsDeleted,
    string Detail,
    DatabaseFile? RecoveryFile = null)
{
    public static ClusterOccupant FromFatx(DatabaseFile file)
    {
        var dirent = file.GetDirent();
        return new ClusterOccupant(
            $"fatx:{dirent.Offset:X}:{dirent.FileName}:{file.IsDeleted}",
            dirent.FileName,
            dirent.IsDirectory() ? "Dirent Stream" : "File Data",
            dirent.FileSize,
            file.IsDeleted,
            file.GetRankingReason(),
            file);
    }

    public static ClusterOccupant FromNtfs(XboxFileEntry entry)
    {
        return new ClusterOccupant(
            $"ntfs:{entry.MftRecordIndex}:{entry.SequenceNumber}:{entry.Path}:{entry.IsDeleted}",
            entry.Name,
            "NTFS file data",
            entry.Length,
            entry.IsDeleted,
            entry.FragmentationStatus);
    }

    public static ClusterOccupant FromNtfsAllocationBitmap(XboxNtfsVolume volume)
    {
        return new ClusterOccupant(
            $"ntfs-bitmap:{RuntimeHelpers.GetHashCode(volume)}",
            "$Bitmap allocated cluster",
            "NTFS allocation bitmap",
            volume.ClusterSize,
            false,
            "Allocated by NTFS $Bitmap; no specific file record is mapped in this cell.");
    }

    public static ClusterOccupant FromPlayStation(PlayStationFileEntry entry)
    {
        return new ClusterOccupant(
            $"ps:{entry.Path}:{entry.Offset:X}",
            entry.Name,
            "PlayStation file data",
            entry.Length,
            false,
            entry.RecoveryDisplayStatus);
    }

    public static ClusterOccupant FromGeneric(GenericFileSystemEntry entry)
    {
        return new ClusterOccupant(
            $"generic:{RuntimeHelpers.GetHashCode(entry.Volume)}:{entry.Path}:{entry.Offset:X}:{entry.IsDeleted}",
            entry.Name,
            $"{entry.Volume.FamilyText} file data",
            entry.Length,
            entry.IsDeleted,
            entry.FragmentationStatus);
    }

    public static ClusterOccupant FromSnapshot(SnapshotFileEntry entry)
    {
        return new ClusterOccupant(
            $"snapshot:{entry.PartitionName}:{entry.Path}:{entry.Name}:{entry.Offset:X}:{entry.IsDeleted}",
            entry.Name,
            entry.Kind,
            entry.Size,
            entry.IsDeleted,
            entry.Fragmentation);
    }
}

public sealed class ClusterOccupancyMap
{
    private const uint ExpandedRangeLimit = 8192;
    private const uint MaxRowsPerLargeRange = 512;
    private readonly Dictionary<uint, List<ClusterOccupant>> _exact = [];
    private readonly List<ClusterOccupantRange> _ranges = [];

    public IEnumerable<uint> DisplayClusters => _exact.Keys.Concat(_ranges.Select(range => range.Start));

    public int RangeCount => _ranges.Count;

    public void Add(uint cluster, ClusterOccupant occupant)
    {
        if (!_exact.TryGetValue(cluster, out var occupants))
        {
            occupants = [];
            _exact[cluster] = occupants;
        }

        AddUnique(occupants, occupant);
    }

    public void AddRange(uint start, uint end, ClusterOccupant occupant)
    {
        if (end < start)
        {
            return;
        }

        var count = (ulong)end - start + 1;
        if (count <= ExpandedRangeLimit)
        {
            for (var cluster = start; cluster <= end; cluster++)
            {
                Add(cluster, occupant);
                if (cluster == uint.MaxValue)
                {
                    break;
                }
            }

            return;
        }

        _ranges.Add(new ClusterOccupantRange(start, end, occupant));
    }

    public void AddRangeCompact(uint start, uint end, ClusterOccupant occupant)
    {
        if (end < start)
        {
            return;
        }

        _ranges.Add(new ClusterOccupantRange(start, end, occupant));
    }

    public IReadOnlyList<ClusterOccupant> GetOccupants(uint cluster)
    {
        var occupants = _exact.TryGetValue(cluster, out var exactOccupants)
            ? new List<ClusterOccupant>(exactOccupants)
            : [];

        foreach (var range in _ranges)
        {
            if (cluster >= range.Start && cluster <= range.End)
            {
                AddUnique(occupants, range.Occupant);
            }
        }

        return occupants;
    }

    public IReadOnlyList<uint> GetDisplayRowStartClusters(ClusterMapAddressSpace addressSpace, int columns)
    {
        if (columns <= 0 || addressSpace.ClusterCount == 0)
        {
            return [];
        }

        var rowIndexes = new SortedSet<uint>();
        if (addressSpace.RootCluster is { } rootCluster && addressSpace.Contains(rootCluster))
        {
            rowIndexes.Add(GetRowIndex(addressSpace, rootCluster, columns));
        }

        foreach (var cluster in _exact.Keys)
        {
            if (addressSpace.Contains(cluster))
            {
                rowIndexes.Add(GetRowIndex(addressSpace, cluster, columns));
            }
        }

        foreach (var range in _ranges)
        {
            var start = Math.Max(range.Start, addressSpace.FirstCluster);
            var lastCluster = (uint)Math.Min(
                uint.MaxValue,
                (ulong)addressSpace.FirstCluster + addressSpace.ClusterCount - 1);
            var end = Math.Min(range.End, lastCluster);
            if (end < start)
            {
                continue;
            }

            var startRow = GetRowIndex(addressSpace, start, columns);
            var endRow = GetRowIndex(addressSpace, end, columns);
            var rowCount = endRow - startRow + 1;
            if (rowCount <= MaxRowsPerLargeRange)
            {
                for (var row = startRow; row <= endRow; row++)
                {
                    rowIndexes.Add(row);
                    if (row == uint.MaxValue)
                    {
                        break;
                    }
                }

                continue;
            }

            var step = Math.Max(1, rowCount / MaxRowsPerLargeRange);
            for (var row = startRow; row <= endRow; row += step)
            {
                rowIndexes.Add(row);
                if (uint.MaxValue - row < step)
                {
                    break;
                }
            }

            rowIndexes.Add(endRow);
        }

        return rowIndexes
            .Select(row => (uint)Math.Min(uint.MaxValue, (ulong)addressSpace.FirstCluster + (ulong)row * (uint)columns))
            .ToList();
    }

    private static uint GetRowIndex(ClusterMapAddressSpace addressSpace, uint cluster, int columns)
    {
        return (cluster - addressSpace.FirstCluster) / (uint)columns;
    }

    private static void AddUnique(List<ClusterOccupant> occupants, ClusterOccupant occupant)
    {
        if (!occupants.Any(existing => existing.Identity == occupant.Identity))
        {
            occupants.Add(occupant);
        }
    }

    private sealed record ClusterOccupantRange(uint Start, uint End, ClusterOccupant Occupant);
}

public sealed class ScanProgressRow : INotifyPropertyChanged
{
    private double _value;
    private string _text = "0%";

    public ScanProgressRow(string title)
    {
        Title = title;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; }

    public double Value
    {
        get => _value;
        private set
        {
            if (Math.Abs(_value - value) < 0.001)
            {
                return;
            }

            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    public string Text
    {
        get => _text;
        private set
        {
            if (_text == value)
            {
                return;
            }

            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }

    public void Update(double value, string text)
    {
        Value = value;
        Text = text;
    }
}

internal sealed record ExportProgressSnapshot(
    long BytesWritten,
    long TotalBytes,
    int ItemsCompleted,
    int TotalItems,
    string CurrentName);

internal sealed class ExportProgressState
{
    private readonly IProgress<ExportProgressSnapshot> _progress;
    private readonly CancellationToken _cancellationToken;
    private long _bytesWritten;
    private int _itemsCompleted;
    private string _currentName = string.Empty;

    public ExportProgressState(long totalBytes, int totalItems, IProgress<ExportProgressSnapshot> progress, CancellationToken cancellationToken)
    {
        TotalBytes = totalBytes;
        TotalItems = totalItems;
        _progress = progress;
        _cancellationToken = cancellationToken;
    }

    public long TotalBytes { get; }

    public int TotalItems { get; }

    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    public int ItemsCompleted => Volatile.Read(ref _itemsCompleted);

    public CancellationToken CancellationToken => _cancellationToken;

    public void StartItem(string name)
    {
        ThrowIfCancellationRequested();
        _currentName = name;
        Report();
    }

    public void AddBytes(long bytes)
    {
        ThrowIfCancellationRequested();
        if (bytes > 0)
        {
            Interlocked.Add(ref _bytesWritten, bytes);
            Report();
        }
    }

    public void CompleteItem()
    {
        ThrowIfCancellationRequested();
        Interlocked.Increment(ref _itemsCompleted);
        Report();
    }

    public void ThrowIfCancellationRequested()
    {
        _cancellationToken.ThrowIfCancellationRequested();
    }

    public void ReportComplete()
    {
        if (TotalBytes > 0)
        {
            Interlocked.Exchange(ref _bytesWritten, Math.Max(BytesWritten, TotalBytes));
        }

        if (TotalItems > 0)
        {
            Interlocked.Exchange(ref _itemsCompleted, Math.Max(ItemsCompleted, TotalItems));
        }

        Report();
    }

    private void Report()
    {
        _progress.Report(new ExportProgressSnapshot(BytesWritten, TotalBytes, ItemsCompleted, TotalItems, _currentName));
    }
}

public sealed class CarvedFileRow
{
    public CarvedFileRow(FileSignature signature, Volume volume)
    {
        Signature = signature;
        Volume = volume;
    }

    public CarvedFileRow(CarvedFileSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public CarvedFileRow(GenericCarvedFile genericFile)
    {
        GenericFile = genericFile;
    }

    public FileSignature? Signature { get; }

    public Volume? Volume { get; }

    public CarvedFileSnapshot? Snapshot { get; }

    public GenericCarvedFile? GenericFile { get; }

    public string Name => Signature?.FileName ?? GenericFile?.Name ?? Snapshot?.Name ?? string.Empty;

    public string Kind => GenericFile?.Kind ?? Snapshot?.Kind ?? "File";

    public string SizeText => MainWindowFormat.Bytes(SizeBytes);

    public long SizeBytes => Signature?.FileSize ?? GenericFile?.Size ?? Snapshot?.Size ?? 0;

    public string OffsetText => $"0x{Offset:X}";

    public long Offset => Signature != null && Volume != null
        ? Volume.Offset + Volume.FileAreaByteOffset + Signature.Offset
        : GenericFile?.DisplayOffset ?? Snapshot?.Offset ?? 0;

    public string RelativeOffsetText => Signature != null
        ? $"0x{Signature.Offset:X}"
        : GenericFile != null
            ? $"0x{GenericFile.SourceOffset:X}"
            : Snapshot?.SourceOffset > 0 ? $"0x{Snapshot.SourceOffset:X}" : string.Empty;

    public string Source => GenericFile?.Source ?? Snapshot?.Source ?? string.Empty;

    public string Detail => GenericFile?.Detail ?? Snapshot?.Detail ?? string.Empty;

    public int FragmentRunCount => GenericFile?.FragmentRunCount
        ?? Snapshot?.FragmentRunCount
        ?? 0;

    public string FragmentRunText => FragmentRunCount <= 0
        ? string.Empty
        : $"{FragmentRunCount:N0} {(FragmentRunCount == 1 ? "run" : "runs")}";

    public string FragmentationStatus => GenericFile?.EffectiveFragmentationStatus
        ?? Snapshot?.Fragmentation
        ?? string.Empty;

    public string ExtentSummary => GenericFile?.EffectiveExtentSummary
        ?? Snapshot?.Extents
        ?? string.Empty;

    public bool HasFileData => Signature != null && Volume != null || GenericFile?.HasFileData == true;
}

public sealed record InspectorRow(string Label, string Value);

internal sealed class UiActionCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool> _canExecute;

    public UiActionCommand(Action execute, Func<bool> canExecute)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter)
    {
        return _canExecute();
    }

    public void Execute(object? parameter)
    {
        _execute();
    }
}

internal static class MainWindowFormat
{
    public static string Bytes(long value)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        int suffix = 0;
        while (size >= 1024 && suffix < suffixes.Length - 1)
        {
            size /= 1024;
            suffix++;
        }

        return $"{size:0.##} {suffixes[suffix]}";
    }
}
