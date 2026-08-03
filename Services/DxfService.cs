using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CadToolsApp.Models;
using netDxf;
using netDxf.Blocks;
using netDxf.Entities;
using netDxf.Objects;
using netDxf.Tables;

namespace CadToolsApp.Services
{
    public class DxfService
    {
        private const string SECTION_LAYER = "Cross sections";

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

        // ── Project scope (derived from the sheet's clipped viewport) ──────────
        // Model space can hold unrelated leftover/duplicate geometry clusters from other
        // projects or revisions saved in the same file. The paperspace layout that plots
        // this project uses a non-rectangularly-clipped Viewport whose visible area
        // (ViewCenter +/- ViewHeight/2, aspect-corrected by Width/Height) is the one
        // authoritative "what belongs to this sheet" boundary — read from the document
        // itself, not a hardcoded layout name or coordinate range.
        internal readonly record struct ScopeBox(double MinX, double MinY, double MaxX, double MaxY)
        {
            public bool Contains(double x, double y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;
        }

        internal static ScopeBox? FindProjectScope(DxfDocument doc)
        {
            Viewport? clipped = null;
            foreach (var layout in doc.Layouts)
            {
                if (!layout.IsPaperSpace) continue;
                clipped = layout.AssociatedBlock.Entities.OfType<Viewport>()
                    .FirstOrDefault(vp => (vp.Status & ViewportStatusFlags.NonRectangularClipping) != 0);
                if (clipped != null) break;
            }
            if (clipped == null) return null;

            double aspect = clipped.Height == 0 ? 1.0 : clipped.Width / clipped.Height;
            double halfH = clipped.ViewHeight / 2.0;
            double halfW = halfH * aspect;

            return new ScopeBox(
                clipped.ViewCenter.X - halfW, clipped.ViewCenter.Y - halfH,
                clipped.ViewCenter.X + halfW, clipped.ViewCenter.Y + halfH);
        }

        // Resolves BYLAYER color/linetype to the entity's actual layer defaults — comparing
        // raw entity.Color/Linetype directly would treat every BYLAYER entity as identical
        // regardless of which layer it's really drawn on.
        internal static (short color, string linetype) ResolveColorLinetype(EntityObject e)
        {
            short color = e.Color.IsByLayer ? e.Layer.Color.Index : e.Color.Index;
            string linetype = e.Linetype.IsByLayer ? e.Layer.Linetype.Name : e.Linetype.Name;
            return (color, linetype);
        }

        // ── Fill summary table ─────────────────────────────────────────────────
        // Step 1: classify every in-scope cable polyline (measurement + type/method evidence).
        // The result is meant to be reviewed/edited by the user (see Dialogs/CreatePlansDialog)
        // before WriteTable ever touches the document — every field here is a best-effort read
        // of free-text callouts and drawing color conventions, not authoritative structured data.
        public CableClassifier.ClassifyResult ClassifyCables(DxfDocument doc, string dxfFilePath, string? cableMapConfigPath = null) =>
            CableClassifier.Classify(doc, dxfFilePath, cableMapConfigPath);

        // Step 2: write the confirmed totals. Cable-type totals sum every entity individually
        // (a real "2xDB7" run is two physical cables); method totals sum per RouteGroupId instead
        // (one physical trench, however many parallel duct entities share it) — see
        // CableClassifier's route-clustering comment for why the same geometry needs both rules.
        // `table` is passed in by CreatePlans, which has already resolved the block for the sheet it
        // just built — re-resolving here could pick a different revision and split the values
        // across two blocks.
        public (int filled, List<string> log) WriteTable(DxfDocument doc, List<ClassifiedCable> cables,
            int pitCount = 0, Block? table = null)
        {
            var log = new List<string>();

            var cableTotals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in cables)
            {
                string? key = ResolveCableRowKey(c.BaseType, c.Stripe, c.Evidence);
                if (key == null) continue;
                cableTotals[key] = cableTotals.GetValueOrDefault(key) + c.Length;
            }

            var methodTotals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var routeGroup in cables.Where(c => c.Method != null).GroupBy(c => c.RouteGroupId))
            {
                foreach (var methodGroup in routeGroup.GroupBy(c => c.Method))
                {
                    double len = methodGroup.Max(c => c.Length);
                    methodTotals[methodGroup.Key!] = methodTotals.GetValueOrDefault(methodGroup.Key!) + len;
                }
            }
            if (methodTotals.Count > 0)
                methodTotals["TOTAAL"] = methodTotals.Values.Sum();

