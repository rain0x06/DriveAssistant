using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Text.Json;
using FATX.FileSystem;

namespace DriveAssistant.Cli;

internal static class FatxImageRebuildCommand
{
    private const uint SectorSize = 0x200;
    private const uint SectorsPerCluster = 0x20;
    private const uint RootCluster = 0x1;
    private static ReadOnlySpan<byte> XtafHeaderSignature => [0x58, 0x54, 0x41, 0x46];
    private const int DirentSize = 0x40;
    private const int DirentsPerCluster = 0x100;
    private const int Error = 1;

    public static int Run(string[] args)
    {
        return Run(args, execution: null);
    }

    public static int Run(string[] args, RebuildExecutionOptions? execution)
    {
        CancellationTokenSource? defaultCancellation = null;
        ConsoleCancelEventHandler? cancelHandler = null;
        if (execution == null)
        {
            defaultCancellation = new CancellationTokenSource();
            var lastPercent = -1;
            string? lastStage = null;
            string? lastDetail = null;
            var lastWriteUtc = DateTime.MinValue;
            var etaEstimator = new ProgressEtaEstimator();
            var consoleProgressLock = new object();
            execution = new RebuildExecutionOptions
            {
                CancellationToken = defaultCancellation.Token,
                Progress = snapshot =>
                {
                    lock (consoleProgressLock)
                    {
                        var now = DateTime.UtcNow;
                        if (snapshot.Percent == lastPercent &&
                            string.Equals(snapshot.Stage, lastStage, StringComparison.Ordinal) &&
                            string.Equals(snapshot.Detail, lastDetail, StringComparison.Ordinal) &&
                            now - lastWriteUtc < TimeSpan.FromSeconds(1))
                        {
                            return;
                        }

                        lastPercent = snapshot.Percent;
                        lastStage = snapshot.Stage;
                        lastDetail = snapshot.Detail;
                        lastWriteUtc = now;
                        var etaText = etaEstimator.BuildStatus(snapshot.Percent, snapshot.Stage);
                        var detail = string.IsNullOrWhiteSpace(etaText)
                            ? snapshot.Detail
                            : $"{snapshot.Detail} | {etaText}";
                        Console.Write($"\r[{snapshot.Percent,3}%] {snapshot.Stage} - {detail}   ");
                        Console.Out.Flush();
                    }
                }
            };

            cancelHandler = (_, e) =>
            {
                e.Cancel = true;
                defaultCancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
        }

        try
        {
        var options = RebuildOptions.Parse(args);
        options = PromptMissing(options);

        if (!File.Exists(options.SnapshotPath))
        {
            Console.Error.WriteLine($"error: Snapshot JSON was not found: {options.SnapshotPath}");
            return Error;
        }

        if (!string.IsNullOrWhiteSpace(options.LiveFilesDirectory) && !Directory.Exists(options.LiveFilesDirectory))
        {
            Console.Error.WriteLine($"error: Live-files directory was not found: {options.LiveFilesDirectory}");
            return Error;
        }

        if (!string.IsNullOrWhiteSpace(options.DeletedFilesDirectory) && !Directory.Exists(options.DeletedFilesDirectory))
        {
            Console.Error.WriteLine($"error: Deleted-files directory was not found: {options.DeletedFilesDirectory}");
            return Error;
        }

        var snapshot = LoadSnapshot(options.SnapshotPath, execution);
        var partition = SelectPartition(snapshot, options.PartitionSelector);
        if (partition.Length <= 0)
        {
            Console.Error.WriteLine($"error: Partition '{partition.Name}' has invalid length {partition.Length}.");
            return Error;
        }

        var rebuildEntries = partition.OriginalFilesystem.Count > 0
            ? partition.OriginalFilesystem
            : partition.Analysis.MetadataAnalyzer;
        ReportProgress(
            execution,
            "Load snapshot",
            0,
            0,
            partition.OriginalFilesystem.Count > 0
                ? $"Using original filesystem tree with {CountSnapshotEntriesRecursive(rebuildEntries):N0} metadata entries"
                : $"Using metadata analyzer tree with {CountSnapshotEntriesRecursive(rebuildEntries):N0} recovered entries");

        var sourceIndex = SourceIndex.Build(options.LiveFilesDirectory, options.DeletedFilesDirectory, execution);
        var treeProgress = new RebuildTreeBuildProgress();
        var nodes = BuildIncludedTree(rebuildEntries, parentPath: string.Empty, partition.Name, sourceIndex, options.IncludeDeletedEntries, execution, treeProgress);
        AddUnmatchedSourceFiles(nodes, sourceIndex, treeProgress, execution);
        ReportProgress(execution, "Build tree", 0, 0, $"Included {treeProgress.IncludedNodes:N0} entries ({treeProgress.IncludedDirectories:N0} dirs, {treeProgress.IncludedFiles:N0} files); visited {treeProgress.VisitedEntries:N0}");
        var outputPath = Path.GetFullPath(options.OutputImagePath);
        var parent = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var outputLength = snapshot.Partitions
            .Select(entry => entry.Offset + entry.Length)
            .Append(partition.Offset + partition.Length)
            .DefaultIfEmpty(partition.Offset + partition.Length)
            .Max();
        outputLength = Math.Max(outputLength, SectorSize);

        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        stream.SetLength(outputLength);

        var effectiveSerial = options.SerialNumber != 0
            ? options.SerialNumber
            : partition.SerialNumber;
        var stats = RebuildPartition(stream, snapshot, partition, rebuildEntries, nodes, effectiveSerial, execution);
        if (defaultCancellation != null)
        {
            Console.WriteLine();
        }

        Console.WriteLine("FATX rebuild complete.");
        Console.WriteLine($"  Snapshot:   {options.SnapshotPath}");
        Console.WriteLine($"  Partition:  {partition.Name} (offset 0x{partition.Offset:X}, length 0x{partition.Length:X})");
        Console.WriteLine($"  Output:     {outputPath}");
        Console.WriteLine($"  Deleted:    {(options.IncludeDeletedEntries ? "included" : "excluded")}");
        Console.WriteLine($"  Sources:    {(string.IsNullOrWhiteSpace(options.LiveFilesDirectory) && string.IsNullOrWhiteSpace(options.DeletedFilesDirectory) ? "metadata-only skeleton (no source folders)" : "snapshot + source folders")}");
        Console.WriteLine($"  Recreated:  {stats.DirectoriesWritten:N0} directories, {stats.FilesWritten:N0} files");
        Console.WriteLine($"  Skipped:    {stats.SkippedEntries:N0} entries not found in source folders");
        Console.WriteLine($"  FAT format: {(stats.IsFat16 ? "FAT16" : "FAT32")} ({stats.MaxUsableCluster:N0} usable clusters)");
        return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.Error.WriteLine("error: FATX rebuild canceled.");
            return Error;
        }
        finally
        {
            if (cancelHandler != null)
            {
                Console.CancelKeyPress -= cancelHandler;
            }

            defaultCancellation?.Dispose();
        }
    }

    private static RebuildStats RebuildPartition(
        FileStream stream,
        RebuildSnapshot snapshot,
        RebuildPartitionSnapshot partition,
        IReadOnlyList<RebuildSnapshotFileEntry> rebuildEntries,
        List<RebuildNode> rootEntries,
        uint serialNumber,
        RebuildExecutionOptions execution)
    {
        var layout = FatxLayout.Create(partition.Offset, partition.Length);
        var allocator = new ClusterAllocator(layout.MaxUsableCluster);

        var rootClusters = AllocateRootDirectoryClusters(rootEntries.Count, allocator);
        var allDirectories = new List<RebuildNode>();
        CollectDirectoryNodes(rootEntries, allDirectories);
        var allFiles = new List<RebuildNode>();
        CollectFileNodes(rootEntries, allFiles);

        var totalStages = Math.Max(1, 5 + allDirectories.Count + allFiles.Count);
        var completedStages = 0L;
        ReportProgress(execution, "Rebuild", completedStages, totalStages, "Preparing layout");

        foreach (var directory in allDirectories)
        {
            ObserveExecution(execution);
            var requiredClusters = Math.Max(1, (int)Math.Ceiling(directory.Children.Count / (double)DirentsPerCluster));
            ReportProgress(execution, "Allocate directories", completedStages, totalStages, $"{directory.RelativePath} ({requiredClusters:N0} cluster(s))");
            directory.Clusters = TryAllocatePreferredContiguous(directory.FirstClusterHint, requiredClusters, allocator)
                ?? allocator.AllocateContiguous(requiredClusters);
            completedStages++;
            ReportProgress(execution, "Allocate directories", completedStages, totalStages, directory.RelativePath);
        }

        foreach (var file in allFiles)
        {
            ObserveExecution(execution);
            if (file.Size <= 0)
            {
                completedStages++;
                ReportProgress(execution, "Allocate files", completedStages, totalStages, file.RelativePath);
                continue;
            }

            var clustersNeeded = (int)Math.Ceiling(file.Size / (double)layout.BytesPerCluster);
            ReportProgress(execution, "Allocate files", completedStages, totalStages, $"{file.RelativePath} ({FormatBytes(file.Size)}, {clustersNeeded:N0} cluster(s))");
            file.Clusters = AllocateFileClusters(file, clustersNeeded, allocator, layout.MaxUsableCluster);
            completedStages++;
            ReportProgress(execution, "Allocate files", completedStages, totalStages, file.RelativePath);
        }

        ObserveExecution(execution);
        FillPartitionRegion(stream, layout.PartitionOffset, partition.Length, 0xFF, execution);
        completedStages++;
        ReportProgress(execution, "Initialize partition", completedStages, totalStages, "Filled partition with 0xFF");

        ObserveExecution(execution);
        WriteDevkitHeader(stream, snapshot.Partitions);
        completedStages++;
        ReportProgress(execution, "Write headers", completedStages, totalStages, "Devkit header");

        ObserveExecution(execution);
        WriteFatxHeader(stream, layout, serialNumber);
        completedStages++;
        ReportProgress(execution, "Write headers", completedStages, totalStages, "FATX header");

        WriteFat(stream, layout, rootClusters, rootEntries, execution);
        completedStages++;
        ReportProgress(execution, "Write FAT", completedStages, totalStages, "FAT table");

        WriteDirectoryStream(stream, layout, rootClusters, rootEntries, execution);
        completedStages++;
        ReportProgress(execution, "Write directories", completedStages, totalStages, "Root directory");
        foreach (var directory in allDirectories)
        {
            WriteDirectoryStream(stream, layout, directory.Clusters, directory.Children, execution);
            completedStages++;
            ReportProgress(execution, "Write directories", completedStages, totalStages, directory.RelativePath);
        }

        var payloadWorkers = execution.PayloadWorkerCount > 0
            ? execution.PayloadWorkerCount
            : Math.Max(1, Environment.ProcessorCount - 1);
        if (payloadWorkers <= 1 || allFiles.Count <= 1)
        {
            foreach (var file in allFiles)
            {
                WriteFilePayload(stream.SafeFileHandle, layout, file, execution);
                completedStages++;
                ReportProgress(execution, "Write payloads", completedStages, totalStages, file.RelativePath);
            }
        }
        else
        {
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = execution.CancellationToken,
                MaxDegreeOfParallelism = payloadWorkers
            };

            Parallel.ForEach(allFiles, parallelOptions, file =>
            {
                ObserveExecution(execution);
                WriteFilePayload(stream.SafeFileHandle, layout, file, execution);
                var current = Interlocked.Increment(ref completedStages);
                ReportProgress(execution, "Write payloads", current, totalStages, file.RelativePath);
            });
        }

        var totalDirs = allDirectories.Count + 1; // include root directory stream
        ReportProgress(execution, "Complete", totalStages, totalStages, "Image rebuild complete");
        return new RebuildStats
        {
            DirectoriesWritten = totalDirs,
            FilesWritten = allFiles.Count(file => file.Clusters.Count > 0 || file.Size == 0),
            SkippedEntries = CountSkippedEntries(rebuildEntries, rootEntries),
            IsFat16 = layout.IsFat16,
            MaxUsableCluster = layout.MaxUsableCluster
        };
    }

