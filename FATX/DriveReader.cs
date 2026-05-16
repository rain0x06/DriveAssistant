using FATX.FileSystem;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FATX
{
    public class DriveReader : EndianReader
    {
        private readonly List<Volume> _partitions = new List<Volume>();

        public DriveReader(Stream stream)
            : base(stream)
        {
        }

        public void Initialize(bool exhaustiveRawPartitionSearch = true)
        {
            Seek(0);

            // Check for memory unit image.
            if (ReadUInt64() == 0x534F44534D9058EB)
            {
                Console.WriteLine("Mounting Xbox 360 Memory Unit..");

                ByteOrder = ByteOrder.Big;
                AddPartition("Storage", 0x20E2A000, 0xCE1D0000);
                AddPartition("SystemExtPartition", 0x13FFA000, 0xCE30000);
                AddPartition("SystemURLCachePartition", 0xDFFA000, 0x6000000);
                AddPartition("TitleURLCachePartition", 0xBFFA000, 0x2000000);
                AddPartition("StorageSystem", 0x7FFA000, 0x4000000);
                return;
            }

            // Check for Original Xbox fixed-offset FATX partition map.
            if (HasOriginalXboxVolumeSignatureAt(0xABE80000) ||
                HasOriginalXboxVolumeSignatureAt(0x8CA80000))
            {
                Console.WriteLine("Mounting Xbox Original HDD..");

                AddOriginalXboxPartitionIfValid("E - Data", 0xABE80000, 0x1312D6000);
                AddOriginalXboxPartitionIfValid("C - System", 0x8CA80000, 0x1f400000);
                AddOriginalXboxPartitionIfValid("Z - Game Cache", 0x5DC80000, 0x2ee00000);
                AddOriginalXboxPartitionIfValid("Y - Game Cache", 0x2EE80000, 0x2ee00000);
                AddOriginalXboxPartitionIfValid("X - Game Cache", 0x80000, 0x2ee00000);
                AddOriginalXboxPartitionIfValid("F - Extended", 0x1DD156000L, Math.Min(Length, 0x1FFFFFFE00L) - 0x1DD156000L);
                AddOriginalXboxPartitionIfValid("G - Extended", 0x1FFFFFFE00L, Length - 0x1FFFFFFE00L);
                return;
            }

            // Check for Original XBOX DVT3 (Prototype Development Kit) partitions.
            Seek(0x80000);
            if (ReadUInt32() == Volume.VolumeSignature)
            {
                Console.WriteLine("Mounting Xbox DVT3 HDD (v2)..");

                AddPartition("Partition1", 0x80000, 0x1312D6000);
                AddPartition("Partition2", 0x131356000, 0x1f400000);
                AddPartition("Partition3", 0x150756000, 0x2ee00000);
                AddPartition("Partition4", 0x17F556000, 0x2ee00000);
                AddPartition("Partition5", 0x1AE356000, 0x2ee00000);
                return;
            }

            Seek(0x80004);
            if (ReadUInt32() == Volume.VolumeSignature)
            {
                Console.WriteLine("Mounting Xbox DVT3 HDD (v1)..");

                AddPartition("Partition1", 0x80000, 0x1312D6000, true);
                AddPartition("Partition2", 0x131356000, 0x1f400000, true);
                AddPartition("Partition3", 0x150756000, 0x2ee00000, true);
                AddPartition("Partition4", 0x17F556000, 0x2ee00000, true);
                AddPartition("Partition5", 0x1AE356000, 0x2ee00000, true);
                return;
            }

            // Check for Xbox 360 partitions.
            Seek(0);
            ByteOrder = ByteOrder.Big;
            if (ReadUInt32() == 0x20000)
            {
                Console.WriteLine("Mounting Xbox 360 Dev HDD..");
                AddXbox360DevkitHeaderPartitions(exhaustiveRawPartitionSearch);
            }
            else
            {
                Console.WriteLine("Mounting Xbox 360 Retail HDD..");

                bool looksLikeRetail = HasVolumeSignatureAt(0x130eb0000) || HasVolumeSignatureAt(0x120eb0000) ||
                                       HasVolumeSignatureAt(0x80000);

                if (looksLikeRetail)
                {
                    AddPartitionIfValid("Partition1", 0x130eb0000, Length - 0x130eb0000);
                    AddPartitionIfValid("SystemPartition", 0x120eb0000, 0x10000000);
                    AddPartitionIfValid("Cache0", 0x80000, 0x80000000);
                    AddPartitionIfValid("Cache1", 0x80080000, 0x80000000);

                    const long dumpPartitionOffset = 0x100080000;
                    AddPartitionIfValid("DumpPartition", 0x100080000, 0x20E30000);
                    AddPartitionIfValid("SystemURLCachePartition", dumpPartitionOffset + 0, 0x6000000);
                    AddPartitionIfValid("TitleURLCachePartition", dumpPartitionOffset + 0x6000000, 0x2000000);
                    AddPartitionIfValid("SystemExtPartition", dumpPartitionOffset + 0x0C000000, 0xCE30000);
                    AddPartitionIfValid("SystemAuxPartition", dumpPartitionOffset + 0x18e30000, 0x8000000);
                }
            }

            if (_partitions.Count == 0)
            {
                Console.WriteLine("No known partition map detected. Attempting raw partition discovery.");

                if (HasVolumeSignatureAt(0))
                {
                    AddPartition("RawPartition", 0, Length);
                    return;
                }

                if (HasVolumeSignatureAt(0x4))
                {
                    AddPartition("RawPartitionLegacy", 0, Length, legacy: true);
                    return;
                }

                if (exhaustiveRawPartitionSearch)
                {
                    SearchForAdditionalPartitions();
                }

                if (_partitions.Count == 0)
                {
                    // Final fallback for non-standard images to allow manual partition operations.
                    AddPartition("RawPartition", 0, Length);
                }
            }
        }

        private void AddPartitionIfValid(string name, long offset, long length)
        {
            if (length <= 0)
            {
                return;
            }

            bool hasStandardHeader = HasVolumeSignatureAt(offset);
            bool hasLegacyHeader = HasVolumeSignatureAt(offset + 0x4);
            if (!hasStandardHeader && !hasLegacyHeader)
            {
                return;
            }

            AddPartition(name, offset, length, legacy: hasLegacyHeader && !hasStandardHeader);
        }

        private void AddXbox360DevkitHeaderPartitions(bool exhaustiveRawPartitionSearch)
        {
            var initialPartitionCount = _partitions.Count;
            var knownHeaderNames = new Dictionary<int, string>
            {
                [0] = "Partition1",
                [1] = "SystemPartition",
                [3] = "DumpPartition",
                [4] = "PixDumpPartition",
                [7] = "AltFlash",
                [8] = "Cache0",
                [9] = "Cache1"
            };

            var knownOffsets = new Dictionary<long, string>
            {
                [0x28C080000] = "SystemExtPartition",
                [0x298EB0000] = "SystemAuxPartition",
                [0x2C0EB0000] = "BackCompatPartition",
                [0x2D0EB0000] = "ContentPartition",
                [0x3934B2E000] = "AltFlash"
            };

            var entries = new List<(int Index, long Offset, long Length)>();
            Seek(8);
            for (var index = 0; index < 10; index++)
            {
                var offset = (long)ReadUInt32() * Constants.SectorSize;
                var length = (long)ReadUInt32() * Constants.SectorSize;
                if (offset > 0 && offset < Length)
                {
                    entries.Add((index, offset, length));
                }
            }

            entries.Sort((left, right) => left.Offset.CompareTo(right.Offset));
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (entry.Length <= 0)
                {
                    var nextOffset = index + 1 < entries.Count ? entries[index + 1].Offset : Length;
                    entry = (entry.Index, entry.Offset, nextOffset - entry.Offset);
                }

                var name = knownHeaderNames.TryGetValue(entry.Index, out var headerName)
                    ? headerName
                    : knownOffsets.TryGetValue(entry.Offset, out var offsetName)
                        ? offsetName
                        : $"HeaderPartition{entry.Index}";
                AddPartitionIfValid(name, entry.Offset, entry.Length);
            }

            foreach (var knownOffset in knownOffsets.OrderBy(pair => pair.Key))
            {
                if (_partitions.Any(partition => partition.Offset == knownOffset.Key) || knownOffset.Key >= Length)
                {
                    continue;
                }

                var nextOffset = knownOffsets.Keys
                    .Where(offset => offset > knownOffset.Key && offset < Length)
                    .DefaultIfEmpty(Length)
                    .Min();
                AddPartitionIfValid(knownOffset.Value, knownOffset.Key, nextOffset - knownOffset.Key);
            }

            var discoveredFromHeader = _partitions.Count > initialPartitionCount;
            if (exhaustiveRawPartitionSearch && !discoveredFromHeader)
            {
                SearchForAdditionalPartitions();
            }
        }

        private bool HasVolumeSignatureAt(long offset)
        {
            if (offset < 0 || offset + 4 > Length)
            {
                return false;
            }

            var originalOrder = ByteOrder;
            try
            {
                ByteOrder = ByteOrder.Big;
                Seek(offset);
                return ReadUInt32() == Volume.VolumeSignature;
            }
            catch
            {
                return false;
            }
            finally
            {
                ByteOrder = originalOrder;
            }
        }

        private bool HasOriginalXboxVolumeSignatureAt(long offset)
        {
            if (offset < 0 || offset + 4 > Length)
            {
                return false;
            }

            var originalOrder = ByteOrder;
            try
            {
                ByteOrder = ByteOrder.Little;
                Seek(offset);
                return ReadUInt32() == Volume.VolumeSignature;
            }
            catch
            {
                return false;
            }
            finally
            {
                ByteOrder = originalOrder;
            }
        }

        private void AddOriginalXboxPartitionIfValid(string name, long offset, long length)
        {
            if (length <= 0 || !HasOriginalXboxVolumeSignatureAt(offset))
            {
                return;
            }

            AddPartition(name, offset, length);
        }

        public void AddPartition(string name, long offset, long length, bool legacy = false)
        {
            if (length <= 0 || offset < 0 || offset >= Length)
            {
                return;
            }

            if (_partitions.Any(p => p.Offset == offset))
            {
                return;
            }

            if (offset + length > Length)
            {
                length = Length - offset;
            }

            Volume partition = new Volume(this, name, offset, length, legacy);
            _partitions.Add(partition);
        }

        public int SearchForAdditionalPartitions(long scanStride = Constants.SectorSize)
        {
            if (scanStride < Constants.SectorSize)
            {
                scanStride = Constants.SectorSize;
            }

            var existingOffsets = new HashSet<long>(_partitions.Select(p => p.Offset));
            var added = 0;
            var maxOffset = Math.Max(0, Length - Constants.SectorSize);

            var originalOrder = ByteOrder;
            ByteOrder = ByteOrder.Big;

            for (long offset = 0; offset <= maxOffset; offset += scanStride)
            {
                if (existingOffsets.Contains(offset))
                {
                    continue;
                }

                try
                {
                    Seek(offset);
                    if (ReadUInt32() != Volume.VolumeSignature)
                    {
                        continue;
                    }

                    var serial = ReadUInt32();
                    var sectorsPerCluster = ReadUInt32();
                    var rootCluster = ReadUInt32();

                    if (sectorsPerCluster == 0 || sectorsPerCluster > 0x2000 || rootCluster == 0)
                    {
                        continue;
                    }

                    long candidateLength = EstimatePartitionLength(offset, existingOffsets);
                    if (candidateLength <= Constants.PageSize)
                    {
                        continue;
                    }

                    AddPartition($"Recovered_{offset:X}", offset, candidateLength);
                    existingOffsets.Add(offset);
                    added++;

                    Console.WriteLine($"Discovered candidate FATX partition at 0x{offset:X} (len=0x{candidateLength:X}, spc={sectorsPerCluster}, root={rootCluster}, serial=0x{serial:X8})");
                }
                catch
                {
                    // Keep scanning.
                }
            }

            ByteOrder = originalOrder;
            return added;
        }

        private long EstimatePartitionLength(long offset, HashSet<long> existingOffsets)
        {
            long nextKnownOffset = Length;

            foreach (var existing in existingOffsets)
            {
                if (existing > offset && existing < nextKnownOffset)
                {
                    nextKnownOffset = existing;
                }
            }

            var length = nextKnownOffset - offset;
            if (length <= 0)
            {
                return 0;
            }

            return length;
        }

        public Volume GetPartition(int index)
        {
            return _partitions[index];
        }

        public bool RemovePartitionAt(int index)
        {
            if (index < 0 || index >= _partitions.Count)
            {
                return false;
            }

            _partitions.RemoveAt(index);
            return true;
        }

        public void ClearPartitions()
        {
            _partitions.Clear();
        }

        public List<Volume> Partitions => _partitions;
    }
}
