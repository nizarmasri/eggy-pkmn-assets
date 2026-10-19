using System;
using System.IO;
using System.IO.Compression;
using PokeModelExporter.Formats;

namespace Eggy.Conv
{
    // PNG to RGBA, for the textures the Unity path (Brilliant Diamond / Shining Pearl) exports as PNG: 8-bit
    // grey, grey + alpha, RGB, RGBA and palette images, not interlaced (what AssetStudio writes). Returned as
    // a Bntx.Texture so the Sword/Shield baker (GfMaterials) takes them as it takes BNTX.
    public static class PngRead
    {
        public static bool Is(byte[] b) => b != null && b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G';

        public static Bntx.Texture Decode(byte[] png)
        {
            int pos = 8, width = 0, height = 0, depth = 0, type = 0, interlace = 0;
            byte[] palette = null, trns = null;
            var idat = new MemoryStream();
            while (pos + 8 <= png.Length)
            {
                int len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
                string kind = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
                int data = pos + 8;
                switch (kind)
                {
                    case "IHDR":
                        width = (png[data] << 24) | (png[data + 1] << 16) | (png[data + 2] << 8) | png[data + 3];
                        height = (png[data + 4] << 24) | (png[data + 5] << 16) | (png[data + 6] << 8) | png[data + 7];
                        depth = png[data + 8]; type = png[data + 9]; interlace = png[data + 12];
                        break;
                    case "PLTE": palette = png.AsSpan(data, len).ToArray(); break;
                    case "tRNS": trns = png.AsSpan(data, len).ToArray(); break;
                    case "IDAT": idat.Write(png, data, len); break;
                }
                if (kind == "IEND") break;
                pos = data + len + 4;
            }
            if (depth != 8 || interlace != 0) throw new NotSupportedException($"PNG depth {depth}, interlace {interlace}");
            int channels = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new NotSupportedException($"PNG colour type {type}") };
            int stride = width * channels;
            var raw = new byte[height * (stride + 1)];
            idat.Position = 0;
            using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            {
                int read = 0;
                while (read < raw.Length)
                {
                    int n = z.Read(raw, read, raw.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
            }
            var px = new byte[height * stride];
            for (int y = 0; y < height; y++)
            {
                int filter = raw[y * (stride + 1)];
                int src = y * (stride + 1) + 1, dst = y * stride, prev = dst - stride;
                for (int x = 0; x < stride; x++)
                {
                    int a = x >= channels ? px[dst + x - channels] : 0;
                    int b = y > 0 ? px[prev + x] : 0;
                    int c = x >= channels && y > 0 ? px[prev + x - channels] : 0;
                    int v = raw[src + x];
                    switch (filter)
                    {
                        case 1: v += a; break;
                        case 2: v += b; break;
                        case 3: v += (a + b) >> 1; break;
                        case 4:
                            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                            v += pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                            break;
                    }
                    px[dst + x] = (byte)v;
                }
            }
            var rgba = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                int s = i * channels, d = i * 4;
                switch (type)
                {
                    case 0: rgba[d] = rgba[d + 1] = rgba[d + 2] = px[s]; rgba[d + 3] = 255; break;
                    case 4: rgba[d] = rgba[d + 1] = rgba[d + 2] = px[s]; rgba[d + 3] = px[s + 1]; break;
                    case 2: rgba[d] = px[s]; rgba[d + 1] = px[s + 1]; rgba[d + 2] = px[s + 2]; rgba[d + 3] = 255; break;
                    case 6: Buffer.BlockCopy(px, s, rgba, d, 4); break;
                    case 3:
                        int k = px[s];
                        rgba[d] = palette[k * 3]; rgba[d + 1] = palette[k * 3 + 1]; rgba[d + 2] = palette[k * 3 + 2];
                        rgba[d + 3] = trns != null && k < trns.Length ? trns[k] : (byte)255;
                        break;
                }
            }
            return new Bntx.Texture { Width = width, Height = height, Rgba = rgba };
        }
    }
}