    private static int CountSkippedEntries(IReadOnlyList<RebuildSnapshotFileEntry> originalEntries, IReadOnlyList<RebuildNode> includedEntries)
    {
        static int CountIncluded(IReadOnlyList<RebuildNode> entries)
        {
            var total = 0;
            foreach (var entry in entries)
            {
                total++;
                total += CountIncluded(entry.Children);
            }

            return total;
        }

        return Math.Max(0, CountSnapshotEntriesRecursive(originalEntries) - CountIncluded(includedEntries));
    }

    private static int CountSnapshotEntriesRecursive(IReadOnlyList<RebuildSnapshotFileEntry> entries)
    {
        var total = 0;
        foreach (var entry in entries)
        {
            total++;
            total += CountSnapshotEntriesRecursive(entry.Children);
        }

        return total;
    }

    private static List<uint> AllocateRootDirectoryClusters(int rootEntryCount, ClusterAllocator allocator)
    {
        var requiredClusters = Math.Max(1, (int)Math.Ceiling(rootEntryCount / (double)DirentsPerCluster));
        allocator.ReserveSpecific([RootCluster]);
        if (requiredClusters == 1)
        {
            return [RootCluster];
        }

        var extras = allocator.AllocateContiguous(requiredClusters - 1);
        var clusters = new List<uint>(requiredClusters) { RootCluster };
        clusters.AddRange(extras);
        return clusters;
    }

    private static List<uint> AllocateFileClusters(RebuildNode file, int clustersNeeded, ClusterAllocator allocator, uint maxUsableCluster)
    {
        if (clustersNeeded <= 0)
        {
            return [];
        }

        var extents = TryParseClusterExtents(file.Extents, maxUsableCluster);
        if (extents.Count >= clustersNeeded)
        {
            var candidate = extents.Take(clustersNeeded).ToList();
            if (allocator.ReserveSpecific(candidate))
            {
                return candidate;
            }
        }

        var preferred = TryAllocatePreferredContiguous(file.FirstClusterHint, clustersNeeded, allocator);
        if (preferred != null)
        {
            return preferred;
        }

        return allocator.AllocateAny(clustersNeeded);
    }

    private static List<uint>? TryAllocatePreferredContiguous(uint preferredFirstCluster, int count, ClusterAllocator allocator)
    {
        if (preferredFirstCluster == 0 || count <= 0)
        {
            return null;
        }

        return allocator.TryAllocateContiguous(preferredFirstCluster, count, out var clusters)
            ? clusters
            : null;
    }

    private static List<uint> TryParseClusterExtents(string extents, uint maxUsableCluster)
    {
        if (string.IsNullOrWhiteSpace(extents))
        {
            return [];
        }

        var clean = extents;
        var suffixIndex = clean.IndexOf("(+", StringComparison.Ordinal);
        if (suffixIndex >= 0)
        {
            clean = clean[..suffixIndex];
        }

        var results = new List<uint>();
        foreach (var token in clean.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Contains('-', StringComparison.Ordinal))
            {
                var parts = token.Split('-', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2 ||
                    !uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) ||
                    !uint.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var end) ||
                    start == 0 ||
                    end < start)
                {
                    continue;
                }

