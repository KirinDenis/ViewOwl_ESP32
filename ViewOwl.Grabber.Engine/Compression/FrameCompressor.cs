using K4os.Compression.LZ4;

namespace ViewOwl.Grabber.Engine.Compression
{
    /// <summary>
    /// Compresses a raw BGR565 frame using palette+LZ4 or palette+RLE encoding.
    /// </summary>
    /// <remarks>
    /// Supported output formats:
    /// <code>
    ///   0x00  Raw fallback
    ///     flag(1) + raw BGR565 pixels
    ///
    ///   0x01  Palette + RLE  (legacy, kept for reference)
    ///     flag(1) + palLen(1) + palette(N×2) + RLE pairs [count:u8][index:u8]
    ///
    ///   0x03  Palette + LZ4 independent blocks  (default)
    ///     flag(1) + palLen(1) + palette(N×2) + block_count(uint16 LE)
    ///     + blocks: { size_field(uint16 LE), data }
    ///       size_field bit 15 = 1 → stored (raw copy), bits 14:0 = payload size
    ///       size_field bit 15 = 0 → LZ4 compressed,   bits 14:0 = compressed size
    ///     Each block covers LZ4BlockIndexSize palette indices.
    /// </code>
    /// </remarks>
    public static class FrameCompressor
    {
        private const byte FlagRaw        = 0x00;
        private const byte FlagPalette    = 0x01;
        private const byte FlagLz4Palette = 0x03;

        // Maximum distinct colors that allow palette mode.
        // 255 entries fit in one byte index (0..254); 0 is reserved in the RLE stream.
        private const int MaxPaletteSize = 255;

        /// <summary>
        /// Number of palette index bytes per LZ4 compressed block.
        /// Must match <c>LZ4_BLOCK_SIZE</c> in <c>frame_decoder.h</c> on the ESP32.
        /// </summary>
        private const int LZ4BlockIndexSize = 4096;

        /// <summary>
        /// Compresses a raw BGR565 buffer using palette+LZ4 independent-block encoding.
        /// Falls back to palette+RLE if the frame has no compressible index stream,
        /// and to raw if the unique-color count exceeds 255.
        /// </summary>
        /// <param name="raw">
        ///   Raw BGR565 bytes: width × height × 2, big-endian per pixel.
        /// </param>
        /// <returns>Compressed frame bytes.</returns>
        public static byte[] Compress(byte[] raw)
        {
            ArgumentNullException.ThrowIfNull(raw);
            if (raw.Length % 2 != 0)
                throw new ArgumentException("Raw BGR565 buffer length must be even.", nameof(raw));

            int pixelCount = raw.Length / 2;

            // Pass 1: collect unique colors and build palette map
            var paletteMap = new Dictionary<ushort, byte>();

            for (int i = 0; i < pixelCount; i++)
            {
                int offset = i * 2;
                ushort color = (ushort)((raw[offset] << 8) | raw[offset + 1]);

                if (!paletteMap.ContainsKey(color))
                {
                    if (paletteMap.Count >= MaxPaletteSize)
                    {
                        // Too many colors — fall back to raw
                        return BuildRaw(raw);
                    }

                    paletteMap[color] = (byte)paletteMap.Count;
                }
            }

            // Pass 2: build flat palette-index array
            byte[] indices = BuildIndexArray(raw, pixelCount, paletteMap);

            // Pass 3: LZ4-compress each block and assemble the frame
            return BuildLz4Palette(indices, paletteMap);
        }

        // ── Private helpers ────────────────────────────────────────────────────

        private static byte[] BuildRaw(byte[] raw)
        {
            // 1 flag byte + original pixels
            byte[] output = new byte[1 + raw.Length];
            output[0] = FlagRaw;
            Buffer.BlockCopy(raw, 0, output, 1, raw.Length);
            return output;
        }

        private static byte[] BuildIndexArray(
            byte[] raw,
            int pixelCount,
            Dictionary<ushort, byte> paletteMap)
        {
            byte[] indices = new byte[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                int offset = i * 2;
                ushort color = (ushort)((raw[offset] << 8) | raw[offset + 1]);
                indices[i] = paletteMap[color];
            }
            return indices;
        }

