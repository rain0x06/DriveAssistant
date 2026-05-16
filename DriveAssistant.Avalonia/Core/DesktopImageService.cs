using FATX;
using FATX.Analyzers;
using FATX.Analyzers.Signatures;
using FATX.FileSystem;
using FATXTools.DiskTypes;
using FATXTools.Utilities;
using FATXTools.Wpf;

namespace DriveAssistant.Avalonia.Core;

public static class DesktopImageService
{
    public static DesktopDriveImage Open(string imagePath, string? keyPath)
    {
        var sourcePath = Path.GetFullPath(imagePath);
        var options = new DesktopOpenOptions(sourcePath, DecodeImgcIfNeeded(sourcePath), NormalizeKeyPath(keyPath));
        var errors = new List<string>();

        foreach (var opener in ImageOpeners)
        {
            try
            {
                var image = opener(options);
                if (image != null)
                {
                    return image;
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }

        var detail = errors.Count == 0
            ? "No supported image signature was found."
            : string.Join("; ", errors.Distinct());
        throw new InvalidDataException(detail);
    }

    private static string DecodeImgcIfNeeded(string imagePath)
    {
        return Path.GetExtension(imagePath).Equals(".imgc", StringComparison.OrdinalIgnoreCase)
            ? ImgcDecoder.DecodeToTempRawImage(imagePath)
            : imagePath;
    }

    private static string? NormalizeKeyPath(string? keyPath)
    {
        return string.IsNullOrWhiteSpace(keyPath) ? null : Path.GetFullPath(keyPath);
    }

    private static readonly Func<DesktopOpenOptions, DesktopDriveImage?>[] ImageOpeners =
    [
        TryOpenFatxImage,
        TryOpenXboxGptImage,
        TryOpenSwitchImage,
        TryOpenNintendoImage,
        TryOpenPs2Image,
        TryOpenGenericImage,
        TryOpenLegacyImage,
        TryOpenPlayStationImage
    ];

    private static DesktopDriveImage? TryOpenFatxImage(DesktopOpenOptions options)
    {
        DriveReader reader = Path.GetExtension(options.ImagePath).Equals(".imgc", StringComparison.OrdinalIgnoreCase)
            ? new CompressedImage(options.ImagePath)
            : new RawImage(options.ActiveImagePath);

        if (reader.Partitions.Count == 0)
        {
            reader.Dispose();
            return null;
        }

        var partitions = reader.Partitions.Select(volume =>
        {
            string status;
            try
            {
                volume.Mount();
                status = $"Mounted, {volume.GetRoot().Count:N0} root entries";
            }
            catch (Exception ex)
            {
                status = $"Mount failed: {ex.Message}";
            }

            return (DesktopPartition)new FatxDesktopPartition(volume, status);
        }).ToList();

        if (!partitions.Any(partition => partition.IsMounted))
        {
            reader.Dispose();
            return null;
        }

        return new DesktopDriveImage(options.ImagePath, options.ActiveImagePath, "FATX", partitions, reader);
    }

    private static DesktopDriveImage? TryOpenXboxGptImage(DesktopOpenOptions options)
    {
        var image = XboxStorageImage.Open(options.ActiveImagePath);
        var partitions = image.Volumes
            .Select(volume => (DesktopPartition)new NtfsDesktopPartition(volume, $"Mounted, NTFS, {volume.GetRoot().Count:N0} root entries"))
            .Concat(image.BootFileSystems.Select(volume => (DesktopPartition)new GenericDesktopPartition(volume, $"Mounted, XBFS, {volume.GetRoot().Count:N0} root entries")))
            .ToList();
        return partitions.Count == 0 ? null : new DesktopDriveImage(options.ImagePath, options.ActiveImagePath, "Xbox GPT/NTFS", partitions, image);
    }

    private static DesktopDriveImage? TryOpenSwitchImage(DesktopOpenOptions options)
    {
        var image = SwitchStorageImage.Open(options.ActiveImagePath, options.KeyPath);
        var partitions = image.Partitions.Select(ToDesktopPartition).ToList();
        return partitions.Count == 0 ? null : new DesktopDriveImage(options.ImagePath, options.ActiveImagePath, "Nintendo Switch", partitions, image);
    }

    private static DesktopDriveImage? TryOpenNintendoImage(DesktopOpenOptions options)
    {
        var image = NintendoStorageImage.Open(options.ActiveImagePath, allowRawWiiUCandidate: false, options.KeyPath);
        var partitions = image.Partitions.Select(ToDesktopPartition).ToList();
        return partitions.Count == 0 ? null : new DesktopDriveImage(options.ImagePath, image.ActiveSourcePath, "Nintendo", partitions, image);
    }

    private static DesktopDriveImage? TryOpenPs2Image(DesktopOpenOptions options)
    {
        var image = Ps2StorageImage.Open(options.ActiveImagePath);
        var partitions = image.Partitions.Select(ToDesktopPartition).ToList();
        return partitions.Count == 0 ? null : new DesktopDriveImage(options.ImagePath, options.ActiveImagePath, "PlayStation 2", partitions, image);
    }

    private static DesktopDriveImage? TryOpenGenericImage(DesktopOpenOptions options)
    {
        var image = GenericFileSystemImage.Open(options.ActiveImagePath);
        var partitions = image.Partitions.Select(ToDesktopPartition).ToList();
        return partitions.Count == 0 ? null : new DesktopDriveImage(options.ImagePath, options.ActiveImagePath, "Generic filesystem", partitions, image);
    }

    private static DesktopDriveImage? TryOpenLegacyImage(DesktopOpenOptions options)
    {
        var image = LegacyConsoleStorageImage.Open(options.ActiveImagePath);
        var partitions = image.Partitions.Select(ToDesktopPartition).ToList();
        return partitions.Count == 0 ? null : new DesktopDriveImage(options.ImagePath, options.ActiveImagePath, "Legacy console/devkit", partitions, image);
    }

    private static DesktopDriveImage? TryOpenPlayStationImage(DesktopOpenOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.KeyPath))
        {
            return null;
        }

        var image = PlayStationStorageImage.Open(options.ActiveImagePath, options.KeyPath);
        var partitions = image.Volumes
            .Select(volume => (DesktopPartition)new PlayStationDesktopPartition(volume, $"Detected {volume.FamilyText}"))
            .ToList();
        return partitions.Count == 0 ? null : new DesktopDriveImage(options.ImagePath, options.ActiveImagePath, "PlayStation", partitions, image);
    }

