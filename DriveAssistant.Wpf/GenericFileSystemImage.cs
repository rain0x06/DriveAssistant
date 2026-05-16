using DiscUtils.Ntfs;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FATXTools.Wpf;

public sealed class GenericFileSystemImage : IDisposable
{
    internal const int SectorSize = 512;
    private readonly FileStream _stream;

    private GenericFileSystemImage(string sourcePath, FileStream stream, IReadOnlyList<PartitionModel> partitions)
    {
        SourcePath = sourcePath;
        _stream = stream;
        Partitions = partitions;
    }

    public string SourcePath { get; }

    public IReadOnlyList<PartitionModel> Partitions { get; }

    public static GenericFileSystemImage Open(string sourcePath)
    {
        var stream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            options: FileOptions.RandomAccess);

        try
        {
            var candidates = DiscoverPartitions(stream).ToList();
            var partitions = new List<PartitionModel>();
            foreach (var candidate in candidates)
            {
                var kind = DetectVolume(stream, candidate.Offset);
                try
                {
                    switch (kind)
                    {
                        case GenericFileSystemKind.Ntfs:
                        {
                            var slice = new PartitionSliceStream(stream, candidate.Offset, candidate.Length);
                            var ntfs = new NtfsFileSystem(slice);
                            var label = string.IsNullOrWhiteSpace(candidate.Name)
                                ? string.IsNullOrWhiteSpace(ntfs.VolumeLabel) ? $"NTFS @ 0x{candidate.Offset:X}" : ntfs.VolumeLabel
                                : candidate.Name;
                            var record = new GptPartitionRecord(candidate.Index, candidate.TypeGuid, candidate.Offset, candidate.Length, label);
                            var volume = new XboxNtfsVolume(ntfs, record, label, XboxStorageFamily.GptNtfs, sourcePath);
                            partitions.Add(new PartitionModel(volume, $"Mounted, NTFS, {volume.GetRoot().Count:N0} root entries"));
                            break;
                        }
                        case GenericFileSystemKind.Fat32:
                        {
                            var volume = Fat32Volume.Open(sourcePath, candidate);
                            partitions.Add(new PartitionModel(volume, $"Mounted, FAT32, {volume.GetRoot().Count:N0} root entries"));
                            break;
                        }
                        case GenericFileSystemKind.Fat16:
                        {
                            var volume = Fat16Volume.Open(sourcePath, candidate);
                            partitions.Add(new PartitionModel(volume, $"Mounted, FAT16, {volume.GetRoot().Count:N0} root entries"));
                            break;
                        }
                        case GenericFileSystemKind.Fat12:
                        {
                            var volume = Fat12Volume.Open(sourcePath, candidate);
                            partitions.Add(new PartitionModel(volume, $"Mounted, FAT12, {volume.GetRoot().Count:N0} root entries"));
                            break;
                        }
                        case GenericFileSystemKind.ExFat:
                        {
                            var volume = ExFatVolume.Open(sourcePath, candidate);
                            partitions.Add(new PartitionModel(volume, $"Mounted, exFAT, {volume.GetRoot().Count:N0} root entries"));
                            break;
                        }
                    }
                }
                catch
                {
                    // Keep probing other partitions. The open path reports failure if none mount.
                }
            }

            if (partitions.Count == 0)
            {
                throw new InvalidDataException("No mountable NTFS, FAT12, FAT16, FAT32, or exFAT volumes were found.");
            }

            return new GenericFileSystemImage(sourcePath, stream, partitions);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var partition in Partitions)
        {
            partition.NtfsVolume?.Dispose();
        }

        _stream.Dispose();
    }

    private static IEnumerable<GenericPartitionCandidate> DiscoverPartitions(Stream stream)
    {
        var seen = new HashSet<long>();
        foreach (var candidate in ReadGpt(stream).Concat(ReadMbr(stream)).Append(new GenericPartitionCandidate(0, Guid.Empty, 0, stream.Length, "Raw volume")))
        {
            if (candidate.Length <= 0 || candidate.Offset < 0 || candidate.Offset >= stream.Length)
            {
                continue;
            }

            var length = Math.Min(candidate.Length, stream.Length - candidate.Offset);
            if (seen.Add(candidate.Offset))
            {
                yield return candidate with { Length = length };
            }
        }
    }

    private static IReadOnlyList<GenericPartitionCandidate> ReadMbr(Stream stream)
    {
        var rows = new List<GenericPartitionCandidate>();
        var sector = new byte[SectorSize];
        if (!ReadExactly(stream, 0, sector) || sector[510] != 0x55 || sector[511] != 0xAA)
        {
            return rows;
        }

        for (var index = 0; index < 4; index++)
        {
            var entry = sector.AsSpan(446 + index * 16, 16);
            var type = entry[4];
            if (type == 0 || type == 0xEE)
            {
                continue;
            }

            var startLba = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var sectorCount = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (startLba == 0 || sectorCount == 0)
            {
                continue;
            }

            rows.Add(new GenericPartitionCandidate(
                (uint)(index + 1),
                Guid.Empty,
                (long)startLba * SectorSize,
                (long)sectorCount * SectorSize,
                $"MBR partition {index + 1}"));
        }

        return rows;
    }

    private static IReadOnlyList<GenericPartitionCandidate> ReadGpt(Stream stream)
    {
        foreach (var logicalSectorSize in new[] { SectorSize, 4096 })
        {
            var rows = ReadGpt(stream, logicalSectorSize);
            if (rows.Count > 0)
            {
                return rows;
            }
        }

        return [];
    }

    private static IReadOnlyList<GenericPartitionCandidate> ReadGpt(Stream stream, int logicalSectorSize)
    {
        var rows = new List<GenericPartitionCandidate>();
        var header = new byte[logicalSectorSize];
        if (!ReadExactly(stream, logicalSectorSize, header) || Encoding.ASCII.GetString(header[..8]) != "EFI PART")
        {
            return rows;
        }

        var entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(header[72..]);
        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header[80..]);
        var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header[84..]);
        if (entriesLba == 0 || entryCount == 0 || entrySize < 128 || entrySize > 4096)
        {
            return rows;
        }

        var entryBuffer = new byte[entrySize];
        for (uint index = 0; index < entryCount; index++)
        {
            var offset = checked((long)entriesLba * logicalSectorSize + index * (long)entrySize);
            if (!ReadExactly(stream, offset, entryBuffer))
            {
                return rows;
            }

            var typeGuid = new Guid(entryBuffer.AsSpan(0, 16));
            if (typeGuid == Guid.Empty)
            {
                continue;
            }

            var firstLba = BinaryPrimitives.ReadUInt64LittleEndian(entryBuffer.AsSpan(32, 8));
            var lastLba = BinaryPrimitives.ReadUInt64LittleEndian(entryBuffer.AsSpan(40, 8));
            if (firstLba == 0 || lastLba < firstLba)
            {
                continue;
            }

            var name = Encoding.Unicode.GetString(entryBuffer, 56, Math.Min(72, entryBuffer.Length - 56)).TrimEnd('\0');
            rows.Add(new GenericPartitionCandidate(
                index + 1,
                typeGuid,
                checked((long)firstLba * logicalSectorSize),
                checked((long)(lastLba - firstLba + 1) * logicalSectorSize),
                string.IsNullOrWhiteSpace(name) ? $"GPT partition {index + 1}" : name,
                logicalSectorSize));
        }

        return rows;
    }

    private static GenericFileSystemKind DetectVolume(Stream stream, long offset)
    {
        Span<byte> sector = stackalloc byte[SectorSize];
        if (!ReadExactly(stream, offset, sector))
        {
            return GenericFileSystemKind.Unknown;
        }

        if (sector[510] != 0x55 || sector[511] != 0xAA)
        {
            return GenericFileSystemKind.Unknown;
        }

        var oem = Encoding.ASCII.GetString(sector.Slice(3, 8));
        if (oem == "NTFS    ")
        {
            return GenericFileSystemKind.Ntfs;
        }

        if (oem == "EXFAT   ")
        {
            return GenericFileSystemKind.ExFat;
        }

        var fatType = Encoding.ASCII.GetString(sector.Slice(82, 8));
        if (fatType == "FAT32   ")
        {
            return GenericFileSystemKind.Fat32;
        }

        fatType = Encoding.ASCII.GetString(sector.Slice(54, 8));
        return fatType switch
        {
            "FAT12   " => GenericFileSystemKind.Fat12,
            "FAT16   " => GenericFileSystemKind.Fat16,
            _ => GenericFileSystemKind.Unknown
        };
    }

    internal static bool ReadExactly(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > stream.Length)
        {
            return false;
        }

        stream.Position = offset;
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    internal static bool ReadExactly(Stream stream, long offset, byte[] buffer)
    {
        return ReadExactly(stream, offset, buffer.AsSpan());
    }
}

