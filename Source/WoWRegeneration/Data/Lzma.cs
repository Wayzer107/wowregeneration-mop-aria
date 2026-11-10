using System;
using System.IO;

namespace WoWRegeneration.Data
{
    /// <summary>
    ///     One-shot LZMA decoder for blocks of known size, following the reference decoder in the
    ///     LZMA specification (LzmaSpec.cpp, public domain). Used for the LZMA-compressed sectors
    ///     of the 5.4.8 update MPQ.
    /// </summary>
    public static class Lzma
    {
        private const int NumStates = 12;
        private const int NumPosBitsMax = 4;
        private const int NumLenToPosStates = 4;
        private const int EndPosModelIndex = 14;
        private const int NumFullDistances = 1 << (EndPosModelIndex >> 1);
        private const int NumAlignBits = 4;
        private const int MatchMinLen = 2;

        /// <summary>Decodes a raw LZMA stream described by its 5 property bytes.</summary>
        public static byte[] Decode(byte[] properties, byte[] input, int offset, int count, int outputSize)
        {
            int d = properties[0];
            if (d >= 9 * 5 * 5)
                throw new InvalidDataException("Bad LZMA properties");
            int lc = d % 9;
            d /= 9;
            int lp = d % 5;
            int pb = d / 5;

            var output = new byte[outputSize];
            var rc = new RangeDecoder(input, offset, count);

            var literalProbs = NewProbs(0x300 << (lc + lp));
            var isMatch = NewProbs(NumStates << NumPosBitsMax);
            var isRep = NewProbs(NumStates);
            var isRepG0 = NewProbs(NumStates);
            var isRepG1 = NewProbs(NumStates);
            var isRepG2 = NewProbs(NumStates);
            var isRep0Long = NewProbs(NumStates << NumPosBitsMax);
            var posSlot = new ushort[NumLenToPosStates][];
            for (int i = 0; i < NumLenToPosStates; i++)
                posSlot[i] = NewProbs(1 << 6);
            var posDecoders = NewProbs(1 + NumFullDistances - EndPosModelIndex);
            var align = NewProbs(1 << NumAlignBits);
            var lenDecoder = new LenDecoder();
            var repLenDecoder = new LenDecoder();

            uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
            int state = 0;
            int pos = 0;
            int pbMask = (1 << pb) - 1;
            int lpMask = (1 << lp) - 1;

            while (pos < outputSize)
            {
                int posState = pos & pbMask;

                if (rc.DecodeBit(ref isMatch[(state << NumPosBitsMax) + posState]) == 0)
                {
                    int prevByte = pos > 0 ? output[pos - 1] : 0;
                    int litState = ((pos & lpMask) << lc) + (prevByte >> (8 - lc));
                    int probsBase = 0x300 * litState;
                    int symbol = 1;

                    if (state >= 7)
                    {
                        int matchByte = output[pos - (int)rep0 - 1];
                        do
                        {
                            int matchBit = (matchByte >> 7) & 1;
                            matchByte <<= 1;
                            int bit = rc.DecodeBit(ref literalProbs[probsBase + ((1 + matchBit) << 8) + symbol]);
                            symbol = (symbol << 1) | bit;
                            if (matchBit != bit)
                                break;
                        } while (symbol < 0x100);
                    }
                    while (symbol < 0x100)
                        symbol = (symbol << 1) | rc.DecodeBit(ref literalProbs[probsBase + symbol]);

                    output[pos++] = (byte)symbol;
                    state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                    continue;
                }

                int len;
                if (rc.DecodeBit(ref isRep[state]) != 0)
                {
                    if (pos == 0)
                        throw new InvalidDataException("Corrupt LZMA data");

                    if (rc.DecodeBit(ref isRepG0[state]) == 0)
                    {
                        if (rc.DecodeBit(ref isRep0Long[(state << NumPosBitsMax) + posState]) == 0)
                        {
                            state = state < 7 ? 9 : 11;
                            output[pos] = output[pos - (int)rep0 - 1];
                            pos++;
                            continue;
                        }
                    }
                    else
                    {
                        uint dist;
                        if (rc.DecodeBit(ref isRepG1[state]) == 0)
                        {
                            dist = rep1;
                        }
                        else
                        {
                            if (rc.DecodeBit(ref isRepG2[state]) == 0)
                            {
                                dist = rep2;
                            }
                            else
                            {
                                dist = rep3;
                                rep3 = rep2;
                            }
                            rep2 = rep1;
                        }
                        rep1 = rep0;
                        rep0 = dist;
                    }
                    len = repLenDecoder.Decode(rc, posState);
                    state = state < 7 ? 8 : 11;
                }
                else
                {
                    rep3 = rep2;
                    rep2 = rep1;
                    rep1 = rep0;
                    len = lenDecoder.Decode(rc, posState);
                    state = state < 7 ? 7 : 10;

                    // Distance
                    int lenState = Math.Min(len, NumLenToPosStates - 1);
                    int slot = BitTreeDecode(rc, posSlot[lenState], 0, 6);
                    if (slot < 4)
                    {
                        rep0 = (uint)slot;
                    }
                    else
                    {
                        int numDirectBits = (slot >> 1) - 1;
                        uint dist = (uint)((2 | (slot & 1)) << numDirectBits);
                        if (slot < EndPosModelIndex)
                        {
                            dist += BitTreeReverseDecode(rc, posDecoders, (int)dist - slot, numDirectBits);
                        }
                        else
                        {
                            dist += rc.DecodeDirectBits(numDirectBits - NumAlignBits) << NumAlignBits;
                            dist += BitTreeReverseDecode(rc, align, 0, NumAlignBits);
                        }
                        rep0 = dist;
                    }

                    if (rep0 == 0xFFFFFFFF)
                        break; // end marker
                }

                len += MatchMinLen;
                if (rep0 >= pos)
                    throw new InvalidDataException("Corrupt LZMA data");
                int from = pos - (int)rep0 - 1;
                int end = Math.Min(pos + len, outputSize);
                while (pos < end)
                    output[pos++] = output[from++];
            }

            if (pos != outputSize)
                throw new InvalidDataException("LZMA data ended early");
            return output;
        }

