using K4os.Compression.LZ4;

namespace ViewOwl.Grabber.Engine.Compression;

/// <summary>
/// Encodes an incremental frame delta: the minimal set of changed screen regions
/// required to update a device that already holds <paramref name="prevRaw"/>
/// so that it renders <paramref name="currRaw"/>.
/// </summary>
/// <remarks>
/// Delta binary format (little-endian throughout):
/// <code>
///   byte      flag         = 0x04  (FRAME_FLAG_DELTA)
///   uint32    base_crc     CRC32(init=0) of the previous .bin file the device has
///   uint32    new_crc      CRC32(init=0) of the current .bin file after the delta is applied
///   uint16    region_count number of changed rectangular regions that follow
///   — for each region —
///   uint16    x            left edge (pixels)
///   uint16    y            top edge (pixels)
///   uint16    w            width (pixels)
///   uint16    h            height (pixels)
///   uint32    comp_size    size of the LZ4-compressed BGR565 payload in bytes
///   byte[]    comp_data    LZ4-compressed raw BGR565 pixels (w × h × 2 bytes)
/// </code>
/// Each region's pixel data is LZ4-block-compressed using the same algorithm as
/// <see cref="FrameCompressor"/> so the existing <c>lz4_block_decompress</c> on
/// the ESP32 can decompress it directly.
/// </remarks>
public static class DeltaEncoder
{
    /// <summary>First byte of every delta .bin file.</summary>
    public const byte FlagDelta = 0x04;

    // Tile grid resolution for change detection.
    // TileH must be small enough that one tile row fits in the ESP32 s_linebuf
    // (LCD_H_RES × LCD_H_LINES × 2 bytes). With TileH = 16 and LCD_H_RES = 480:
    //   max region = 480 × 16 × 2 = 15 360 bytes ≪ 76 800 bytes (s_linebuf).
    private const int TileW = 16;
    private const int TileH = 16;

    /// <summary>
    /// Discard the delta when it would be larger than this fraction of the raw frame.
    /// The device falls back to requesting a full frame on the next poll.
    /// </summary>
    public const double MaxDeltaRatio = 0.6;

    /// <summary>
    /// Computes a delta binary from <paramref name="prevRaw"/> to <paramref name="currRaw"/>.
    /// Returns <c>null</c> when the frames are identical or the delta is not worthwhile.
    /// </summary>
    /// <param name="prevRaw">Raw BGR565 pixels of the previous frame (2 bytes per pixel, row-major).</param>
    /// <param name="currRaw">Raw BGR565 pixels of the current frame.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="baseCrc">
    ///   CRC32 (esp_rom_crc32_le style, init=0) of the previous compressed .bin file.
    ///   Embedded in the delta header so the device can verify it has the right base.
    /// </param>
    /// <param name="newCrc">
    ///   CRC32 (esp_rom_crc32_le style, init=0) of the current compressed .bin file.
    ///   Stored by the device after applying the delta so future HELLOs carry the correct CRC.
    /// </param>
    /// <returns>Encoded delta bytes, or <c>null</c>.</returns>
    public static byte[]? Encode(
        byte[] prevRaw,
        byte[] currRaw,
        int width,
        int height,
        uint baseCrc,
        uint newCrc,
        out int changedTileCount)
    {
        changedTileCount = 0;

        ArgumentNullException.ThrowIfNull(prevRaw);
        ArgumentNullException.ThrowIfNull(currRaw);

        if (prevRaw.Length != currRaw.Length || prevRaw.Length != width * height * 2)
            return null;

        List<(int Tx, int Ty)> changedTiles = FindChangedTiles(prevRaw, currRaw, width, height);
        changedTileCount = changedTiles.Count;
        if (changedTiles.Count == 0)
            return null;

        List<(int X, int Y, int W, int H)> regions = BuildRegions(changedTiles, width, height);

        using MemoryStream ms = new();
        using BinaryWriter bw = new(ms);

        bw.Write(FlagDelta);
        bw.Write(baseCrc);
        bw.Write(newCrc);
        bw.Write((ushort)regions.Count);

        foreach (var (x, y, w, h) in regions)
        {
            byte[] pixels     = ExtractRegion(currRaw, width, x, y, w, h);
            byte[] compressed = CompressRegion(pixels);

            bw.Write((ushort)x);
            bw.Write((ushort)y);
            bw.Write((ushort)w);
            bw.Write((ushort)h);
            bw.Write((uint)compressed.Length);
            bw.Write(compressed);
        }

        byte[] result = ms.ToArray();

        // Skip when the delta is not meaningfully smaller than a full raw frame.
        if ((double)result.Length / (width * height * 2) >= MaxDeltaRatio)
            return null;

        return result;
    }

