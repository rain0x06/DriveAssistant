using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DriveAssistant.Avalonia;
using DriveAssistant.Avalonia.Core;

namespace DriveAssistant.Avalonia.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private DesktopDriveImage? _openImage;
    private PartitionViewModel? _selectedPartition;
    private DirectoryNodeViewModel? _selectedDirectory;
    private FileEntryViewModel? _selectedFile;
    private FileEntryViewModel? _selectedMetadataResult;
    private FileEntryViewModel? _selectedCarvedFile;
    private ClusterRowViewModel? _selectedClusterRow;
    private string _statusText = "Ready";
    private string _logText = string.Empty;
    private string _currentFileSystemTitle = "ORIGINAL FILESYSTEM";
    private string _currentDirectorySummary = "Open image to browse HDD tree.";
    private string _inspectorTitle = "No file selected";
    private string _inspectorSubtitle = "Open an image and select a file.";
    private string _searchText = string.Empty;
    private bool _isBusy;
    private string? _keyPath;
    private CancellationTokenSource? _operationCancellation;

    public ObservableCollection<PartitionViewModel> Partitions { get; } = [];

    public ObservableCollection<DirectoryNodeViewModel> DirectoryRoots { get; } = [];

    public ObservableCollection<FileEntryViewModel> Files { get; } = [];

    public ObservableCollection<FileEntryViewModel> MetadataResults { get; } = [];

    public ObservableCollection<FileEntryViewModel> CarvedFiles { get; } = [];

    public ObservableCollection<ClusterRowViewModel> ClusterRows { get; } = [];

    public ObservableCollection<ScanProgressRowViewModel> ScanProgressRows { get; } = [];

    public ObservableCollection<InspectorRowViewModel> InspectorRows { get; } = [];

    public PartitionViewModel? SelectedPartition
    {
        get => _selectedPartition;
        set
        {
            if (SetField(ref _selectedPartition, value))
            {
                LoadSelectedPartition();
            }
        }
    }

    public DirectoryNodeViewModel? SelectedDirectory
    {
        get => _selectedDirectory;
        set
        {
            if (SetField(ref _selectedDirectory, value) && value != null)
            {
                LoadDirectory(value);
            }
        }
    }

    public FileEntryViewModel? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetField(ref _selectedFile, value) && value != null)
            {
                SelectedMetadataResult = null;
                SelectedCarvedFile = null;
                UpdateInspector(value);
                RefreshComputedState();
            }
        }
    }

    public FileEntryViewModel? SelectedMetadataResult
    {
        get => _selectedMetadataResult;
        set
        {
            if (SetField(ref _selectedMetadataResult, value) && value != null)
            {
                SelectedFile = null;
                SelectedCarvedFile = null;
                UpdateInspector(value);
            }
        }
    }

    public FileEntryViewModel? SelectedCarvedFile
    {
        get => _selectedCarvedFile;
        set
        {
            if (SetField(ref _selectedCarvedFile, value) && value != null)
            {
                SelectedFile = null;
                SelectedMetadataResult = null;
                UpdateInspector(value);
            }
        }
    }

    public ClusterRowViewModel? SelectedClusterRow
    {
        get => _selectedClusterRow;
        set
        {
            if (SetField(ref _selectedClusterRow, value) && value != null)
            {
                InspectorTitle = value.Occupants;
                InspectorSubtitle = value.Status;
                InspectorRows.Clear();
                InspectorRows.Add(new InspectorRowViewModel("Unit", value.Unit));
                InspectorRows.Add(new InspectorRowViewModel("Offset", value.Offset));
                InspectorRows.Add(new InspectorRowViewModel("Length", value.Length));
            }
        }
    }

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

    public string CurrentFileSystemTitle
    {
        get => _currentFileSystemTitle;
        set => SetField(ref _currentFileSystemTitle, value);
    }

    public string CurrentDirectorySummary
    {
        get => _currentDirectorySummary;
        set => SetField(ref _currentDirectorySummary, value);
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

    public string SearchText
    {
        get => _searchText;
        set => SetField(ref _searchText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetField(ref _isBusy, value))
            {
                RefreshComputedState();
            }
        }
    }

    public bool CanRunMetadataScan => SelectedPartition?.Partition.SupportsMetadataScan == true && !IsBusy;

    public bool CanRunFileCarver => SelectedPartition?.Partition.SupportsFileCarver == true && !IsBusy;

    public bool CanCancelOperation => IsBusy && _operationCancellation?.IsCancellationRequested == false;

    public bool HasExportSelection => GetExportSelection() != null && !IsBusy;

    public bool CanUnmountPartition => SelectedPartition != null && !IsBusy;

    public bool CanAddCustomPartition => _openImage?.CanAddFatxPartition == true && !IsBusy;

    public string? KeyPath
    {
        get => _keyPath;
        set
        {
            if (SetField(ref _keyPath, value))
            {
                AppendLog(string.IsNullOrWhiteSpace(value) ? "Cleared key path." : $"Using key path: {value}");
            }
        }
    }

    public async Task OpenImageAsync(string imagePath)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "Opening image...";
        AppendLog($"Open image requested: {imagePath}");

        try
        {
            var image = await Task.Run(() => DesktopImageService.Open(imagePath, KeyPath));
            ReplaceOpenImage(image);
            StatusText = "Ready";
            AppendLog($"Opened {image.Kind}: {image.SourcePath}");
            if (!string.Equals(image.SourcePath, image.ActiveSourcePath, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog($"Using decoded IMGC cache: {image.ActiveSourcePath}");
            }

            AppendLog($"Detected {image.Partitions.Count:N0} partitions.");
        }
        catch (Exception ex)
        {
            StatusText = "Open failed";
            AppendLog($"Open failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ExportSelectedAsync(string outputPath)
    {
        var selected = GetExportSelection();
        if (SelectedPartition == null || selected == null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "Exporting...";
        try
        {
            var partition = SelectedPartition.Partition;
            var entry = selected.Entry;
            await Task.Run(() => ExportEntry(partition, entry, outputPath));
            StatusText = "Ready";
            AppendLog($"Exported {entry.Path} -> {outputPath}");
        }
        catch (Exception ex)
        {
            StatusText = "Export failed";
            AppendLog($"Export failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            RefreshComputedState();
        }
    }

    public async Task RunMetadataScanAsync()
    {
        if (SelectedPartition?.Partition.SupportsMetadataScan != true || IsBusy)
        {
            return;
        }

        IsBusy = true;
        _operationCancellation = new CancellationTokenSource();
        RefreshComputedState();
        ScanProgressRows.Clear();
        var progressRow = new ScanProgressRowViewModel("Metadata scan");
        ScanProgressRows.Add(progressRow);
        StatusText = "Scanning metadata...";
        AppendLog($"Metadata scan started: {SelectedPartition.Name}.");
        var progress = new Progress<int>(value =>
        {
            progressRow.UpdateIndeterminate($"{value:N0} records");
            StatusText = $"Scanning metadata... {value:N0}";
        });

        try
        {
            var token = _operationCancellation.Token;
            var rows = await Task.Run(() => SelectedPartition.Partition.ScanMetadata(token, progress), token);
            MetadataResults.Clear();
            foreach (var row in rows.Select(entry => new FileEntryViewModel(entry)))
            {
                MetadataResults.Add(row);
            }

            RebuildClusterRows();
            progressRow.Update(100, $"100% - {MetadataResults.Count:N0} entries");
            StatusText = "Ready";
            AppendLog($"Metadata scan complete: {MetadataResults.Count:N0} entries.");
        }
        catch (OperationCanceledException)
        {
            progressRow.UpdateIndeterminate("Canceled");
            StatusText = "Metadata scan canceled";
            AppendLog("Metadata scan canceled.");
        }
        catch (Exception ex)
        {
            progressRow.UpdateIndeterminate("Failed");
            StatusText = "Metadata scan failed";
            AppendLog($"Metadata scan failed: {ex.Message}");
        }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            IsBusy = false;
            RefreshComputedState();
        }
    }

    public async Task RunFileCarverAsync()
    {
        if (SelectedPartition?.Partition.SupportsFileCarver != true || IsBusy)
        {
            return;
        }

        IsBusy = true;
        _operationCancellation = new CancellationTokenSource();
        RefreshComputedState();
        ScanProgressRows.Clear();
        var progressRow = new ScanProgressRowViewModel("File carver");
        ScanProgressRows.Add(progressRow);
        StatusText = "Carving files...";
        AppendLog($"File carver started: {SelectedPartition.Name}.");
        var progress = new Progress<int>(value =>
        {
            progressRow.UpdateIndeterminate($"{value:N0} scan steps");
            StatusText = $"Carving files... {value:N0}";
        });

        try
        {
            var token = _operationCancellation.Token;
            var rows = await Task.Run(() => SelectedPartition.Partition.CarveFiles(token, progress), token);
            CarvedFiles.Clear();
            foreach (var row in rows.Select(entry => new FileEntryViewModel(entry)))
            {
                CarvedFiles.Add(row);
            }

            RebuildClusterRows();
            progressRow.Update(100, $"100% - {CarvedFiles.Count:N0} files");
            StatusText = "Ready";
            AppendLog($"File carver complete: {CarvedFiles.Count:N0} files.");
        }
        catch (OperationCanceledException)
        {
            progressRow.UpdateIndeterminate("Canceled");
            StatusText = "File carver canceled";
            AppendLog("File carver canceled.");
        }
        catch (Exception ex)
        {
            progressRow.UpdateIndeterminate("Failed");
            StatusText = "File carver failed";
            AppendLog($"File carver failed: {ex.Message}");
        }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            IsBusy = false;
            RefreshComputedState();
        }
    }

    public void CancelOperation()
    {
        _operationCancellation?.Cancel();
        StatusText = "Canceling...";
        AppendLog("Cancel requested.");
        RefreshComputedState();
    }

    public void UnmountSelectedPartition()
    {
        if (SelectedPartition == null || IsBusy)
        {
            return;
        }

        var index = Partitions.IndexOf(SelectedPartition);
        AppendLog($"Unmounted partition from current session: {SelectedPartition.Name}.");
        Partitions.Remove(SelectedPartition);
        DirectoryRoots.Clear();
        Files.Clear();
        MetadataResults.Clear();
        CarvedFiles.Clear();
        ClusterRows.Clear();
        InspectorRows.Clear();
        SelectedPartition = Partitions.ElementAtOrDefault(Math.Min(index, Partitions.Count - 1));
        if (SelectedPartition == null)
        {
            CurrentFileSystemTitle = "ORIGINAL FILESYSTEM";
            CurrentDirectorySummary = "No mounted partition selected.";
            InspectorTitle = "No file selected";
            InspectorSubtitle = "Open an image and select a file.";
        }

        RefreshComputedState();
    }

    public void AddCustomPartition(string name, long offset, long length)
    {
        if (_openImage?.CanAddFatxPartition != true || IsBusy)
        {
            return;
        }

        var partition = _openImage.AddFatxPartition(name, offset, length);
        var model = new PartitionViewModel(Partitions.Count, partition);
        Partitions.Add(model);
        SelectedPartition = model;
        AppendLog($"Added custom FATX partition: {name}, offset 0x{offset:X}, length 0x{length:X}.");
        RefreshComputedState();
    }

    public void FindNext()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return;
        }

        var match = Files.FirstOrDefault(file =>
            file.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
            file.Path.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            SelectedFile = match;
            StatusText = $"Found {match.Name}";
            return;
        }

        StatusText = "No match";
    }

    public void Dispose()
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _openImage?.Dispose();
    }

    private void ReplaceOpenImage(DesktopDriveImage image)
    {
        _openImage?.Dispose();
        _openImage = image;

        Partitions.Clear();
        DirectoryRoots.Clear();
        Files.Clear();
        MetadataResults.Clear();
        CarvedFiles.Clear();
        ClusterRows.Clear();
        ScanProgressRows.Clear();
        InspectorRows.Clear();

        for (var index = 0; index < image.Partitions.Count; index++)
        {
            Partitions.Add(new PartitionViewModel(index, image.Partitions[index]));
        }

        SelectedPartition = Partitions.FirstOrDefault(partition => partition.IsMounted) ?? Partitions.FirstOrDefault();
    }

    private void LoadSelectedPartition()
    {
        DirectoryRoots.Clear();
        Files.Clear();
        InspectorRows.Clear();
        SelectedFile = null;

        if (SelectedPartition == null)
        {
            CurrentFileSystemTitle = "ORIGINAL FILESYSTEM";
            CurrentDirectorySummary = "No mounted partition selected.";
            return;
        }

        CurrentFileSystemTitle = SelectedPartition.Name.ToUpperInvariant();
        CurrentDirectorySummary = SelectedPartition.Status;
        UpdatePartitionInspector(SelectedPartition);

        if (!SelectedPartition.IsMounted)
        {
            return;
        }

        IReadOnlyList<DesktopEntry> entries;
        try
        {
            entries = SelectedPartition.Partition.GetRootEntries();
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to list partition root: {ex.Message}");
            return;
        }

        foreach (var entry in entries.Where(entry => entry.IsDirectory))
        {
            DirectoryRoots.Add(new DirectoryNodeViewModel(entry));
        }

        LoadFiles(entries);
        RebuildClusterRows();
        CurrentDirectorySummary = $"{entries.Count:N0} root entries";
        RefreshComputedState();
    }

    private void LoadDirectory(DirectoryNodeViewModel node)
    {
        node.EnsureChildrenLoaded();
        var children = node.Entry.GetChildren();
        LoadFiles(children);
        CurrentDirectorySummary = $"{node.Entry.Path} - {children.Count:N0} entries";
    }

    private void LoadFiles(IEnumerable<DesktopEntry> entries)
    {
        Files.Clear();
        foreach (var entry in entries.OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            Files.Add(new FileEntryViewModel(entry));
        }
    }

    private void RebuildClusterRows()
    {
        ClusterRows.Clear();
        if (SelectedPartition == null)
        {
            return;
        }

        var entries = Files.Concat(MetadataResults).Concat(CarvedFiles).Select(row => row.Entry);
        foreach (var row in SelectedPartition.Partition.BuildClusterRows(entries).Select(row => new ClusterRowViewModel(row)))
        {
            ClusterRows.Add(row);
        }

        AppendLog($"Cluster map updated: {ClusterRows.Count:N0} occupied extents listed.");
    }

    private void UpdatePartitionInspector(PartitionViewModel partition)
    {
        InspectorTitle = partition.Name;
        InspectorSubtitle = partition.Status;
        InspectorRows.Clear();
        InspectorRows.Add(new InspectorRowViewModel("Kind", partition.Partition.DisplayName));
        InspectorRows.Add(new InspectorRowViewModel("Offset", $"0x{partition.Partition.Offset:X}"));
        InspectorRows.Add(new InspectorRowViewModel("Length", FormatBytes(partition.Partition.Length)));
        InspectorRows.Add(new InspectorRowViewModel("Mounted", partition.IsMounted ? "Yes" : "No"));
    }

    private void UpdateInspector(FileEntryViewModel file)
    {
        InspectorTitle = file.Name;
        InspectorSubtitle = file.Path;
        InspectorRows.Clear();
        InspectorRows.Add(new InspectorRowViewModel("Type", file.IsDirectory ? "Folder" : "File"));
        InspectorRows.Add(new InspectorRowViewModel("Size", file.SizeText));
        InspectorRows.Add(new InspectorRowViewModel("Path", file.Path));
        InspectorRows.Add(new InspectorRowViewModel("Source", file.Source));
        InspectorRows.Add(new InspectorRowViewModel("Offset", file.OffsetText));
        if (!string.IsNullOrWhiteSpace(file.Detail))
        {
            InspectorRows.Add(new InspectorRowViewModel("Detail", file.Detail));
        }
    }

    private static void ExportEntry(DesktopPartition partition, DesktopEntry entry, string outputPath)
    {
        if (entry is IExportableDesktopEntry exportable)
        {
            var parent = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            exportable.CopyTo(outputPath, CancellationToken.None);
            return;
        }

        if (!entry.IsDirectory)
        {
            var parent = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            partition.CopyFile(entry, outputPath);
            return;
        }

        Directory.CreateDirectory(outputPath);
        foreach (var child in entry.GetChildren())
        {
            ExportEntry(partition, child, Path.Combine(outputPath, SanitizeFileName(child.Name)));
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? "entry" : name;
    }

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogText = string.IsNullOrEmpty(LogText) ? line : $"{LogText}{Environment.NewLine}{line}";
    }

    private FileEntryViewModel? GetExportSelection()
    {
        return SelectedFile ?? SelectedMetadataResult ?? SelectedCarvedFile;
    }

    private void RefreshComputedState()
    {
        OnPropertyChanged(nameof(CanRunMetadataScan));
        OnPropertyChanged(nameof(CanRunFileCarver));
        OnPropertyChanged(nameof(CanCancelOperation));
        OnPropertyChanged(nameof(HasExportSelection));
        OnPropertyChanged(nameof(CanUnmountPartition));
        OnPropertyChanged(nameof(CanAddCustomPartition));
    }

    public static string FormatBytes(long value)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        var suffix = 0;
        while (size >= 1024 && suffix < suffixes.Length - 1)
        {
            size /= 1024;
            suffix++;
        }

        return $"{size:0.##} {suffixes[suffix]}";
    }
}

