using System;
using System.Linq;
using System.Text.RegularExpressions;
using PokeModelExporter.Export;
using PokeModelExporter.Model;

namespace Eggy.Conv
{
    // How the game should draw a material, decided once for a model and its shiny alike. The role travels in
    // the material's options as EggyRole and the optimiser turns it into glTF material extras.role:
    //   glow  a flame or glow core: unlit, drawn additively without writing depth (Charizard's tail flame,
    //         Rapidash's mane): Trinity's Unlit layered materials with over-bright (HDR) layer colours, or
    //         named for fire, flame or glow.
    public static class Looks
    {
        static readonly Regex GlowName = new Regex("fire|flame|glow|burn|plasma|aura", RegexOptions.IgnoreCase);

        public static MaterialMapping Trinity(Job job, MaterialData m, MaterialMapping map)
        {
            if (m.Shader == "Unlit" && (Hdr(m) || GlowName.IsMatch(m.Name)))
            {
                m.ShaderOptions["EggyRole"] = "glow";
                map.Unlit = true;
                map.DoubleSided = true;
            }
            return map;
        }

        static bool Hdr(MaterialData m) =>
            m.Colors.Where(kv => kv.Key.StartsWith("BaseColorLayer") || kv.Key.StartsWith("EmissionColor"))
                .Any(kv => kv.Value.X > 1.01f || kv.Value.Y > 1.01f || kv.Value.Z > 1.01f)
            || m.Floats.Any(kv => kv.Key.StartsWith("EmissionIntensity") && kv.Value > 1.01f);
    }
}
