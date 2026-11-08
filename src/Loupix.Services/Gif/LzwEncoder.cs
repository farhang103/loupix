namespace Loupix.Services.Gif;

/// <summary>
/// GIF variable-width LZW compressor. Codes start at minCodeSize+1 bits and grow to 12; when the table is
/// full a Clear code is emitted and the table restarts. Output is packed LSB-first into ≤255-byte sub-blocks
/// followed by the zero-length block terminator.
/// </summary>
internal static class LzwEncoder
{
    private const int MaxBits = 12;
    private const int MaxCode = 1 << MaxBits; // 4096 table entries
    private const int HashSize = 5003;        // prime, ~80% occupancy at 4096 entries

    public static void Encode(Stream output, ReadOnlySpan<byte> indices, int minCodeSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minCodeSize, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minCodeSize, 8);

        output.WriteByte((byte)minCodeSize);
        var writer = new BitWriter(output);

        int clearCode = 1 << minCodeSize;
        int eoiCode = clearCode + 1;
        int initBits = minCodeSize + 1;

        var hashKeys = new int[HashSize];   // (prefix << 8 | pixel) + 1, 0 = empty
        var hashCodes = new int[HashSize];

        int codeBits = initBits;
        int nextCode = clearCode + 2;

        void Emit(int code)
        {
            writer.Write(code, codeBits);
            // The decoder adds its table entries one step behind us, so widen once the *previous* addition
            // has filled the current width (same rule as the reference compress/GIF encoders).
            if (nextCode > (1 << codeBits) - 1 && codeBits < MaxBits) codeBits++;
        }

        void Reset()
        {
            Array.Clear(hashKeys);
            nextCode = clearCode + 2;
        }

        // Emit Clear first; then codeBits returns to the initial width.
        writer.Write(clearCode, codeBits);
        codeBits = initBits;

        if (indices.Length == 0)
        {
            writer.Write(eoiCode, codeBits);
            writer.Flush();
            output.WriteByte(0);
            return;
        }

        int prefix = indices[0];
        for (int i = 1; i < indices.Length; i++)
        {
            int k = indices[i];
            int key = (prefix << 8 | k) + 1;
            int h = (k << 4 ^ prefix) % HashSize;
            int step = h == 0 ? 1 : HashSize - h;
            int found = -1;
            while (hashKeys[h] != 0)
            {
                if (hashKeys[h] == key) { found = hashCodes[h]; break; }
                h -= step;
                if (h < 0) h += HashSize;
            }
            if (found >= 0)
            {
                prefix = found;
                continue;
            }

            Emit(prefix);
            prefix = k;
            if (nextCode < MaxCode)
            {
                hashKeys[h] = key;
                hashCodes[h] = nextCode++;
            }
            else
            {
                writer.Write(clearCode, codeBits);
                Reset();
                codeBits = initBits;
            }
        }
        Emit(prefix);
        Emit(eoiCode);
        writer.Flush();
        output.WriteByte(0); // block terminator
    }

    /// <summary>Packs codes LSB-first and writes them as length-prefixed sub-blocks.</summary>
    private sealed class BitWriter(Stream output)
    {
        private readonly byte[] _block = new byte[255];
        private int _blockLen;
        private int _acc;
        private int _accBits;

        public void Write(int code, int bits)
        {
            _acc |= code << _accBits;
            _accBits += bits;
            while (_accBits >= 8)
            {
                Put((byte)_acc);
                _acc >>= 8;
                _accBits -= 8;
            }
        }

        public void Flush()
        {
            if (_accBits > 0)
            {
                Put((byte)_acc);
                _acc = 0;
                _accBits = 0;
            }
            if (_blockLen > 0) FlushBlock();
        }

        private void Put(byte b)
        {
            _block[_blockLen++] = b;
            if (_blockLen == 255) FlushBlock();
        }

        private void FlushBlock()
        {
            output.WriteByte((byte)_blockLen);
            output.Write(_block, 0, _blockLen);
            _blockLen = 0;
        }
    }
}
