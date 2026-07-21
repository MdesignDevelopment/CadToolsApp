using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CadToolsApp.Models;
using netDxf;
using netDxf.Blocks;
using netDxf.Entities;
using netDxf.Tables;

namespace CadToolsApp.Services
{
    public class DxfService
    {
        private const string SECTION_LAYER = "Cross sections";
        private const string CABLE_LAYER   = "PDDucts BO";

        // ── Load / Save ────────────────────────────────────────────────────────
        public DxfDocument Load(string path)
        {
            var doc = DxfDocument.Load(path);
            doc.Entities.ActiveLayout = "Model";
            return doc;
        }

        public void Save(DxfDocument doc, string path) => doc.Save(path);

        // ── Find section points on SECTION_LAYER ───────────────────────────────
        public List<SectionInfo> FindSectionPoints(DxfDocument doc, string folder)
        {
            var pts = new List<(int id, double x, double y)>();

            foreach (var e in doc.Entities.All)
            {
                if (!e.Layer.Name.Equals(SECTION_LAYER, StringComparison.OrdinalIgnoreCase))
                    continue;

                double? x = null, y = null;
                switch (e)
                {
                    case Circle  c:  x = c.Center.X;   y = c.Center.Y;   break;
                    case Point   p:  x = p.Position.X; y = p.Position.Y; break;
                    case Insert  i:  x = i.Position.X; y = i.Position.Y; break;
                    case Text    t:  x = t.Position.X; y = t.Position.Y; break;
                    case MText   m:  x = m.Position.X; y = m.Position.Y; break;
                    case Polyline2D lp when lp.Vertexes.Count > 0:
                        x = lp.Vertexes.Average(v => v.Position.X);
                        y = lp.Vertexes.Average(v => v.Position.Y);
                        break;
                }

                if (x.HasValue && y.HasValue)
                {
                    int id = pts.Count + 1;
                    pts.Add((id, x.Value, y.Value));
                    StreetViewService.StoreSectionCoords(id, x.Value, y.Value);
                }
            }

            return pts.Select(p => new SectionInfo(
                p.id,
                Path.Combine(folder, $"section_{p.id:D3}_left.jpg"),
                Path.Combine(folder, $"section_{p.id:D3}_right.jpg")
            )).ToList();
        }

        // ── Draw cross-section into doc ────────────────────────────────────────
        public void DrawCrossSection(DxfDocument doc, string sectionId,
            List<LayoutZone> zones, double ox, double oy,
            string? templateDxfPath = null)
        {
            if (!string.IsNullOrEmpty(templateDxfPath) && File.Exists(templateDxfPath))
                ImportTemplateBlocks(doc, templateDxfPath);

            EnsureXsecBlocks(doc);
            var layer = EnsureLayer(doc, SECTION_LAYER, 7);

            DrawXsecView(doc, layer, ox, oy, sectionId, zones,
                "Situatie vóór de werken");

            const double LBL_Y_ABS   = 0.70;
            const double ID_Y_OFF    = 6.80;
            const double ID_TH_OFF   = 0.80;
            const double SECTION_GAP = 2.5;
            double gedY = oy - (LBL_Y_ABS + SECTION_GAP + ID_Y_OFF + ID_TH_OFF);

            DrawXsecView(doc, layer, ox, gedY, sectionId, zones,
                "Situatie gedurende de werken");
        }

        // ── Apply layer cleanup ────────────────────────────────────────────────
        public List<string> ApplyCleanup(DxfDocument doc)
        {
            var exactOff = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ADN","ADT","HOT","WEGKNOOP","WEGSEGMENT2",
                "WPI1","WPI3","WPI4","WPI5","WPI6",
                "WRI1","WRI2","0","ADP"
            };

            var turnedOff = new List<string>();
            foreach (var layer in doc.Layers)
            {
                if (exactOff.Contains(layer.Name) ||
                    Regex.IsMatch(layer.Name, @"^GVP\d+$"))
                {
                    layer.IsVisible = false;
                    turnedOff.Add(layer.Name);
                }
            }

            foreach (var e in doc.Entities.All)
            {
                if (e.Layer != null && e.Layer.IsVisible)
                    e.Color = new AciColor((short)8);
            }

