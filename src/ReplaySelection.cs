using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace F10Recorder
{
    public sealed class Segment
    {
        public string Path;
        public double Start, End;
    }
    public static class ReplaySelection
    {
        public static List<Segment> Read(string manifest, string directory)
        {
            if (!File.Exists(manifest)) return new List<Segment>();
            string text;
            using (var stream = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream)) text = reader.ReadToEnd();
            var result = new List<Segment>();
            int lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0) return result;
            foreach (string line in text.Substring(0, lastNewline).Split('\n'))
            {
                Match m = Regex.Match(line.Trim(), "^(?:\"((?:[^\"]|\"\")*)\"|([^,]+)),([0-9.]+),([0-9.]+)$");
                double start, end;
                if (!m.Success || !double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out start) || !double.TryParse(m.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out end) || end <= start) continue;
                string filename = m.Groups[1].Success ? m.Groups[1].Value.Replace("\"\"", "\"") : m.Groups[2].Value;
                string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, filename));
                if (!path.StartsWith(System.IO.Path.GetFullPath(directory) + System.IO.Path.DirectorySeparatorChar, System.IO.Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Segment path is outside the buffer.");
                result.Add(new Segment { Path = path, Start = start, End = end });
            }
            return result.OrderBy(s => s.Start).ToList();
        }
        public static List<Segment> Select(List<Segment> segments, double end, double seconds)
        {
            // Begin on a keyframe at/after the requested start; copy saves are at most one GOP short.
            double start = Math.Max(0, end - seconds);
            return segments.Where(s => s.Start + 0.001 >= start && s.Start < end).OrderBy(s => s.Start).ToList();
        }
    }
}