    private static DesktopPartition ToDesktopPartition(PartitionModel partition)
    {
        if (partition.GenericVolume != null)
        {
            return new GenericDesktopPartition(partition.GenericVolume, partition.Status);
        }

        if (partition.NtfsVolume != null)
        {
            return new NtfsDesktopPartition(partition.NtfsVolume, partition.Status);
        }

        if (partition.PlayStationVolume != null)
        {
            return new PlayStationDesktopPartition(partition.PlayStationVolume, partition.Status);
        }

        if (partition.FatxVolume != null)
        {
            return new FatxDesktopPartition(partition.FatxVolume, partition.Status);
        }

        throw new InvalidDataException($"Unsupported partition model '{partition.Name}'.");
    }
}

public sealed class DesktopDriveImage : IDisposable
{
    private readonly IDisposable? _disposable;
    private readonly List<DesktopPartition> _partitions;

    public DesktopDriveImage(string sourcePath, string activeSourcePath, string kind, IReadOnlyList<DesktopPartition> partitions, IDisposable? disposable)
    {
        SourcePath = sourcePath;
        ActiveSourcePath = activeSourcePath;
        Kind = kind;
        _partitions = partitions.ToList();
        _disposable = disposable;
    }

    public string SourcePath { get; }

    public string ActiveSourcePath { get; }

    public string Kind { get; }

