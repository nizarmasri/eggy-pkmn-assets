using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PokeModelExporter.Model;
using PokeModelExporter.Scarlet;

namespace Eggy.Conv
{
    public static class Clips
    {
        // The clips in an animation folder, without the "<folder>_" prefix and the extension:
        //   pm0025_00_00_00400_attack01.tranm -> 00400_attack01;  pm0006_00_ba10_waitA01.gfbanm -> ba10_waitA01
        public static List<string> List(string dir, string prefix, string ext)
        {
            if (dir == null || !Directory.Exists(dir)) return new List<string>();
            return Directory.GetFiles(dir, "*" + ext)
                .Select(Path.GetFileName)
                .Where(f => f.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase) && f.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Substring(prefix.Length + 1, f.Length - prefix.Length - 1 - ext.Length))
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();
        }

        // GFBANM (SwSh, LGPE) and TRANM (SV, LA, Z-A) share the skeletal schema (info, bone tracks of
        // fixed/dynamic/framed keys, 48-bit quaternions), so PME's TRANM reader reads both.
        // Trinity clips also carry notifies (.traef) and material/visibility tracks (.tracm).
        public static AnimationData Load(string prefix, string clip, bool gf, Func<string, byte[]> read, JsonArray notes)
        {
            string file = $"{prefix}_{clip}{(gf ? ".gfbanm" : ".tranm")}";
            var data = read(file) ?? throw new FileNotFoundException("missing clip " + file);
            var anim = TrinityAnimationReader.Read(data, clip);
            if (anim.FrameCount < 1) throw new InvalidDataException($"{file}: no frames");
            if (gf) return anim;
            var traef = read($"{prefix}_{clip}.traef");
            if (traef != null)
            {
                try { anim.Events = TrinityAnimationReader.ReadEvents(traef); }
                catch (Exception e) { notes.Add($"{clip}.traef unreadable: {e.Message}"); }
            }
            var tracm = read($"{prefix}_{clip}.tracm");
            if (tracm != null)
            {
                try { TrinityCurveReader.Read(tracm, anim); }
                catch (Exception e) { notes.Add($"{clip}.tracm unreadable: {e.Message}"); }
            }
            return anim;
        }
    }
}