public abstract class GenericFileSystemVolume
{
    protected GenericFileSystemVolume(string sourcePath, GenericPartitionCandidate partition, string familyText)
    {
        SourcePath = sourcePath;
        Name = partition.Name;
        Offset = partition.Offset;
        Length = partition.Length;
        FamilyText = familyText;
    }

    public string SourcePath { get; }

    public string Name { get; protected set; }

    public long Offset { get; }

    public long Length { get; }

    public string FamilyText { get; }

    public abstract long ClusterSize { get; }

    public abstract long UsedSpace { get; }

    public long FreeSpace => Math.Max(0, TotalSpace - UsedSpace);

    public long TotalSpace => Length;

    public abstract IReadOnlyList<GenericFileSystemEntry> GetRoot();

    public virtual IReadOnlyList<GenericFileSystemEntry> GetChildren(GenericFileSystemEntry? directory)
    {
        return directory?.Children ?? GetRoot();
    }

    public abstract IReadOnlyList<GenericFileSystemEntry> ScanDeleted(CancellationToken cancellationToken, IProgress<int>? progress);

    public void CopyFile(GenericFileSystemEntry entry, string destinationPath)
    {
        CopyFile(entry, destinationPath, null, CancellationToken.None);
    }

    public virtual void CopyFile(GenericFileSystemEntry entry, string destinationPath, Action<long>? progress, CancellationToken cancellationToken)
    {
        if (entry.IsDirectory)
        {
            throw new InvalidOperationException("Select a file, not a directory.");
        }

        const int bufferSize = 0x100000;
        var remaining = entry.Length;
        var buffer = new byte[bufferSize];
        using var input = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, FileOptions.RandomAccess);
        using var output = File.Create(destinationPath);
        foreach (var extent in entry.Extents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remaining <= 0)
            {
                break;
            }

            var readable = Math.Min(extent.Length, remaining);
            if (extent.Offset < 0 || extent.Offset >= input.Length)
            {
                break;
            }

            input.Position = extent.Offset;
            readable = Math.Min(readable, input.Length - extent.Offset);
            while (readable > 0 && remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, Math.Min(readable, remaining)));
                if (read == 0)
                {
                    return;
                }

                output.Write(buffer, 0, read);
                readable -= read;
                remaining -= read;
                progress?.Invoke(read);
            }
        }
    }

    protected List<FileExtent> BuildExtents(IEnumerable<uint> clusters, long logicalLength)
    {
        var clusterList = clusters.Where(cluster => cluster > 0).ToList();
        var extents = new List<FileExtent>();
        if (clusterList.Count == 0)
        {
            return extents;
        }

        var remaining = logicalLength <= 0 ? clusterList.Count * ClusterSize : logicalLength;
        var start = clusterList[0];
        var previous = start;
        for (var index = 1; index <= clusterList.Count; index++)
        {
            if (index < clusterList.Count && clusterList[index] == previous + 1)
            {
                previous = clusterList[index];
                continue;
            }

            var length = Math.Min(remaining, (previous - start + 1) * ClusterSize);
            extents.Add(new FileExtent(ClusterToOffset(start), length));
            remaining -= length;
            if (index < clusterList.Count)
            {
                start = previous = clusterList[index];
            }
        }

        return extents;
    }

    protected abstract long ClusterToOffset(uint cluster);
}

public sealed class GenericFileSystemEntry
{
    public required GenericFileSystemVolume Volume { get; init; }
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public bool IsDirectory { get; init; }
    public long Length { get; init; }
    public DateTime Created { get; init; }
    public DateTime Modified { get; init; }
    public DateTime Accessed { get; init; }
    public long Offset { get; init; }
    public long Cluster { get; init; }
    public bool IsDeleted { get; init; }
    public string Attributes { get; init; } = string.Empty;
    public string MetadataStatus { get; init; } = string.Empty;
    public IReadOnlyList<FileExtent> Extents { get; init; } = [];
    public List<GenericFileSystemEntry> Children { get; } = [];

