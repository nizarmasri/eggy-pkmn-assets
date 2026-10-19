using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using PokeModelExporter.Model;
using PokeModelExporter.Scarlet;

namespace Eggy.Conv
{
    // Which game clips become the contract's clips, and how.
    public sealed class ClipPlan
    {
        // contract name -> game clips, played one after another (a _start/_loop/_end sequence is joined)
        public readonly Dictionary<string, List<string>> Sources = new Dictionary<string, List<string>>();
        public string Set; // Trinity: the clip set used (0 standing, 1 swimming, 2 floating/flying)
        public IEnumerable<string> AllClips => Sources.Values.SelectMany(x => x).Distinct();
    }

    public static class Battle
    {
        public static readonly string[] Names = { "idle", "appear", "cry", "physical", "special", "hit", "faint", "blink" };

        // ---- Trinity (SV, LA, Z-A): "<5-digit id>_<name>", the id's first digit is the clip set ----

        public static ClipPlan PlanTrinity(IList<string> clips)
        {
            var plan = new ClipPlan();
            char set = SvExporter.MainClipSet(clips) ?? '0';
            plan.Set = set.ToString();
            static string Kind(string c) => c.Length > 6 && char.IsDigit(c[0]) && c[5] == '_' ? c.Substring(6) : c;
            string One(string kind, bool anySet = false) =>
                clips.Where(c => c[0] == set && Kind(c) == kind).OrderBy(c => c, StringComparer.Ordinal).FirstOrDefault()
                ?? (anySet ? clips.Where(c => Kind(c) == kind).OrderBy(c => c, StringComparer.Ordinal).FirstOrDefault() : null);
            List<string> Seq(string kind)
            {
                var single = One(kind);
                if (single != null) return new List<string> { single };
                var start = One(kind + "_start");
                if (start == null) return null;
                var list = new List<string> { start };
                var loop = One(kind + "_loop");
                if (loop != null) list.Add(loop);
                var end = One(kind + "_end");
                if (end != null) list.Add(end);
                return list;
            }
            Put(plan, "idle", One("battlewait01_loop") ?? One("defaultwait01_loop"));
            Put(plan, "appear", One("land02") ?? One("land01"));
            Put(plan, "cry", One("roar01"));
            Put(plan, "physical", Seq("attack01"));
            Put(plan, "special", Seq("rangeattack01"));
            Put(plan, "hit", Seq("damage01"));
            Put(plan, "faint", Seq("down01"));
            Put(plan, "blink", One("eye01", anySet: true));
            return plan;
        }

        // ---- SwSh / LGPE: ba01_land01, ba02_roar01, ba10_waitA01, ba20_buturi01, ba21_tokusyu01, ba30_damageS01, ba41_down01 ----

        public static ClipPlan PlanGf(IList<string> clips)
        {
            var plan = new ClipPlan();
            string Exact(string name) => clips.FirstOrDefault(c => c == name);
            string Prefix(string p) => clips.Where(c => c.StartsWith(p, StringComparison.Ordinal)).OrderBy(c => c, StringComparer.Ordinal).FirstOrDefault();
            Put(plan, "idle", Exact("ba10_waitA01") ?? Prefix("ba10_wait"));
            Put(plan, "appear", Exact("ba01_land01") ?? Exact("ba01_landA01") ?? Prefix("ba01_land"));
            Put(plan, "cry", Exact("ba02_roar01") ?? Prefix("ba02_roar"));
            Put(plan, "physical", Exact("ba20_buturi01") ?? Prefix("ba20_buturi"));
            Put(plan, "special", Exact("ba21_tokusyu01") ?? Prefix("ba21_tokusyu"));
            Put(plan, "hit", Exact("ba30_damageS01") ?? Prefix("ba30_damage"));
            Put(plan, "faint", Exact("ba41_down01") ?? Prefix("ba41_down"));
            Put(plan, "blink", Exact("eye01"));
            return plan;
        }

        static void Put(ClipPlan plan, string name, string clip)
        {
            if (clip != null) plan.Sources[name] = new List<string> { clip };
        }

        static void Put(ClipPlan plan, string name, List<string> clips)
        {
            if (clips != null && clips.Count > 0) plan.Sources[name] = clips;
        }

        // ---- The final clip list ----