            return turnedOff;
        }

        // ── Fill summary table ─────────────────────────────────────────────────
        public (int filled, string log) FillTable(DxfDocument doc)
        {
            var allTexts = new List<(Vector3 pos, string text)>();
            foreach (var e in doc.Entities.All)
            {
                switch (e)
                {
                    case Text   t: allTexts.Add((t.Position, t.Value)); break;
                    case MText  m: allTexts.Add((m.Position, StripMText(m.Value))); break;
                }
            }

            var cables = new List<(double len, Vector3 mid, string label)>();
            foreach (var e in doc.Entities.All)
            {
                if (e is Polyline2D lp &&
                    lp.Layer.Name.Equals(CABLE_LAYER, StringComparison.OrdinalIgnoreCase))
                {
                    double  len = PolylineLength(lp);
                    Vector3 mid = PolylineMid(lp);
                    string label = NearestText(mid, allTexts);
                    cables.Add((len, mid, label));
                }
            }

            if (cables.Count == 0)
                return (0, $"No cables found on layer '{CABLE_LAYER}'.");

            var cableTotals  = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var methodTotals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (len, _, label) in cables)
            {
                string? ck = MatchCableKey(label);
                string? mk = MatchMethodKey(label);
                if (ck != null) { cableTotals.TryGetValue(ck,  out double cv); cableTotals[ck]  = cv + len; }
                if (mk != null) { methodTotals.TryGetValue(mk, out double mv); methodTotals[mk] = mv + len; }
            }
            methodTotals["TOTAAL"] = methodTotals.Values.Sum();

            Insert? tableInsert = FindTableInsert(doc);
            if (tableInsert == null)
                return (0, "Table block not found — no block contains the expected cable labels.");

            int filled = FillTableBlock(tableInsert.Block, cableTotals, methodTotals);
            string log = $"Cables: {string.Join(", ", cableTotals.Select(kv => $"{kv.Key}={kv.Value:F0}m"))}\n" +
                         $"Methods: {string.Join(", ", methodTotals.Select(kv => $"{kv.Key}={kv.Value:F0}m"))}\n" +
                         $"{filled} cell(s) written.";
            return (filled, log);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Drawing helpers
        // ─────────────────────────────────────────────────────────────────────

        private static Layer EnsureLayer(DxfDocument doc, string name, short colorIdx)
        {
            if (doc.Layers.Contains(name)) return doc.Layers[name];
            var layer = new Layer(name) { Color = new AciColor(colorIdx) };
            doc.Layers.Add(layer);
            return layer;
        }

        private static void DrawXsecView(DxfDocument doc, Layer layer,
            double ox, double oy, string sectionId, List<LayoutZone> zones, string title)
        {
            const double CELL_H   = 4.0;
            const double DIM_H    = 4.8;
            const double DIM_TICK = 0.15;
            const double LBL_Y    = -0.70;
            const double ID_Y     = 6.80;
            const double TITLE_Y  = 5.90;
            const double GND_EXT  = 0.50;
            const double ID_TH    = 0.80;
            const double TITLE_TH = 0.45;
            const double LBL_TH   = 0.40;
            const double DIM_TH   = 0.35;

            double totalW = zones.Sum(z => z.Width);

            AddLine(doc, layer, ox - GND_EXT, oy, ox + totalW + GND_EXT, oy);
            AddLine(doc, layer, ox, oy + CELL_H, ox + totalW, oy + CELL_H);
            AddLine(doc, layer, ox, oy, ox, oy + CELL_H);

            double x = ox;
            foreach (var zone in zones)
            {
                double x2 = x + zone.Width, cx = (x + x2) / 2.0;

                AddLine(doc, layer, x2, oy, x2, oy + CELL_H);
                AddLine(doc, layer, x, oy + DIM_H, x2, oy + DIM_H);
                AddLine(doc, layer, x, oy + DIM_H - DIM_TICK, x, oy + DIM_H + DIM_TICK);
                AddLine(doc, layer, x2, oy + DIM_H - DIM_TICK, x2, oy + DIM_H + DIM_TICK);

                AddText(doc, layer, zone.Width.ToString("0.##"),
                    cx, oy + DIM_H + 0.12, DIM_TH, center: true);

                AddText(doc, layer, ZoneMeta.DrawingLabel(zone.Type),
                    cx, oy + LBL_Y, LBL_TH, center: true);

                InsertIcon(doc, layer, zone, cx, oy, CELL_H);

                x = x2;
            }

            AddText(doc, layer, sectionId,  ox,       oy + ID_Y,    ID_TH,    center: false);
            AddText(doc, layer, title,       ox + 2.0, oy + TITLE_Y, TITLE_TH, center: false);
        }