    public IReadOnlyList<DesktopPartition> Partitions => _partitions;

    public bool CanAddFatxPartition => _disposable is DriveReader;

    public DesktopPartition AddFatxPartition(string name, long offset, long length)
    {
        if (_disposable is not DriveReader reader)
        {
            throw new InvalidOperationException("Custom FATX partitions require an open FATX image.");
        }

        reader.AddPartition(name, offset, length);
        var volume = reader.Partitions.Last();
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

        var partition = new FatxDesktopPartition(volume, status);
        _partitions.Add(partition);
        return partition;
    }

    public void Dispose()
    {
        _disposable?.Dispose();
    }
}

public abstract class DesktopPartition
{
    protected DesktopPartition(string displayName, long offset, long length, string status, bool isMounted)
    {
        DisplayName = displayName;
        Offset = offset;
        Length = length;
        Status = status;
        IsMounted = isMounted;
    }

    public string DisplayName { get; }

    public long Offset { get; }

    public long Length { get; }

    public string Status { get; }

    public bool IsMounted { get; }

    public virtual bool SupportsMetadataScan => false;

    public virtual bool SupportsFileCarver => false;

    public abstract IReadOnlyList<DesktopEntry> GetRootEntries();

    public abstract void CopyFile(DesktopEntry entry, string outputPath);

    public virtual IReadOnlyList<DesktopEntry> ScanMetadata(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        return [];
    }

    public virtual IReadOnlyList<DesktopEntry> CarveFiles(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        return [];
    }

    public virtual IReadOnlyList<DesktopClusterRow> BuildClusterRows(IEnumerable<DesktopEntry> entries)
    {
        return DesktopClusterRow.Build(DisplayName, entries);
    }
}

public abstract class DesktopEntry
{
    protected DesktopEntry(
        string path,
        string name,
        bool isDirectory,
        long length,
        string source = "",
        string detail = "",
        long offset = 0,
        IReadOnlyList<FileExtent>? extents = null)
    {
        Path = path;
        Name = name;
        IsDirectory = isDirectory;
        Length = length;
        Source = source;
        Detail = detail;
        Offset = offset;
        Extents = extents ?? [];
    }

    public string Path { get; }

    public string Name { get; }

    public bool IsDirectory { get; }

    public long Length { get; }

    public string Source { get; }

    public string Detail { get; }

    public long Offset { get; }

    public IReadOnlyList<FileExtent> Extents { get; }

    public abstract IReadOnlyList<DesktopEntry> GetChildren();
}

public interface IExportableDesktopEntry
{
    void CopyTo(string outputPath, CancellationToken cancellationToken);
}

public sealed record DesktopClusterRow(string Unit, string Status, string Occupants, string Offset, string Length)
{
    public static IReadOnlyList<DesktopClusterRow> Build(string unitName, IEnumerable<DesktopEntry> entries)
    {
        return entries
            .Where(entry => entry.Extents.Count > 0)
            .SelectMany(entry => entry.Extents.Select((extent, index) => new DesktopClusterRow(
                $"{unitName} run {index + 1}",
                entry.Source.Length > 0 ? entry.Source : (entry.IsDirectory ? "Directory" : "Active"),
                entry.Name,
                $"0x{extent.Offset:X}",
                FormatBytes(extent.Length))))
            .OrderBy(row => row.Offset, StringComparer.OrdinalIgnoreCase)
            .Take(2000)
            .ToList();
    }

    private static string FormatBytes(long value)
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

internal sealed class GenericDesktopPartition : DesktopPartition
{
    private readonly GenericFileSystemVolume _volume;

    public GenericDesktopPartition(GenericFileSystemVolume volume, string status)
        : base($"{volume.FamilyText}: {volume.Name}", volume.Offset, volume.Length, status, isMounted: true)
    {
        _volume = volume;
    }

    public override IReadOnlyList<DesktopEntry> GetRootEntries() => _volume.GetRoot().Select(entry => new GenericDesktopEntry(entry)).ToList();