                for (var cluster = start; cluster <= end && cluster <= maxUsableCluster; cluster++)
                {
                    results.Add(cluster);
                }
            }
            else if (uint.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var single) &&
                     single > 0 &&
                     single <= maxUsableCluster)
            {
                results.Add(single);
            }
        }

        return results.Distinct().ToList();
    }

    private static void WriteFat(FileStream stream, FatxLayout layout, IReadOnlyList<uint> rootClusters, IReadOnlyList<RebuildNode> rootEntries, RebuildExecutionOptions execution)
    {
        var fat = new uint[layout.MaxClusters];
        fat[0] = layout.EndOfChain;
        SetFatChain(fat, rootClusters, layout.EndOfChain);

        foreach (var entry in WalkNodes(rootEntries))
        {
            ObserveExecution(execution);
            if (entry.Clusters.Count == 0)
            {
                continue;
            }

            if (entry.IsDirectory || !entry.IsDeleted)
            {
                SetFatChain(fat, entry.Clusters, layout.EndOfChain);
            }
        }

        stream.Position = layout.PartitionOffset + Constants.ReservedBytes;
        if (layout.IsFat16)
        {
            Span<byte> raw = stackalloc byte[2];
            for (var index = 0; index < fat.Length; index++)
            {
                if ((index & 0xFFF) == 0)
                {
                    ObserveExecution(execution);
                }

                var entry = fat[index];
                BinaryPrimitives.WriteUInt16BigEndian(raw, (ushort)Math.Min(entry, Constants.Cluster16Last));
                stream.Write(raw);
            }
        }
        else
        {
            Span<byte> raw = stackalloc byte[4];
            for (var index = 0; index < fat.Length; index++)
            {
                if ((index & 0xFFF) == 0)
                {
                    ObserveExecution(execution);
                }

                var entry = fat[index];
                BinaryPrimitives.WriteUInt32BigEndian(raw, entry);
                stream.Write(raw);
            }
        }
    }

    private static void SetFatChain(uint[] fat, IReadOnlyList<uint> clusters, uint endOfChain)
    {
        if (clusters.Count == 0)
        {
            return;
        }

        for (var index = 0; index < clusters.Count; index++)
        {
            var cluster = clusters[index];
            if (cluster >= fat.Length)
            {
                continue;
            }

            fat[cluster] = index + 1 < clusters.Count ? clusters[index + 1] : endOfChain;
        }
    }

    private static void WriteDirectoryStream(FileStream stream, FatxLayout layout, IReadOnlyList<uint> clusters, IReadOnlyList<RebuildNode> entries, RebuildExecutionOptions execution)
    {
        if (clusters.Count == 0)
        {
            return;
        }

        var clusterBuffers = new byte[clusters.Count][];
        for (var index = 0; index < clusters.Count; index++)
        {
            ObserveExecution(execution);
            clusterBuffers[index] = new byte[layout.BytesPerCluster];
            Array.Fill(clusterBuffers[index], (byte)0xFF);
        }

        for (var entryIndex = 0; entryIndex < entries.Count; entryIndex++)
        {
            ObserveExecution(execution);
            var clusterIndex = entryIndex / DirentsPerCluster;
            var slot = entryIndex % DirentsPerCluster;
            if (clusterIndex >= clusterBuffers.Length)
            {
                break;
            }

            var dirent = CreateDirent(entries[entryIndex]);
            Buffer.BlockCopy(dirent, 0, clusterBuffers[clusterIndex], slot * DirentSize, DirentSize);
        }

        for (var index = 0; index < clusters.Count; index++)
        {
            ObserveExecution(execution);
            var offset = layout.ClusterToPhysicalOffset(clusters[index]);
            stream.Position = offset;
            stream.Write(clusterBuffers[index], 0, clusterBuffers[index].Length);
        }
    }

    private static byte[] CreateDirent(RebuildNode entry)
    {
        var dirent = new byte[DirentSize];
        var nameBytes = Encoding.ASCII.GetBytes(SanitizeName(entry.Name));
        var writeLength = Math.Min(nameBytes.Length, 42);

        dirent[0] = entry.IsDeleted ? (byte)Constants.DirentDeleted : (byte)writeLength;
        dirent[1] = GetAttributes(entry);
        Buffer.BlockCopy(nameBytes, 0, dirent, 2, writeLength);
        if (entry.IsDeleted)
        {
            for (var index = 2 + writeLength; index < 2 + 42; index++)
            {
                dirent[index] = 0xFF;
            }
        }

        var firstCluster = entry.Clusters.Count > 0 ? entry.Clusters[0] : entry.FirstClusterHint;
        var fileSize = entry.IsDirectory ? 0u : (uint)Math.Clamp(entry.Size, 0, uint.MaxValue);

        BinaryPrimitives.WriteUInt32BigEndian(dirent.AsSpan(0x2C, 4), firstCluster);
        BinaryPrimitives.WriteUInt32BigEndian(dirent.AsSpan(0x30, 4), fileSize);
        BinaryPrimitives.WriteUInt32BigEndian(dirent.AsSpan(0x34, 4), EncodeFatxTimestamp(entry.Created));
        BinaryPrimitives.WriteUInt32BigEndian(dirent.AsSpan(0x38, 4), EncodeFatxTimestamp(entry.Modified));
        BinaryPrimitives.WriteUInt32BigEndian(dirent.AsSpan(0x3C, 4), EncodeFatxTimestamp(entry.Accessed));
        return dirent;
    }

    private static byte GetAttributes(RebuildNode entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Attributes))
        {
            var text = entry.Attributes.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                byte.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexValue))
            {
                return hexValue;
            }

            var parsedFlags = 0;
            foreach (var token in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Enum.TryParse<FileAttribute>(token, ignoreCase: true, out var parsed))
                {
                    parsedFlags |= (int)parsed;
                }
            }

            if (parsedFlags > 0)
            {
                if (entry.IsDirectory)
                {
                    parsedFlags |= (int)FileAttribute.Directory;
                }

                return (byte)parsedFlags;
            }
        }

        return entry.IsDirectory ? (byte)FileAttribute.Directory : (byte)FileAttribute.Archive;
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unnamed";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (character == '/' || character == '\\' || invalid.Contains(character))
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.Length == 0 ? "unnamed" : builder.ToString();
    }

    private static uint EncodeFatxTimestamp(DateTime value)
    {
        if (value == default)
        {
            value = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        }

        if (value.Kind == DateTimeKind.Utc)
        {
            value = value.ToLocalTime();
        }
        else if (value.Kind == DateTimeKind.Unspecified)
        {
            value = DateTime.SpecifyKind(value, DateTimeKind.Local);
        }

        value = value < new DateTime(1980, 1, 1) ? new DateTime(1980, 1, 1) : value;
        value = value > new DateTime(2107, 12, 31, 23, 59, 58) ? new DateTime(2107, 12, 31, 23, 59, 58) : value;

        var year = value.Year - 1980;
        var second = value.Second / 2;
        return (uint)((year << 25) | (value.Month << 21) | (value.Day << 16) | (value.Hour << 11) | (value.Minute << 5) | second);
    }

    private static void WriteFilePayload(SafeFileHandle outputHandle, FatxLayout layout, RebuildNode file, RebuildExecutionOptions execution)
    {
        ObserveExecution(execution);
        if (file.Clusters.Count == 0 || string.IsNullOrWhiteSpace(file.SourcePath) || !File.Exists(file.SourcePath))
        {
            return;
        }

        using var input = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var targetSize = file.Size > 0 ? file.Size : input.Length;
        if (targetSize <= 0)
        {
            return;
        }

        var remaining = targetSize;
        var buffer = new byte[layout.BytesPerCluster];
        foreach (var cluster in file.Clusters)
        {
            ObserveExecution(execution);
            if (remaining <= 0)
            {
                break;
            }

            Array.Clear(buffer, 0, buffer.Length);
            var bytesToCopy = (int)Math.Min(buffer.Length, remaining);
            var offset = 0;
            while (offset < bytesToCopy)
            {
                var read = input.Read(buffer, offset, bytesToCopy - offset);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            var destinationOffset = layout.ClusterToPhysicalOffset(cluster);
            RandomAccess.Write(outputHandle, buffer.AsSpan(0, buffer.Length), destinationOffset);
            remaining -= bytesToCopy;
        }
    }

    private static void ObserveExecution(RebuildExecutionOptions execution)
    {
        while (execution.IsPaused?.Invoke() == true)
        {
            execution.CancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(125);
        }

        execution.CancellationToken.ThrowIfCancellationRequested();
    }

    private static void ReportProgress(
        RebuildExecutionOptions execution,
        string stage,
        long completed,
        long total,
        string detail)
    {
        execution.Progress?.Invoke(new RebuildProgressSnapshot(stage, completed, total, detail));
    }

    private static void WriteFatxHeader(FileStream stream, FatxLayout layout, uint serialNumber)
    {
        Span<byte> header = stackalloc byte[0x10];
        XtafHeaderSignature.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(4, 4), serialNumber);
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(8, 4), SectorsPerCluster);
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(12, 4), RootCluster);
        stream.Position = layout.PartitionOffset;
        stream.Write(header);
    }

    private static void FillPartitionRegion(FileStream stream, long offset, long length, byte value, RebuildExecutionOptions execution)
    {
        if (length <= 0)
        {
            return;
        }

        stream.Position = offset;
        var buffer = new byte[16 * 1024 * 1024];
        Array.Fill(buffer, value);
        var remaining = length;
        var written = 0L;
        var lastReported = 0L;
        ReportProgress(execution, "Initialize partition", 0, length, $"Filling partition with 0x{value:X2}: {FormatBytes(0)} / {FormatBytes(length)}");
        while (remaining > 0)
        {
            ObserveExecution(execution);
            var chunk = (int)Math.Min(buffer.Length, remaining);
            stream.Write(buffer, 0, chunk);
            remaining -= chunk;
            written += chunk;
            if (written == length || written - lastReported >= 256L * 1024 * 1024)
            {
                lastReported = written;
                ReportProgress(execution, "Initialize partition", written, length, $"Filling partition with 0x{value:X2}: {FormatBytes(written)} / {FormatBytes(length)}");
            }
        }
    }

    private static void WriteDevkitHeader(FileStream stream, IReadOnlyList<RebuildPartitionSnapshot> partitions)
    {
        var header = new byte[SectorSize];
        header[0] = 0x00;
        header[1] = 0x02;
        header[2] = 0x00;
        header[3] = 0x00;
        header[4] = 0x53;
        header[5] = 0x02;
        header[6] = 0x00;
        header[7] = 0x00;

        var indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Partition1"] = 0,
            ["SystemPartition"] = 1,
            ["DumpPartition"] = 3,
            ["PixDumpPartition"] = 4,
            ["AltFlash"] = 7,
            ["Cache0"] = 8,
            ["Cache1"] = 9
        };

        var usedIndices = new HashSet<int>();
        foreach (var partition in partitions)
        {
            var index = indexByName.TryGetValue(partition.Name, out var mapped)
                ? mapped
                : Enumerable.Range(0, 10).FirstOrDefault(candidate => !usedIndices.Contains(candidate));
            if (index < 0 || index > 9 || usedIndices.Contains(index))
            {
                continue;
            }

            usedIndices.Add(index);
            var offsetSectors = partition.Offset / SectorSize;
            var lengthSectors = partition.Length / SectorSize;
            if (offsetSectors <= 0 || lengthSectors <= 0 || offsetSectors > uint.MaxValue || lengthSectors > uint.MaxValue)
            {
                continue;
            }

            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8 + (index * 8), 4), (uint)offsetSectors);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12 + (index * 8), 4), (uint)lengthSectors);
        }

        stream.Position = 0;
        stream.Write(header, 0, header.Length);
    }

    private static IEnumerable<RebuildNode> WalkNodes(IEnumerable<RebuildNode> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
            foreach (var child in WalkNodes(entry.Children))
            {
                yield return child;
            }
        }
    }

    private static void CollectDirectoryNodes(IEnumerable<RebuildNode> entries, List<RebuildNode> target)
    {
        foreach (var entry in entries)
        {
            if (!entry.IsDirectory)
            {
                continue;
            }

            target.Add(entry);
            CollectDirectoryNodes(entry.Children, target);
        }
    }

    private static void CollectFileNodes(IEnumerable<RebuildNode> entries, List<RebuildNode> target)
    {
        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                CollectFileNodes(entry.Children, target);
                continue;
            }

            target.Add(entry);
        }
    }

    private static List<RebuildNode> BuildIncludedTree(
        IReadOnlyList<RebuildSnapshotFileEntry> entries,
        string parentPath,
        string partitionName,
        SourceIndex sourceIndex,
        bool includeDeletedEntries,
        RebuildExecutionOptions execution,
        RebuildTreeBuildProgress progress)
    {
        var results = new List<RebuildNode>();
        foreach (var entry in entries)
        {
            var node = BuildNode(entry, parentPath, partitionName, sourceIndex, includeDeletedEntries, execution, progress);
            if (node != null)
            {
                results.Add(node);
            }
        }

        return results;
    }

    private static RebuildNode? BuildNode(
        RebuildSnapshotFileEntry entry,
        string parentPath,
        string partitionName,
        SourceIndex sourceIndex,
        bool includeDeletedEntries,
        RebuildExecutionOptions execution,
        RebuildTreeBuildProgress progress)
    {
        var relativePath = ResolveRelativePath(entry, parentPath, partitionName);
        progress.ObserveVisited(relativePath, execution);

        if (!includeDeletedEntries && entry.IsDeleted)
        {
            return null;
        }

        var children = BuildIncludedTree(entry.Children, relativePath, partitionName, sourceIndex, includeDeletedEntries, execution, progress);

        var isDirectory = entry.IsDirectory || string.Equals(entry.Kind, "Folder", StringComparison.OrdinalIgnoreCase);
        if (isDirectory)
        {
            var exists = sourceIndex.HasDirectory(relativePath, out var canonicalDirectoryPath);
            if (!exists && children.Count == 0)
            {
                return null;
            }

            var directoryDedupKey = !string.IsNullOrWhiteSpace(canonicalDirectoryPath)
                ? $"D:{canonicalDirectoryPath}"
                : $"D:{relativePath}";
            if (!progress.TryIncludePath(directoryDedupKey))
            {
                return null;
            }

            var directory = new RebuildNode
            {
                Name = entry.Name,
                RelativePath = relativePath,
                IsDirectory = true,
                IsDeleted = false,
                Size = -1,
                FirstClusterHint = entry.FirstCluster,
                Attributes = entry.Attributes,
                Extents = entry.Extents,
                Created = entry.Created,
                Modified = entry.Modified,
                Accessed = entry.Accessed,
                Children = children
            };
            progress.ObserveIncluded(directory, execution);
            return directory;
        }

        var filePath = sourceIndex.FindFile(relativePath, entry.IsDeleted, out var canonicalFilePath);
        if (filePath == null)
        {
            return null;
        }

        var fileDedupKey = !string.IsNullOrWhiteSpace(canonicalFilePath)
            ? $"F:{canonicalFilePath}"
            : $"F:{relativePath}";
        if (!progress.TryIncludePath(fileDedupKey))
        {
            return null;
        }

        var normalizedName = string.IsNullOrWhiteSpace(entry.Name)
            ? Path.GetFileName(relativePath)
            : entry.Name;
        var sourceLength = new FileInfo(filePath).Length;

        var file = new RebuildNode
        {
            Name = normalizedName,
            RelativePath = relativePath,
            IsDirectory = false,
            IsDeleted = false,
            Size = sourceLength,
            FirstClusterHint = entry.FirstCluster,
            Attributes = entry.Attributes,
            Extents = entry.Extents,
            Created = entry.Created,
            Modified = entry.Modified,
            Accessed = entry.Accessed,
            SourcePath = filePath,
            Children = []
        };
        progress.ObserveIncluded(file, execution);
        return file;
    }

    private static void AddUnmatchedSourceFiles(
        List<RebuildNode> rootEntries,
        SourceIndex sourceIndex,
        RebuildTreeBuildProgress progress,
        RebuildExecutionOptions execution)
    {
        AddUnmatchedSourceFiles(rootEntries, sourceIndex.LiveFiles, progress, execution, "live source fallback");
        AddUnmatchedSourceFiles(rootEntries, sourceIndex.DeletedFiles, progress, execution, "recovered source fallback");
    }

    private static void AddUnmatchedSourceFiles(
        List<RebuildNode> rootEntries,
        IEnumerable<SourceFileRecord> sourceFiles,
        RebuildTreeBuildProgress progress,
        RebuildExecutionOptions execution,
        string label)
    {
        foreach (var source in sourceFiles)
        {
            if (!progress.TryIncludePath($"F:{source.RelativePath}"))
            {
                continue;
            }

            var imagePath = NormalizePath(source.RelativePath);
            if (!imagePath.StartsWith("DEVKIT/", StringComparison.OrdinalIgnoreCase))
            {
                imagePath = $"DEVKIT/{imagePath}";
            }

            var fileInfo = new FileInfo(source.SourcePath);
            var node = AddSourceFileNode(rootEntries, imagePath, source.SourcePath, fileInfo);
            progress.ObserveIncluded(node, execution);
            if (progress.IncludedFiles % 250 == 0)
            {
                ReportProgress(execution, "Build tree", 0, 0, $"{label}: added {progress.IncludedFiles:N0} files; latest {ShortenProgressPath(imagePath)}");
            }
        }
    }

    private static RebuildNode AddSourceFileNode(
        List<RebuildNode> rootEntries,
        string imagePath,
        string sourcePath,
        FileInfo fileInfo)
    {
        var parts = NormalizePath(imagePath).Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new InvalidDataException($"Source file path '{imagePath}' did not contain a file name.");
        }

        var currentChildren = rootEntries;
        var currentPath = string.Empty;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            var name = parts[index];
            currentPath = string.IsNullOrWhiteSpace(currentPath) ? name : $"{currentPath}/{name}";
            var existing = currentChildren.FirstOrDefault(entry => entry.IsDirectory && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new RebuildNode
                {
                    Name = name,
                    RelativePath = currentPath,
                    IsDirectory = true,
                    IsDeleted = false,
                    Size = -1,
                    FirstClusterHint = 0,
                    Attributes = "Directory",
                    Created = fileInfo.CreationTime,
                    Modified = fileInfo.LastWriteTime,
                    Accessed = fileInfo.LastAccessTime,
                    Children = []
                };
                currentChildren.Add(existing);
            }

            currentChildren = existing.Children;
        }

        var fileName = parts[^1];
        var filePath = string.Join('/', parts);
        var file = new RebuildNode
        {
            Name = fileName,
            RelativePath = filePath,
            IsDirectory = false,
            IsDeleted = false,
            Size = fileInfo.Length,
            FirstClusterHint = 0,
            Attributes = "Archive",
            Created = fileInfo.CreationTime,
            Modified = fileInfo.LastWriteTime,
            Accessed = fileInfo.LastAccessTime,
            SourcePath = sourcePath,
            Children = []
        };
        currentChildren.Add(file);
        return file;
    }

    private static string ResolveRelativePath(RebuildSnapshotFileEntry entry, string parentPath, string partitionName)
    {
        var path = NormalizePath(entry.Path);
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (path.StartsWith(partitionName + "/", StringComparison.OrdinalIgnoreCase))
            {
                path = path[(partitionName.Length + 1)..];
            }

            if (!path.StartsWith('/'))
            {
                return path;
            }
        }

        var name = SanitizePathSegment(entry.Name);
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            return name;
        }

        return string.IsNullOrWhiteSpace(name) ? parentPath : $"{parentPath}/{name}";
    }

    private static string SanitizePathSegment(string value)
    {
        value = value.Replace('\\', '/').Trim('/');
        if (value.Contains('/'))
        {
            value = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? value;
        }

        return value;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        path = path.Replace('\\', '/').Trim();
        path = path.TrimStart('/');
        return path.TrimEnd('/');
    }

    private static string ShortenProgressPath(string path, int maxLength = 96)
    {
        path = NormalizePath(path);
        if (path.Length <= maxLength)
        {
            return string.IsNullOrWhiteSpace(path) ? "<root>" : path;
        }

        var keep = Math.Max(1, maxLength - 3);
        return "..." + path[^keep..];
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit + 1 < units.Length)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private static RebuildPartitionSnapshot SelectPartition(RebuildSnapshot snapshot, string? selector)
    {
        var fatxPartitions = snapshot.Partitions
            .Where(entry =>
                string.Equals(entry.Family, "FATX", StringComparison.OrdinalIgnoreCase) ||
                entry.Family.Contains("FATX", StringComparison.OrdinalIgnoreCase) ||
                entry.Name.Contains("Partition", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (fatxPartitions.Count == 0)
        {
            throw new InvalidDataException("No FATX-like partitions were found in the snapshot.");
        }

        if (!string.IsNullOrWhiteSpace(selector))
        {
            if (int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                if (index < 0 || index >= fatxPartitions.Count)
                {
                    throw new InvalidDataException($"Partition index {index} is out of range.");
                }

                return fatxPartitions[index];
            }

            var byName = fatxPartitions.FirstOrDefault(entry => string.Equals(entry.Name, selector, StringComparison.OrdinalIgnoreCase));
            if (byName != null)
            {
                return byName;
            }

            throw new InvalidDataException($"Partition '{selector}' was not found.");
        }

        if (fatxPartitions.Count == 1)
        {
            return fatxPartitions[0];
        }

        var first = fatxPartitions[0];
        Console.WriteLine($"Multiple FATX partitions found; defaulting to first partition: {first.Name} (offset 0x{first.Offset:X}, length 0x{first.Length:X}).");
        return first;
    }

    private static RebuildSnapshot LoadSnapshot(string path, RebuildExecutionOptions execution)
    {
        var snapshotBytes = new FileInfo(path).Length;
        ReportProgress(execution, "Load snapshot", 0, 0, $"Reading {Path.GetFileName(path)} ({FormatBytes(snapshotBytes)})");
        var json = File.ReadAllText(path);
        ReportProgress(execution, "Load snapshot", 0, 0, $"Parsing JSON ({FormatBytes(snapshotBytes)})");

        if (LooksLikeLegacySnapshot(json))
        {
            ReportProgress(execution, "Load snapshot", 0, 0, "Detected legacy FATXTools JSON; converting directly");
            var legacy = TryConvertLegacySnapshot(json, execution);
            if (legacy != null && legacy.Partitions.Count > 0)
            {
                ReportProgress(execution, "Load snapshot", 0, 0, $"Loaded legacy snapshot with {legacy.Partitions.Count:N0} partition(s)");
                return legacy;
            }
        }

        var snapshot = JsonSerializer.Deserialize<RebuildSnapshot>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        if (snapshot != null && snapshot.Partitions.Count > 0)
        {
            ReportProgress(execution, "Load snapshot", 0, 0, $"Loaded {snapshot.Partitions.Count:N0} partition(s)");
            return snapshot;
        }

        ReportProgress(execution, "Load snapshot", 0, 0, "Converting legacy FATXTools JSON");
        var legacyFallback = TryConvertLegacySnapshot(json, execution);
        if (legacyFallback != null && legacyFallback.Partitions.Count > 0)
        {
            ReportProgress(execution, "Load snapshot", 0, 0, $"Loaded legacy snapshot with {legacyFallback.Partitions.Count:N0} partition(s)");
            return legacyFallback;
        }

        throw new InvalidDataException("Snapshot JSON did not contain any partitions.");
    }

    private static bool LooksLikeLegacySnapshot(string json)
    {
        var probeLength = Math.Min(json.Length, 4096);
        var probe = json.AsSpan(0, probeLength);
        return probe.Contains("\"Drive\"", StringComparison.OrdinalIgnoreCase) &&
            probe.Contains("\"Partitions\"", StringComparison.OrdinalIgnoreCase);
    }

    private static RebuildSnapshot? TryConvertLegacySnapshot(string json, RebuildExecutionOptions execution)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Drive", out var drive) ||
            !drive.TryGetProperty("Partitions", out var partitionsElement) ||
            partitionsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var snapshot = new RebuildSnapshot
        {
            Version = 1,
            Application = "FATXTools legacy",
            SavedAtUtc = DateTime.UtcNow,
            SourceImage = GetJsonString(drive, "FileName")
        };

        var conversionProgress = new LegacySnapshotConversionProgress();
        foreach (var partitionElement in partitionsElement.EnumerateArray())
        {
            var partition = new RebuildPartitionSnapshot
            {
                Name = GetJsonString(partitionElement, "Name"),
                Offset = GetJsonInt64(partitionElement, "Offset"),
                Length = GetJsonInt64(partitionElement, "Length"),
                SerialNumber = GetJsonUInt32(partitionElement, "SerialNumber"),
                Family = "FATX",
                Status = "Loaded from legacy FATXTools database",
                TotalSpace = GetJsonInt64(partitionElement, "Length")
            };

            if (partitionElement.TryGetProperty("Analysis", out var analysis) &&
                analysis.TryGetProperty("MetadataAnalyzer", out var metadata) &&
                metadata.ValueKind == JsonValueKind.Array)
            {
                partition.Analysis.MetadataAnalyzer = metadata
                    .EnumerateArray()
                    .Select(entry => ConvertLegacyDirectoryEntry(entry, partition.Name, conversionProgress, execution))
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                    .ToList();
            }

            snapshot.Partitions.Add(partition);
            ReportProgress(execution, "Convert legacy JSON", 0, 0, $"Converted partition {partition.Name}; {conversionProgress.EntriesConverted:N0} metadata entries");
        }

        return snapshot.Partitions.Count > 0 ? snapshot : null;
    }

    private static RebuildSnapshotFileEntry ConvertLegacyDirectoryEntry(
        JsonElement entry,
        string partitionName,
        LegacySnapshotConversionProgress progress,
        RebuildExecutionOptions execution,
        string parentPath = "")
    {
        var name = GetJsonString(entry, "FileName");
        var attributes = GetJsonInt32(entry, "FileAttributes");
        var isDirectory = (attributes & 0x10) != 0;
        var path = string.IsNullOrWhiteSpace(parentPath) ? name : $"{parentPath}/{name}";
        progress.Observe(path, execution);
        var snapshot = new RebuildSnapshotFileEntry
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
                .Select(child => ConvertLegacyDirectoryEntry(child, partitionName, progress, execution, path))
                .Where(child => !string.IsNullOrWhiteSpace(child.Name))
                .ToList();
        }

        return snapshot;
    }

    private static DateTime ReadLegacyFatxTimestamp(JsonElement entry, string propertyName)
    {
        var raw = GetJsonInt64(entry, propertyName);
        if (raw <= 0)
        {
            return new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        }

        try
        {
            return new X360TimeStamp((uint)raw).AsDateTime();
        }
        catch
        {
            return new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        }
    }

    private static string FormatLegacyClusters(JsonElement entry)
    {
        if (!entry.TryGetProperty("Clusters", out var clustersElement) || clustersElement.ValueKind != JsonValueKind.Array)
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

        if (clusters.Count == 0)
        {
            return string.Empty;
        }

        clusters.Sort();
        var ranges = new List<string>();
        var start = clusters[0];
        var previous = clusters[0];
        for (var index = 1; index < clusters.Count; index++)
        {
            var current = clusters[index];
            if (current == previous + 1)
            {
                previous = current;
                continue;
            }

            ranges.Add(start == previous ? start.ToString(CultureInfo.InvariantCulture) : $"{start}-{previous}");
            start = previous = current;
        }

        ranges.Add(start == previous ? start.ToString(CultureInfo.InvariantCulture) : $"{start}-{previous}");
        return string.Join(", ", ranges);
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
        var value = GetJsonInt64(element, propertyName);
        return (int)Math.Clamp(value, int.MinValue, int.MaxValue);
    }

    private static uint GetJsonUInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number))
        {
            return number;
        }

        var text = value.ToString().Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
            return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
                ? hex
                : 0;
        }

        return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static RebuildOptions PromptMissing(RebuildOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SnapshotPath))
        {
            options.SnapshotPath = Prompt("Snapshot JSON path");
        }

        if (string.IsNullOrWhiteSpace(options.OutputImagePath))
        {
            options.OutputImagePath = Prompt("Output .img path");
        }

        return options;
    }

    private static string Prompt(string label)
    {
        while (true)
        {
            Console.Write($"{label}: ");
            var value = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }
    }

    private sealed class RebuildStats
    {
        public int DirectoriesWritten { get; init; }

        public int FilesWritten { get; init; }

        public int SkippedEntries { get; init; }

        public bool IsFat16 { get; init; }

        public uint MaxUsableCluster { get; init; }
    }

    private sealed class RebuildTreeBuildProgress
    {
        public long VisitedEntries { get; private set; }

        public long IncludedNodes { get; private set; }

        public long IncludedDirectories { get; private set; }

        public long IncludedFiles { get; private set; }

        private readonly HashSet<string> _includedPaths = new(StringComparer.OrdinalIgnoreCase);

        public bool TryIncludePath(string path)
        {
            return _includedPaths.Add(NormalizePath(path));
        }

        public void ObserveVisited(string relativePath, RebuildExecutionOptions execution)
        {
            VisitedEntries++;
            if (VisitedEntries == 1 || VisitedEntries % 250 == 0)
            {
                ReportProgress(execution, "Build tree", 0, 0, $"Visited {VisitedEntries:N0} metadata entries; latest {ShortenProgressPath(relativePath)}");
            }
        }

        public void ObserveIncluded(RebuildNode node, RebuildExecutionOptions execution)
        {
            IncludedNodes++;
            if (node.IsDirectory)
            {
                IncludedDirectories++;
            }
            else
            {
                IncludedFiles++;
            }

            if (IncludedNodes == 1 || IncludedNodes % 250 == 0)
            {
                ReportProgress(
                    execution,
                    "Build tree",
                    0,
                    0,
                    $"Included {IncludedNodes:N0} entries ({IncludedDirectories:N0} dirs, {IncludedFiles:N0} files); latest {ShortenProgressPath(node.RelativePath)}");
            }
        }
    }

    private sealed class LegacySnapshotConversionProgress
    {
        public long EntriesConverted { get; private set; }

        public void Observe(string path, RebuildExecutionOptions execution)
        {
            EntriesConverted++;
            if (EntriesConverted == 1 || EntriesConverted % 250 == 0)
            {
                ReportProgress(execution, "Convert legacy JSON", 0, 0, $"Converted {EntriesConverted:N0} metadata entries; latest {ShortenProgressPath(path)}");
            }
        }
    }

    private sealed class RebuildNode
    {
        public string Name { get; init; } = string.Empty;

        public string RelativePath { get; init; } = string.Empty;

        public bool IsDirectory { get; init; }

        public bool IsDeleted { get; init; }

        public long Size { get; init; }

        public uint FirstClusterHint { get; init; }

        public string Attributes { get; init; } = string.Empty;

        public string Extents { get; init; } = string.Empty;

        public DateTime Created { get; init; }

        public DateTime Modified { get; init; }

        public DateTime Accessed { get; init; }

        public string? SourcePath { get; init; }

        public List<RebuildNode> Children { get; init; } = [];

        public List<uint> Clusters { get; set; } = [];
    }

    private sealed class SourceIndex
    {
        private readonly Dictionary<string, string> _liveFiles;
        private readonly Dictionary<string, string> _deletedFiles;
        private readonly HashSet<string> _liveDirectories;
        private readonly HashSet<string> _deletedDirectories;

        private SourceIndex(
            Dictionary<string, string> liveFiles,
            Dictionary<string, string> deletedFiles,
            HashSet<string> liveDirectories,
            HashSet<string> deletedDirectories)
        {
            _liveFiles = liveFiles;
            _deletedFiles = deletedFiles;
            _liveDirectories = liveDirectories;
            _deletedDirectories = deletedDirectories;
        }

        public static SourceIndex Build(string liveRoot, string deletedRoot, RebuildExecutionOptions execution)
        {
            SourceRootIndex liveIndex;
            SourceRootIndex deletedIndex;
            if (HasUsableRoot(liveRoot) && HasUsableRoot(deletedRoot))
            {
                SourceRootIndex? liveTaskResult = null;
                SourceRootIndex? deletedTaskResult = null;
                Parallel.Invoke(
                    () => liveTaskResult = BuildRootIndex(liveRoot, "live source", execution),
                    () => deletedTaskResult = BuildRootIndex(deletedRoot, "deleted source", execution));
                liveIndex = liveTaskResult ?? SourceRootIndex.Empty;
                deletedIndex = deletedTaskResult ?? SourceRootIndex.Empty;
            }
            else
            {
                liveIndex = BuildRootIndex(liveRoot, "live source", execution);
                deletedIndex = BuildRootIndex(deletedRoot, "deleted source", execution);
            }

            return new SourceIndex(
                liveIndex.Files,
                deletedIndex.Files,
                liveIndex.Directories,
                deletedIndex.Directories);
        }

        public IEnumerable<SourceFileRecord> LiveFiles => _liveFiles.Select(entry => new SourceFileRecord(entry.Key, entry.Value));

        public IEnumerable<SourceFileRecord> DeletedFiles => _deletedFiles.Select(entry => new SourceFileRecord(entry.Key, entry.Value));

        public bool HasDirectory(string relativePath, out string canonicalPath)
        {
            relativePath = NormalizePath(relativePath);
            canonicalPath = relativePath;
            if (string.IsNullOrEmpty(relativePath))
            {
                return true;
            }

            if (_liveDirectories.Contains(relativePath) || _deletedDirectories.Contains(relativePath))
            {
                return true;
            }

            if (TryWithoutPartitionPrefix(relativePath, _liveDirectories, out canonicalPath) ||
                TryWithoutPartitionPrefix(relativePath, _deletedDirectories, out canonicalPath))
            {
                return true;
            }

            canonicalPath = relativePath;
            return false;
        }

        public string? FindFile(string relativePath, bool deletedPreference, out string canonicalPath)
        {
            relativePath = NormalizePath(relativePath);
            canonicalPath = relativePath;
            if (deletedPreference)
            {
                if (TryFindFile(_deletedFiles, relativePath, out var deleted, out canonicalPath))
                {
                    return deleted;
                }

                if (TryFindFile(_liveFiles, relativePath, out var liveFallback, out canonicalPath))
                {
                    return liveFallback;
                }
            }
            else
            {
                if (TryFindFile(_liveFiles, relativePath, out var live, out canonicalPath))
                {
                    return live;
                }

                if (TryFindFile(_deletedFiles, relativePath, out var deletedFallback, out canonicalPath))
                {
                    return deletedFallback;
                }
            }

            return null;
        }

        private static bool HasUsableRoot(string root)
        {
            return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root);
        }

        private static SourceRootIndex BuildRootIndex(string root, string label, RebuildExecutionOptions execution)
        {
            if (!HasUsableRoot(root))
            {
                return SourceRootIndex.Empty;
            }

            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { string.Empty };
            ReportProgress(execution, "Index sources", 0, 0, $"Scanning {label}: {root}");
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                var relative = NormalizePath(Path.GetRelativePath(root, path));
                if (Directory.Exists(path))
                {
                    directories.Add(relative);
                }
                else if (!files.ContainsKey(relative))
                {
                    files.Add(relative, path);
                }

                var total = files.Count + directories.Count;
                if (total % 250 == 0)
                {
                    ReportProgress(execution, "Index sources", 0, 0, $"{label}: {files.Count:N0} files, {directories.Count:N0} dirs; latest {ShortenProgressPath(relative)}");
                }
            }

            ReportProgress(execution, "Index sources", 0, 0, $"{label}: indexed {files.Count:N0} files and {directories.Count:N0} directories");
            return new SourceRootIndex(files, directories);
        }

        private static bool TryFindFile(Dictionary<string, string> map, string relativePath, out string path, out string canonicalPath)
        {
            canonicalPath = relativePath;
            if (map.TryGetValue(relativePath, out path!))
            {
                return true;
            }

            var separator = relativePath.IndexOf('/');
            if (separator > 0)
            {
                var trimmed = relativePath[(separator + 1)..];
                if (map.TryGetValue(trimmed, out path!))
                {
                    canonicalPath = trimmed;
                    return true;
                }
            }

            return false;
        }

        private static bool TryWithoutPartitionPrefix(string relativePath, HashSet<string> set, out string trimmed)
        {
            trimmed = string.Empty;
            var separator = relativePath.IndexOf('/');
            if (separator <= 0)
            {
                return false;
            }

            trimmed = relativePath[(separator + 1)..];
            return set.Contains(trimmed);
        }
    }

    private sealed class ClusterAllocator
    {
        private readonly bool[] _used;
        private readonly uint _maxCluster;
        private uint _nextSearchCluster = 1;

        public ClusterAllocator(uint maxCluster)
        {
            _maxCluster = maxCluster;
            _used = new bool[maxCluster + 1];
            _used[0] = true;
        }

        public bool ReserveSpecific(IReadOnlyList<uint> clusters)
        {
            foreach (var cluster in clusters)
            {
                if (cluster == 0 || cluster > _maxCluster || _used[cluster])
                {
                    return false;
                }
            }

            foreach (var cluster in clusters)
            {
                _used[cluster] = true;
            }

            AdvanceNextSearchCluster();
            return true;
        }

        public bool TryAllocateContiguous(uint start, int count, out List<uint> clusters)
        {
            clusters = [];
            if (count <= 0 || start == 0)
            {
                return false;
            }

            var end = start + (uint)count - 1;
            if (end > _maxCluster)
            {
                return false;
            }

            for (var cluster = start; cluster <= end; cluster++)
            {
                if (_used[cluster])
                {
                    return false;
                }
            }

            clusters = new List<uint>(count);
            for (var cluster = start; cluster <= end; cluster++)
            {
                _used[cluster] = true;
                clusters.Add(cluster);
            }

            if (start <= _nextSearchCluster && end >= _nextSearchCluster)
            {
                _nextSearchCluster = end == uint.MaxValue ? end : end + 1;
                AdvanceNextSearchCluster();
            }

            return true;
        }

        public List<uint> AllocateContiguous(int count)
        {
            if (count <= 0)
            {
                return [];
            }

            for (uint cluster = _nextSearchCluster; cluster <= _maxCluster; cluster++)
            {
                if (!TryAllocateContiguous(cluster, count, out var allocated))
                {
                    continue;
                }

                return allocated;
            }

            for (uint cluster = 1; cluster < _nextSearchCluster; cluster++)
            {
                if (!TryAllocateContiguous(cluster, count, out var allocated))
                {
                    continue;
                }

                return allocated;
            }

            throw new IOException($"Not enough free clusters to allocate {count} cluster(s).");
        }

        public List<uint> AllocateAny(int count)
        {
            if (count <= 0)
            {
                return [];
            }

            var clusters = new List<uint>(count);
            CollectFreeClusters(_nextSearchCluster, _maxCluster, count, clusters);
            if (clusters.Count < count && _nextSearchCluster > 1)
            {
                CollectFreeClusters(1, _nextSearchCluster - 1, count, clusters);
            }

            if (clusters.Count < count)
            {
                throw new IOException($"Not enough free clusters to allocate {count} cluster(s).");
            }

            foreach (var cluster in clusters)
            {
                _used[cluster] = true;
            }

            AdvanceNextSearchCluster();
            return clusters;
        }

        private void CollectFreeClusters(uint start, uint end, int count, List<uint> clusters)
        {
            if (start == 0 || start > end)
            {
                return;
            }

            for (var cluster = start; cluster <= end && clusters.Count < count; cluster++)
            {
                if (!_used[cluster])
                {
                    clusters.Add(cluster);
                }
            }
        }

        private void AdvanceNextSearchCluster()
        {
            while (_nextSearchCluster <= _maxCluster && _used[_nextSearchCluster])
            {
                _nextSearchCluster++;
            }
        }
    }

    private sealed class FatxLayout
    {
        public required long PartitionOffset { get; init; }

        public required long PartitionLength { get; init; }

        public required int BytesPerCluster { get; init; }

        public required uint MaxClusters { get; init; }

        public required uint MaxUsableCluster { get; init; }

        public required bool IsFat16 { get; init; }

        public required long FatSizeBytes { get; init; }

        public required long FileAreaOffset { get; init; }

        public uint EndOfChain => IsFat16 ? Constants.Cluster16Last : Constants.ClusterLast;

        public static FatxLayout Create(long partitionOffset, long partitionLength)
        {
            var bytesPerCluster = checked((int)(SectorsPerCluster * SectorSize));
            var maxClusters = (uint)(partitionLength / bytesPerCluster) + Constants.ReservedClusters;
            var isFat16 = maxClusters < Constants.Cluster16Reserved;
            var bytesPerFatRaw = maxClusters * (isFat16 ? 2u : 4u);
            var bytesPerFatAligned = AlignUp(bytesPerFatRaw, Constants.PageSize);
            var fileAreaOffset = Constants.ReservedBytes + bytesPerFatAligned;
            var fileAreaLength = partitionLength - fileAreaOffset;
            if (fileAreaLength <= 0)
            {
                throw new InvalidDataException("Partition does not have enough space for FATX file area.");
            }

            var maxUsableCluster = (uint)(fileAreaLength / bytesPerCluster);
            if (maxUsableCluster < 1)
            {
                throw new InvalidDataException("Partition does not have any usable FATX clusters.");
            }

            return new FatxLayout
            {
                PartitionOffset = partitionOffset,
                PartitionLength = partitionLength,
                BytesPerCluster = bytesPerCluster,
                MaxClusters = maxClusters,
                MaxUsableCluster = maxUsableCluster,
                IsFat16 = isFat16,
                FatSizeBytes = bytesPerFatAligned,
                FileAreaOffset = fileAreaOffset
            };
        }

        public long ClusterToPhysicalOffset(uint cluster)
        {
            if (cluster == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cluster), "Cluster indexes start at 1.");
            }

            return PartitionOffset + FileAreaOffset + (long)(cluster - 1) * BytesPerCluster;
        }

        private static uint AlignUp(uint value, uint alignment)
        {
            return (value + (alignment - 1)) & ~(alignment - 1);
        }
    }

    private sealed class RebuildOptions
    {
        public string SnapshotPath { get; set; } = string.Empty;

        public string LiveFilesDirectory { get; set; } = string.Empty;

        public string DeletedFilesDirectory { get; set; } = string.Empty;

        public string OutputImagePath { get; set; } = string.Empty;

        public string? PartitionSelector { get; set; }

        public uint SerialNumber { get; set; }

        public bool IncludeDeletedEntries { get; set; } = true;

        public static RebuildOptions Parse(string[] args)
        {
            var options = new RebuildOptions();
            var positionals = new List<string>();
            for (var index = 0; index < args.Length; index++)
            {
                var arg = args[index];
                switch (arg)
                {
                    case "--output":
                    case "-o":
                        options.OutputImagePath = RequireValue(args, ref index, arg);
                        break;
                    case "--live-files":
                        options.LiveFilesDirectory = RequireValue(args, ref index, arg);
                        break;
                    case "--deleted-files":
                        options.DeletedFilesDirectory = RequireValue(args, ref index, arg);
                        break;
                    case "--partition":
                    case "-p":
                        options.PartitionSelector = RequireValue(args, ref index, arg);
                        break;
                    case "--serial":
                    case "-s":
                        var value = RequireValue(args, ref index, arg);
                        value = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
                        if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var serial))
                        {
                            throw new ArgumentException("--serial expects a 32-bit hex value.");
                        }

                        options.SerialNumber = serial;
                        break;
                    case "--include-deleted":
                        var includeDeletedText = RequireValue(args, ref index, arg);
                        if (!bool.TryParse(includeDeletedText, out var includeDeleted))
                        {
                            throw new ArgumentException("--include-deleted expects true or false.");
                        }

                        options.IncludeDeletedEntries = includeDeleted;
                        break;
                    case "--no-deleted":
                        options.IncludeDeletedEntries = false;
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

            if (positionals.Count > 0 && string.IsNullOrWhiteSpace(options.SnapshotPath))
            {
                options.SnapshotPath = positionals[0];
            }

            if (positionals.Count == 2 && string.IsNullOrWhiteSpace(options.OutputImagePath))
            {
                options.OutputImagePath = positionals[1];
            }
            else if (positionals.Count > 1 && string.IsNullOrWhiteSpace(options.LiveFilesDirectory))
            {
                options.LiveFilesDirectory = positionals[1];
            }

            if (positionals.Count > 2 && string.IsNullOrWhiteSpace(options.DeletedFilesDirectory))
            {
                options.DeletedFilesDirectory = positionals[2];
            }

            if (positionals.Count > 3 && string.IsNullOrWhiteSpace(options.OutputImagePath))
            {
                options.OutputImagePath = positionals[3];
            }

            return options;
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
    }

    private sealed record SourceRootIndex(Dictionary<string, string> Files, HashSet<string> Directories)
    {
        public static SourceRootIndex Empty { get; } = new(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { string.Empty });
    }

    private sealed record SourceFileRecord(string RelativePath, string SourcePath);
}