    // ── Private helpers ────────────────────────────────────────────────────

    /// <summary>Finds all TileW×TileH tiles that differ between the two frames.</summary>
    private static List<(int Tx, int Ty)> FindChangedTiles(
        byte[] prev, byte[] curr, int width, int height)
    {
        int tilesX = (width  + TileW - 1) / TileW;
        int tilesY = (height + TileH - 1) / TileH;

        var changed = new List<(int, int)>();

        for (int ty = 0; ty < tilesY; ty++)
        {
            for (int tx = 0; tx < tilesX; tx++)
            {
                if (IsTileChanged(prev, curr, width, height, tx, ty))
                    changed.Add((tx, ty));
            }
        }

        return changed;
    }

    private static bool IsTileChanged(
        byte[] prev, byte[] curr, int width, int height, int tx, int ty)
    {
        int x0 = tx * TileW;
        int y0 = ty * TileH;
        int x1 = Math.Min(x0 + TileW, width);
        int y1 = Math.Min(y0 + TileH, height);

        for (int y = y0; y < y1; y++)
        {
            int start = (y * width + x0) * 2;
            int len   = (x1 - x0) * 2;
            if (!prev.AsSpan(start, len).SequenceEqual(curr.AsSpan(start, len)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Converts the tile list to pixel-coordinate rectangles.
    /// Within each tile row, consecutive tiles are merged into a single wide region.
    /// Rows are kept separate to bound region height to TileH, which guarantees the
    /// decompressed region fits in the ESP32 s_linebuf (LCD_H_RES × LCD_H_LINES × 2 B).
    /// </summary>
    private static List<(int X, int Y, int W, int H)> BuildRegions(
        List<(int Tx, int Ty)> tiles, int width, int height)
    {
        var result = new List<(int, int, int, int)>();

        foreach (var row in tiles.GroupBy(t => t.Ty).OrderBy(g => g.Key))
        {
            int ty = row.Key;
            int y0 = ty * TileH;
            int ph = Math.Min(y0 + TileH, height) - y0;

            List<int> txList = row.Select(t => t.Tx).OrderBy(x => x).ToList();
            int runStart = txList[0];
            int runLast  = txList[0];

            for (int i = 1; i <= txList.Count; i++)
            {
                bool isEnd = i == txList.Count;
                bool hasGap = !isEnd && txList[i] != runLast + 1;

                if (isEnd || hasGap)
                {
                    int x0 = runStart * TileW;
                    int pw = Math.Min((runLast + 1) * TileW, width) - x0;
                    result.Add((x0, y0, pw, ph));

                    if (!isEnd)
                    {
                        runStart = txList[i];
                        runLast  = txList[i];
                    }
                }
                else
                {
                    runLast = txList[i];
                }
            }
        }

        return result;
    }

    /// <summary>Copies a rectangular region from <paramref name="raw"/> into a contiguous buffer.</summary>
    private static byte[] ExtractRegion(byte[] raw, int width, int x, int y, int w, int h)
    {
        byte[] pixels = new byte[w * h * 2];
        for (int row = 0; row < h; row++)
        {
            int src = ((y + row) * width + x) * 2;
            int dst = row * w * 2;
            Buffer.BlockCopy(raw, src, pixels, dst, w * 2);
        }
        return pixels;
    }

    /// <summary>LZ4-compresses raw BGR565 pixels; returns the source unchanged on failure.</summary>
    private static byte[] CompressRegion(byte[] pixels)
    {
        byte[] buf = new byte[LZ4Codec.MaximumOutputSize(pixels.Length)];
        int len = LZ4Codec.Encode(pixels, 0, pixels.Length, buf, 0, buf.Length);

        if (len <= 0)
            return pixels; // store raw on encoding error

        byte[] result = new byte[len];
        Buffer.BlockCopy(buf, 0, result, 0, len);
        return result;
    }
}