        private static ushort[] NewProbs(int count)
        {
            var probs = new ushort[count];
            Array.Fill(probs, (ushort)1024);
            return probs;
        }

        private static int BitTreeDecode(RangeDecoder rc, ushort[] probs, int offset, int numBits)
        {
            int m = 1;
            for (int i = 0; i < numBits; i++)
                m = (m << 1) + rc.DecodeBit(ref probs[offset + m]);
            return m - (1 << numBits);
        }

        private static uint BitTreeReverseDecode(RangeDecoder rc, ushort[] probs, int offset, int numBits)
        {
            int m = 1;
            uint symbol = 0;
            for (int i = 0; i < numBits; i++)
            {
                int bit = rc.DecodeBit(ref probs[offset + m]);
                m = (m << 1) + bit;
                symbol |= (uint)bit << i;
            }
            return symbol;
        }

        private sealed class LenDecoder
        {
            private ushort _choice = 1024;
            private ushort _choice2 = 1024;
            private readonly ushort[] _low = NewProbs((1 << NumPosBitsMax) << 3);
            private readonly ushort[] _mid = NewProbs((1 << NumPosBitsMax) << 3);
            private readonly ushort[] _high = NewProbs(1 << 8);

            public int Decode(RangeDecoder rc, int posState)
            {
                if (rc.DecodeBit(ref _choice) == 0)
                    return BitTreeDecode(rc, _low, posState << 3, 3);
                if (rc.DecodeBit(ref _choice2) == 0)
                    return 8 + BitTreeDecode(rc, _mid, posState << 3, 3);
                return 16 + BitTreeDecode(rc, _high, 0, 8);
            }
        }

        private sealed class RangeDecoder
        {
            private readonly byte[] _input;
            private readonly int _end;
            private int _pos;
            private uint _range = 0xFFFFFFFF;
            private uint _code;

            public RangeDecoder(byte[] input, int offset, int count)
            {
                _input = input;
                _pos = offset;
                _end = offset + count;
                if (NextByte() != 0)
                    throw new InvalidDataException("Corrupt LZMA data");
                for (int i = 0; i < 4; i++)
                    _code = (_code << 8) | NextByte();
                if (_code == _range)
                    throw new InvalidDataException("Corrupt LZMA data");
            }

            private byte NextByte()
            {
                // Past the end, return zeros; a truncated stream then fails the MD5 checks.
                return _pos < _end ? _input[_pos++] : (byte)0;
            }

            private void Normalize()
            {
                if (_range < (1u << 24))
                {
                    _range <<= 8;
                    _code = (_code << 8) | NextByte();
                }
            }

            public uint DecodeDirectBits(int numBits)
            {
                uint result = 0;
                for (; numBits > 0; numBits--)
                {
                    _range >>= 1;
                    _code -= _range;
                    uint t = 0 - (_code >> 31);
                    _code += _range & t;
                    result = (result << 1) + (t + 1);
                    Normalize();
                }
                return result;
            }

            public int DecodeBit(ref ushort prob)
            {
                uint bound = (_range >> 11) * prob;
                int symbol;
                if (_code < bound)
                {
                    prob += (ushort)((2048 - prob) >> 5);
                    _range = bound;
                    symbol = 0;
                }
                else
                {
                    prob -= (ushort)(prob >> 5);
                    _code -= bound;
                    _range -= bound;
                    symbol = 1;
                }
                Normalize();
                return symbol;
            }
        }
    }
}