public sealed record RebuildProgressSnapshot(string Stage, long Completed, long Total, string Detail)
{
    public int Percent => Total <= 0
        ? 0
        : Math.Clamp((int)Math.Round(Completed * 100.0 / Total), 0, 100);
}

public sealed class RebuildExecutionOptions
{
    public CancellationToken CancellationToken { get; init; } = CancellationToken.None;

    public Func<bool>? IsPaused { get; init; }

    public Action<RebuildProgressSnapshot>? Progress { get; init; }

    public int PayloadWorkerCount { get; init; }
}

internal sealed class ProgressEtaEstimator
{
    private readonly Queue<(DateTime TimestampUtc, double Percent)> _samples = new();
    private DateTime _startedAtUtc = DateTime.UtcNow;
    private double _lastPercent = double.NaN;

    public string BuildStatus(double percent, string? stateText)
    {
        var now = DateTime.UtcNow;
        var clampedPercent = Math.Clamp(percent, 0d, 100d);
        if (double.IsNaN(_lastPercent) || clampedPercent + 0.001 < _lastPercent)
        {
            Reset(now, clampedPercent);
        }

        TrackSample(now, clampedPercent);

        var elapsed = now - _startedAtUtc;
        if (clampedPercent >= 100 || IsTerminal(stateText))
        {
            return $"Elapsed {FormatDuration(elapsed)}";
        }

        var eta = TryEstimateEta(clampedPercent);
        if (eta == null)
        {
            return $"ETA calculating, elapsed {FormatDuration(elapsed)}";
        }

        return $"ETA {FormatDuration(eta.Value)}, elapsed {FormatDuration(elapsed)}";
    }