        private static byte[] BuildLz4Palette(
            byte[] indices,
            Dictionary<ushort, byte> paletteMap)
        {
            // Build the ordered palette table
            ushort[] palette = new ushort[paletteMap.Count];
            foreach (var kv in paletteMap)
                palette[kv.Value] = kv.Key;

            int palLen      = palette.Length;          // 1..255
            int paletteBytes = palLen * 2;
            int pixelCount  = indices.Length;

            // Determine number of blocks
            int blockCount = (pixelCount + LZ4BlockIndexSize - 1) / LZ4BlockIndexSize;

            // Compress each block; collect into a list to compute total size up front
            var compressedBlocks = new List<(byte[] Data, bool Stored)>(blockCount);

            // Worst-case LZ4 output per block: source size + a small overhead
            byte[] lz4Buf = new byte[LZ4Codec.MaximumOutputSize(LZ4BlockIndexSize)];

            for (int b = 0; b < blockCount; b++)
            {
                int srcOff = b * LZ4BlockIndexSize;
                int srcLen = Math.Min(LZ4BlockIndexSize, pixelCount - srcOff);

                int compLen = LZ4Codec.Encode(indices, srcOff, srcLen, lz4Buf, 0, lz4Buf.Length);

                if (compLen <= 0 || compLen >= srcLen)
                {
                    // Store raw when LZ4 does not help (compLen==0 means error too)
                    byte[] stored = new byte[srcLen];
                    Buffer.BlockCopy(indices, srcOff, stored, 0, srcLen);
                    compressedBlocks.Add((stored, Stored: true));
                }
                else
                {
                    byte[] comp = new byte[compLen];
                    Buffer.BlockCopy(lz4Buf, 0, comp, 0, compLen);
                    compressedBlocks.Add((comp, Stored: false));
                }
            }

            // Calculate total output size:
            // flag(1) + palLen(1) + palette(N×2) + block_count(2) + blocks(2+data each)
            int blocksBytes = compressedBlocks.Sum(b => 2 + b.Data.Length);
            int totalSize   = 2 + paletteBytes + 2 + blocksBytes;
            byte[] output   = new byte[totalSize];
            int pos         = 0;

            // Header
            output[pos++] = FlagLz4Palette;
            output[pos++] = (byte)palLen;

            foreach (ushort entry in palette)
            {
                output[pos++] = (byte)(entry >> 8);
                output[pos++] = (byte)(entry & 0xFF);
            }

            // block_count: uint16 little-endian
            output[pos++] = (byte)(blockCount & 0xFF);
            output[pos++] = (byte)((blockCount >> 8) & 0xFF);

            // Blocks
            foreach (var (data, stored) in compressedBlocks)
            {
                // size_field: bit15 = stored flag, bits14:0 = payload size
                ushort sizeField = (ushort)(data.Length & 0x7FFF);
                if (stored)
                    sizeField |= 0x8000;

                output[pos++] = (byte)(sizeField & 0xFF);
                output[pos++] = (byte)((sizeField >> 8) & 0xFF);

                Buffer.BlockCopy(data, 0, output, pos, data.Length);
                pos += data.Length;
            }

            return output;
        }

        /// <summary>
        /// Compresses a raw BGR565 buffer using palette+RLE encoding (legacy, not used by default).
        /// </summary>
        /// <param name="raw">Raw BGR565 bytes.</param>
        /// <returns>Palette+RLE encoded frame.</returns>
        public static byte[] CompressRle(byte[] raw)
        {
            ArgumentNullException.ThrowIfNull(raw);
            if (raw.Length % 2 != 0)
                throw new ArgumentException("Raw BGR565 buffer length must be even.", nameof(raw));

            int pixelCount = raw.Length / 2;
            var paletteMap = new Dictionary<ushort, byte>();

            for (int i = 0; i < pixelCount; i++)
            {
                int offset = i * 2;
                ushort color = (ushort)((raw[offset] << 8) | raw[offset + 1]);

                if (!paletteMap.ContainsKey(color))
                {
                    if (paletteMap.Count >= MaxPaletteSize)
                        return BuildRaw(raw);

                    paletteMap[color] = (byte)paletteMap.Count;
                }
            }

            return BuildPaletteRle(raw, pixelCount, paletteMap);
        }

        private static byte[] BuildPaletteRle(
            byte[] raw,
            int pixelCount,
            Dictionary<ushort, byte> paletteMap)
        {
            ushort[] palette = new ushort[paletteMap.Count];
            foreach (var kv in paletteMap)
                palette[kv.Value] = kv.Key;

            var rle = new List<byte>(pixelCount * 2);

            int idx = 0;
            while (idx < pixelCount)
            {
                int offset = idx * 2;
                ushort color = (ushort)((raw[offset] << 8) | raw[offset + 1]);
                byte palIdx = paletteMap[color];

                int run = 1;
                while (run < 255 && idx + run < pixelCount)
                {
                    int nextOffset = (idx + run) * 2;
                    ushort nextColor = (ushort)((raw[nextOffset] << 8) | raw[nextOffset + 1]);
                    if (nextColor != color) break;
                    run++;
                }

                rle.Add((byte)run);
                rle.Add(palIdx);
                idx += run;
            }

            int paletteBytes = palette.Length * 2;
            byte[] output = new byte[2 + paletteBytes + rle.Count];
            int pos = 0;

            output[pos++] = FlagPalette;
            output[pos++] = (byte)palette.Length;

            foreach (ushort entry in palette)
            {
                output[pos++] = (byte)(entry >> 8);
                output[pos++] = (byte)(entry & 0xFF);
            }

            rle.CopyTo(output, pos);
            return output;
        }
    }
}
