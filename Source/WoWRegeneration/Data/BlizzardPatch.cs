using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace WoWRegeneration.Data
{
    /// <summary>
    ///     Applies a Blizzard "PTCH" binary patch of type BSD0, the format the MPQ-era updaters use
    ///     to upgrade executables. A PTCH file holds the MD5 of the file it applies to and of the
    ///     result; BSD0 is an RLE-packed bsdiff 4.0 patch with 32-bit control words.
    /// </summary>
    public static class BlizzardPatch
    {
        private const int PatchHeaderSize = 68; // PTCH (16) + MD5_ (40) + XFRM header (12)

        public static bool IsPatch(byte[] data)
        {
            return data.Length >= PatchHeaderSize && Tag(data, 0) == "PTCH";
        }

        /// <summary>MD5 (upper-case hex) of the file the patch must be applied to.</summary>
        public static string SourceMd5(byte[] patch)
        {
            return Convert.ToHexString(patch, 24, 16);
        }

        /// <summary>
        ///     Applies patch to original and returns the new file. Throws InvalidDataException if
        ///     original is not the file the patch was made for, or the result has the wrong MD5.
        /// </summary>
        public static byte[] Apply(byte[] patch, byte[] original)
        {
            if (!IsPatch(patch) || Tag(patch, 16) != "MD5_")
                throw new InvalidDataException("Not a PTCH patch");

            int patchDataSize = (int)ReadUInt32(patch, 4);
            int sizeAfter = (int)ReadUInt32(patch, 12);
            int md5BlockSize = (int)ReadUInt32(patch, 20);
            string md5Before = Convert.ToHexString(patch, 24, 16);
            string md5After = Convert.ToHexString(patch, 40, 16);

            int xfrm = 16 + md5BlockSize;
            if (Tag(patch, xfrm) != "XFRM")
                throw new InvalidDataException("PTCH patch has no XFRM block");
            int xfrmSize = (int)ReadUInt32(patch, xfrm + 4);
            string type = Tag(patch, xfrm + 8);

            if (Convert.ToHexString(MD5.HashData(original)) != md5Before)
                throw new InvalidDataException("The file to patch has MD5 " + Convert.ToHexString(MD5.HashData(original)) +
                                               ", expected " + md5Before);

            byte[] result;
            if (type == "COPY")
            {
                result = patch.AsSpan(xfrm + 12, xfrmSize - 12).ToArray();
            }
            else if (type == "BSD0")
            {
                byte[] bsdiff = patch.AsSpan(xfrm + 12, xfrmSize - 12).ToArray();
                int unpackedSize = patchDataSize - PatchHeaderSize;
                if (bsdiff.Length < unpackedSize)
                    bsdiff = UnpackRle(bsdiff, unpackedSize);
                result = ApplyBsdiff(bsdiff, original);
            }
            else
            {
                throw new InvalidDataException("Unknown PTCH patch type " + type);
            }

            if (result.Length != sizeAfter || Convert.ToHexString(MD5.HashData(result)) != md5After)
                throw new InvalidDataException("Patched file has MD5 " + Convert.ToHexString(MD5.HashData(result)) +
                                               ", expected " + md5After);
            return result;
        }

        /// <summary>
        ///     After a 4-byte size, a byte with the high bit set is followed by (low 7 bits + 1)
        ///     literal bytes; any other byte skips (value + 1) zero bytes.
        /// </summary>
        private static byte[] UnpackRle(byte[] packed, int size)
        {
            var output = new byte[size];
            int i = 4;
            int o = 0;
            while (i < packed.Length && o < size)
            {
                byte b = packed[i++];
                if ((b & 0x80) != 0)
                {
                    int count = Math.Min(Math.Min((b & 0x7F) + 1, size - o), packed.Length - i);
                    Buffer.BlockCopy(packed, i, output, o, count);
                    i += count;
                    o += count;
                }
                else
                {
                    o += b + 1;
                }
            }
            return output;
        }

        private static byte[] ApplyBsdiff(byte[] patch, byte[] original)
        {
            if (Tag(patch, 0) != "BSDI" || Tag(patch, 4) != "FF40")
                throw new InvalidDataException("BSD0 patch has no BSDIFF40 header");

            long controlSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(patch.AsSpan(8));
            long dataSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(patch.AsSpan(16));
            int newSize = (int)BinaryPrimitives.ReadUInt64LittleEndian(patch.AsSpan(24));

            int control = 32;
            int controlEnd = (int)(control + controlSize);
            int diff = controlEnd;
            int extra = (int)(controlEnd + dataSize);

            var result = new byte[newSize];
            int newPos = 0;
            long oldPos = 0;

            while (newPos < newSize)
            {
                if (control + 12 > controlEnd)
                    throw new InvalidDataException("BSD0 patch control block is truncated");
                int addLength = (int)ReadUInt32(patch, control);
                int copyLength = (int)ReadUInt32(patch, control + 4);
                uint seek = ReadUInt32(patch, control + 8);
                control += 12;

                if (newPos + addLength > newSize || newPos + addLength + copyLength > newSize)
                    throw new InvalidDataException("BSD0 patch writes past the end of the file");

                // Diff bytes are added to the old file's bytes.
                for (int i = 0; i < addLength; i++, newPos++, oldPos++)
                {
                    byte value = patch[diff + i];
                    if (oldPos >= 0 && oldPos < original.Length)
                        value += original[oldPos];
                    result[newPos] = value;
                }
                diff += addLength;

                // Extra bytes are copied as they are.
                Buffer.BlockCopy(patch, extra, result, newPos, copyLength);
                extra += copyLength;
                newPos += copyLength;

                // Sign-and-magnitude seek in the old file.
                oldPos += (seek & 0x80000000) != 0 ? -(long)(seek & 0x7FFFFFFF) : seek;
            }
            return result;
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        }

        private static string Tag(byte[] data, int offset)
        {
            return System.Text.Encoding.ASCII.GetString(data, offset, 4);
        }
    }
}