    public int FolderCount => Children.Count(entry => entry.IsDirectory);

    public int FileCount => Children.Count(entry => !entry.IsDirectory);

    public string FragmentationStatus
    {
        get
        {
            if (IsDirectory)
            {
                return string.Empty;
            }

            if (Length == 0)
            {
                return "Empty";
            }

            if (Extents.Count == 0)
            {
                return "Unrecoverable";
            }

            return Extents.Count == 1 ? "Contiguous" : $"Fragmented into {Extents.Count:N0} runs";
        }
    }

    public string ExtentSummary => Extents.Count == 0
        ? string.Empty
        : string.Join(", ", Extents.Take(8).Select(extent => $"0x{extent.Offset:X}+0x{extent.Length:X}"));
}

public sealed class Fat32Volume : GenericFileSystemVolume
{
    private readonly List<GenericFileSystemEntry> _root = [];
    private readonly HashSet<uint> _loadedDirectoryClusters = [];
    private readonly uint[] _fat;
    private readonly ushort _bytesPerSector;
    private readonly byte _sectorsPerCluster;
    private readonly uint _rootCluster;
    private readonly long _dataOffset;

    private Fat32Volume(
        string sourcePath,
        GenericPartitionCandidate partition,
        ushort bytesPerSector,
        byte sectorsPerCluster,
        uint rootCluster,
        long dataOffset,
        uint[] fat)
        : base(sourcePath, partition, "FAT32")
    {
        _bytesPerSector = bytesPerSector;
        _sectorsPerCluster = sectorsPerCluster;
        _rootCluster = rootCluster;
        _dataOffset = dataOffset;
        _fat = fat;
    }

    public override long ClusterSize => (long)_bytesPerSector * _sectorsPerCluster;

    public override long UsedSpace => Walk(_root).Where(entry => !entry.IsDirectory).Sum(entry => Math.Max(0, entry.Length));