public sealed class PartitionViewModel
{
    public PartitionViewModel(int index, DesktopPartition partition)
    {
        Index = index;
        Partition = partition;
    }

    public int Index { get; }

    public DesktopPartition Partition { get; }

    public string Name => $"[{Index}] {Partition.DisplayName}";

    public string OffsetText => $"Offset 0x{Partition.Offset:X}";

    public string LengthText => MainWindowViewModel.FormatBytes(Partition.Length);

    public string Status => Partition.Status;

    public string StatusDisplay => Status.Length <= 78 ? Status : Status[..75] + "...";

    public bool IsMounted => Partition.IsMounted;
}

public sealed class DirectoryNodeViewModel : ObservableObject
{
    private bool _childrenLoaded;

    public DirectoryNodeViewModel(DesktopEntry entry)
    {
        Entry = entry;
    }

    public DesktopEntry Entry { get; }

    public string Name => Entry.Name;

    public string Glyph => "Folder";

    public ObservableCollection<DirectoryNodeViewModel> Children { get; } = [];

    public void EnsureChildrenLoaded()
    {
        if (_childrenLoaded)
        {
            return;
        }

        _childrenLoaded = true;
        foreach (var child in Entry.GetChildren().Where(entry => entry.IsDirectory).OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            Children.Add(new DirectoryNodeViewModel(child));
        }
    }
}

