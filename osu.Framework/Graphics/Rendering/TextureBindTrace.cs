// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>
    /// Diagnostic tracer that records the sequence of texture-bind and wrap-mode batch breaks within a frame when <c>TORII_DRAW_CENSUS=1</c> or <c>TUMTHAI_DRAW_CENSUS=1</c> is set.
    /// </summary>
    public static class TextureBindTrace
    {
        private const int max_entries = 4000;

        private static readonly List<string> entries = new List<string>(max_entries);
        private static readonly object syncLock = new object();

        private static bool? enabled;

        public static bool Enabled => enabled ??=
            Environment.GetEnvironmentVariable("TUMTHAI_DRAW_CENSUS") == "1"
            || Environment.GetEnvironmentVariable("TORII_DRAW_CENSUS") == "1";

        private static volatile bool recording;

        public static bool Recording => recording;

        public static string CurrentSource;

        public static void Start()
        {
            if (!Enabled)
                return;

            lock (syncLock)
            {
                entries.Clear();
                recording = true;
            }
        }

        public static void Record(INativeTexture texture, string reason)
        {
            if (!Recording)
                return;

            lock (syncLock)
            {
                if (entries.Count >= max_entries)
                {
                    recording = false;
                    return;
                }

                string id = texture == null
                    ? "(null)"
                    : $"{texture.GetType().Name}#{texture.GetHashCode():x8} {texture.Width}x{texture.Height}";

                entries.Add($"{reason}\t{id}\t{CurrentSource ?? "?"}");
            }
        }

        public static void RecordWrap(string reason)
        {
            if (!Recording)
                return;

            lock (syncLock)
            {
                if (entries.Count >= max_entries)
                {
                    recording = false;
                    return;
                }

                entries.Add($"{reason}\t-");
            }
        }

        public static string Dump(string label)
        {
            List<string> snapshot;

            lock (syncLock)
            {
                recording = false;

                if (entries.Count == 0)
                    return null;

                snapshot = entries.ToList();
                entries.Clear();
            }

            string path = Path.Combine(Path.GetTempPath(), $"texture-trace-{label}-{DateTime.Now:HHmmss}.txt");

            using (var w = new StreamWriter(path))
            {
                w.WriteLine($"# {snapshot.Count} batch breaks recorded");
                w.WriteLine();

                w.WriteLine("# by reason:");

                foreach (var g in snapshot.GroupBy(x => x.Split('\t')[0]).OrderByDescending(g => g.Count()))
                    w.WriteLine($"{g.Count(),8}  {g.Key}");

                w.WriteLine();
                w.WriteLine("# distinct textures involved:");

                foreach (var g in snapshot.Select(x => x.Split('\t')[1]).Where(x => x != "-").GroupBy(x => x).OrderByDescending(g => g.Count()))
                    w.WriteLine($"{g.Count(),8}  {g.Key}");

                w.WriteLine();
                w.WriteLine("# drawables causing texture changes (top 15):");

                foreach (var g in snapshot.Select(x => x.Split('\t')).Where(p => p.Length > 2).GroupBy(p => p[2]).OrderByDescending(g => g.Count()).Take(15))
                    w.WriteLine($"{g.Count(),8}  {g.Key}");

                w.WriteLine();
                w.WriteLine("# sequence in order:");

                foreach (string e in snapshot)
                    w.WriteLine(e);
            }

            return path;
        }
    }
}
