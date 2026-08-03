using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using netDxf;
using netDxf.Blocks;
using netDxf.Entities;

namespace CadToolsApp.Services
{
    // Reads the drawing's own "Legende:" block to learn which (color, linetype) pair identifies
    // each cable base type — this varies per company/template, so it must be read from each
    // drawing rather than hardcoded. Falls back to an external config file (see LoadFallback)
    // when a drawing has no legend, or a label can't be resolved to a nearby swatch.
    public static class LegendReader
    {
        public const string ExistingCableType = "EXISTING"; // dashed "bestaande" cable — excluded from totals, not a row key

        // Predicates rather than a plain substring, since "HDPE %%C50" and "bestaande HDPE %%C50"
        // both contain "HDPE" — a naive Contains() for the new-HDPE row matches whichever of the
        // two happens to come first in the (unordered) entity collection, silently picking the
        // wrong swatch. "label" here is only for logging when a row can't be resolved.
        private static readonly (string label, string baseType, Func<string, bool> match)[] TargetLabels =
        {
            ("DB7", "DB7", t => t.Equals("DB7", StringComparison.OrdinalIgnoreCase)),
            ("DB2", "DB2", t => t.Equals("DB2", StringComparison.OrdinalIgnoreCase)),
            ("coax kabel", "COAX", t => t.StartsWith("coax", StringComparison.OrdinalIgnoreCase)),
            ("bestaande HDPE", ExistingCableType, t => t.StartsWith("bestaande", StringComparison.OrdinalIgnoreCase)),
            ("HDPE", "HDPE", t => t.StartsWith("HDPE", StringComparison.OrdinalIgnoreCase)),
        };

        private const double MaxSwatchDistance = 25.0;

        public record Result(
            Dictionary<(short Color, string Linetype), string> TypeMap,
            List<string> UnresolvedLabels,
            string Source);

        public static Result BuildTypeMap(DxfDocument doc, string? fallbackConfigPath = null)
        {
            var map = new Dictionary<(short, string), string>();
            var unresolved = new List<string>();

            Block? legend = doc.Blocks.FirstOrDefault(b =>
                b.Entities.OfType<MText>().Any(m => m.Value.Contains("Legende", StringComparison.OrdinalIgnoreCase)));

            if (legend == null)
                return ApplyFallback(map, TargetLabels.Select(t => t.label).ToList(),
                    "no Legende block found", fallbackConfigPath);

            var heading = legend.Entities.OfType<MText>()
                .First(m => m.Value.Contains("Legende", StringComparison.OrdinalIgnoreCase));

            // Legend rows sit below their own "Legende:" heading; text above it (same Y range as
            // the summary table's row labels) must never be considered — "DB7"/"DB2"/"HDPE" also
            // appear there as substrings of the table's longer row labels (e.g. "DB7 glasvezelbuis...").
            var belowHeading = legend.Entities.OfType<MText>()
                .Where(m => m.Position.Y < heading.Position.Y)
                .Select(m => (pos: m.Position, text: StripFormatting(m.Value)))
                .ToList();

            var swatchCandidates = legend.Entities.OfType<Polyline2D>().ToList();

            foreach (var (label, baseType, matchFn) in TargetLabels)
            {
                var match = belowHeading.FirstOrDefault(t => matchFn(t.text));

                if (match.text == null)
                {
                    unresolved.Add(label);
                    continue;
                }

                var swatch = swatchCandidates
                    .Select(p => (poly: p, dist: Distance(p, match.pos)))
                    .Where(x => x.dist < MaxSwatchDistance)
                    .OrderBy(x => x.dist)
                    .Select(x => x.poly)
                    .FirstOrDefault();

                if (swatch == null)
                {
                    unresolved.Add(label);
                    continue;
                }

                var (color, linetype) = DxfService.ResolveColorLinetype(swatch);
                map[(color, linetype)] = baseType;
            }

            string source = $"Legende block '{legend.Name}'";
            return ApplyFallback(map, unresolved, source, fallbackConfigPath);
        }

        // Fills in whatever the legend couldn't resolve from an optional external config file
        // (KEY=color,linetype per line, KEY one of DB7/DB2/HDPE/COAX/EXISTING) — the rarely-
        // exercised path for a template revision this heuristic doesn't handle. Lenient like
        // MainWindow's xsec_template.txt: a missing or malformed file is not an error, just means
        // no fallback is available.
        private static Result ApplyFallback(
            Dictionary<(short, string), string> map, List<string> unresolved, string source, string? path)
        {
            if (unresolved.Count == 0 || string.IsNullOrEmpty(path) || !File.Exists(path))
                return new Result(map, unresolved, unresolved.Count == 0 ? source : $"{source} (unresolved: {string.Join(", ", unresolved)})");

            var fallback = LoadFallbackConfig(path);
            var stillUnresolved = new List<string>();
            var recoveredBaseTypes = new List<string>();
            foreach (var label in unresolved)
            {
                var entry = TargetLabels.First(t => t.label == label);
                if (fallback.TryGetValue(entry.baseType, out var colorLinetype))
                {
                    map[colorLinetype] = entry.baseType;
                    recoveredBaseTypes.Add(entry.baseType);
                }
                else
                {
                    stillUnresolved.Add(label);
                }
            }

            string finalSource = recoveredBaseTypes.Count > 0
                ? $"{source} + {Path.GetFileName(path)} fallback for: {string.Join(", ", recoveredBaseTypes)}"
                : source;
            if (stillUnresolved.Count > 0)
                finalSource += $" (still unresolved: {string.Join(", ", stillUnresolved)})";

            return new Result(map, stillUnresolved, finalSource);
        }

        private static Dictionary<string, (short, string)> LoadFallbackConfig(string path)
        {
            var result = new Dictionary<string, (short, string)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;

                    string key = line[..eq].Trim();
                    string[] parts = line[(eq + 1)..].Split(',');
                    if (parts.Length != 2) continue;
                    if (short.TryParse(parts[0].Trim(), out short color))
                        result[key] = (color, parts[1].Trim());
                }
            }
            catch { /* malformed/unreadable fallback file — treat as no fallback available */ }
            return result;
        }

        private static double Distance(Polyline2D poly, Vector3 pos)
        {
            double best = double.MaxValue;
            foreach (var v in poly.Vertexes)
            {
                double dx = v.Position.X - pos.X, dy = v.Position.Y - pos.Y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < best) best = d;
            }
            return best;
        }

        private static string StripFormatting(string raw)
        {
            string s = System.Text.RegularExpressions.Regex.Replace(raw, @"\\[A-Za-z][^;]*;", "");
            s = s.Replace(@"\P", "\n").Replace(@"\p", "\n")
                 .Replace(@"\L", "").Replace(@"\l", "")
                 .Replace("{", "").Replace("}", "");
            return s.Trim();
        }
    }
}