    public override void CopyFile(DesktopEntry entry, string outputPath)
    {
        if (entry is IExportableDesktopEntry exportable)
        {
            exportable.CopyTo(outputPath, CancellationToken.None);
            return;
        }

        _volume.CopyFile(((GenericDesktopEntry)entry).Entry, outputPath);
    }

    public override bool SupportsMetadataScan => true;

    public override bool SupportsFileCarver => File.Exists(_volume.SourcePath);

    public override IReadOnlyList<DesktopEntry> ScanMetadata(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        return _volume.ScanDeleted(cancellationToken, progress)
            .Select(entry => new GenericDesktopEntry(entry, source: "Metadata"))
            .ToList();
    }

    public override IReadOnlyList<DesktopEntry> CarveFiles(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        var carver = new GenericFileCarver(_volume.SourcePath, _volume.Offset, _volume.Length, _volume.Offset, 0x200, _volume.FamilyText, ScanProfile.Balanced);
        carver.SetCustomSignatures(CustomSignatureLoader.Load("custom_carvers.json"));
        return carver.Analyze(cancellationToken, progress)
            .Select(file => new GenericCarvedDesktopEntry(file))
            .ToList();
    }
}

internal sealed class GenericDesktopEntry : DesktopEntry
{
    public GenericDesktopEntry(GenericFileSystemEntry entry, string source = "Active")
        : base(Normalize(entry.Path), entry.Name, entry.IsDirectory, entry.Length, source, entry.FragmentationStatus, entry.Offset, entry.Extents)
    {
        Entry = entry;
    }

    public GenericFileSystemEntry Entry { get; }

    public override IReadOnlyList<DesktopEntry> GetChildren() => Entry.Volume.GetChildren(Entry).Select(entry => new GenericDesktopEntry(entry)).ToList();

    private static string Normalize(string path) => path.Replace('\\', '/');
}

internal sealed class NtfsDesktopPartition : DesktopPartition
{
    private readonly XboxNtfsVolume _volume;

    public NtfsDesktopPartition(XboxNtfsVolume volume, string status)
        : base($"{volume.FamilyText}: {volume.Name}", volume.Offset, volume.Length, status, isMounted: true)
    {
        _volume = volume;
    }

    public override IReadOnlyList<DesktopEntry> GetRootEntries() => _volume.GetRoot().Select(entry => new NtfsDesktopEntry(_volume, entry)).ToList();

    public override void CopyFile(DesktopEntry entry, string outputPath)
    {
        if (entry is IExportableDesktopEntry exportable)
        {
            exportable.CopyTo(outputPath, CancellationToken.None);
            return;
        }

        _volume.CopyFile(((NtfsDesktopEntry)entry).Entry, outputPath);
    }

    public override bool SupportsMetadataScan => true;

    public override bool SupportsFileCarver => File.Exists(_volume.SourcePath);

    public override IReadOnlyList<DesktopEntry> ScanMetadata(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        var ntfsProgress = progress == null
            ? null
            : new Progress<NtfsMetadataScanProgress>(value => progress.Report(value.Current));
        return _volume.ScanMetadata(cancellationToken, ntfsProgress)
            .Select(entry => new NtfsDesktopEntry(_volume, entry, source: entry.IsDeleted ? "Deleted" : "Metadata"))
            .ToList();
    }

    public override IReadOnlyList<DesktopEntry> CarveFiles(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        var carver = new GenericFileCarver(_volume.SourcePath, _volume.Offset, _volume.Length, _volume.Offset, 0x200, _volume.FamilyText, ScanProfile.Balanced);
        carver.SetCustomSignatures(CustomSignatureLoader.Load("custom_carvers.json"));
        return carver.Analyze(cancellationToken, progress)
            .Select(file => new GenericCarvedDesktopEntry(file))
            .ToList();
    }
}

internal sealed class NtfsDesktopEntry : DesktopEntry
{
    private readonly XboxNtfsVolume _volume;

