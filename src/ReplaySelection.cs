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
            // Decode the preceding independently decodable head as well. Export
            // trims its unwanted pictures; skipping it loses up to a whole GOP.
            double start = Math.Max(0, end - seconds);
            return segments.Where(s => s.End > start && s.Start < end).OrderBy(s => s.Start).ToList();
        }
    }

    internal sealed record ReplayInterval(double Start, double End, double RequestedSeconds)
    {
        internal double Seconds => End - Start;
        internal bool LimitedHistory => Seconds < RequestedSeconds - 0.000001;
        internal static ReplayInterval AtRequest(List<Segment> available, double end, double seconds)
        {
            if (!double.IsFinite(end) || !double.IsFinite(seconds) || seconds <= 0 || available.Count == 0)
                throw new IOException("At least one second of replay footage is required.");
            var interval = new ReplayInterval(Math.Max(Math.Max(0, end - seconds), available.Min(s => s.Start)), end, seconds);
            if (interval.Seconds < 1 - 0.000000001) throw new IOException("At least one second of replay footage is required.");
            return interval;
        }
        internal void RequireCoverage(IReadOnlyList<Segment> segments)
        {
            double covered = Start;
            double? previousEnd = null;
            foreach (var segment in segments)
            {
                if (!double.IsFinite(segment.Start) || !double.IsFinite(segment.End) || segment.End <= segment.Start
                    || segment.Start > covered + 0.000001 || previousEnd.HasValue && segment.Start < previousEnd.Value - 0.000001)
                    throw new IOException("Requested replay history is unavailable or contains a gap; no padded replay was saved.");
                covered = Math.Max(covered, segment.End);
                previousEnd = segment.End;
            }
            if (covered < End - 0.000001)
                throw new IOException("Requested replay endpoint is unavailable; no shortened replay was saved.");
        }
    }
}
