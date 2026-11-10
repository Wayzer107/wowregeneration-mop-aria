using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace WoWRegeneration.Data
{
    /// <summary>
    ///     Minimal read-only MPQ reader: enough to pull files out of the client's base-Win.MPQ and
    ///     the Updates/wow-0-*-Win-final.MPQ. Supports format versions 1-4 (including the
    ///     compressed hash and block tables of version 4), single-unit and sectored files, and
    ///     zlib and LZMA compression. Encrypted, imploded and patch files are rejected.
    /// </summary>
    public sealed class MpqArchive : IDisposable
    {
        private const uint FileImplode = 0x00000100;
        private const uint FileCompress = 0x00000200;
        private const uint FileEncrypted = 0x00010000;
        private const uint FilePatchFile = 0x00100000;
        private const uint FileSingleUnit = 0x01000000;
        private const uint FileExists = 0x80000000;

        private const byte CompressionZlib = 0x02;
        private const byte CompressionLzma = 0x12;

        private const uint HashTableEmpty = 0xFFFFFFFF;
        private const uint HashTableDeleted = 0xFFFFFFFE;

        private static readonly uint[] CryptTable = BuildCryptTable();

        private readonly FileStream _stream;
        private readonly int _sectorSize;
        private readonly byte[] _hashTable;
        private readonly byte[] _blockTable;
        private readonly uint _hashCount;

        public MpqArchive(string path)
        {
            _stream = File.OpenRead(path);
            try
            {
                byte[] header = ReadAt(0, 208);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x1A51504D) // "MPQ\x1A"
                    throw new InvalidDataException(Path.GetFileName(path) + " is not an MPQ archive");

                ushort formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x0C));
                _sectorSize = 512 << BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x0E));
                long hashPos = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x10));
                long blockPos = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x14));
                _hashCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x18));
                uint blockCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x1C));

                if (formatVersion >= 1)
                {
                    hashPos |= (long)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x28)) << 32;
                    blockPos |= (long)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x2A)) << 32;
                }

                long hashSize = _hashCount * 16L;
                long blockSize = blockCount * 16L;
                if (formatVersion >= 3)
                {
                    // Version 4 stores the on-disk table sizes; smaller than expected means compressed.
                    hashSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x44));
                    blockSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x4C));
                }

                _hashTable = ReadTable(hashPos, hashSize, _hashCount * 16, HashString("(hash table)", 3));
                _blockTable = ReadTable(blockPos, blockSize, blockCount * 16, HashString("(block table)", 3));
            }
            catch
            {
                _stream.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _stream.Dispose();
        }

        /// <summary>Returns the file's contents, or null if the archive does not contain it.</summary>
        public byte[] ReadFile(string name)
        {
            int block = FindBlock(name);
            if (block < 0)
                return null;

            ReadOnlySpan<byte> entry = _blockTable.AsSpan(block * 16, 16);
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            int compressedSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(4));
            int fileSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(12));

            if ((flags & FileExists) == 0)
                return null;
            if ((flags & (FileEncrypted | FilePatchFile | FileImplode)) != 0)
                throw new NotSupportedException(name + " uses an MPQ feature this reader does not support (flags 0x" +
                                                flags.ToString("X8") + ")");

            if ((flags & FileSingleUnit) != 0)
            {
                byte[] data = ReadAt(offset, compressedSize);
                return (flags & FileCompress) != 0 ? Decompress(data, fileSize, name) : data;
            }

            if ((flags & FileCompress) == 0)
                return ReadAt(offset, fileSize);

            int sectorCount = (fileSize + _sectorSize - 1) / _sectorSize;
            var result = new byte[fileSize];

            // Sector offset table; a trailing CRC entry, if present, is ignored.
            byte[] offsetTable = ReadAt(offset, (sectorCount + 1) * 4);
            for (int i = 0; i < sectorCount; i++)
            {
                uint start = BinaryPrimitives.ReadUInt32LittleEndian(offsetTable.AsSpan(i * 4));
                uint end = BinaryPrimitives.ReadUInt32LittleEndian(offsetTable.AsSpan(i * 4 + 4));
                int expected = Math.Min(_sectorSize, fileSize - i * _sectorSize);
                byte[] sector = ReadAt(offset + start, (int)(end - start));
                byte[] plain = Decompress(sector, expected, name);
                Buffer.BlockCopy(plain, 0, result, i * _sectorSize, expected);
            }
            return result;
        }

        private int FindBlock(string name)
        {
            if (_hashCount == 0)
                return -1;

            uint nameA = HashString(name, 1);
            uint nameB = HashString(name, 2);
            uint start = HashString(name, 0) % _hashCount;

            for (uint n = 0; n < _hashCount; n++)
            {
                int i = (int)((start + n) % _hashCount) * 16;
                uint block = BinaryPrimitives.ReadUInt32LittleEndian(_hashTable.AsSpan(i + 12));
                if (block == HashTableEmpty)
                    return -1;
                if (block != HashTableDeleted &&
                    BinaryPrimitives.ReadUInt32LittleEndian(_hashTable.AsSpan(i)) == nameA &&
                    BinaryPrimitives.ReadUInt32LittleEndian(_hashTable.AsSpan(i + 4)) == nameB)
                    return (int)block;
            }
            return -1;
        }

        private byte[] ReadTable(long position, long diskSize, long size, uint key)
        {
            byte[] data = ReadAt(position, (int)diskSize);
            Decrypt(data, key);
            if (diskSize < size)
                data = Decompress(data, (int)size, "the archive's tables");
            if (data.Length != size)
                throw new InvalidDataException("MPQ table has the wrong size");
            return data;
        }

        private byte[] ReadAt(long position, int count)
        {
            var buffer = new byte[count];
            _stream.Position = position;
            _stream.ReadExactly(buffer);
            return buffer;
        }

        /// <summary>A block that is no smaller than its uncompressed size is stored as is.</summary>
        private static byte[] Decompress(byte[] data, int size, string name)
        {
            if (data.Length >= size)
                return data;

            byte method = data[0];
            switch (method)
            {
                case CompressionZlib:
                    var output = new byte[size];
                    using (var zlib = new ZLibStream(new MemoryStream(data, 1, data.Length - 1), CompressionMode.Decompress))
                        zlib.ReadExactly(output);
                    return output;

                case CompressionLzma:
                    // StormLib layout: method, a zero byte, 5 bytes of LZMA properties,
                    // the 64-bit uncompressed size, then the raw LZMA stream.
                    if (data.Length < 15 || data[1] != 0)
                        throw new InvalidDataException("Bad LZMA block in " + name);
                    return Lzma.Decode(data.AsSpan(2, 5).ToArray(), data, 15, data.Length - 15, size);

                default:
                    throw new NotSupportedException(name + " uses MPQ compression 0x" + method.ToString("X2") +
                                                    ", which this reader does not support");
            }
        }

        /// <summary>Decrypts whole 32-bit words in place; a trailing partial word is left as is.</summary>
        private static void Decrypt(byte[] data, uint key)
        {
            uint seed = 0xEEEEEEEE;
            for (int i = 0; i + 4 <= data.Length; i += 4)
            {
                seed += CryptTable[0x400 + (key & 0xFF)];
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) ^ (key + seed);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i), value);
                key = ((~key << 0x15) + 0x11111111) | (key >> 0x0B);
                seed = value + seed + (seed << 5) + 3;
            }
        }

        private static uint HashString(string value, int hashType)
        {
            uint seed1 = 0x7FED7FED;
            uint seed2 = 0xEEEEEEEE;
            foreach (char c in value.ToUpperInvariant().Replace('/', '\\'))
            {
                uint ch = c;
                seed1 = CryptTable[(hashType << 8) + ch] ^ (seed1 + seed2);
                seed2 = ch + seed1 + seed2 + (seed2 << 5) + 3;
            }
            return seed1;
        }

        private static uint[] BuildCryptTable()
        {
            var table = new uint[0x500];
            uint seed = 0x00100001;
            for (int i = 0; i < 0x100; i++)
            {
                for (int index = i, n = 0; n < 5; n++, index += 0x100)
                {
                    seed = (seed * 125 + 3) % 0x2AAAAB;
                    uint high = (seed & 0xFFFF) << 16;
                    seed = (seed * 125 + 3) % 0x2AAAAB;
                    uint low = seed & 0xFFFF;
                    table[index] = high | low;
                }
            }
            return table;
        }
    }
}