    public NtfsDesktopEntry(XboxNtfsVolume volume, XboxFileEntry entry, string source = "Active")
        : base(Normalize(entry.Path), entry.Name, entry.IsDirectory, entry.Length, source, entry.FragmentationStatus, entry.Offset, entry.Extents)
    {
        _volume = volume;
        Entry = entry;
    }

    public XboxFileEntry Entry { get; }

    public override IReadOnlyList<DesktopEntry> GetChildren() => Entry.IsDirectory
        ? _volume.GetChildren(Entry).Select(entry => new NtfsDesktopEntry(_volume, entry)).ToList()
        : [];

    private static string Normalize(string path)
    {
        path = path.Replace('\\', '/');
        return path == "/" ? "/" : path.TrimEnd('/');
    }
}

internal sealed class PlayStationDesktopPartition : DesktopPartition
{
    private readonly PlayStationVolume _volume;

    public PlayStationDesktopPartition(PlayStationVolume volume, string status)
        : base($"{volume.FamilyText}: {volume.Name}", volume.Offset, volume.Length, status, volume.TryLoad())
    {
        _volume = volume;
    }

    public override IReadOnlyList<DesktopEntry> GetRootEntries() => _volume.GetRoot().Select(entry => new PlayStationDesktopEntry(entry)).ToList();

    public override void CopyFile(DesktopEntry entry, string outputPath)
    {
        if (entry is IExportableDesktopEntry exportable)
        {
            exportable.CopyTo(outputPath, CancellationToken.None);
            return;
        }

        _volume.CopyFile(((PlayStationDesktopEntry)entry).Entry, outputPath);
    }

    public override bool SupportsMetadataScan => true;

    public override bool SupportsFileCarver => false;

    public override IReadOnlyList<DesktopEntry> ScanMetadata(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        var rows = _volume.ScanMetadata()
            .Select(entry => new PlayStationDesktopEntry(entry, "Metadata"))
            .ToList<DesktopEntry>();
        progress?.Report(rows.Count);
        cancellationToken.ThrowIfCancellationRequested();
        return rows;
    }
}

internal sealed class PlayStationDesktopEntry : DesktopEntry
{
    public PlayStationDesktopEntry(PlayStationFileEntry entry, string source = "Active")
        : base(Normalize(entry.Path), entry.Name, entry.IsDirectory, entry.Length, source, entry.FragmentationStatus, entry.Offset, entry.Extents)
    {
        Entry = entry;
    }

    public PlayStationFileEntry Entry { get; }

    public override IReadOnlyList<DesktopEntry> GetChildren() => Entry.Children.Select(entry => new PlayStationDesktopEntry(entry)).ToList();

    private static string Normalize(string path) => path.Replace('\\', '/');
}

internal sealed class FatxDesktopPartition : DesktopPartition
{
    private readonly Volume _volume;

    public FatxDesktopPartition(Volume volume, string status)
        : base($"FATX: {volume.Name}", volume.Offset, volume.Length, status, volume.Mounted)
    {
        _volume = volume;
    }

    public override IReadOnlyList<DesktopEntry> GetRootEntries() => _volume.GetRoot().Select(entry => new FatxDesktopEntry(entry)).ToList();

    public override void CopyFile(DesktopEntry entry, string outputPath)
    {
        if (entry is IExportableDesktopEntry exportable)
        {
            exportable.CopyTo(outputPath, CancellationToken.None);
            return;
        }

        var fatxEntry = ((FatxDesktopEntry)entry).Entry;
        using var output = File.Create(outputPath);
        var remaining = fatxEntry.FileSize;
        foreach (var cluster in _volume.GetClusterChain(fatxEntry))
        {
            if (remaining <= 0)
            {
                break;
            }

            var data = _volume.ReadCluster(cluster);
            var count = (int)Math.Min(data.Length, remaining);
            output.Write(data, 0, count);
            remaining -= (uint)count;
        }
    }

    public override bool SupportsMetadataScan => _volume.Mounted;