        public static void Assemble(ModelData model, ClipPlan plan, Dictionary<string, AnimationData> loaded, Skinner skinner, JsonObject report)
        {
            var final = new Dictionary<string, AnimationData>();
            var from = new JsonObject();
            var idleSource = loaded[plan.Sources["idle"][0]];
            float idleTop = skinner.Bounds(idleSource, 0).Max.Y;

            foreach (var kv in plan.Sources)
            {
                var clips = kv.Value.ToList();
                // The down sequence: _start falls, _loop stays down, _end gets back up (measured on every B0
                // model: _end finishes at the idle's full height). A fainted Pokemon stays down, so an _end
                // that finishes standing is left out and faint ends on the down pose.
                if (kv.Key == "faint" && clips.Count == 3)
                {
                    float lying = Top(skinner, loaded[clips[1]], last: true) / idleTop;
                    float after = Top(skinner, loaded[clips[2]], last: true) / idleTop;
                    report["faintTops"] = new JsonObject { ["loop"] = Math.Round(lying, 3), ["endLast"] = Math.Round(after, 3) };
                    if (after > 0.9f) clips.RemoveAt(2);
                }
                var parts = clips.Select(c => loaded[c]).ToList();
                final[kv.Key] = parts.Count == 1 ? Copy(parts[0], kv.Key) : Join(model, kv.Key, parts);
                from[kv.Key] = string.Join(" + ", clips);
            }
            final["idle"].Loop = true;

            // blink only when the eye clip moves bones (in many models it only scrolls the eye texture).
            // Kept, it carries only the bones it moves (the eyelids), so it can play over idle at full weight.
            if (final.TryGetValue("blink", out var blink))
            {
                blink.Tracks = blink.Tracks.Where(Moves).ToList();
                if (blink.Tracks.Count == 0)
                {
                    final.Remove("blink");
                    from["blink"] = "left out: " + from["blink"] + " moves no bones (texture animation only)";
                }
            }

            // A missing battle clip falls back to the nearest one.
            void Fallback(string name, params string[] options)
            {
                if (final.ContainsKey(name)) return;
                var use = options.FirstOrDefault(final.ContainsKey);
                if (use == null) return;
                final[name] = Copy(final[use], name);
                from[name] = "fallback: " + use;
            }
            Fallback("physical", "special");
            Fallback("special", "physical");
            Fallback("appear", "idle");
            Fallback("cry", "physical", "special");
            Fallback("hit", "idle");
            Fallback("faint", "hit");

            model.Animations = Names.Where(final.ContainsKey).Select(n => final[n]).ToList();
            var durations = new JsonObject();
            foreach (var a in model.Animations) durations[a.Name] = Math.Round(a.Duration, 3);
            report["clips"] = durations;
            report["clipSources"] = from;

            // Every notify of the battle clips, for the record (cries, sounds, effect triggers).
            var events = new JsonObject();
            foreach (var a in model.Animations.Where(a => a.Events.Count > 0 && a.Name != "blink"))
                events[a.Name] = new JsonArray(a.Events.Select(e => (JsonNode)new JsonObject
                {
                    ["t"] = Math.Round(e.StartFrame / Fps(a), 3),
                    ["type"] = e.Type,
                    ["params"] = new JsonObject(e.Parameters.Select(p => new KeyValuePair<string, JsonNode>(p.Key, p.Value))),
                }).ToArray());
            if (events.Count > 0) report["events"] = events;
        }

        static float Top(Skinner s, AnimationData a, bool last) => s.Bounds(a, last ? Math.Max(0, a.FrameCount - 1) : 0).Max.Y;

        static float Fps(AnimationData a) => a.FramesPerSecond > 0 ? a.FramesPerSecond : 30;

        static AnimationData Copy(AnimationData a, string name) => new AnimationData
        {
            Name = name,
            FramesPerSecond = a.FramesPerSecond,
            FrameCount = a.FrameCount,
            Loop = a.Loop && name == "idle",
            Tracks = a.Tracks,
            Events = a.Events,
            RootSpeed = a.RootSpeed,
        };

        static bool Moves(BoneTrackData t) =>
            t.Rotation != null && t.Rotation.Values.Any(q => Math.Abs(Quaternion.Dot(q, t.Rotation.Values[0])) < 0.9999f)
            || t.Translation != null && t.Translation.Values.Any(v => Vector3.Distance(v, t.Translation.Values[0]) > 1e-4f)
            || t.Scale != null && t.Scale.Values.Any(v => Vector3.Distance(v, t.Scale.Values[0]) > 1e-4f);

