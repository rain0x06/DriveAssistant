using FATX;
using FATX.FileSystem;
using FATXTools.DiskTypes;
using FATXTools.Utilities;
using FATXTools.Wpf;

namespace DriveAssistant.Cli;

internal static class Program
{
    private const int Success = 0;
    private const int Error = 1;

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return Success;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "info" => RunInfo(args[1..]),
                "list" => RunList(args[1..]),
                "export" => RunExport(args[1..]),
                "rebuild-fatx" => FatxImageRebuildCommand.Run(args[1..]),
                "version" => RunVersion(),
                _ => Fail($"Unknown command '{args[0]}'. Run 'drive-assistant --help'.")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return Error;
        }
    }

    private static int RunVersion()
    {
        Console.WriteLine($"Drive Assistant CLI {BuildInfo.Version}");
        return Success;
    }

    private static int RunInfo(string[] args)
    {
        var options = CliOptions.Parse(args, requireImage: true);
        using var image = OpenImage(options);
        Console.WriteLine($"{image.Kind}: {image.SourcePath}");
        Console.WriteLine($"Partitions: {image.Partitions.Count}");
        for (var index = 0; index < image.Partitions.Count; index++)
        {
            var partition = image.Partitions[index];
            Console.WriteLine($"[{index}] {partition.DisplayName}");
            Console.WriteLine($"    Offset: 0x{partition.Offset:X}");
            Console.WriteLine($"    Length: 0x{partition.Length:X}");
            Console.WriteLine($"    Status: {partition.Status}");
        }

        return Success;
    }

    private static int RunList(string[] args)
    {
        var options = CliOptions.Parse(args, requireImage: true);
        using var image = OpenImage(options);
        var partition = image.GetPartition(options.PartitionIndex);
        Console.WriteLine($"[{options.PartitionIndex}] {partition.DisplayName}");
        foreach (var entry in partition.GetRootEntries())
        {
            PrintEntry(entry, options.Recursive, depth: 0);
        }

        return Success;
    }

    private static int RunExport(string[] args)
    {
        var options = CliOptions.Parse(args, requireImage: true, requireExport: true);
        using var image = OpenImage(options);
        var partition = image.GetPartition(options.PartitionIndex);
        var entry = partition.FindEntry(options.EntryPath!);
        if (entry == null)
        {
            return Fail($"Entry '{options.EntryPath}' was not found in partition {options.PartitionIndex}.");
        }

        if (entry.IsDirectory)
        {
            return Fail($"Entry '{options.EntryPath}' is a directory. Directory export is not implemented in the CLI yet.");
        }

        var outputPath = Path.GetFullPath(options.OutputPath!);
        var parent = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        partition.CopyFile(entry, outputPath);
        Console.WriteLine($"Exported {entry.Path} -> {outputPath}");
        return Success;
    }

    private static DriveAssistantImage OpenImage(CliOptions options)
    {
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

        var detail = errors.Count == 0 ? "No supported image signature was found." : string.Join("; ", errors.Distinct());
        throw new InvalidDataException(detail);
    }

    private static readonly Func<CliOptions, DriveAssistantImage?>[] ImageOpeners =
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

    private static DriveAssistantImage? TryOpenFatxImage(CliOptions options)
    {
        DriveReader reader = Path.GetExtension(options.ImagePath).Equals(".imgc", StringComparison.OrdinalIgnoreCase)
            ? new CompressedImage(options.ImagePath)
            : new RawImage(options.ImagePath);
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

            return (CliPartition)new FatxCliPartition(volume, status);
        }).ToList();

        if (!partitions.Any(partition => partition.IsMounted))
        {
            reader.Dispose();
            return null;
        }

        return new DriveAssistantImage(options.ImagePath, "FATX", partitions, reader);
    }

    private static DriveAssistantImage? TryOpenXboxGptImage(CliOptions options)
    {
        var image = XboxStorageImage.Open(options.ActiveImagePath);
        var partitions = image.Volumes.Select(volume => (CliPartition)new NtfsCliPartition(volume, $"Mounted, NTFS, {volume.GetRoot().Count:N0} root entries"))
            .Concat(image.BootFileSystems.Select(volume => (CliPartition)new GenericCliPartition(volume, $"Mounted, XBFS, {volume.GetRoot().Count:N0} root entries")))
            .ToList();
        return partitions.Count == 0 ? null : new DriveAssistantImage(options.ImagePath, "Xbox GPT/NTFS", partitions, image);
    }

    private static DriveAssistantImage? TryOpenSwitchImage(CliOptions options)
    {
        var image = SwitchStorageImage.Open(options.ActiveImagePath, options.KeyPath);
        var partitions = image.Partitions.Select(ToCliPartition).ToList();
        return partitions.Count == 0 ? null : new DriveAssistantImage(options.ImagePath, "Nintendo Switch", partitions, image);
    }

    private static DriveAssistantImage? TryOpenNintendoImage(CliOptions options)
    {
        var image = NintendoStorageImage.Open(options.ActiveImagePath, allowRawWiiUCandidate: false, options.KeyPath);
        var partitions = image.Partitions.Select(ToCliPartition).ToList();
        return partitions.Count == 0 ? null : new DriveAssistantImage(options.ImagePath, "Nintendo", partitions, image);
    }

    private static DriveAssistantImage? TryOpenPs2Image(CliOptions options)
    {
        var image = Ps2StorageImage.Open(options.ActiveImagePath);
        var partitions = image.Partitions.Select(ToCliPartition).ToList();
        return partitions.Count == 0 ? null : new DriveAssistantImage(options.ImagePath, "PlayStation 2", partitions, image);
    }

    private static DriveAssistantImage? TryOpenGenericImage(CliOptions options)
    {
        var image = GenericFileSystemImage.Open(options.ActiveImagePath);
        var partitions = image.Partitions.Select(ToCliPartition).ToList();
        return partitions.Count == 0 ? null : new DriveAssistantImage(options.ImagePath, "Generic filesystem", partitions, image);
    }

    private static DriveAssistantImage? TryOpenLegacyImage(CliOptions options)
    {
        var image = LegacyConsoleStorageImage.Open(options.ActiveImagePath);
        var partitions = image.Partitions.Select(ToCliPartition).ToList();
        return partitions.Count == 0 ? null : new DriveAssistantImage(options.ImagePath, "Legacy console/devkit", partitions, image);
    }

    private static DriveAssistantImage? TryOpenPlayStationImage(CliOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.KeyPath))
        {
            return null;
        }

        var image = PlayStationStorageImage.Open(options.ActiveImagePath, options.KeyPath);
        var partitions = image.Volumes.Select(volume => (CliPartition)new PlayStationCliPartition(volume, $"Detected {volume.FamilyText}")).ToList();
        return partitions.Count == 0 ? null : new DriveAssistantImage(options.ImagePath, "PlayStation", partitions, image);
    }

    private static CliPartition ToCliPartition(PartitionModel partition)
    {
        if (partition.GenericVolume != null)
        {
            return new GenericCliPartition(partition.GenericVolume, partition.Status);
        }

        if (partition.NtfsVolume != null)
        {
            return new NtfsCliPartition(partition.NtfsVolume, partition.Status);
        }

        if (partition.PlayStationVolume != null)
        {
            return new PlayStationCliPartition(partition.PlayStationVolume, partition.Status);
        }

        if (partition.FatxVolume != null)
        {
            return new FatxCliPartition(partition.FatxVolume, partition.Status);
        }

        throw new InvalidDataException($"Unsupported partition model '{partition.Name}'.");
    }

    private static void PrintEntry(CliEntry entry, bool recursive, int depth)
    {
        var indent = new string(' ', depth * 2);
        var marker = entry.IsDirectory ? "dir " : "file";
        Console.WriteLine($"{indent}{marker} {entry.Path} ({entry.Length:N0} bytes)");
        if (!recursive || !entry.IsDirectory)
        {
            return;
        }

        foreach (var child in entry.GetChildren())
        {
            PrintEntry(child, recursive, depth + 1);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return Error;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
Drive Assistant CLI

Usage:
  drive-assistant info <image> [--key <path>]
  drive-assistant list <image> [--partition <index>] [--recursive] [--key <path>]
  drive-assistant export <image> <entry-path> <output-path> [--partition <index>] [--key <path>]
  drive-assistant rebuild-fatx [<snapshot.json> [<live-files-dir> <deleted-files-dir>] <output.img>] [--output <path>] [--live-files <dir>] [--deleted-files <dir>] [--partition <name-or-index>] [--serial <hex>] [--include-deleted <true|false>]
  drive-assistant version

Examples:
  drive-assistant info ./disk.img
  drive-assistant list ./psp-nand.bin --partition 0 --recursive
  drive-assistant export ./disk.img /Content/save.bin ./save.bin --partition 1
  drive-assistant rebuild-fatx ./db.json ./rebuilt.img --include-deleted false
  drive-assistant rebuild-fatx ./db.json --live-files ./live --deleted-files ./deleted --output ./rebuilt.img --partition Partition1

`info`, `list`, and `export` are read-only against source images. `rebuild-fatx` creates a new image from snapshot metadata plus supplied source files.
""");
    }
}

internal sealed class CliOptions
{
    public required string ImagePath { get; init; }

    public string ActiveImagePath => Path.GetExtension(ImagePath).Equals(".imgc", StringComparison.OrdinalIgnoreCase)
        ? ImgcDecoder.DecodeToTempRawImage(ImagePath)
        : ImagePath;

    public string? EntryPath { get; init; }

    public string? OutputPath { get; init; }

    public string? KeyPath { get; init; }

    public int PartitionIndex { get; init; }

    public bool Recursive { get; init; }

    public static CliOptions Parse(string[] args, bool requireImage, bool requireExport = false)
    {
        var positionals = new List<string>();
        string? keyPath = null;
        var partition = 0;
        var recursive = false;
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--key":
                case "-k":
                    keyPath = RequireValue(args, ref index, arg);
                    break;
                case "--partition":
                case "-p":
                    var value = RequireValue(args, ref index, arg);
                    if (!int.TryParse(value, out partition) || partition < 0)
                    {
                        throw new ArgumentException("--partition expects a non-negative integer.");
                    }

                    break;
                case "--recursive":
                case "-r":
                    recursive = true;
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        throw new ArgumentException($"Unknown option '{arg}'.");
                    }

                    positionals.Add(arg);
                    break;
            }
        }

        if (requireImage && positionals.Count == 0)
        {
            throw new ArgumentException("Missing image path.");
        }

        if (requireExport && positionals.Count < 3)
        {
            throw new ArgumentException("Export requires <image> <entry-path> <output-path>.");
        }

        return new CliOptions
        {
            ImagePath = positionals.Count > 0 ? Path.GetFullPath(positionals[0]) : string.Empty,
            EntryPath = positionals.Count > 1 ? NormalizeEntryPath(positionals[1]) : null,
            OutputPath = positionals.Count > 2 ? positionals[2] : null,
            KeyPath = string.IsNullOrWhiteSpace(keyPath) ? null : Path.GetFullPath(keyPath),
            PartitionIndex = partition,
            Recursive = recursive
        };
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        index++;
        return args[index];
    }

    private static string NormalizeEntryPath(string path)
    {
        path = path.Replace('\\', '/');
        return path.StartsWith('/') ? path : "/" + path;
    }
}

internal sealed class DriveAssistantImage : IDisposable
{
    private readonly IDisposable? _disposable;

    public DriveAssistantImage(string sourcePath, string kind, IReadOnlyList<CliPartition> partitions, IDisposable? disposable)
    {
        SourcePath = sourcePath;
        Kind = kind;
        Partitions = partitions;
        _disposable = disposable;
    }

    public string SourcePath { get; }

    public string Kind { get; }

    public IReadOnlyList<CliPartition> Partitions { get; }

    public CliPartition GetPartition(int index)
    {
        if (index < 0 || index >= Partitions.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"Partition index {index} is outside 0..{Partitions.Count - 1}.");
        }

        return Partitions[index];
    }

    public void Dispose()
    {
        _disposable?.Dispose();
    }
}

internal abstract class CliPartition
{
    protected CliPartition(string displayName, long offset, long length, string status, bool isMounted)
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

    public abstract IReadOnlyList<CliEntry> GetRootEntries();

    public abstract void CopyFile(CliEntry entry, string outputPath);

    public CliEntry? FindEntry(string path)
    {
        path = path.Replace('\\', '/');
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        foreach (var entry in Walk(GetRootEntries()))
        {
            if (entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static IEnumerable<CliEntry> Walk(IEnumerable<CliEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
            foreach (var child in Walk(entry.GetChildren()))
            {
                yield return child;
            }
        }
    }
}

internal abstract class CliEntry
{
    protected CliEntry(string path, string name, bool isDirectory, long length)
    {
        Path = path;
        Name = name;
        IsDirectory = isDirectory;
        Length = length;
    }

    public string Path { get; }

    public string Name { get; }

    public bool IsDirectory { get; }

    public long Length { get; }

    public abstract IReadOnlyList<CliEntry> GetChildren();
}

internal sealed class GenericCliPartition : CliPartition
{
    private readonly GenericFileSystemVolume _volume;

    public GenericCliPartition(GenericFileSystemVolume volume, string status)
        : base($"{volume.FamilyText}: {volume.Name}", volume.Offset, volume.Length, status, isMounted: true)
    {
        _volume = volume;
    }

    public override IReadOnlyList<CliEntry> GetRootEntries() => _volume.GetRoot().Select(entry => new GenericCliEntry(entry)).ToList();

    public override void CopyFile(CliEntry entry, string outputPath)
    {
        _volume.CopyFile(((GenericCliEntry)entry).Entry, outputPath);
    }
}

internal sealed class GenericCliEntry : CliEntry
{
    public GenericCliEntry(GenericFileSystemEntry entry)
        : base(Normalize(entry.Path), entry.Name, entry.IsDirectory, entry.Length)
    {
        Entry = entry;
    }

    public GenericFileSystemEntry Entry { get; }

    public override IReadOnlyList<CliEntry> GetChildren() => Entry.Volume.GetChildren(Entry).Select(entry => new GenericCliEntry(entry)).ToList();

    private static string Normalize(string path) => path.Replace('\\', '/');
}

internal sealed class NtfsCliPartition : CliPartition
{
    private readonly XboxNtfsVolume _volume;

    public NtfsCliPartition(XboxNtfsVolume volume, string status)
        : base($"{volume.FamilyText}: {volume.Name}", volume.Offset, volume.Length, status, isMounted: true)
    {
        _volume = volume;
    }

    public override IReadOnlyList<CliEntry> GetRootEntries() => _volume.GetRoot().Select(entry => new NtfsCliEntry(_volume, entry)).ToList();

    public override void CopyFile(CliEntry entry, string outputPath)
    {
        _volume.CopyFile(((NtfsCliEntry)entry).Entry, outputPath);
    }
}

internal sealed class NtfsCliEntry : CliEntry
{
    private readonly XboxNtfsVolume _volume;

    public NtfsCliEntry(XboxNtfsVolume volume, XboxFileEntry entry)
        : base(Normalize(entry.Path), entry.Name, entry.IsDirectory, entry.Length)
    {
        _volume = volume;
        Entry = entry;
    }

    public XboxFileEntry Entry { get; }

    public override IReadOnlyList<CliEntry> GetChildren() => Entry.IsDirectory
        ? _volume.GetChildren(Entry).Select(entry => new NtfsCliEntry(_volume, entry)).ToList()
        : [];

    private static string Normalize(string path)
    {
        path = path.Replace('\\', '/');
        return path == "/" ? "/" : path.TrimEnd('/');
    }
}

internal sealed class PlayStationCliPartition : CliPartition
{
    private readonly PlayStationVolume _volume;

    public PlayStationCliPartition(PlayStationVolume volume, string status)
        : base($"{volume.FamilyText}: {volume.Name}", volume.Offset, volume.Length, status, volume.TryLoad())
    {
        _volume = volume;
    }

    public override IReadOnlyList<CliEntry> GetRootEntries() => _volume.GetRoot().Select(entry => new PlayStationCliEntry(entry)).ToList();

    public override void CopyFile(CliEntry entry, string outputPath)
    {
        _volume.CopyFile(((PlayStationCliEntry)entry).Entry, outputPath);
    }
}

internal sealed class PlayStationCliEntry : CliEntry
{
    public PlayStationCliEntry(PlayStationFileEntry entry)
        : base(Normalize(entry.Path), entry.Name, entry.IsDirectory, entry.Length)
    {
        Entry = entry;
    }

    public PlayStationFileEntry Entry { get; }

    public override IReadOnlyList<CliEntry> GetChildren() => Entry.Children.Select(entry => new PlayStationCliEntry(entry)).ToList();

    private static string Normalize(string path) => path.Replace('\\', '/');
}

internal sealed class FatxCliPartition : CliPartition
{
    private readonly Volume _volume;

    public FatxCliPartition(Volume volume, string status)
        : base($"FATX: {volume.Name}", volume.Offset, volume.Length, status, volume.Mounted)
    {
        _volume = volume;
    }

    public override IReadOnlyList<CliEntry> GetRootEntries() => _volume.GetRoot().Select(entry => new FatxCliEntry(entry)).ToList();

    public override void CopyFile(CliEntry entry, string outputPath)
    {
        var fatxEntry = ((FatxCliEntry)entry).Entry;
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
}

internal sealed class FatxCliEntry : CliEntry
{
    public FatxCliEntry(DirectoryEntry entry)
        : base(Normalize(entry.GetFullPath()), entry.FileName, entry.IsDirectory(), entry.IsDirectory() ? 0 : entry.FileSize)
    {
        Entry = entry;
    }

    public DirectoryEntry Entry { get; }

    public override IReadOnlyList<CliEntry> GetChildren() => Entry.IsDirectory()
        ? Entry.Children.Select(entry => new FatxCliEntry(entry)).ToList()
        : [];

    private static string Normalize(string path)
    {
        path = path.Replace('\\', '/');
        return path.StartsWith('/') ? path : "/" + path;
    }
}