    private void Reset(DateTime now, double percent)
    {
        _samples.Clear();
        _startedAtUtc = now;
        _lastPercent = percent;
    }

    private void TrackSample(DateTime now, double percent)
    {
        if (_samples.Count == 0 || percent > _lastPercent + 0.001)
        {
            _samples.Enqueue((now, percent));
        }
        else if (_samples.Count == 0)
        {
            _samples.Enqueue((now, percent));
        }

        _lastPercent = Math.Max(_lastPercent, percent);
        var cutoff = now - TimeSpan.FromSeconds(45);
        while (_samples.Count > 2 && _samples.Peek().TimestampUtc < cutoff)
        {
            _samples.Dequeue();
        }
    }

    private TimeSpan? TryEstimateEta(double percent)
    {
        if (percent <= 0.1 || _samples.Count < 2)
        {
            return null;
        }

        var first = _samples.Peek();
        var latest = _samples.Last();
        var deltaPercent = latest.Percent - first.Percent;
        var deltaSeconds = (latest.TimestampUtc - first.TimestampUtc).TotalSeconds;
        if (deltaPercent <= 0.01 || deltaSeconds <= 0.1)
        {
            return null;
        }

        var percentPerSecond = deltaPercent / deltaSeconds;
        if (percentPerSecond <= 0)
        {
            return null;
        }

        var remainingPercent = Math.Max(0, 100 - percent);
        return TimeSpan.FromSeconds(remainingPercent / percentPerSecond);
    }

