using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using CadToolsApp.Models;
using netDxf;
using netDxf.Blocks;
using netDxf.Entities;
using netDxf.Tables;

namespace CadToolsApp.Services
{
    // Fills the title-block text the template leaves blank, and re-annotates the two values the
    // template hardcodes (trench depth, plot scale) so the printed sheet cannot contradict the
    // geometry it sits next to.
    //
    // The template has no ATTDEFs — every label is a plain MText — so each field is placed
    // relative to its own label and clipped to the grid cell that label sits in, all read from
    // the block's own line work. Nothing here uses an absolute coordinate, so a re-issued
    // template with a shifted grid still lands correctly.
    public static class TitleBlockWriter
    {
        // Written values go on their own layer so a re-run replaces its predecessor instead of
        // stacking a second copy of every field on top of the first.
        public const string FillLayer = "TITLEBLOCK_FILL";

        private const double Gap        = 2.0;  // block units between a label and its value
        private const double ValueScale = 0.85; // values print slightly smaller than their labels

        private enum Place
        {
            RightOf,  // value shares the label's baseline, to its right
            Below,    // value starts under the label and may wrap inside the cell
        }

        private sealed record FieldSpec(string Label, Place Place, Func<SheetInfo, string> Value);

        private static readonly List<FieldSpec> Fields = new()
        {
            new("Begin der werken:",  Place.RightOf, i => i.BeginDerWerken),
            new("Einde der werken:",  Place.RightOf, i => i.EindeDerWerken),
            new("Geplaatste lengte:", Place.RightOf, i => i.GeplaatsteLengte),
            new("Gesloopte lengte:",  Place.RightOf, i => i.GesloopteLengte),
            new("Uitgevoerd door:",   Place.Below,   i => i.UitgevoerdDoor),
            new("Dossiernummer:",     Place.Below,   i => i.Dossiernummer),
            new("Opgemaakt:",         Place.RightOf, i => i.Opgemaakt),
            new("Nagekeken:",         Place.RightOf, i => i.Nagekeken),
            new("Getekend door:",     Place.RightOf, i => i.GetekendDoor),
        };

        private readonly record struct Cell(double Left, double Right, double Bottom, double Top);

        public static (int written, List<string> log) Write(DxfDocument doc, Block blk, SheetInfo info)
        {
            var log = new List<string>();
            Layer fill = EnsureLayer(doc, FillLayer);
            int removed = RemovePreviousFill(blk);
            if (removed > 0) log.Add($"Title block: cleared {removed} value(s) from a previous run.");

            int written = 0;
            var missing = new List<string>();

            foreach (var spec in Fields)
            {
                string value = spec.Value(info)?.Trim() ?? "";
                if (value.Length == 0) continue;   // blank in the samples too — leave the cell empty

                var label = FindLabel(blk, spec.Label);
                if (label == null) { missing.Add(spec.Label); continue; }

                var (pos, height, wrap) = PlaceValue(blk, label, spec.Place);
                blk.Entities.Add(new MText(value, pos, height, wrap)
                {
                    Layer = fill,
                    AttachmentPoint = MTextAttachmentPoint.TopLeft,
                    Style = label.Style,
                });
                written++;
            }

            written += WriteStreetTitle(blk, fill, info, log);

            if (missing.Count > 0)
                log.Add($"Title block: no label found for {string.Join(", ", missing)} — those field(s) skipped.");

            int depth = ApplyTrenchDepth(blk, info.TrenchDepthCm);
            log.Add(depth > 0
                ? $"Trench depth: {info.TrenchDepthCm}cm applied to {depth} annotation(s) in the cross-section details."
                : "Trench depth: no depth annotation found to update.");

            int scaleTexts = ApplyScaleAnnotation(blk, info.ScaleDenominator);
            log.Add(scaleTexts > 0
                ? $"Scale annotation: {scaleTexts} text(s) updated to {info.ScaleLabel} (note + scale-bar ticks)."
                : "Scale annotation: no 'SCHAAL 1/x' text found to update.");

            log.Add($"Title block: {written} field(s) written on layer '{FillLayer}'.");
            return (written, log);
        }

        // ── Field placement ───────────────────────────────────────────────────
        private static MText? FindLabel(Block blk, string label) =>
            blk.Entities.OfType<MText>()
                .FirstOrDefault(m => DxfService.StripMTextCodes(m.Value)
                    .Equals(label, StringComparison.OrdinalIgnoreCase));