    public static Fat32Volume Open(string sourcePath, GenericPartitionCandidate partition)
    {
        using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        Span<byte> boot = stackalloc byte[GenericFileSystemImage.SectorSize];
        if (!GenericFileSystemImage.ReadExactly(stream, partition.Offset, boot))
        {
            throw new InvalidDataException("Could not read FAT32 boot sector.");
        }

        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot[11..]);
        var sectorsPerCluster = boot[13];
        var reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot[14..]);
        var fatCount = boot[16];
        var sectorsPerFat = BinaryPrimitives.ReadUInt32LittleEndian(boot[36..]);
        var rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot[44..]);
        if (bytesPerSector == 0 || sectorsPerCluster == 0 || sectorsPerFat == 0 || rootCluster < 2)
        {
            throw new InvalidDataException("Invalid FAT32 boot sector.");
        }

        var fatOffset = partition.Offset + (long)reservedSectors * bytesPerSector;
        var fatBytes = checked((int)Math.Min((long)sectorsPerFat * bytesPerSector, int.MaxValue));
        var fatRaw = new byte[fatBytes];
        GenericFileSystemImage.ReadExactly(stream, fatOffset, fatRaw);
        var fat = new uint[fatRaw.Length / 4];
        for (var index = 0; index < fat.Length; index++)
        {
            fat[index] = BinaryPrimitives.ReadUInt32LittleEndian(fatRaw.AsSpan(index * 4, 4)) & 0x0FFFFFFF;
        }

        var dataOffset = partition.Offset + ((long)reservedSectors + fatCount * (long)sectorsPerFat) * bytesPerSector;
        var volume = new Fat32Volume(sourcePath, partition, bytesPerSector, sectorsPerCluster, rootCluster, dataOffset, fat);
        volume._root.AddRange(volume.ReadDirectory(rootCluster, "/", includeDeleted: false, CancellationToken.None, new HashSet<uint>(), loadChildren: false));
        volume._loadedDirectoryClusters.Add(rootCluster);
        return volume;
    }

    public override IReadOnlyList<GenericFileSystemEntry> GetRoot() => _root;

    public override IReadOnlyList<GenericFileSystemEntry> GetChildren(GenericFileSystemEntry? directory)
    {
        if (directory == null)
        {
            return _root;
        }

        if (!directory.IsDirectory || directory.IsDeleted || directory.Cluster < 2)
        {
            return directory.Children;
        }

        var cluster = (uint)directory.Cluster;
        lock (_loadedDirectoryClusters)
        {
            if (_loadedDirectoryClusters.Add(cluster))
            {
                directory.Children.AddRange(ReadDirectory(cluster, directory.Path, includeDeleted: false, CancellationToken.None, new HashSet<uint>(), loadChildren: false));
            }
        }

        return directory.Children;
    }

    public override IReadOnlyList<GenericFileSystemEntry> ScanDeleted(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        var rows = new List<GenericFileSystemEntry>();
        ScanDeletedDirectory(_rootCluster, "/", rows, cancellationToken, new HashSet<uint>());
        progress?.Report(rows.Count);
        return rows;
    }

    protected override long ClusterToOffset(uint cluster)
    {
        return _dataOffset + (cluster - 2L) * ClusterSize;
    }

    private void ScanDeletedDirectory(uint cluster, string path, List<GenericFileSystemEntry> rows, CancellationToken cancellationToken, HashSet<uint> visitedDirectories)
    {
        if (!visitedDirectories.Add(cluster))
        {
            return;
        }

        foreach (var entry in ReadDirectory(cluster, path, includeDeleted: true, cancellationToken, visitedDirectories, currentDirectoryAlreadyVisited: true, loadChildren: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDeleted)
            {
                rows.Add(entry);
            }
            else if (entry.IsDirectory && entry.Cluster >= 2)
            {
                ScanDeletedDirectory((uint)entry.Cluster, entry.Path, rows, cancellationToken, visitedDirectories);
            }
        }

        visitedDirectories.Remove(cluster);
    }

    private List<GenericFileSystemEntry> ReadDirectory(
        uint firstCluster,
        string path,
        bool includeDeleted,
        CancellationToken cancellationToken,
        HashSet<uint> visitedDirectories,
        bool currentDirectoryAlreadyVisited = false,
        bool loadChildren = true)
    {
        var addedCurrentDirectory = false;
        if (!currentDirectoryAlreadyVisited)
        {
            if (!visitedDirectories.Add(firstCluster))
            {
                return [];
            }

            addedCurrentDirectory = true;
        }
        else if (!visitedDirectories.Contains(firstCluster))
        {
            return [];
        }

        var entries = new List<GenericFileSystemEntry>();
        var lfnParts = new List<string>();
        var completed = false;
        foreach (var cluster in GetClusterChain(firstCluster))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = ReadCluster(cluster);
            for (var offset = 0; offset + 32 <= data.Length; offset += 32)
            {
                var entry = data.AsSpan(offset, 32);
                var first = entry[0];
                if (first == 0x00)
                {
                    completed = true;
                    break;
                }

                var attr = entry[11];
                if (attr == 0x0F)
                {
                    lfnParts.Insert(0, DecodeFatLongName(entry));
                    continue;
                }

                var deleted = first == 0xE5;
                if (!IsPlausibleFatShortEntry(entry, deleted))
                {
                    lfnParts.Clear();
                    continue;
                }

                if (deleted && !includeDeleted)
                {
                    lfnParts.Clear();
                    continue;
                }

                if ((attr & 0x08) != 0)
                {
                    lfnParts.Clear();
                    continue;
                }

                var shortName = DecodeFatShortName(entry, deleted);
                if (shortName is "." or "..")
                {
                    lfnParts.Clear();
                    continue;
                }

                var name = string.Concat(lfnParts).TrimEnd('\0');
                lfnParts.Clear();
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = shortName;
                }

                var isDirectory = (attr & 0x10) != 0;
                var firstDataCluster = ((uint)BinaryPrimitives.ReadUInt16LittleEndian(entry[20..]) << 16)
                                       | BinaryPrimitives.ReadUInt16LittleEndian(entry[26..]);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[28..]);
                var childPath = CombinePath(path, name);
                var clusters = deleted
                    ? BuildContiguousClusterList(firstDataCluster, isDirectory ? ClusterSize : size)
                    : GetClusterChain(firstDataCluster);
                var extents = isDirectory ? new List<FileExtent>() : BuildExtents(clusters, size);
                var item = new GenericFileSystemEntry
                {
                    Volume = this,
                    Path = childPath,
                    Name = deleted ? $"_{name.TrimStart('_')}" : name,
                    Kind = isDirectory ? "Folder" : "File",
                    IsDirectory = isDirectory,
                    Length = isDirectory ? 0 : size,
                    Modified = DecodeFatDateTime(entry),
                    Accessed = DecodeFatDate(entry),
                    Created = DecodeFatDateTime(entry, createTime: true),
                    Offset = ClusterToOffset(cluster) + offset,
                    Cluster = firstDataCluster,
                    IsDeleted = deleted,
                    Attributes = $"0x{attr:X2}",
                    MetadataStatus = deleted ? "Deleted FAT32 directory entry" : "Active FAT32 directory entry",
                    Extents = extents
                };

                if (loadChildren && !deleted && isDirectory && firstDataCluster >= 2)
                {
                    item.Children.AddRange(ReadDirectory(firstDataCluster, childPath, includeDeleted: false, cancellationToken, visitedDirectories, loadChildren: true));
                }

                entries.Add(item);
            }

            if (completed)
            {
                break;
            }
        }

        if (addedCurrentDirectory)
        {
            visitedDirectories.Remove(firstCluster);
        }

        return entries;
    }

    private byte[] ReadCluster(uint cluster)
    {
        var buffer = new byte[ClusterSize];
        using var stream = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        GenericFileSystemImage.ReadExactly(stream, ClusterToOffset(cluster), buffer);
        return buffer;
    }

    private List<uint> GetClusterChain(uint firstCluster)
    {
        var chain = new List<uint>();
        var seen = new HashSet<uint>();
        var cluster = firstCluster;
        while (cluster >= 2 && cluster < _fat.Length && cluster < 0x0FFFFFF8 && seen.Add(cluster))
        {
            chain.Add(cluster);
            var next = _fat[cluster];
            if (next >= 0x0FFFFFF8 || next == 0)
            {
                break;
            }

            cluster = next;
        }

        return chain;
    }

    private List<uint> BuildContiguousClusterList(uint firstCluster, long length)
    {
        if (firstCluster < 2)
        {
            return [];
        }

        var count = Math.Max(1, (int)((length + ClusterSize - 1) / ClusterSize));
        return Enumerable.Range((int)firstCluster, count)
            .Where(cluster => cluster > 1 && cluster < _fat.Length)
            .Select(cluster => (uint)cluster)
            .ToList();
    }

    private static string DecodeFatLongName(ReadOnlySpan<byte> entry)
    {
        Span<byte> raw = stackalloc byte[26];
        entry.Slice(1, 10).CopyTo(raw);
        entry.Slice(14, 12).CopyTo(raw[10..]);
        entry.Slice(28, 4).CopyTo(raw[22..]);
        return Encoding.Unicode.GetString(raw).TrimEnd('\0', '\uffff');
    }

    private static bool IsPlausibleFatShortEntry(ReadOnlySpan<byte> entry, bool deleted)
    {
        var attr = entry[11];
        if ((attr & 0xC0) != 0)
        {
            return false;
        }

        if ((attr & 0x18) == 0x18)
        {
            return false;
        }

        for (var index = 0; index < 11; index++)
        {
            var value = entry[index];
            if (index == 0 && deleted && value == 0xE5)
            {
                continue;
            }

            if (value == 0x20)
            {
                continue;
            }

            if (value < 0x21 || value > 0x7E || IsInvalidFatShortNameCharacter(value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsInvalidFatShortNameCharacter(byte value)
    {
        return value is (byte)'"' or (byte)'*' or (byte)'+' or (byte)',' or (byte)'.' or (byte)'/' or (byte)':'
            or (byte)';' or (byte)'<' or (byte)'=' or (byte)'>' or (byte)'?' or (byte)'[' or (byte)'\\'
            or (byte)']' or (byte)'|';
    }

    private static string DecodeFatShortName(ReadOnlySpan<byte> entry, bool deleted)
    {
        Span<byte> nameRaw = stackalloc byte[11];
        entry[..11].CopyTo(nameRaw);
        if (deleted)
        {
            nameRaw[0] = (byte)'_';
        }

        var name = Encoding.ASCII.GetString(nameRaw[..8]).Trim();
        var extension = Encoding.ASCII.GetString(nameRaw[8..]).Trim();
        return string.IsNullOrWhiteSpace(extension) ? name : $"{name}.{extension}";
    }

    private static DateTime DecodeFatDateTime(ReadOnlySpan<byte> entry, bool createTime = false)
    {
        var timeOffset = createTime ? 14 : 22;
        var dateOffset = createTime ? 16 : 24;
        var time = BinaryPrimitives.ReadUInt16LittleEndian(entry[timeOffset..]);
        var date = BinaryPrimitives.ReadUInt16LittleEndian(entry[dateOffset..]);
        return DecodeFatDateTime(date, time);
    }

    private static DateTime DecodeFatDate(ReadOnlySpan<byte> entry)
    {
        var date = BinaryPrimitives.ReadUInt16LittleEndian(entry[18..]);
        return DecodeFatDateTime(date, 0);
    }

    private static DateTime DecodeFatDateTime(ushort date, ushort time)
    {
        try
        {
            var year = 1980 + ((date >> 9) & 0x7F);
            var month = (date >> 5) & 0x0F;
            var day = date & 0x1F;
            var hour = (time >> 11) & 0x1F;
            var minute = (time >> 5) & 0x3F;
            var second = (time & 0x1F) * 2;
            return month == 0 || day == 0 ? DateTime.MinValue : new DateTime(year, month, day, hour, minute, second);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static string CombinePath(string path, string name)
    {
        return path.TrimEnd('/') + "/" + name;
    }

    private static IEnumerable<GenericFileSystemEntry> Walk(IEnumerable<GenericFileSystemEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
            foreach (var child in Walk(entry.Children))
            {
                yield return child;
            }
        }
    }
}

public sealed class Fat16Volume : GenericFileSystemVolume
{
    private readonly List<GenericFileSystemEntry> _root = [];
    private readonly HashSet<uint> _loadedDirectoryClusters = [];
    private readonly ushort[] _fat;
    private readonly ushort _bytesPerSector;
    private readonly byte _sectorsPerCluster;
    private readonly ushort _rootEntryCount;
    private readonly long _rootDirectoryOffset;
    private readonly long _rootDirectoryLength;
    private readonly long _dataOffset;

    private Fat16Volume(
        string sourcePath,
        GenericPartitionCandidate partition,
        ushort bytesPerSector,
        byte sectorsPerCluster,
        ushort rootEntryCount,
        long rootDirectoryOffset,
        long rootDirectoryLength,
        long dataOffset,
        ushort[] fat)
        : base(sourcePath, partition, "FAT16")
    {
        _bytesPerSector = bytesPerSector;
        _sectorsPerCluster = sectorsPerCluster;
        _rootEntryCount = rootEntryCount;
        _rootDirectoryOffset = rootDirectoryOffset;
        _rootDirectoryLength = rootDirectoryLength;
        _dataOffset = dataOffset;
        _fat = fat;
    }

    public override long ClusterSize => (long)_bytesPerSector * _sectorsPerCluster;

    public override long UsedSpace => Walk(_root).Where(entry => !entry.IsDirectory).Sum(entry => Math.Max(0, entry.Length));

    public static Fat16Volume Open(string sourcePath, GenericPartitionCandidate partition)
    {
        using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        Span<byte> boot = stackalloc byte[GenericFileSystemImage.SectorSize];
        if (!GenericFileSystemImage.ReadExactly(stream, partition.Offset, boot))
        {
            throw new InvalidDataException("Could not read FAT16 boot sector.");
        }

        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot[11..]);
        var sectorsPerCluster = boot[13];
        var reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot[14..]);
        var fatCount = boot[16];
        var rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(boot[17..]);
        var totalSectors16 = BinaryPrimitives.ReadUInt16LittleEndian(boot[19..]);
        var sectorsPerFat = BinaryPrimitives.ReadUInt16LittleEndian(boot[22..]);
        var totalSectors32 = BinaryPrimitives.ReadUInt32LittleEndian(boot[32..]);
        var totalSectors = totalSectors16 != 0 ? totalSectors16 : totalSectors32;
        if (bytesPerSector == 0 || sectorsPerCluster == 0 || fatCount == 0 || rootEntryCount == 0 || sectorsPerFat == 0 || totalSectors == 0)
        {
            throw new InvalidDataException("Invalid FAT16 boot sector.");
        }

        var fatOffset = partition.Offset + (long)reservedSectors * bytesPerSector;
        var fatBytes = checked((int)Math.Min((long)sectorsPerFat * bytesPerSector, int.MaxValue));
        var fatRaw = new byte[fatBytes];
        GenericFileSystemImage.ReadExactly(stream, fatOffset, fatRaw);
        var fat = new ushort[fatRaw.Length / 2];
        for (var index = 0; index < fat.Length; index++)
        {
            fat[index] = BinaryPrimitives.ReadUInt16LittleEndian(fatRaw.AsSpan(index * 2, 2));
        }

        var rootDirectoryOffset = fatOffset + fatCount * (long)sectorsPerFat * bytesPerSector;
        var rootDirectoryLength = ((rootEntryCount * 32L + bytesPerSector - 1) / bytesPerSector) * bytesPerSector;
        var dataOffset = rootDirectoryOffset + rootDirectoryLength;
        var volume = new Fat16Volume(sourcePath, partition, bytesPerSector, sectorsPerCluster, rootEntryCount, rootDirectoryOffset, rootDirectoryLength, dataOffset, fat);
        volume._root.AddRange(volume.ReadDirectoryBytes(volume.ReadBytes(rootDirectoryOffset, rootDirectoryLength), "/", rootDirectoryOffset, includeDeleted: false, CancellationToken.None, new HashSet<uint>(), loadChildren: false));
        return volume;
    }

    public override IReadOnlyList<GenericFileSystemEntry> GetRoot() => _root;

    public override IReadOnlyList<GenericFileSystemEntry> GetChildren(GenericFileSystemEntry? directory)
    {
        if (directory == null)
        {
            return _root;
        }

        if (!directory.IsDirectory || directory.IsDeleted || directory.Cluster < 2)
        {
            return directory.Children;
        }

        var cluster = (uint)directory.Cluster;
        lock (_loadedDirectoryClusters)
        {
            if (_loadedDirectoryClusters.Add(cluster))
            {
                directory.Children.AddRange(ReadDirectoryClusterChain(cluster, directory.Path, includeDeleted: false, CancellationToken.None, new HashSet<uint>(), loadChildren: false));
            }
        }

        return directory.Children;
    }

    public override IReadOnlyList<GenericFileSystemEntry> ScanDeleted(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        var rows = new List<GenericFileSystemEntry>();
        var visitedDirectories = new HashSet<uint>();
        foreach (var entry in ReadDirectoryBytes(ReadBytes(_rootDirectoryOffset, _rootDirectoryLength), "/", _rootDirectoryOffset, includeDeleted: true, cancellationToken, visitedDirectories, loadChildren: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDeleted)
            {
                rows.Add(entry);
            }
            else if (entry.IsDirectory && entry.Cluster >= 2)
            {
                ScanDeletedDirectory((uint)entry.Cluster, entry.Path, rows, cancellationToken, visitedDirectories);
            }
        }

        progress?.Report(100);
        return rows;
    }

    protected override long ClusterToOffset(uint cluster)
    {
        return _dataOffset + (cluster - 2L) * ClusterSize;
    }

    private void ScanDeletedDirectory(uint cluster, string path, List<GenericFileSystemEntry> rows, CancellationToken cancellationToken, HashSet<uint> visitedDirectories)
    {
        if (!visitedDirectories.Add(cluster))
        {
            return;
        }

        foreach (var entry in ReadDirectoryClusterChain(cluster, path, includeDeleted: true, cancellationToken, visitedDirectories, currentDirectoryAlreadyVisited: true, loadChildren: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDeleted)
            {
                rows.Add(entry);
            }
            else if (entry.IsDirectory && entry.Cluster >= 2)
            {
                ScanDeletedDirectory((uint)entry.Cluster, entry.Path, rows, cancellationToken, visitedDirectories);
            }
        }

        visitedDirectories.Remove(cluster);
    }

    private List<GenericFileSystemEntry> ReadDirectoryClusterChain(
        uint firstCluster,
        string path,
        bool includeDeleted,
        CancellationToken cancellationToken,
        HashSet<uint> visitedDirectories,
        bool currentDirectoryAlreadyVisited = false,
        bool loadChildren = true)
    {
        var addedCurrentDirectory = false;
        if (!currentDirectoryAlreadyVisited)
        {
            if (!visitedDirectories.Add(firstCluster))
            {
                return [];
            }

            addedCurrentDirectory = true;
        }
        else if (!visitedDirectories.Contains(firstCluster))
        {
            return [];
        }

        using var output = new MemoryStream();
        foreach (var cluster in GetClusterChain(firstCluster))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = ReadBytes(ClusterToOffset(cluster), ClusterSize);
            output.Write(data, 0, data.Length);
        }

        var entries = ReadDirectoryBytes(output.ToArray(), path, ClusterToOffset(firstCluster), includeDeleted, cancellationToken, visitedDirectories, loadChildren);
        if (addedCurrentDirectory)
        {
            visitedDirectories.Remove(firstCluster);
        }

        return entries;
    }

    private List<GenericFileSystemEntry> ReadDirectoryBytes(
        byte[] data,
        string path,
        long directoryOffset,
        bool includeDeleted,
        CancellationToken cancellationToken,
        HashSet<uint> visitedDirectories,
        bool loadChildren = true)
    {
        var entries = new List<GenericFileSystemEntry>();
        var lfnParts = new List<string>();
        for (var offset = 0; offset + 32 <= data.Length; offset += 32)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = data.AsSpan(offset, 32);
            var first = entry[0];
            if (first == 0x00)
            {
                break;
            }

            var attr = entry[11];
            if (attr == 0x0F)
            {
                lfnParts.Insert(0, DecodeFatLongName(entry));
                continue;
            }

            var deleted = first == 0xE5;
            if (!IsPlausibleFatShortEntry(entry, deleted))
            {
                lfnParts.Clear();
                continue;
            }

            if (deleted && !includeDeleted)
            {
                lfnParts.Clear();
                continue;
            }

            if ((attr & 0x08) != 0)
            {
                lfnParts.Clear();
                continue;
            }

            var shortName = DecodeFatShortName(entry, deleted);
            if (shortName is "." or "..")
            {
                lfnParts.Clear();
                continue;
            }

            var name = string.Concat(lfnParts).TrimEnd('\0');
            lfnParts.Clear();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = shortName;
            }

            var isDirectory = (attr & 0x10) != 0;
            var firstDataCluster = BinaryPrimitives.ReadUInt16LittleEndian(entry[26..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[28..]);
            var childPath = CombinePath(path, name);
            var clusters = deleted
                ? BuildContiguousClusterList(firstDataCluster, isDirectory ? ClusterSize : size)
                : GetClusterChain(firstDataCluster);
            var extents = isDirectory ? new List<FileExtent>() : BuildExtents(clusters, size);
            var item = new GenericFileSystemEntry
            {
                Volume = this,
                Path = childPath,
                Name = deleted ? $"_{name.TrimStart('_')}" : name,
                Kind = isDirectory ? "Folder" : "File",
                IsDirectory = isDirectory,
                Length = isDirectory ? 0 : size,
                Modified = DecodeFatDateTime(entry),
                Accessed = DecodeFatDate(entry),
                Created = DecodeFatDateTime(entry, createTime: true),
                Offset = directoryOffset + offset,
                Cluster = firstDataCluster,
                IsDeleted = deleted,
                Attributes = $"0x{attr:X2}",
                MetadataStatus = deleted ? "Deleted FAT16 directory entry" : "Active FAT16 directory entry",
                Extents = extents
            };

            if (loadChildren && !deleted && isDirectory && firstDataCluster >= 2)
            {
                item.Children.AddRange(ReadDirectoryClusterChain(firstDataCluster, childPath, includeDeleted: false, cancellationToken, visitedDirectories, loadChildren: true));
            }

            entries.Add(item);
        }

        return entries;
    }

    private byte[] ReadBytes(long offset, long length)
    {
        var buffer = new byte[checked((int)Math.Min(length, int.MaxValue))];
        using var stream = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        GenericFileSystemImage.ReadExactly(stream, offset, buffer);
        return buffer;
    }

    private List<uint> GetClusterChain(uint firstCluster)
    {
        var chain = new List<uint>();
        var seen = new HashSet<uint>();
        var cluster = firstCluster;
        while (cluster >= 2 && cluster < _fat.Length && cluster < 0xFFF8 && seen.Add(cluster))
        {
            chain.Add(cluster);
            var next = _fat[cluster];
            if (next >= 0xFFF8 || next == 0)
            {
                break;
            }

            cluster = next;
        }

        return chain;
    }

    private List<uint> BuildContiguousClusterList(uint firstCluster, long length)
    {
        if (firstCluster < 2)
        {
            return [];
        }

        var count = Math.Max(1, (int)((length + ClusterSize - 1) / ClusterSize));
        return Enumerable.Range((int)firstCluster, count)
            .Where(cluster => cluster > 1 && cluster < _fat.Length)
            .Select(cluster => (uint)cluster)
            .ToList();
    }

    private static string DecodeFatLongName(ReadOnlySpan<byte> entry)
    {
        Span<byte> raw = stackalloc byte[26];
        entry.Slice(1, 10).CopyTo(raw);
        entry.Slice(14, 12).CopyTo(raw[10..]);
        entry.Slice(28, 4).CopyTo(raw[22..]);
        return Encoding.Unicode.GetString(raw).TrimEnd('\0', '\uffff');
    }

    private static bool IsPlausibleFatShortEntry(ReadOnlySpan<byte> entry, bool deleted)
    {
        var attr = entry[11];
        if ((attr & 0xC0) != 0)
        {
            return false;
        }

        if ((attr & 0x18) == 0x18)
        {
            return false;
        }

        for (var index = 0; index < 11; index++)
        {
            var value = entry[index];
            if (index == 0 && deleted && value == 0xE5)
            {
                continue;
            }

            if (value == 0x20)
            {
                continue;
            }

            if (value < 0x21 || value > 0x7E || IsInvalidFatShortNameCharacter(value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsInvalidFatShortNameCharacter(byte value)
    {
        return value is (byte)'"' or (byte)'*' or (byte)'+' or (byte)',' or (byte)'.' or (byte)'/' or (byte)':'
            or (byte)';' or (byte)'<' or (byte)'=' or (byte)'>' or (byte)'?' or (byte)'[' or (byte)'\\'
            or (byte)']' or (byte)'|';
    }

    private static string DecodeFatShortName(ReadOnlySpan<byte> entry, bool deleted)
    {
        Span<byte> nameRaw = stackalloc byte[11];
        entry[..11].CopyTo(nameRaw);
        if (deleted)
        {
            nameRaw[0] = (byte)'_';
        }

        var name = Encoding.ASCII.GetString(nameRaw[..8]).Trim();
        var extension = Encoding.ASCII.GetString(nameRaw[8..]).Trim();
        return string.IsNullOrWhiteSpace(extension) ? name : $"{name}.{extension}";
    }

    private static DateTime DecodeFatDateTime(ReadOnlySpan<byte> entry, bool createTime = false)
    {
        var timeOffset = createTime ? 14 : 22;
        var dateOffset = createTime ? 16 : 24;
        var time = BinaryPrimitives.ReadUInt16LittleEndian(entry[timeOffset..]);
        var date = BinaryPrimitives.ReadUInt16LittleEndian(entry[dateOffset..]);
        return DecodeFatDateTime(date, time);
    }

    private static DateTime DecodeFatDate(ReadOnlySpan<byte> entry)
    {
        var date = BinaryPrimitives.ReadUInt16LittleEndian(entry[18..]);
        return DecodeFatDateTime(date, 0);
    }

    private static DateTime DecodeFatDateTime(ushort date, ushort time)
    {
        try
        {
            var year = 1980 + ((date >> 9) & 0x7F);
            var month = (date >> 5) & 0x0F;
            var day = date & 0x1F;
            var hour = (time >> 11) & 0x1F;
            var minute = (time >> 5) & 0x3F;
            var second = (time & 0x1F) * 2;
            return month == 0 || day == 0 ? DateTime.MinValue : new DateTime(year, month, day, hour, minute, second);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static string CombinePath(string path, string name)
    {
        return path.TrimEnd('/') + "/" + name;
    }

    private static IEnumerable<GenericFileSystemEntry> Walk(IEnumerable<GenericFileSystemEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
            foreach (var child in Walk(entry.Children))
            {
                yield return child;
            }
        }
    }
}

public sealed class ExFatVolume : GenericFileSystemVolume
{
    private readonly List<GenericFileSystemEntry> _root = [];
    private readonly uint[] _fat;
    private readonly uint _rootCluster;
    private readonly long _clusterHeapOffset;
    private readonly long _clusterSize;

    private ExFatVolume(string sourcePath, GenericPartitionCandidate partition, long clusterHeapOffset, long clusterSize, uint rootCluster, uint[] fat)
        : base(sourcePath, partition, "exFAT")
    {
        _clusterHeapOffset = clusterHeapOffset;
        _clusterSize = clusterSize;
        _rootCluster = rootCluster;
        _fat = fat;
    }

    public override long ClusterSize => _clusterSize;

    public override long UsedSpace => Walk(_root).Where(entry => !entry.IsDirectory).Sum(entry => Math.Max(0, entry.Length));

    public static ExFatVolume Open(string sourcePath, GenericPartitionCandidate partition)
    {
        using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        Span<byte> boot = stackalloc byte[GenericFileSystemImage.SectorSize];
        if (!GenericFileSystemImage.ReadExactly(stream, partition.Offset, boot))
        {
            throw new InvalidDataException("Could not read exFAT boot sector.");
        }

        var fatOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot[80..]);
        var fatLengthSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot[84..]);
        var clusterHeapOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot[88..]);
        var rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot[96..]);
        var bytesPerSector = 1L << boot[108];
        var sectorsPerCluster = 1L << boot[109];
        var clusterSize = bytesPerSector * sectorsPerCluster;
        if (fatOffsetSectors == 0 || fatLengthSectors == 0 || clusterHeapOffsetSectors == 0 || rootCluster < 2 || clusterSize <= 0)
        {
            throw new InvalidDataException("Invalid exFAT boot sector.");
        }

        var fatOffset = partition.Offset + fatOffsetSectors * bytesPerSector;
        var fatBytes = checked((int)Math.Min(fatLengthSectors * bytesPerSector, int.MaxValue));
        var fatRaw = new byte[fatBytes];
        GenericFileSystemImage.ReadExactly(stream, fatOffset, fatRaw);
        var fat = new uint[fatRaw.Length / 4];
        for (var index = 0; index < fat.Length; index++)
        {
            fat[index] = BinaryPrimitives.ReadUInt32LittleEndian(fatRaw.AsSpan(index * 4, 4));
        }

        var volume = new ExFatVolume(
            sourcePath,
            partition,
            partition.Offset + clusterHeapOffsetSectors * bytesPerSector,
            clusterSize,
            rootCluster,
            fat);
        volume._root.AddRange(volume.ReadDirectory(
            rootCluster,
            "/",
            includeDeleted: false,
            CancellationToken.None,
            noFatChain: false,
            maxBytes: 256L * volume.ClusterSize));
        return volume;
    }

    public override IReadOnlyList<GenericFileSystemEntry> GetRoot() => _root;

    public override IReadOnlyList<GenericFileSystemEntry> ScanDeleted(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        var entries = ReadDirectory(
            _rootCluster,
            "/",
            includeDeleted: true,
            cancellationToken,
            noFatChain: false,
            maxBytes: 256L * ClusterSize);
        var rows = Walk(entries).Where(entry => entry.IsDeleted).ToList();
        progress?.Report(rows.Count);
        return rows;
    }

    protected override long ClusterToOffset(uint cluster)
    {
        return _clusterHeapOffset + (cluster - 2L) * ClusterSize;
    }

    private List<GenericFileSystemEntry> ReadDirectory(
        uint firstCluster,
        string path,
        bool includeDeleted,
        CancellationToken cancellationToken,
        bool noFatChain,
        long maxBytes)
    {
        var entries = new List<GenericFileSystemEntry>();
        var data = ReadClusterChain(firstCluster, noFatChain, maxBytes);
        for (var offset = 0; offset + 32 <= data.Length; offset += 32)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = data.AsSpan(offset, 32);
            var type = entry[0];
            if (type == 0x00)
            {
                break;
            }

            var baseType = type & 0x7F;
            if (baseType != 0x05)
            {
                continue;
            }

            var active = (type & 0x80) != 0;
            if (!active && !includeDeleted)
            {
                continue;
            }

            var secondaryCount = entry[1];
            if (secondaryCount <= 0 || offset + (secondaryCount + 1) * 32 > data.Length)
            {
                continue;
            }

            var attributes = BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]);
            var nameBuilder = new StringBuilder();
            var firstDataCluster = 0u;
            var dataLength = 0L;
            var childNoFatChain = false;
            for (var secondary = 1; secondary <= secondaryCount; secondary++)
            {
                var secondaryEntry = data.AsSpan(offset + secondary * 32, 32);
                var secondaryBaseType = secondaryEntry[0] & 0x7F;
                if (secondaryBaseType == 0x40)
                {
                    childNoFatChain = (secondaryEntry[1] & 0x02) != 0;
                    firstDataCluster = BinaryPrimitives.ReadUInt32LittleEndian(secondaryEntry[20..]);
                    dataLength = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(secondaryEntry[24..]), long.MaxValue);
                }
                else if (secondaryBaseType == 0x41)
                {
                    nameBuilder.Append(Encoding.Unicode.GetString(secondaryEntry.Slice(2, 30)).TrimEnd('\0', '\uffff'));
                }
            }

            var name = nameBuilder.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"entry_{ClusterToOffset(firstCluster) + offset:X}";
            }

            var isDirectory = (attributes & 0x10) != 0;
            var childPath = path.TrimEnd('/') + "/" + name;
            var clusters = childNoFatChain
                ? BuildContiguousClusterList(firstDataCluster, isDirectory ? ClusterSize : dataLength)
                : GetClusterChain(firstDataCluster);
            var extents = isDirectory ? new List<FileExtent>() : BuildExtents(clusters, dataLength);
            var item = new GenericFileSystemEntry
            {
                Volume = this,
                Path = childPath,
                Name = active ? name : $"_{name.TrimStart('_')}",
                Kind = isDirectory ? "Folder" : "File",
                IsDirectory = isDirectory,
                Length = isDirectory ? 0 : dataLength,
                Offset = ClusterToOffset(firstCluster) + offset,
                Cluster = firstDataCluster,
                IsDeleted = !active,
                Attributes = $"0x{attributes:X4}",
                MetadataStatus = active ? "Active exFAT directory entry" : "Deleted exFAT directory entry",
                Extents = extents
            };

            if (active && isDirectory && firstDataCluster >= 2)
            {
                item.Children.AddRange(ReadDirectory(
                    firstDataCluster,
                    childPath,
                    includeDeleted,
                    cancellationToken,
                    childNoFatChain,
                    dataLength > 0 ? dataLength : 256L * ClusterSize));
            }

            entries.Add(item);
            offset += secondaryCount * 32;
        }

        return entries;
    }

    private byte[] ReadClusterChain(uint firstCluster, bool noFatChain, long maxBytes)
    {
        var clusters = noFatChain ? BuildContiguousClusterList(firstCluster, maxBytes) : GetClusterChain(firstCluster);
        using var output = new MemoryStream();
        using var stream = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        var buffer = new byte[ClusterSize];
        foreach (var cluster in clusters)
        {
            if (output.Length >= maxBytes)
            {
                break;
            }

            if (!GenericFileSystemImage.ReadExactly(stream, ClusterToOffset(cluster), buffer))
            {
                break;
            }

            output.Write(buffer, 0, (int)Math.Min(buffer.Length, maxBytes - output.Length));
        }

        return output.ToArray();
    }

    private List<uint> GetClusterChain(uint firstCluster)
    {
        var chain = new List<uint>();
        var seen = new HashSet<uint>();
        var cluster = firstCluster;
        while (cluster >= 2 && cluster < _fat.Length && cluster < 0xFFFFFFF8 && seen.Add(cluster))
        {
            chain.Add(cluster);
            var next = _fat[cluster];
            if (next >= 0xFFFFFFF8 || next == 0)
            {
                break;
            }

            cluster = next;
        }

        return chain;
    }

    private List<uint> BuildContiguousClusterList(uint firstCluster, long length)
    {
        if (firstCluster < 2)
        {
            return [];
        }

        var count = Math.Max(1, (int)((length + ClusterSize - 1) / ClusterSize));
        return Enumerable.Range((int)firstCluster, count)
            .Where(cluster => cluster > 1 && cluster < _fat.Length)
            .Select(cluster => (uint)cluster)
            .ToList();
    }

    private static IEnumerable<GenericFileSystemEntry> Walk(IEnumerable<GenericFileSystemEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
            foreach (var child in Walk(entry.Children))
            {
                yield return child;
            }
        }
    }
}

public enum GenericFileSystemKind
{
    Unknown,
    Ntfs,
    Fat12,
    Fat16,
    Fat32,
    ExFat
}

public sealed record GenericPartitionCandidate(uint Index, Guid TypeGuid, long Offset, long Length, string Name, int LogicalSectorSize = 512);
