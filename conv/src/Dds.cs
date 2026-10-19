using System;
using System.IO;
using System.Text;
using PokeModelExporter.Formats;

namespace Eggy.Conv
{
    // DirectDraw Surface textures (the battle-map rips carry their textures as .dds, already unswizzled):
    // the top mip of the first face/slice, decoded to RGBA8 rows from the top. BC1-5/BC7 through PME's
    // decoder; DX10 headers; plain RGB(A)/luminance with bit masks.
    public static class Dds
    {
        public sealed class Image
        {
            public int Width, Height, Mips;
            public byte[] Rgba;
            public string Format;
            public bool Cube, Srgb;
        }

        public static Image Read(byte[] f)
        {
            if (f.Length < 128 || Encoding.ASCII.GetString(f, 0, 4) != "DDS ") throw new InvalidDataException("not a DDS file");
            int height = I32(f, 12), width = I32(f, 16), mips = Math.Max(1, I32(f, 28));
            int pfFlags = I32(f, 80);
            string four = Encoding.ASCII.GetString(f, 84, 4);
            int bits = I32(f, 88);
            uint rMask = U32(f, 92), gMask = U32(f, 96), bMask = U32(f, 100), aMask = U32(f, 104);
            int caps2 = I32(f, 112);
            int data = 128;
            var img = new Image { Width = width, Height = height, Mips = mips, Cube = (caps2 & 0x200) != 0 };
            BcDecoder.Format? bc = null;
            string plain = null; // "rgba8", "bgra8", "r8", "rg8"
            if ((pfFlags & 4) != 0)
            {
                switch (four)
                {
                    case "DXT1": bc = BcDecoder.Format.BC1; break;
                    case "DXT2": case "DXT3": bc = BcDecoder.Format.BC2; break;
                    case "DXT4": case "DXT5": bc = BcDecoder.Format.BC3; break;
                    case "ATI1": case "BC4U": case "BC4S": bc = BcDecoder.Format.BC4; break;
                    case "ATI2": case "BC5U": case "BC5S": bc = BcDecoder.Format.BC5; break;
                    case "DX10":
                        int dxgi = I32(f, 128);
                        data = 148;
                        img.Srgb = dxgi is 29 or 72 or 75 or 78 or 91 or 99;
                        if (dxgi >= 70 && dxgi <= 72) bc = BcDecoder.Format.BC1;
                        else if (dxgi >= 73 && dxgi <= 75) bc = BcDecoder.Format.BC2;
                        else if (dxgi >= 76 && dxgi <= 78) bc = BcDecoder.Format.BC3;
                        else if (dxgi >= 79 && dxgi <= 81) bc = BcDecoder.Format.BC4;
                        else if (dxgi >= 82 && dxgi <= 84) bc = BcDecoder.Format.BC5;
                        else if (dxgi >= 97 && dxgi <= 99) bc = BcDecoder.Format.BC7;
                        else if (dxgi >= 27 && dxgi <= 32) plain = "rgba8";
                        else if (dxgi is 87 or 88 or 90 or 91) plain = "bgra8";
                        else if (dxgi is 61 or 62 or 65) plain = "r8";
                        else if (dxgi is 49 or 50) plain = "rg8";
                        else throw new NotSupportedException($"DDS DXGI format {dxgi}");
                        img.Format = $"dxgi{dxgi}";
                        break;
                    default: throw new NotSupportedException($"DDS FourCC {four}");
                }
                img.Format ??= four;
            }
            if (bc != null)
            {
                int size = ((width + 3) / 4) * ((height + 3) / 4) * BcDecoder.BlockBytes(bc.Value);
                var blocks = new byte[size];
                Buffer.BlockCopy(f, data, blocks, 0, Math.Min(size, f.Length - data));
                img.Rgba = BcDecoder.Decode(blocks, width, height, bc.Value);
                img.Format += "/" + bc.Value;
                return img;
            }
            var rgba = new byte[width * height * 4];
            if (plain != null)
            {
                int bpp = plain switch { "r8" => 1, "rg8" => 2, _ => 4 };
                for (int i = 0; i < width * height && data + i * bpp + bpp <= f.Length; i++)
                {
                    int p = data + i * bpp;
                    switch (plain)
                    {
                        case "rgba8": Buffer.BlockCopy(f, p, rgba, i * 4, 4); break;
                        case "bgra8": rgba[i * 4] = f[p + 2]; rgba[i * 4 + 1] = f[p + 1]; rgba[i * 4 + 2] = f[p]; rgba[i * 4 + 3] = f[p + 3]; break;
                        case "r8": rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = f[p]; rgba[i * 4 + 3] = 255; break;
                        case "rg8": rgba[i * 4] = f[p]; rgba[i * 4 + 1] = f[p + 1]; rgba[i * 4 + 3] = 255; break;
                    }
                }
                img.Rgba = rgba;
                return img;
            }
            // Legacy uncompressed: channels by bit mask (RGB, RGBA, luminance, alpha).
            int bytes = Math.Max(1, bits / 8);
            bool lum = (pfFlags & 0x20000) != 0, alphaOnly = (pfFlags & 0x2) != 0 && (pfFlags & 0x40) == 0 && !lum;
            for (int i = 0; i < width * height && data + i * bytes + bytes <= f.Length; i++)
            {
                uint v = 0;
                for (int k = 0; k < bytes; k++) v |= (uint)f[data + i * bytes + k] << (8 * k);
                byte r = Channel(v, rMask), g = Channel(v, gMask), b = Channel(v, bMask);
                byte a = aMask != 0 ? Channel(v, aMask) : (byte)255;
                if (lum) g = b = r;
                if (alphaOnly) { r = g = b = 255; }
                rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = a;
            }
            img.Rgba = rgba;
            img.Format = $"rgb{bits} {rMask:X}/{gMask:X}/{bMask:X}/{aMask:X}";
            return img;
        }

        static byte Channel(uint v, uint mask)
        {
            if (mask == 0) return 0;
            int shift = 0;
            while (((mask >> shift) & 1) == 0) shift++;
            uint max = mask >> shift;
            return (byte)(((v & mask) >> shift) * 255 / max);
        }

        // A smaller copy (box filter) for looking at.
        public static (byte[] Rgba, int W, int H) Shrink(byte[] rgba, int w, int h, int max)
        {
            int f = 1;
            while (w / f > max || h / f > max) f *= 2;
            if (f == 1) return (rgba, w, h);
            int nw = Math.Max(1, w / f), nh = Math.Max(1, h / f);
            var o = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int sum = 0;
                        for (int dy = 0; dy < f; dy++)
                            for (int dx = 0; dx < f; dx++)
                                sum += rgba[((y * f + dy) * w + x * f + dx) * 4 + c];
                        o[(y * nw + x) * 4 + c] = (byte)(sum / (f * f));
                    }
            return (o, nw, nh);
        }

        static int I32(byte[] b, int p) => BitConverter.ToInt32(b, p);
        static uint U32(byte[] b, int p) => BitConverter.ToUInt32(b, p);
    }
}