            table ??= FindTableBlock(doc, log);
            if (table == null)
            {
                log.Add("Table block not found — no block contains the expected header + row labels.");
                return (0, log);
            }

            var onLayouts = doc.Layouts
                .Where(l => l.AssociatedBlock.Entities.OfType<Insert>().Any(i => i.Block == table))
                .Select(l => l.Name)
                .ToList();
            log.Add(onLayouts.Count > 0
                ? $"Table block '{table.Name}' — writing here updates every sheet that references it: {string.Join(", ", onLayouts)}."
                : $"Table block '{table.Name}' found, but no layout currently inserts it (writing to the definition anyway).");

            // Every length cell reads "<n>m"; the pit row is the one exception — it counts units
            // ("1st"), so values are formatted here and the writer stays format-agnostic.
            var cableCells = cableTotals.ToDictionary(
                kv => kv.Key, kv => $"{(int)Math.Round(kv.Value)}m", StringComparer.OrdinalIgnoreCase);
            var methodCells = methodTotals.ToDictionary(
                kv => kv.Key, kv => $"{(int)Math.Round(kv.Value)}m", StringComparer.OrdinalIgnoreCase);
            if (pitCount > 0) cableCells[PitRowKey] = $"{pitCount}st";

            int filled = WriteValuesIntoBlock(table, cableCells, methodCells);
            log.Add($"Cables: {string.Join(", ", cableTotals.Select(kv => $"{kv.Key}={kv.Value:F0}m"))}");
            log.Add($"Methods: {string.Join(", ", methodTotals.Select(kv => $"{kv.Key}={kv.Value:F0}m"))}");
            if (pitCount > 0) log.Add($"Glasvezelput (Beton/CTB): {pitCount}st.");
            log.Add($"{filled} cell(s) written.");

            // "Geplaatste lengte" / "Gesloopte lengte" sit in the upper title-block band, not in
            // this two-column table, so they are written by TitleBlockWriter from the confirmed
            // sheet info instead of being derived here. The computed installed total is logged as
            // a cross-check against whatever the drafter entered.
            double installed = cables.Sum(c => c.Length);
            log.Add($"Geplaatste lengte cross-check (sum of confirmed cable lengths): {installed:F0}m.");

            return (filled, log);
        }

        private static string? ResolveCableRowKey(string? baseType, string? stripe, string evidence)
        {
            switch (baseType)
            {
                case "DB7":  return stripe switch { "GREY" => "DB7-GREY", "ORANGE" => "DB7-ORANGE", _ => null };
                case "DB2":  return stripe switch { "GREY" => "DB2-GREY", _ => null };
                case "HDPE": return stripe switch { "GREY" => "HDPE50-GREY", "ORANGE" => "HDPE50-ORANGE", "GREEN" => "HDPE50-GREEN", _ => null };
                case "COAX":
                    if (Regex.IsMatch(evidence, @"14\s*mm", RegexOptions.IgnoreCase)) return "COAX14";
                    if (Regex.IsMatch(evidence, @"20\s*mm", RegexOptions.IgnoreCase)) return "COAX20";
                    return null;
                default: return null;
            }
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
            ("Glasvezelput (Beton/CTB)",           PitRowKey       ),
        };

        // Shares the cable column but holds a unit count, not a length — see WriteTable.
        internal const string PitRowKey = "GVP-BETON";

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

        // Finds the table by content, not a template-specific block name — requires both the
        // header labels and at least two row labels, since scanning every block definition (not
        // just Model space) widens the surface for an accidental false-positive match.
        //
        // Real project files accumulate a dozen-plus near-identical title-block revisions (one per
        // sheet the drafter ever set up), all of which match on content. Writing to whichever one
        // the block table happens to list first would silently fill a sheet nobody prints, so a
        // block that some paper-space layout actually inserts always wins over one that is only a
        // leftover definition.
        private static Block? FindTableBlock(DxfDocument doc, List<string>? log = null)
        {
            var matches = doc.Blocks.Where(IsTableBlock).ToList();
            if (matches.Count == 0) return null;

            var referenced = doc.Layouts
                .Where(l => l.IsPaperSpace)
                .SelectMany(l => l.AssociatedBlock.Entities.OfType<Insert>())
                .Select(i => i.Block)
                .Distinct()
                .ToHashSet();

            var chosen = matches.FirstOrDefault(referenced.Contains) ?? matches[0];

            if (matches.Count > 1 && log != null)
            {
                int used = matches.Count(referenced.Contains);
                log.Add($"{matches.Count} blocks match the table layout; chose '{chosen.Name}' " +
                        $"({used} of them are placed on a sheet). Others: " +
                        $"{string.Join(", ", matches.Where(b => b != chosen).Select(b => b.Name))}.");
            }
            return chosen;
        }

