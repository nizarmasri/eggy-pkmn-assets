using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Eggy.Conv
{
    // One species to convert: its files, already fetched into plain folders.
    public sealed class Job
    {
        public string Id { get; set; }
        public int Dex { get; set; }
        public string Src { get; set; }      // sv | za | la | swsh | lgpe | bdsp
        public string Folder { get; set; }   // pm0025_00_00, pm0006_00 ... (also the files' name prefix)
        public string ModelDir { get; set; }
        public string AnimDir { get; set; }
        public bool Shiny { get; set; }      // the folder has a shiny (_rare) material set
        public string Variant { get; set; }  // Arceus: the type whose material set this model wears
        public string Female { get; set; }   // the base id of a female model
        public string ShinyDir { get; set; } // BDSP: the shiny bundle's folder (its _rare materials and textures)
        public bool IsGf => Src == "swsh" || Src == "lgpe";
    }

    //   eggyconv <jobs.json> <out dir> [--gf-uv-both]
    // Writes <out dir>/<id>.glb (raw: PNG textures, float buffers) and <id>.json (the report the
    // optimiser and models.json are built from). A species that fails gets a report with "error".
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length >= 4 && args[0] == "--arenas") return Arenas.Run(args[1], args[2], args.Skip(3).ToArray());
            if (args.Length >= 3 && args[0] == "--inspect") return Inspect.Run(args[1], args[2], args.Length > 3 ? args[3] : null);
            if (args.Length < 2)
            {
                Console.Error.WriteLine("eggyconv <jobs.json> <out dir> [--gf-uv-both] [--debug <dir>]");
                return 2;
            }
            var jobs = JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(args[0]),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            string outDir = args[1];
            bool gfBoth = args.Contains("--gf-uv-both");
            int di = Array.IndexOf(args, "--debug");
            string debugDir = di >= 0 && di + 1 < args.Length ? args[di + 1] : null;
            int gi = Array.IndexOf(args, "--diag");
            string diagDir = gi >= 0 && gi + 1 < args.Length ? args[gi + 1] : null;
            if (debugDir != null) Directory.CreateDirectory(debugDir);
            Directory.CreateDirectory(outDir);
            var pretty = new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
            var compact = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

            foreach (var job in jobs)
            {
                // --gf-uv-both: also write the SwSh/LGPE model with its V axis as stored, for a side-by-side check.
                var variants = job.IsGf && gfBoth ? new[] { true, false } : new[] { true };
                foreach (bool flipV in variants)
                {
                    string name = flipV ? job.Id : job.Id + "--noflip";
                    var clock = Stopwatch.StartNew();
                    JsonObject report;
                    try
                    {
                        report = Converter.Convert(job, flipV, Path.Combine(outDir, name + ".glb"), flipV ? debugDir : null, flipV ? diagDir : null);
                    }
                    catch (Exception e)
                    {
                        report = new JsonObject { ["id"] = job.Id, ["src"] = job.Src, ["folder"] = job.Folder, ["error"] = e.ToString() };
                    }
                    report["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
                    string json;
                    try { json = report.ToJsonString(pretty); }
                    catch (Exception e) { json = new JsonObject { ["id"] = job.Id, ["src"] = job.Src, ["error"] = "report: " + e }.ToJsonString(pretty); }
                    File.WriteAllText(Path.Combine(outDir, name + ".json"), json);
                    string summary = report.ContainsKey("error")
                        ? "FAILED: " + report["error"].ToString().Split('\n')[0]
                        : $"{report["tris"]} tris, {report["bones"]} bones, {report["clips"].ToJsonString(compact)}";
                    Console.WriteLine($"{name} ({job.Src} {job.Folder}, {clock.Elapsed.TotalSeconds:F1} s): {summary}");
                }
            }
            return 0;
        }
    }
}