        private static void AddLine(DxfDocument doc, Layer layer,
            double x1, double y1, double x2, double y2)
        {
            doc.Entities.Add(new Line(new Vector3(x1, y1, 0), new Vector3(x2, y2, 0))
            {
                Layer = layer,
                Color = new AciColor((short)7),
            });
        }

        private static void AddText(DxfDocument doc, Layer layer,
            string value, double x, double y, double height, bool center)
        {
            doc.Entities.Add(new Text(value, new Vector3(x, y, 0), height)
            {
                Layer     = layer,
                Color     = new AciColor((short)7),
                Alignment = center ? TextAlignment.MiddleCenter : TextAlignment.BaselineLeft,
            });
        }

        private static void InsertIcon(DxfDocument doc, Layer layer,
            LayoutZone zone, double cx, double groundY, double cellH)
        {
            string blockName = ZoneMeta.BlockName(zone.Type);
            if (blockName == null || !doc.Blocks.Contains(blockName)) return;

            var block = doc.Blocks[blockName];

            Vector3 insertPt;
            Vector3 scale;

            if (ZoneMeta.IsWidthFill(zone.Type))
            {
                double bw = zone.Width, bh = cellH * 0.65;
                insertPt = new Vector3(cx - bw / 2.0, groundY + cellH * 0.07, 0);
                scale    = new Vector3(bw, bh, 1.0);
            }
            else
            {
                var (rw, rh) = ZoneMeta.IconRef(zone.Type);
                double s = Math.Min(zone.Width * 0.80 / rw, cellH * 0.65 / rh);
                insertPt = new Vector3(cx - rw * s / 2.0, groundY + (cellH - rh * s) / 2.0, 0);
                scale    = new Vector3(s, s, 1.0);
            }

            doc.Entities.Add(new Insert(block, insertPt)
            {
                Scale = scale,
                Layer = layer,
            });
        }

        // ── Block definitions ─────────────────────────────────────────────────
        private static void EnsureXsecBlocks(DxfDocument doc)
        {
            EnsureBlock(doc, "XSEC_BERM",       CreateBerm);
            EnsureBlock(doc, "XSEC_BERM_TREES", CreateBermTrees);
            EnsureBlock(doc, "XSEC_FIETSPAD",   CreateFietspad);
            EnsureBlock(doc, "XSEC_VOETPAD",    (_) => { });
            EnsureBlock(doc, "XSEC_RIJBAAN_F",  CreateRijbaan);
            EnsureBlock(doc, "XSEC_RIJBAAN_B",  CreateRijbaanB);
        }

        private static void EnsureBlock(DxfDocument doc, string name,
            Action<Block> populate)
        {
            if (doc.Blocks.Contains(name)) return;
            var block = new Block(name);
            populate(block);
            doc.Blocks.Add(block);
        }

        private static void CreateBerm(Block b)
        {
            double[] xs = { 0.10, 0.28, 0.50, 0.68, 0.88 };
            double[] hs = { 0.52, 0.64, 0.56, 0.66, 0.50 };
            for (int i = 0; i < xs.Length; i++)
            {
                double bx = xs[i], h = hs[i];
                b.Entities.Add(BLine(bx, 0, bx - 0.07, h));
                b.Entities.Add(BLine(bx, 0, bx, h + 0.10));
                b.Entities.Add(BLine(bx, 0, bx + 0.07, h));
            }
        }