    public override bool SupportsFileCarver => _volume.Mounted;

    public override IReadOnlyList<DesktopEntry> ScanMetadata(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        var interval = Math.Max(1L, _volume.BytesPerCluster);
        var analyzer = new MetadataAnalyzer(_volume, interval, _volume.FileAreaLength);
        return analyzer.Analyze(cancellationToken, progress)
            .Select(entry => new FatxDesktopEntry(entry, "Recovered"))
            .ToList();
    }

    public override IReadOnlyList<DesktopEntry> CarveFiles(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        var carver = new FileCarver(_volume, FileCarverInterval.Sector, _volume.FileAreaLength);
        carver.SetCustomSignatures(CustomSignatureLoader.Load("custom_carvers.json"));
        return carver.Analyze(cancellationToken, progress)
            .Select(signature => new FatxSignatureDesktopEntry(_volume, signature))
            .ToList();
    }
}

internal sealed class FatxDesktopEntry : DesktopEntry
{
    public FatxDesktopEntry(DirectoryEntry entry, string source = "Active")
        : base(
            Normalize(entry.GetFullPath()),
            entry.FileName,
            entry.IsDirectory(),
            entry.IsDirectory() ? 0 : entry.FileSize,
            source,
            entry.IsDirectory() ? "FATX directory entry" : "FATX file entry",
            entry.Offset,
            [])
    {
        Entry = entry;
    }

    public DirectoryEntry Entry { get; }

    public override IReadOnlyList<DesktopEntry> GetChildren() => Entry.IsDirectory()
        ? Entry.Children.Select(entry => new FatxDesktopEntry(entry)).ToList()
        : [];

    private static string Normalize(string path)
    {
        path = path.Replace('\\', '/');
        return path.StartsWith('/') ? path : "/" + path;
    }
}

internal sealed class FatxSignatureDesktopEntry : DesktopEntry, IExportableDesktopEntry
{
    private readonly Volume _volume;
    private readonly FileSignature _signature;

    public FatxSignatureDesktopEntry(Volume volume, FileSignature signature)
        : base(
            "/" + signature.FileName,
            signature.FileName,
            isDirectory: false,
            signature.FileSize,
            "Carved",
            signature.GetType().Name,
            volume.Offset + volume.FileAreaByteOffset + signature.Offset,
            [new FileExtent(volume.Offset + volume.FileAreaByteOffset + signature.Offset, signature.FileSize)])
    {
        _volume = volume;
        _signature = signature;
    }

    public override IReadOnlyList<DesktopEntry> GetChildren() => [];

    public void CopyTo(string outputPath, CancellationToken cancellationToken)
    {
        const int bufferSize = 0x100000;
        var remaining = _signature.FileSize;
        var buffer = new byte[bufferSize];
        using var output = File.Create(outputPath);
        _volume.SeekFileArea(_signature.Offset);
        var reader = _volume.GetReader();
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }
}

internal sealed class GenericCarvedDesktopEntry : DesktopEntry, IExportableDesktopEntry
{
    private readonly GenericCarvedFile _file;

    public GenericCarvedDesktopEntry(GenericCarvedFile file)
        : base(
            "/" + file.Name,
            file.Name,
            isDirectory: false,
            file.Size,
            "Carved",
            file.Detail,
            file.DisplayOffset,
            [new FileExtent(file.SourceOffset, file.Size)])
    {
        _file = file;
    }

    public override IReadOnlyList<DesktopEntry> GetChildren() => [];

    public void CopyTo(string outputPath, CancellationToken cancellationToken)
    {
        const int bufferSize = 0x100000;
        var remaining = _file.Size;
        var buffer = new byte[bufferSize];
        using var input = new FileStream(_file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, FileOptions.RandomAccess);
        using var output = File.Create(outputPath);
        input.Position = _file.SourceOffset;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }
}

internal sealed record DesktopOpenOptions(string ImagePath, string ActiveImagePath, string? KeyPath);