        private static bool IsTableBlock(Block block)
        {
            int labelHits = 0;
            bool hasHeader = false;
            foreach (var e in block.Entities)
            {
                string raw = e switch
                {
                    Text  t => t.Value,
                    MText m => StripMTextCodes(m.Value),
                    _       => "",
                };
                if (CableRows.Concat(MethodRows).Any(r => raw.Equals(r.label, StringComparison.OrdinalIgnoreCase)))
                    labelHits++;
                if (raw.Equals("Te plaatsen net:", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Uitvoering:", StringComparison.OrdinalIgnoreCase))
                    hasHeader = true;
            }
            return labelHits >= 2 && hasHeader;
        }

        private static int WriteValuesIntoBlock(Block blk,
            Dictionary<string, string> cableValues,
            Dictionary<string, string> methodValues)
        {
            const double X_TOL = 8.0;
            const double Y_TOL = 3.0;

            var cells = new List<(EntityObject ent, Vector3 pos, string text)>();
            foreach (var e in blk.Entities)
            {
                switch (e)
                {
                    case Text  t: cells.Add((e, t.Position, t.Value)); break;
                    case MText m: cells.Add((e, m.Position, StripMTextCodes(m.Value))); break;
                }
            }

            // Value-column X is read from the document itself — the "m" header nearest to (and
            // to the right of) each section's own label column — instead of a hardcoded literal
            // that only matches one specific template revision.
            double? ValueColumnX(string sectionLabel)
            {
                var label = cells.FirstOrDefault(c => c.text.Equals(sectionLabel, StringComparison.OrdinalIgnoreCase));
                if (label.ent == null) return null;
                return cells
                    .Where(c => c.text.Trim().Equals("m", StringComparison.OrdinalIgnoreCase) && c.pos.X > label.pos.X)
                    .OrderBy(c => c.pos.X)
                    .Select(c => (double?)c.pos.X)
                    .FirstOrDefault();
            }

            double? cableX = ValueColumnX("Te plaatsen net:");
            double? methodX = ValueColumnX("Uitvoering:");

            int filled = 0;

            void WriteCell(double? targetX, string key, string val)
            {
                if (targetX == null) return;
                string lbl = (CableRows.Concat(MethodRows))
                    .FirstOrDefault(r => r.key == key).label ?? key;

                var labelCell = cells.FirstOrDefault(c =>
                    c.text.Equals(lbl, StringComparison.OrdinalIgnoreCase));
                if (labelCell.ent == null) return;

                double rowY = labelCell.pos.Y;
                var valCell = cells.FirstOrDefault(c =>
                    Math.Abs(c.pos.X - targetX.Value) < X_TOL &&
                    Math.Abs(c.pos.Y - rowY)           < Y_TOL);

                if (valCell.ent != null)
                {
                    if (valCell.ent is Text  tv) tv.Value = val;
                    if (valCell.ent is MText mv) mv.Value = val;
                }
                else
                {
                    blk.Entities.Add(new Text(val, new Vector3(targetX.Value, rowY, 0), 2.5));
                }
                filled++;
            }

            foreach (var (_, key) in CableRows)
                if (cableValues.TryGetValue(key, out string? v)) WriteCell(cableX, key, v);
            foreach (var (_, key) in MethodRows)
                if (methodValues.TryGetValue(key, out string? v)) WriteCell(methodX, key, v);

            return filled;
        }

        // ── Create layout (fresh sheet from the title-block template) ──────────
        // The template is authored full size on an A0 sheet, with "SCHAAL 1/500" and a scale bar
        // drawn to match that. Reproducing a real permit sheet therefore means inserting the block
        // 1:1 on A0 paper and giving the viewport a *true* 1/500 view height. Scaling the block
        // down onto A4 (as this did previously) reduced the drawing four-fold while leaving the
        // printed scale note and the scale bar untouched — every sheet claimed a scale it wasn't.
        private const string LayoutBoxLayer  = "LAYOUT_BOX";
        private const double ViewportMargin  = 6.0;   // mm inset from the template's divider line
        private const double ViewZoomPadding = 1.05;  // headroom so the marked area isn't flush against the frame

        // Plot scales a permit sheet is allowed to use, smallest first. 1/500 is the house default;
        // anything larger is only reached because the marked plan area cannot fit at 1/500.
        private static readonly int[] StandardScales = { 100, 200, 250, 500, 1000, 2000, 2500, 5000 };

        // Model units are metres (Belgian Lambert 72), paper units are millimetres.
        private static double MetresPerPaperMm(int denominator) => denominator / 1000.0;

        public (bool ok, List<string> log, Block? table) CreateLayout(
            DxfDocument doc, string templatePath, SheetInfo info)
        {
            var log = new List<string>();

            ScopeBox? userBox = FindLayoutBoxRectangle(doc);
            if (userBox == null)
            {
                log.Add($"No closed rectangle found on layer '{LayoutBoxLayer}' — draw one there to mark the plan area, then try again.");
                return (false, log, null);
            }
            log.Add($"Plan area (from '{LayoutBoxLayer}'): X[{userBox.Value.MinX:F1}, {userBox.Value.MaxX:F1}]  Y[{userBox.Value.MinY:F1}, {userBox.Value.MaxY:F1}]");

            Block tableBlock;
            try
            {
                tableBlock = ImportOrReuseTableBlock(doc, templatePath, log);
            }
            catch (Exception ex)
            {
                log.Add($"Template error: {ex.Message}");
                return (false, log, null);
            }

            var divider = FindVerticalDivider(tableBlock);
            if (divider == null)
            {
                log.Add("Could not find the divider line between the drawing area and the table column in the template block.");
                return (false, log, null);
            }

            // The sheet is the template's own border, used at 1:1 — so block units are paper mm.
            var sheet = BlockGeometryBBox(tableBlock);
            double sheetW = sheet.MaxX - sheet.MinX;
            double sheetH = sheet.MaxY - sheet.MinY;

            double pMinX = sheet.MinX + ViewportMargin;
            double pMaxX = divider.Value.x - ViewportMargin;
            double pMinY = divider.Value.yMin + ViewportMargin;
            double pMaxY = divider.Value.yMax - ViewportMargin;
            double vpWidth = pMaxX - pMinX, vpHeight = pMaxY - pMinY;

            if (vpWidth <= 1 || vpHeight <= 1)
            {
                log.Add($"Template drawing area came out as {vpWidth:F1}x{vpHeight:F1}mm — the sheet border or the column divider was not recognised.");
                return (false, log, null);
            }

            double rectW = userBox.Value.MaxX - userBox.Value.MinX;
            double rectH = userBox.Value.MaxY - userBox.Value.MinY;
            int denominator = ChooseScale(info.ScaleDenominator, rectW, rectH, vpWidth, vpHeight, log);
            info.ScaleDenominator = denominator;

            string layoutName = NextLayoutName(doc);
            var layout = new Layout(layoutName)
            {
                MinLimit = new Vector2(sheet.MinX, sheet.MinY),
                MaxLimit = new Vector2(sheet.MaxX, sheet.MaxY),
            };
            var (paperName, paperSize) = MatchIsoPaper(sheetW, sheetH);
            layout.PlotSettings.PaperSizeName = paperName;
            layout.PlotSettings.PaperSize     = paperSize;
            layout.PlotSettings.PaperUnits    = PlotPaperUnits.Milimeters;
            layout.PlotSettings.PrintScaleNumerator   = 1.0;
            layout.PlotSettings.PrintScaleDenominator = 1.0;   // the sheet itself plots 1:1
            layout.PlotSettings.ScaleToFit = false;
            doc.Layouts.Add(layout);

            layout.AssociatedBlock.Entities.Add(new Insert(tableBlock, new Vector3(0, 0, 0)));

            double rectCx = (userBox.Value.MinX + userBox.Value.MaxX) / 2.0;
            double rectCy = (userBox.Value.MinY + userBox.Value.MaxY) / 2.0;

            // At a fixed plot scale the view height follows from the viewport's paper height —
            // it is no longer fitted to the rectangle, which is what made the old scale arbitrary.
            double viewHeight = vpHeight * MetresPerPaperMm(denominator);

            var viewport = new Viewport(new Vector2((pMinX + pMaxX) / 2.0, (pMinY + pMaxY) / 2.0), vpWidth, vpHeight)
            {
                ViewCenter = new Vector2(rectCx, rectCy),
                ViewHeight = viewHeight,
            };
            viewport.Status &= ~ViewportStatusFlags.GridMode; // netDxf defaults new viewports to grid-on
            // A loose (not-yet-owned) Layer instance is required here — netDxf's FrozenLayers
            // collection rejects one already registered in doc.Layers and binds this one by
            // name to the real table entry once the viewport is added to the document.
            viewport.FrozenLayers.Add(new Layer(LayoutBoxLayer)); // the marker rectangle is drafting-only, not part of the sheet
            layout.AssociatedBlock.Entities.Add(viewport);

            // Per-viewport freezing did not survive a save/reload round-trip, so the marker also
            // gets its layer flagged non-plotting — that is a layer-table property and does
            // persist, and it is what actually keeps the rectangle off the printed sheet.
            if (doc.Layers.Contains(LayoutBoxLayer))
            {
                doc.Layers[LayoutBoxLayer].Plot = false;
                log.Add($"Layer '{LayoutBoxLayer}' set to non-plotting — the marker rectangle stays visible on screen but off the print.");
            }

            log.Add($"Layout '{layoutName}' created — {paperName.Replace("psk:ISO", "")} landscape " +
                    $"({sheetW:F0}x{sheetH:F0}mm), title block inserted at 1:1.");
            log.Add($"Plot scale 1/{denominator}: viewport {vpWidth:F0}x{vpHeight:F0}mm covers " +
                    $"{vpWidth * MetresPerPaperMm(denominator):F0}x{viewHeight:F0}m of ground, " +
                    $"centred on ({rectCx:F1}, {rectCy:F1}).");

            return (true, log, tableBlock);
        }

        // Keeps the requested scale when the marked area fits, otherwise steps up to the first
        // standard scale that does — never an in-between value, because the sheet's scale bar and
        // "SCHAAL 1/x" note have to state a scale a reader can measure against.
        private static int ChooseScale(int requested, double rectW, double rectH,
            double vpWidth, double vpHeight, List<string> log)
        {
            double needW = rectW * ViewZoomPadding, needH = rectH * ViewZoomPadding;

            bool Fits(int d) =>
                vpWidth * MetresPerPaperMm(d) >= needW && vpHeight * MetresPerPaperMm(d) >= needH;

            if (Fits(requested)) return requested;

            foreach (int d in StandardScales.Where(d => d > requested))
            {
                if (!Fits(d)) continue;
                log.Add($"Plan area is {rectW:F0}x{rectH:F0}m — too large for 1/{requested}; " +
                        $"stepped up to the next standard scale that fits, 1/{d}.");
                return d;
            }

            int largest = StandardScales[^1];
            log.Add($"WARNING: plan area is {rectW:F0}x{rectH:F0}m and does not fit even at 1/{largest} — " +
                    $"using 1/{largest}; the sheet will be clipped. Split the project over several sheets.");
            return largest;
        }

        // ISO paper the template border corresponds to, so the layout opens with the right sheet
        // selected instead of whatever the receiving CAD session defaults to.
        private static (string name, Vector2 size) MatchIsoPaper(double width, double height)
        {
            double shortSide = Math.Min(width, height), longSide = Math.Max(width, height);
            (string name, double w, double h)[] iso =
            {
                ("psk:ISOA4", 210, 297), ("psk:ISOA3", 297, 420), ("psk:ISOA2", 420, 594),
                ("psk:ISOA1", 594, 841), ("psk:ISOA0", 841, 1189),
            };

            foreach (var (name, w, h) in iso)
                if (Math.Abs(shortSide - w) <= 3 && Math.Abs(longSide - h) <= 3)
                    return (name, new Vector2(w, h));

            return ("psk:UserDefined", new Vector2(shortSide, longSide));
        }

        // ── Create Plans ───────────────────────────────────────────────────────
        // One pass over everything a finished sheet needs, in the only order that works: the sheet
        // has to exist (and pull in the title block) before there is anything to write values into.
        // Each step logs and reports its own outcome — a failed locator map must not lose the table
        // and title-block work that already succeeded.
        public (bool ok, List<string> log) CreatePlans(
            DxfDocument doc, string templatePath, List<ClassifiedCable> confirmed,
            SheetInfo info, string dxfFilePath)
        {
            var log = new List<string>();

            var (layoutOk, layoutLog, table) = CreateLayout(doc, templatePath, info);
            log.AddRange(layoutLog);
            if (!layoutOk || table == null) return (false, log);

            var (filled, tableLog) = WriteTable(doc, confirmed, info.PitCount, table);
            log.AddRange(tableLog);

            var (fields, titleLog) = TitleBlockWriter.Write(doc, table, info);
            log.AddRange(titleLog);

            if (info.FetchLiggingMap)
            {
                var (_, mapLog) = LiggingMapService.AddLiggingMap(
                    doc, table, RouteGeometry(doc, confirmed),
                    Path.GetDirectoryName(dxfFilePath) ?? AppContext.BaseDirectory,
                    Path.GetFileNameWithoutExtension(dxfFilePath));
                log.AddRange(mapLog);
            }
            else
            {
                log.Add("Ligging map: skipped at your request — paste the locator image manually.");
            }

            log.Add($"Create Plans finished — {filled} table cell(s) and {fields} title-block field(s) written.");
            return (true, log);
        }

        // One vertex list per physical route, for tracing on the locator map. Parallel ducts in the
        // same trench would draw the same red line twice, so only the longest entity per route
        // group is traced.
        private static List<IReadOnlyList<Vector2>> RouteGeometry(DxfDocument doc, List<ClassifiedCable> cables)
        {
            var routes = new List<IReadOnlyList<Vector2>>();

            foreach (var group in cables.GroupBy(c => c.RouteGroupId))
            {
                Polyline2D? longest = null;
                double bestLength = 0;
                foreach (var cable in group)
                {
                    if (doc.GetObjectByHandle(cable.Handle) is not Polyline2D poly) continue;
                    if (longest != null && cable.Length <= bestLength) continue;
                    longest = poly;
                    bestLength = cable.Length;
                }
                if (longest != null)
                    routes.Add(longest.Vertexes.Select(v => v.Position).ToList());
            }

            return routes;
        }

        private static string NextLayoutName(DxfDocument doc)
        {
            int n = 1;
            while (doc.Layouts.Contains($"Overzicht {n}")) n++;
            return $"Overzicht {n}";
        }

        private static ScopeBox? FindLayoutBoxRectangle(DxfDocument doc)
        {
            foreach (var e in doc.Entities.All)
            {
                if (e is not Polyline2D poly) continue;
                if (poly.Layer == null || !poly.Layer.Name.Equals(LayoutBoxLayer, StringComparison.OrdinalIgnoreCase)) continue;
                if (!poly.IsClosed || poly.Vertexes.Count < 4) continue;

                double minX = poly.Vertexes.Min(v => v.Position.X);
                double maxX = poly.Vertexes.Max(v => v.Position.X);
                double minY = poly.Vertexes.Min(v => v.Position.Y);
                double maxY = poly.Vertexes.Max(v => v.Position.Y);
                return new ScopeBox(minX, minY, maxX, maxY);
            }
            return null;
        }

        // Reuses a table block already in the document (e.g. from a previous Create Layout
        // run) instead of importing a duplicate; only pulls from the template when this
        // document doesn't have one yet.
        private static Block ImportOrReuseTableBlock(DxfDocument doc, string templatePath, List<string> log)
        {
            var existing = FindTableBlock(doc, log);
            if (existing != null)
            {
                log.Add($"Reusing existing table block '{existing.Name}' already in this document.");
                return existing;
            }

            var src = DxfDocument.Load(templatePath);
            var srcBlock = FindTableBlock(src);
            if (srcBlock == null)
                throw new InvalidOperationException(
                    "Template file does not contain a recognizable table block (expected header + row labels).");

            var visited = new Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase);
            var clone = CloneBlockIntoDocument(doc, srcBlock, visited);
            log.Add($"Imported table block '{clone.Name}' from template '{Path.GetFileName(templatePath)}'.");
            return clone;
        }

        // Deep-copies a block (and, recursively, any block referenced by a nested Insert)
        // into the target document — netDxf's Clone() copies entities but nested Insert
        // references still point at the source document's blocks, so those get rebuilt
        // against the newly cloned copies here.
        private static Block CloneBlockIntoDocument(DxfDocument doc, Block src, Dictionary<string, Block> visited)
        {
            if (visited.TryGetValue(src.Name, out var already)) return already;
            if (doc.Blocks.Contains(src.Name))
            {
                var existing = doc.Blocks[src.Name];
                visited[src.Name] = existing;
                return existing;
            }

            var clone = (Block)src.Clone();
            clone.Name = src.Name;
            visited[src.Name] = clone;
            doc.Blocks.Add(clone);

            foreach (var ins in clone.Entities.OfType<Insert>().ToList())
            {
                Block nestedClone = CloneBlockIntoDocument(doc, ins.Block, visited);
                if (ReferenceEquals(nestedClone, ins.Block)) continue;

                var replacement = new Insert(nestedClone, ins.Position)
                {
                    Scale     = ins.Scale,
                    Rotation  = ins.Rotation,
                    Layer     = ins.Layer,
                    Color     = ins.Color,
                    Linetype  = ins.Linetype,
                    Lineweight = ins.Lineweight,
                };
                clone.Entities.Remove(ins);
                clone.Entities.Add(replacement);
            }

            return clone;
        }

        private static ScopeBox BlockGeometryBBox(Block blk)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            void Consider(double x, double y)
            {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            foreach (var e in blk.Entities)
            {
                switch (e)
                {
                    case Line l: Consider(l.StartPoint.X, l.StartPoint.Y); Consider(l.EndPoint.X, l.EndPoint.Y); break;
                    case Polyline2D p: foreach (var v in p.Vertexes) Consider(v.Position.X, v.Position.Y); break;
                }
            }

            return new ScopeBox(minX, minY, maxX, maxY);
        }

        // Finds the divider between the drawing-area box and the title-block column: the
        // longest vertical line in the block spans the box's full height by construction,
        // so its X is the divider and its Y-span is the box's vertical extent — read from
        // the template's own geometry instead of a hardcoded coordinate.
        private static (double x, double yMin, double yMax)? FindVerticalDivider(Block blk)
        {
            Line? best = null;
            double bestLen = 0;
            foreach (var l in blk.Entities.OfType<Line>())
            {
                if (Math.Abs(l.StartPoint.X - l.EndPoint.X) > 0.01) continue;
                double len = Math.Abs(l.EndPoint.Y - l.StartPoint.Y);
                if (len > bestLen) { bestLen = len; best = l; }
            }
            if (best == null) return null;
            return (best.StartPoint.X, Math.Min(best.StartPoint.Y, best.EndPoint.Y), Math.Max(best.StartPoint.Y, best.EndPoint.Y));
        }

        // ── Polyline helpers ──────────────────────────────────────────────────
        // Segment length, following the vertex's bulge (tan(included-angle/4)) when
        // nonzero — a straight chord-length sum silently undercounts any curved run.
        private static double SegmentLength(Vector2 a, Vector2 b, double bulge)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double chord = Math.Sqrt(dx * dx + dy * dy);
            if (bulge == 0.0 || chord == 0.0) return chord;

            double theta = 4.0 * Math.Atan(bulge);
            double radius = chord / (2.0 * Math.Sin(theta / 2.0));
            return Math.Abs(radius * theta);
        }

        internal static double PolylineLength(Polyline2D poly)
        {
            double len = 0;
            var verts = poly.Vertexes;
            for (int i = 0; i < verts.Count - 1; i++)
                len += SegmentLength(verts[i].Position, verts[i + 1].Position, verts[i].Bulge);

            if (poly.IsClosed && verts.Count > 1)
                len += SegmentLength(verts[^1].Position, verts[0].Position, verts[^1].Bulge);

            return len;
        }


        internal static string StripMTextCodes(string raw)
        {
            string s = Regex.Replace(raw, @"\\[A-Za-z][^;]*;", "");
            s = s.Replace(@"\P", "\n").Replace(@"\p", "\n")
                 .Replace(@"\L", "").Replace(@"\l", "")
                 .Replace("{", "").Replace("}", "");
            return s.Trim();
        }
    }
}