        private static void CreateBermTrees(Block b)
        {
            CreateBerm(b);
            b.Entities.Add(BLine(0.50, 0.65, 0.50, 0.85));
            b.Entities.Add(BLine(0.28, 0.65, 0.50, 0.95));
            b.Entities.Add(BLine(0.72, 0.65, 0.50, 0.95));
        }

        private static void CreateFietspad(Block b)
        {
            const double wr = 0.36;
            b.Entities.Add(new Circle(new Vector3(-0.60, wr, 0), wr));
            b.Entities.Add(new Circle(new Vector3( 0.60, wr, 0), wr));
            b.Entities.Add(BLine(-0.60, wr,   0.00, 0.52));
            b.Entities.Add(BLine( 0.60, wr,   0.00, 0.52));
            b.Entities.Add(BLine( 0.00, 0.52,-0.10, 1.08));
            b.Entities.Add(BLine(-0.10, 1.08, 0.58, 0.88));
            b.Entities.Add(BLine( 0.58, 0.88, 0.60, wr));
            b.Entities.Add(BLine( 0.55, 0.88, 0.42, 0.96));
            b.Entities.Add(BLine( 0.55, 0.88, 0.68, 0.94));
            b.Entities.Add(BLine(-0.22, 1.10, 0.04, 1.10));
        }

        private static void CreateRijbaan(Block b)
        {
            const double wr = 0.22;
            b.Entities.Add(new Circle(new Vector3(-0.75, wr, 0), wr));
            b.Entities.Add(new Circle(new Vector3( 0.75, wr, 0), wr));
            b.Entities.Add(BLine(-1.10, 0.28, 1.10, 0.28));
            b.Entities.Add(BLine(-1.10, 0.28,-1.10, 0.85));
            b.Entities.Add(BLine( 1.10, 0.28, 1.10, 0.85));
            b.Entities.Add(BLine(-1.10, 0.85, 1.10, 0.85));
            b.Entities.Add(BLine(-1.10, 0.85,-0.65, 1.38));
            b.Entities.Add(BLine(-0.65, 1.38, 0.65, 1.38));
            b.Entities.Add(BLine( 0.65, 1.38, 1.10, 0.85));
            b.Entities.Add(BLine(-0.62, 0.87,-0.30, 1.34));
            b.Entities.Add(BLine( 0.62, 0.87, 0.30, 1.34));
        }

        private static void CreateRijbaanB(Block b)
        {
            const double wr = 0.22;
            b.Entities.Add(new Circle(new Vector3( 0.75, wr, 0), wr));
            b.Entities.Add(new Circle(new Vector3(-0.75, wr, 0), wr));
            b.Entities.Add(BLine( 1.10, 0.28,-1.10, 0.28));
            b.Entities.Add(BLine( 1.10, 0.28, 1.10, 0.85));
            b.Entities.Add(BLine(-1.10, 0.28,-1.10, 0.85));
            b.Entities.Add(BLine( 1.10, 0.85,-1.10, 0.85));
            b.Entities.Add(BLine( 1.10, 0.85, 0.65, 1.38));
            b.Entities.Add(BLine( 0.65, 1.38,-0.65, 1.38));
            b.Entities.Add(BLine(-0.65, 1.38,-1.10, 0.85));
            b.Entities.Add(BLine( 0.62, 0.87, 0.30, 1.34));
            b.Entities.Add(BLine(-0.62, 0.87,-0.30, 1.34));
        }

        private static Line BLine(double x1, double y1, double x2, double y2) =>
            new Line(new Vector3(x1, y1, 0), new Vector3(x2, y2, 0));

        // ── Import XSEC_ blocks from a DXF template ────────────────────────────
        private static void ImportTemplateBlocks(DxfDocument doc, string templatePath)
        {
            try
            {
                var src = DxfDocument.Load(templatePath);
                foreach (var srcBlock in src.Blocks)
                {
                    if (!srcBlock.Name.StartsWith("XSEC_",
                        StringComparison.OrdinalIgnoreCase)) continue;
                    if (doc.Blocks.Contains(srcBlock.Name)) continue;

                    // Copy block geometry (Lines and Circles only)
                    var newBlock = new Block(srcBlock.Name);
                    foreach (var e in srcBlock.Entities)
                    {
                        EntityObject? copy = e switch
                        {
                            Line l  => new Line(l.StartPoint, l.EndPoint),
                            Circle c => new Circle(c.Center, c.Radius),
                            _       => null,
                        };
                        if (copy != null) newBlock.Entities.Add(copy);
                    }
                    doc.Blocks.Add(newBlock);
                }
            }
            catch { /* skip on error */ }
        }