        // Clips played back to back as one: each part keeps its own keys, with explicit keys at its first
        // and last frame (a channel a part lacks holds the rest pose there), and the next part starts one
        // frame later. Run after ScaleCompensation, so its helper tracks join like any other.
        public static AnimationData Join(ModelData model, string name, List<AnimationData> parts)
        {
            float fps = Fps(parts[0]);
            var result = new AnimationData { Name = name, FramesPerSecond = fps, Loop = false };
            var nodes = new Dictionary<string, NodeData>();
            foreach (var n in model.Nodes) if (n.Name != null && !nodes.ContainsKey(n.Name)) nodes[n.Name] = n;

            var offsets = new List<float>();
            var lengths = new List<float>();
            float offset = 0;
            foreach (var p in parts)
            {
                float length = Math.Max(0, p.FrameCount - 1) * fps / Fps(p);
                offsets.Add(offset);
                lengths.Add(length);
                offset += length + 1;
            }
            result.FrameCount = (int)Math.Round(offset);

            var bones = parts.SelectMany(p => p.Tracks).Select(t => t.Bone).Where(b => b != null && nodes.ContainsKey(b)).Distinct().ToList();
            foreach (var bone in bones)
            {
                var node = nodes[bone];
                var tracks = parts.Select(p => p.Tracks.FirstOrDefault(t => t.Bone == bone)).ToList();
                var joined = new BoneTrackData { Bone = bone };
                if (tracks.Any(t => t?.Translation != null))
                    joined.Translation = JoinKeys(parts, offsets, lengths, tracks.Select(t => t?.Translation).ToList(), node.Translation, Vector3.Lerp);
                if (tracks.Any(t => t?.Rotation != null))
                    joined.Rotation = JoinKeys(parts, offsets, lengths, tracks.Select(t => t?.Rotation).ToList(), node.Rotation, Quaternion.Slerp);
                if (tracks.Any(t => t?.Scale != null))
                    joined.Scale = JoinKeys(parts, offsets, lengths, tracks.Select(t => t?.Scale).ToList(), node.Scale, Vector3.Lerp);
                result.Tracks.Add(joined);
            }
            for (int i = 0; i < parts.Count; i++)
                foreach (var e in parts[i].Events)
                    result.Events.Add(new AnimationEvent
                    {
                        Type = e.Type,
                        StartFrame = (int)Math.Round(offsets[i] + e.StartFrame * fps / Fps(parts[i])),
                        DurationFrames = e.DurationFrames,
                        Parameters = e.Parameters,
                    });
            return result;
        }

        static Keys<T> JoinKeys<T>(List<AnimationData> parts, List<float> offsets, List<float> lengths, List<Keys<T>> keys, T rest, Func<T, T, float, T> lerp)
        {
            var times = new List<float>();
            var values = new List<T>();
            void Add(float t, T v)
            {
                if (times.Count > 0 && t <= times[times.Count - 1] + 1e-4f) return;
                times.Add(t);
                values.Add(v);
            }
            for (int i = 0; i < parts.Count; i++)
            {
                var k = keys[i];
                float scale = parts[i].FrameCount > 1 ? lengths[i] / (parts[i].FrameCount - 1) : 1;
                float last = Math.Max(0, parts[i].FrameCount - 1);
                if (k == null || k.Times.Length == 0)
                {
                    Add(offsets[i], rest);
                    Add(offsets[i] + lengths[i], rest);
                    continue;
                }
                Add(offsets[i], Skinner.Sample(k, 0, lerp));
                for (int j = 0; j < k.Times.Length; j++)
                    if (k.Times[j] > 0 && k.Times[j] < last) Add(offsets[i] + k.Times[j] * scale, k.Values[j]);
                Add(offsets[i] + lengths[i], Skinner.Sample(k, last, lerp));
            }
            return new Keys<T> { Times = times.ToArray(), Values = values.ToArray() };
        }

        // ---- When the attacks connect ----
        //
        // From the game's own notifies when it has them: Scarlet/Violet-family clips fire
        // "Pokemon_EffectStart" (the move's effect begins) at the moment of the strike. Otherwise
        // estimated: a physical attack connects at its furthest reach forward (+Z), a special one
        // halfway through its clip.
        public static JsonObject HitTimes(ModelData model, Skinner skinner, float height, JsonObject report)
        {
            var hit = new JsonObject();
            var how = new JsonObject();
            foreach (var name in new[] { "physical", "special" })
            {
                var a = model.Animations.FirstOrDefault(x => x.Name == name);
                if (a == null) continue;
                float fps = Fps(a);
                var effect = a.Events
                    .Where(e => e.Parameters.Values.Any(v => v.IndexOf("EffectStart", StringComparison.OrdinalIgnoreCase) >= 0))
                    .OrderBy(e => e.StartFrame).FirstOrDefault();
                if (effect != null && effect.StartFrame > 0)
                {
                    hit[name] = Math.Round(effect.StartFrame / fps, 3);
                    how[name] = "event";
                    continue;
                }
                if (name == "physical")
                {
                    float rest = skinner.Bounds(a, 0).Max.Z, best = rest;
                    int bestFrame = -1;
                    for (int f = 0; f < a.FrameCount; f++)
                    {
                        float z = skinner.Bounds(a, f, step: 4).Max.Z;
                        if (z > best) { best = z; bestFrame = f; }
                    }
                    if (bestFrame > 0 && best - rest > 0.05f * height)
                    {
                        hit[name] = Math.Round(bestFrame / fps, 3);
                        how[name] = $"estimated: furthest reach (+{(best - rest) / height:P0} of height)";
                        continue;
                    }
                }
                hit[name] = Math.Round(a.Duration * 0.5, 3);
                how[name] = "estimated: half the clip";
            }
            report["hitSource"] = how;
            return hit;
        }
    }
}