    private static bool IsTerminal(string? stateText)
    {
        if (string.IsNullOrWhiteSpace(stateText))
        {
            return false;
        }

        return stateText.Contains("canceled", StringComparison.OrdinalIgnoreCase)
            || stateText.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || stateText.Contains("complete", StringComparison.OrdinalIgnoreCase)
            || stateText.Contains("done", StringComparison.OrdinalIgnoreCase)
            || stateText.Contains("ready", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }
}

internal sealed class RebuildSnapshot
{
    public int Version { get; set; }

    public string Application { get; set; } = string.Empty;

    public DateTime SavedAtUtc { get; set; }

    public string SourceImage { get; set; } = string.Empty;

    public string ActivePartitionName { get; set; } = string.Empty;

    public List<RebuildPartitionSnapshot> Partitions { get; set; } = [];
}

internal sealed class RebuildPartitionSnapshot
{
    public string Name { get; set; } = string.Empty;

    public long Offset { get; set; }

    public long Length { get; set; }

    public uint SerialNumber { get; set; }

    public string Family { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public long UsedSpace { get; set; }

    public long FreeSpace { get; set; }

    public long TotalSpace { get; set; }

    public List<RebuildSnapshotFileEntry> OriginalFilesystem { get; set; } = [];

    public RebuildPartitionAnalysisSnapshot Analysis { get; set; } = new();
}

internal sealed class RebuildPartitionAnalysisSnapshot
{
    public List<RebuildSnapshotFileEntry> MetadataAnalyzer { get; set; } = [];
}

internal sealed class RebuildSnapshotFileEntry
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

    public uint Cluster { get; set; }

    public bool IsDeleted { get; set; }

    public string Attributes { get; set; } = string.Empty;

    public uint FirstCluster { get; set; }

    public string Fragmentation { get; set; } = string.Empty;

    public string Extents { get; set; } = string.Empty;

    public string MetadataStatus { get; set; } = string.Empty;

    public List<RebuildSnapshotFileEntry> Children { get; set; } = [];
}