        // ── FILLTABLE helpers ─────────────────────────────────────────────────
        private static readonly List<(string label, string key)> CableRows = new()
        {
            ("DB7 glasvezelbuis grijze streep",    "DB7-GREY"      ),
            ("DB7 glasvezelbuis oranje streep",    "DB7-ORANGE"    ),
            ("DB2 glasvezelbuis grijze streep",    "DB2-GREY"      ),
            ("HDPE50 glasvezelbuis grijze streep", "HDPE50-GREY"   ),
            ("HDPE50 glasvezelbuis oranje streep", "HDPE50-ORANGE" ),
            ("HDPE50 glasvezelbuis groen",         "HDPE50-GREEN"  ),
            ("Coax 14mm in groene buis %%C32mm",   "COAX14"        ),
            ("Coax 20mm in groene buis %%C40mm",   "COAX20"        ),
        };

        private static readonly List<(string label, string key)> MethodRows = new()
        {
            ("Sleuflengte",                        "SLEUF"     ),
            ("Doorsteek",                          "DOORSTEEK" ),
            ("Handboring",                         "HANDBORING"),
            ("Gestuurde lijnboring",               "LIJNBORING"),
            ("Gestuurde mantelboring %%C110mm",    "MANTEL110" ),
            ("Gestuurde mantelboring %%C125mm",    "MANTEL125" ),
            ("Gestuurde mantelboring %%C200mm",    "MANTEL200" ),
            ("Droogtrekken leiding",               "DROOGTREK" ),
            ("Totale aanleg lengte",               "TOTAAL"    ),
        };

        private static string? MatchCableKey(string text)
        {
            if (Regex.IsMatch(text, @"DB7",      RegexOptions.IgnoreCase)) return "DB7-GREY";
            if (Regex.IsMatch(text, @"DB2",      RegexOptions.IgnoreCase)) return "DB2-GREY";
            if (Regex.IsMatch(text, @"HDPE",     RegexOptions.IgnoreCase)) return "HDPE50-GREY";
            if (Regex.IsMatch(text, @"Coax.*14", RegexOptions.IgnoreCase)) return "COAX14";
            if (Regex.IsMatch(text, @"Coax.*20", RegexOptions.IgnoreCase)) return "COAX20";
            return null;
        }

        private static string? MatchMethodKey(string text)
        {
            if (Regex.IsMatch(text, @"mantelboring.*110", RegexOptions.IgnoreCase)) return "MANTEL110";
            if (Regex.IsMatch(text, @"mantelboring.*125", RegexOptions.IgnoreCase)) return "MANTEL125";
            if (Regex.IsMatch(text, @"mantelboring.*200", RegexOptions.IgnoreCase)) return "MANTEL200";
            if (Regex.IsMatch(text, @"mantelboring",      RegexOptions.IgnoreCase)) return "MANTEL125";
            if (Regex.IsMatch(text, @"lijnboring",        RegexOptions.IgnoreCase)) return "LIJNBORING";
            if (Regex.IsMatch(text, @"Handboring",        RegexOptions.IgnoreCase)) return "HANDBORING";
            if (Regex.IsMatch(text, @"Droogtrekken",      RegexOptions.IgnoreCase)) return "DROOGTREK";
            if (Regex.IsMatch(text, @"Doorsteek",         RegexOptions.IgnoreCase)) return "DOORSTEEK";
            return "SLEUF";
        }

        private static Insert? FindTableInsert(DxfDocument doc)
        {
            foreach (var ins in doc.Entities.Inserts)
            {
                int hits = 0;
                foreach (var e in ins.Block.Entities)
                {
                    string raw = e switch
                    {
                        Text  t => t.Value,
                        MText m => StripMText(m.Value),
                        _       => "",
                    };
                    if (CableRows.Any(r =>
                        raw.Equals(r.label, StringComparison.OrdinalIgnoreCase))) hits++;
                }
                if (hits >= 2) return ins;
            }
            return null;
        }