public sealed class FileEntryViewModel
{
    public FileEntryViewModel(DesktopEntry entry)
    {
        Entry = entry;
    }

    public DesktopEntry Entry { get; }

    public string Name => Entry.Name;

    public string Path => Entry.Path;

    public bool IsDirectory => Entry.IsDirectory;

    public string Kind => Entry.IsDirectory ? "Folder" : "File";

    public string SizeText => Entry.IsDirectory ? string.Empty : MainWindowViewModel.FormatBytes(Entry.Length);

    public string Source => string.IsNullOrWhiteSpace(Entry.Source) ? "Active" : Entry.Source;

    public string Detail => Entry.Detail;

    public string OffsetText => Entry.Offset > 0 ? $"0x{Entry.Offset:X}" : string.Empty;
}

public sealed record InspectorRowViewModel(string Label, string Value);

public sealed class ClusterRowViewModel
{
    public ClusterRowViewModel(DesktopClusterRow row)
    {
        Unit = row.Unit;
        Status = row.Status;
        Occupants = row.Occupants;
        Offset = row.Offset;
        Length = row.Length;
    }

    public string Unit { get; }

    public string Status { get; }

    public string Occupants { get; }

    public string Offset { get; }

    public string Length { get; }
}

public sealed class ScanProgressRowViewModel : ObservableObject
{
    private double _value;
    private string _text = "0%";
    private bool _isIndeterminate;
    private readonly ProgressEtaEstimator _etaEstimator = new();

    public ScanProgressRowViewModel(string title)
    {
        Title = title;
    }

    public string Title { get; }

    public double Value
    {
        get => _value;
        private set => SetField(ref _value, value);
    }

    public string Text
    {
        get => _text;
        private set => SetField(ref _text, value);
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => SetField(ref _isIndeterminate, value);
    }

    public void Update(double value, string text)
    {
        IsIndeterminate = false;
        Value = value;
        var etaText = _etaEstimator.BuildStatus(value, text);
        Text = string.IsNullOrWhiteSpace(etaText)
            ? text
            : $"{text} | {etaText}";
    }

    public void UpdateIndeterminate(string text)
    {
        IsIndeterminate = true;
        var etaText = _etaEstimator.BuildIndeterminateStatus(text);
        Text = string.IsNullOrWhiteSpace(etaText)
            ? text
            : $"{text} | {etaText}";
    }
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