        private static (Vector3 pos, double height, double wrap) PlaceValue(Block blk, MText label, Place place)
        {
            var cell = CellAt(blk, label.Position.X, label.Position.Y);
            double height = Math.Max(1.5, label.Height * ValueScale);

            if (place == Place.RightOf)
            {
                // RectangleWidth is the label's own column width in this template, so it is the
                // gap-free way to clear the label text without measuring glyphs.
                double x = label.Position.X + Math.Max(label.RectangleWidth, label.Height * 6) + Gap;
                x = Math.Min(x, cell.Right - Gap);
                return (new Vector3(x, label.Position.Y, 0), height, Math.Max(cell.Right - x - Gap, height * 4));
            }

            double belowY = label.Position.Y - label.Height - Gap * 0.5;
            double wrap = Math.Max(cell.Right - label.Position.X - Gap, height * 4);
            return (new Vector3(label.Position.X, belowY, 0), height, wrap);
        }

        // The street/municipality print in the empty band under "Vergunningsaanvraag:" — located
        // as the next full-width cell below that heading rather than by coordinate.
        private static int WriteStreetTitle(Block blk, Layer fill, SheetInfo info, List<string> log)
        {
            string text = string.Join("\\P",
                new[] { info.Street, info.City }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (text.Length == 0) return 0;

            var heading = FindLabel(blk, "Vergunningsaanvraag:");
            if (heading == null)
            {
                log.Add("Title block: 'Vergunningsaanvraag:' heading not found — street name skipped.");
                return 0;
            }

            double headingBottom = heading.Position.Y - heading.Height;
            var dividers = FullWidthDividers(blk);
            double top = dividers.Where(y => y < headingBottom + 0.5).DefaultIfEmpty(double.NaN).Max();
            if (double.IsNaN(top))
            {
                log.Add("Title block: no divider below 'Vergunningsaanvraag:' — street name skipped.");
                return 0;
            }
            double bottom = dividers.Where(y => y < top - 0.5).DefaultIfEmpty(top - heading.Height * 2).Max();

            var cell = CellAt(blk, heading.Position.X, (top + bottom) / 2.0);
            blk.Entities.Add(new MText(text,
                new Vector3((cell.Left + cell.Right) / 2.0, (top + bottom) / 2.0, 0),
                heading.Height * 0.45, cell.Right - cell.Left - Gap * 4)
            {
                Layer = fill,
                AttachmentPoint = MTextAttachmentPoint.MiddleCenter,
                Style = heading.Style,
            });
            log.Add($"Title block: sheet title '{text.Replace("\\P", " ")}' placed under 'Vergunningsaanvraag:'.");
            return 1;
        }

        // ── Re-annotating the template's hardcoded values ─────────────────────
        // The depth appears twice as a sentence ("...op 80cm diepte...") and twice as the
        // dimension text on the trench detail ("0,8 m"). The shallower "0,3 m" cover and "0,1 m"
        // warning-mesh dimensions are the same in both variants and must not be touched.
        private static readonly Regex DepthSentence = new(@"\b\d{2}cm\s+diepte", RegexOptions.IgnoreCase);
        private static readonly Regex DepthDimension = new(@"^0,[5-9]\s*m$", RegexOptions.IgnoreCase);

        private static int ApplyTrenchDepth(Block blk, int depthCm)
        {
            string sentence = $"{depthCm}cm diepte";
            string dimension = (depthCm / 100.0).ToString("0.0", CultureInfo.InvariantCulture)
                                   .Replace('.', ',') + " m";   // Dutch decimal comma, as drawn
            int hits = 0;

            foreach (var e in blk.Entities.ToList())
            {
                string raw = e switch { Text t => t.Value, MText m => m.Value, _ => null! };
                if (raw == null) continue;

                string updated = raw;
                if (DepthSentence.IsMatch(DxfService.StripMTextCodes(raw)))
                    updated = DepthSentence.Replace(updated, sentence);
                if (DepthDimension.IsMatch(DxfService.StripMTextCodes(raw).Trim()))
                    updated = dimension;

                if (updated == raw) continue;
                if (e is Text t2) t2.Value = updated;
                if (e is MText m2) m2.Value = updated;
                hits++;
            }
            return hits;
        }

        private static readonly Regex ScaleNote = new(@"(SCHAAL\s*1\s*/\s*)(\d+)", RegexOptions.IgnoreCase);

        // The scale bar is drawn once, at a fixed length; only its tick labels carry the scale.
        // They are relabelled by the same factor the scale changed by, so bar and note agree.
        private static int ApplyScaleAnnotation(Block blk, int denominator)
        {
            var note = blk.Entities.OfType<Text>()
                .FirstOrDefault(t => ScaleNote.IsMatch(t.Value));
            if (note == null) return 0;

            int oldDenominator = int.Parse(ScaleNote.Match(note.Value).Groups[2].Value);
            note.Value = ScaleNote.Replace(note.Value, m => m.Groups[1].Value + denominator);
            int hits = 1;
            if (oldDenominator == denominator) return hits;

            double factor = (double)denominator / oldDenominator;
            foreach (var tick in blk.Entities.OfType<Text>()
                         .Where(t => !ReferenceEquals(t, note))
                         .Where(t => Math.Abs(t.Position.Y - note.Position.Y) < note.Height * 3)
                         .Where(t => Regex.IsMatch(t.Value.Trim(), @"^\d+(\s*m)?$")))
            {
                var m = Regex.Match(tick.Value.Trim(), @"^(\d+)(\s*m)?$");
                double scaled = int.Parse(m.Groups[1].Value) * factor;
                tick.Value = $"{Math.Round(scaled):0}{m.Groups[2].Value}";
                hits++;
            }
            return hits;
        }

        // ── Grid geometry read from the template's own line work ──────────────
        internal readonly record struct Rect(double Left, double Bottom, double Width, double Height);

        // The area the "Ligging" locator map occupies: the band between the "Ligging:" heading
        // and the next divider below it, inset so the image does not touch the grid lines.
        internal static Rect? FindLiggingImageArea(Block blk, double inset = 4.0)
        {
            var heading = FindLabel(blk, "Ligging:");
            if (heading == null) return null;

            double top = heading.Position.Y - heading.Height - inset;
            double bottom = FullWidthDividers(blk)
                .Where(y => y < top - 1.0)
                .DefaultIfEmpty(double.NaN)
                .Max();
            if (double.IsNaN(bottom)) return null;
            bottom += inset;

            var cell = CellAt(blk, heading.Position.X, (top + bottom) / 2.0);
            double left = cell.Left + inset, right = cell.Right - inset;
            if (right - left < 1.0 || top - bottom < 1.0) return null;

            return new Rect(left, bottom, right - left, top - bottom);
        }

        private static Cell CellAt(Block blk, double x, double y)
        {
            var bbox = BlockLineBounds(blk);
            double left = bbox.Left, right = bbox.Right, bottom = bbox.Bottom, top = bbox.Top;

            foreach (var l in blk.Entities.OfType<Line>())
            {
                double x1 = l.StartPoint.X, x2 = l.EndPoint.X;
                double y1 = Math.Min(l.StartPoint.Y, l.EndPoint.Y), y2 = Math.Max(l.StartPoint.Y, l.EndPoint.Y);

                if (Math.Abs(x1 - x2) < 0.01 && y >= y1 - 0.5 && y <= y2 + 0.5)
                {
                    if (x1 > x + 0.5 && x1 < right) right = x1;
                    if (x1 < x - 0.5 && x1 > left) left = x1;
                }
                else if (Math.Abs(l.StartPoint.Y - l.EndPoint.Y) < 0.01)
                {
                    double xa = Math.Min(x1, x2), xb = Math.Max(x1, x2);
                    if (x < xa - 0.5 || x > xb + 0.5) continue;
                    double ly = l.StartPoint.Y;
                    if (ly > y + 0.5 && ly < top) top = ly;
                    if (ly < y - 0.5 && ly > bottom) bottom = ly;
                }
            }
            return new Cell(left, right, bottom, top);
        }

        // Y positions of the dividers that span the whole title-block column.
        private static List<double> FullWidthDividers(Block blk)
        {
            var bounds = BlockLineBounds(blk);
            double span = (bounds.Right - bounds.Left) * 0.9;
            return blk.Entities.OfType<Line>()
                .Where(l => Math.Abs(l.StartPoint.Y - l.EndPoint.Y) < 0.01)
                .Where(l => Math.Abs(l.EndPoint.X - l.StartPoint.X) >= span)
                .Select(l => l.StartPoint.Y)
                .Distinct()
                .OrderByDescending(v => v)
                .ToList();
        }

        // Bounds of the title-block column only: the widest run of grid lines, deliberately
        // ignoring the sheet-border polyline that BlockGeometryBBox needs.
        private static Cell BlockLineBounds(Block blk)
        {
            double left = double.MaxValue, right = double.MinValue, bottom = double.MaxValue, top = double.MinValue;
            foreach (var l in blk.Entities.OfType<Line>())
            {
                left = Math.Min(left, Math.Min(l.StartPoint.X, l.EndPoint.X));
                right = Math.Max(right, Math.Max(l.StartPoint.X, l.EndPoint.X));
                bottom = Math.Min(bottom, Math.Min(l.StartPoint.Y, l.EndPoint.Y));
                top = Math.Max(top, Math.Max(l.StartPoint.Y, l.EndPoint.Y));
            }
            if (left > right) return new Cell(0, 0, 0, 0);
            return new Cell(left, right, bottom, top);
        }

        private static int RemovePreviousFill(Block blk)
        {
            var stale = blk.Entities
                .Where(e => e.Layer != null && e.Layer.Name.Equals(FillLayer, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var e in stale) blk.Entities.Remove(e);
            return stale.Count;
        }

        internal static Layer EnsureLayer(DxfDocument doc, string name)
        {
            if (doc.Layers.Contains(name)) return doc.Layers[name];
            var layer = new Layer(name);
            doc.Layers.Add(layer);
            return layer;
        }
    }
}