        private static int FillTableBlock(Block blk,
            Dictionary<string, double> cableTotals,
            Dictionary<string, double> methodTotals)
        {
            const double X_CABLE  = 924.80;
            const double X_METHOD = 1056.18;
            const double X_TOL    = 8.0;
            const double Y_TOL    = 3.0;

            var cells = new List<(EntityObject ent, Vector3 pos, string text)>();
            foreach (var e in blk.Entities)
            {
                switch (e)
                {
                    case Text  t: cells.Add((e, t.Position, t.Value)); break;
                    case MText m: cells.Add((e, m.Position, StripMText(m.Value))); break;
                }
            }

            int filled = 0;

            void WriteCell(double targetX, string key, double metres)
            {
                string lbl = (CableRows.Concat(MethodRows))
                    .FirstOrDefault(r => r.key == key).label ?? key;

                var labelCell = cells.FirstOrDefault(c =>
                    c.text.Equals(lbl, StringComparison.OrdinalIgnoreCase));
                if (labelCell.ent == null) return;

                double rowY = labelCell.pos.Y;
                var valCell = cells.FirstOrDefault(c =>
                    Math.Abs(c.pos.X - targetX) < X_TOL &&
                    Math.Abs(c.pos.Y - rowY)    < Y_TOL);

                string val = $"{(int)Math.Round(metres)}m";
                if (valCell.ent != null)
                {
                    if (valCell.ent is Text  tv) tv.Value = val;
                    if (valCell.ent is MText mv) mv.Value = val;
                }
                else
                {
                    blk.Entities.Add(new Text(val, new Vector3(targetX, rowY, 0), 2.5));
                }
                filled++;
            }

            foreach (var (_, key) in CableRows)
                if (cableTotals.TryGetValue(key, out double m)) WriteCell(X_CABLE, key, m);
            foreach (var (_, key) in MethodRows)
                if (methodTotals.TryGetValue(key, out double m)) WriteCell(X_METHOD, key, m);

            return filled;
        }

        // ── Polyline helpers ──────────────────────────────────────────────────
        private static double PolylineLength(Polyline2D poly)
        {
            double len = 0;
            var verts = poly.Vertexes;
            for (int i = 0; i < verts.Count - 1; i++)
            {
                double dx = verts[i + 1].Position.X - verts[i].Position.X;
                double dy = verts[i + 1].Position.Y - verts[i].Position.Y;
                len += Math.Sqrt(dx * dx + dy * dy);
            }
            if (poly.IsClosed && verts.Count > 1)
            {
                double dx = verts[0].Position.X - verts[verts.Count - 1].Position.X;
                double dy = verts[0].Position.Y - verts[verts.Count - 1].Position.Y;
                len += Math.Sqrt(dx * dx + dy * dy);
            }
            return len;
        }

        private static Vector3 PolylineMid(Polyline2D poly)
        {
            if (poly.Vertexes.Count == 0) return Vector3.Zero;
            double x = poly.Vertexes.Average(v => v.Position.X);
            double y = poly.Vertexes.Average(v => v.Position.Y);
            return new Vector3(x, y, 0);
        }

        private static string NearestText(Vector3 origin,
            List<(Vector3 pos, string text)> texts)
        {
            if (texts.Count == 0) return string.Empty;
            double best = double.MaxValue;
            string res  = string.Empty;
            foreach (var (pos, text) in texts)
            {
                double dx = origin.X - pos.X, dy = origin.Y - pos.Y;
                double d  = Math.Sqrt(dx * dx + dy * dy);
                if (d < best) { best = d; res = text; }
            }
            return res;
        }

        private static string StripMText(string raw)
        {
            string s = Regex.Replace(raw, @"\\[A-Za-z][^;]*;", "");
            s = s.Replace(@"\P", "\n").Replace(@"\p", "\n")
                 .Replace(@"\L", "").Replace(@"\l", "")
                 .Replace("{", "").Replace("}", "");
            return s.Trim();
        }
    }
}
