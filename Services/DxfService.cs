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
using netDxf.Units;

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

        public void Save(DxfDocument doc, string path)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            doc.Entities.ActiveLayout = "Model";
            doc.Save(path);
        }

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
                }
            }

            var result = new List<SectionInfo>();
            foreach (var p in pts)
            {
                var (lat, lon) = BelgianCrs.ToWgs84(p.x, p.y);
                var sec = new SectionInfo(p.id, lat, lon, p.x, p.y);
                string imgPath = Path.Combine(folder, $"section_{p.id:D3}.jpg");
                if (File.Exists(imgPath)) sec.ImagePath = imgPath;
                result.Add(sec);
            }
            return result;
        }

        // ── Cross-section preset layouts ──────────────────────────────────────
        // Returns the names of all paper-space layouts in the preset template.
        // These are shown to the user as choices in CrossSectionDialog.
        public List<string> GetPresetNames(string templatePath)
        {
            try
            {
                var src = DxfDocument.Load(templatePath);
                return src.Layouts
                    .Where(l => l.IsPaperSpace)
                    .Select(l => l.Name)
                    .ToList();
            }
            catch { return new List<string>(); }
        }

        // Clones the named paper-space layout from the preset template into the
        // working document as a new layout tab named after the section ID.
        public (bool ok, List<string> log) AppendPresetLayout(
            DxfDocument doc, string templatePath, string presetName, string sectionId,
            string? imagePath = null, string? address = null, string? dossierNumber = null,
            double modelX = 0, double modelY = 0,
            IReadOnlyDictionary<string, double>? cableLengths = null)
        {
            var log = new List<string>();

            DxfDocument template;
            try { template = DxfDocument.Load(templatePath); }
            catch (Exception ex)
            {
                log.Add($"Cannot load template: {ex.Message}");
                return (false, log);
            }

            var src = template.Layouts
                .FirstOrDefault(l => l.IsPaperSpace &&
                    l.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase));

            if (src == null)
            {
                log.Add($"Preset layout '{presetName}' not found in template.");
                return (false, log);
            }

            string layoutName = MakeUniqueLayoutName(doc, sectionId);
            var newLayout = new Layout(layoutName)
            {
                MinLimit = src.MinLimit,
                MaxLimit = src.MaxLimit,
            };
            newLayout.PlotSettings.PaperSizeName          = src.PlotSettings.PaperSizeName;
            newLayout.PlotSettings.PaperSize              = src.PlotSettings.PaperSize;
            newLayout.PlotSettings.PaperUnits             = src.PlotSettings.PaperUnits;
            newLayout.PlotSettings.PrintScaleNumerator    = src.PlotSettings.PrintScaleNumerator;
            newLayout.PlotSettings.PrintScaleDenominator  = src.PlotSettings.PrintScaleDenominator;
            newLayout.PlotSettings.ScaleToFit             = src.PlotSettings.ScaleToFit;
            doc.Layouts.Add(newLayout);

            var visited = new Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase);
            int count   = 0;
            foreach (var e in src.AssociatedBlock.Entities.ToList())
            {
                var cloned = CloneLayoutEntity(doc, e, visited, sectionId);
                if (cloned == null) continue;
                newLayout.AssociatedBlock.Entities.Add(cloned);
                count++;
            }

            log.Add($"Layout '{layoutName}' appended -- {count} entities from '{presetName}'.");

            PatchDateFields(doc, newLayout, log);
            PatchAddressField(doc, newLayout, address ?? "", log);
            PatchDossierField(doc, newLayout, dossierNumber ?? "", log);

            if (modelX != 0 || modelY != 0)
                AddLiggingViewport(newLayout, modelX, modelY, log);

            if (cableLengths != null && cableLengths.Count > 0)
                PatchCableLengths(doc, newLayout, cableLengths, log);

            if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                AddPhotoToLayout(doc, newLayout, imagePath, log);

            return (true, log);
        }

        private static EntityObject? CloneLayoutEntity(
            DxfDocument doc, EntityObject e,
            Dictionary<string, Block> visited, string sectionId)
        {
            EntityObject? result = e switch
            {
                Insert     ins => CloneInsertEntity(doc, ins, visited),
                Viewport   vp  => CloneViewportEntity(vp),
                Text       t   => new Text(ReplaceSectionId(t.Value, sectionId), t.Position, t.Height)
                                  { Rotation = t.Rotation, Alignment = t.Alignment,
                                    WidthFactor = t.WidthFactor, ObliqueAngle = t.ObliqueAngle, Color = t.Color },
                MText      mt  => new MText(ReplaceSectionId(mt.Value, sectionId), mt.Position, mt.Height, mt.RectangleWidth)
                                  { AttachmentPoint = mt.AttachmentPoint, Rotation = mt.Rotation, Color = mt.Color },
                Line       l   => new Line(l.StartPoint, l.EndPoint)                        { Color = l.Color },
                Circle     c   => new Circle(c.Center, c.Radius)                            { Color = c.Color },
                Arc        a   => new Arc(a.Center, a.Radius, a.StartAngle, a.EndAngle)     { Color = a.Color },
                Polyline2D p   => ClonePolyline2D(p),
                Point            pt  => new Point(pt.Position)        { Color = pt.Color },
                Image            img => CloneImageEntity(doc, img),
                AlignedDimension dim => CloneAlignedDimension(doc, dim),
                _                   => null,
            };

            if (result != null && e.Layer != null)
                result.Layer = EnsureLayer(doc, e.Layer.Name, e.Layer.Color.Index);

            return result;
        }

        private static Insert CloneInsertEntity(DxfDocument doc, Insert ins, Dictionary<string, Block> visited)
        {
            var block = CloneBlockIntoDocument(doc, ins.Block, visited);
            return new Insert(block, ins.Position)
            {
                Scale      = ins.Scale,
                Rotation   = ins.Rotation,
                Color      = ins.Color,
                Lineweight = ins.Lineweight,
            };
        }

        private static Viewport CloneViewportEntity(Viewport vp) =>
            new Viewport(new Vector2(vp.Center.X, vp.Center.Y), vp.Width, vp.Height)
            {
                ViewCenter = vp.ViewCenter,
                ViewHeight = vp.ViewHeight,
                Status     = vp.Status,
            };

        private static string ReplaceSectionId(string text, string sectionId) =>
            Regex.Replace(text, @"Section\s+\d{3}", sectionId, RegexOptions.IgnoreCase);

        private static string MakeUniqueLayoutName(DxfDocument doc, string sectionId)
        {
            string c = sectionId.Length > 240 ? sectionId[..240] : sectionId;
            if (!doc.Layouts.Contains(c)) return c;
            int n = 2;
            while (doc.Layouts.Contains($"{c} ({n})")) n++;
            return $"{c} ({n})";
        }

        private static Image? CloneImageEntity(DxfDocument doc, Image src)
        {
            if (src.Definition == null) return null;
            if (!doc.ImageDefinitions.TryGetValue(src.Definition.Name, out var imgDef))
            {
                imgDef = new ImageDefinition(
                    src.Definition.Name, src.Definition.File,
                    src.Definition.Width, src.Definition.HorizontalResolution,
                    src.Definition.Height, src.Definition.VerticalResolution,
                    src.Definition.ResolutionUnits);
                doc.ImageDefinitions.Add(imgDef);
            }
            return new Image(imgDef, src.Position, src.Width, src.Height)
            {
                Brightness = src.Brightness,
                Contrast   = src.Contrast,
                Fade       = src.Fade,
                Color      = src.Color,
            };
        }

        private static AlignedDimension CloneAlignedDimension(DxfDocument doc, AlignedDimension src)
        {
            // Use netDxf's own Clone() to preserve the anonymous geometry block
            // (arrows, text, lines) that AutoCAD pre-renders for each dimension.
            // Constructing a new AlignedDimension from scratch drops that block,
            // causing wrong text/arrow sizes when the style settings differ.
            var dim = (AlignedDimension)src.Clone();
            dim.Style = EnsureDimensionStyle(doc, src.Style);
            return dim;
        }

        private static DimensionStyle EnsureDimensionStyle(DxfDocument doc, DimensionStyle src)
        {
            // If a style with this name already exists but has different scale settings,
            // using it would silently apply wrong proportions. Use a suffixed name instead.
            string name = src.Name;
            if (doc.DimensionStyles.TryGetValue(name, out var existing))
            {
                bool matches = Math.Abs(existing.DimScaleOverall - src.DimScaleOverall) < 0.001
                            && Math.Abs(existing.TextHeight       - src.TextHeight)       < 0.001
                            && Math.Abs(existing.ArrowSize        - src.ArrowSize)        < 0.001;
                if (matches) return existing;
                name = name + "_xs";
                if (doc.DimensionStyles.TryGetValue(name, out var suffixed)) return suffixed;
            }
            var ds = new DimensionStyle(name)
            {
                ArrowSize       = src.ArrowSize,
                DimScaleOverall = src.DimScaleOverall,
                TextHeight      = src.TextHeight,
                TextOffset      = src.TextOffset,
                DimLineExtend   = src.DimLineExtend,
                ExtLineOffset   = src.ExtLineOffset,
                ExtLineExtend   = src.ExtLineExtend,
                LengthPrecision = src.LengthPrecision,
                DimScaleLinear  = src.DimScaleLinear,
            };
            return doc.DimensionStyles.Add(ds);
        }

        // Creates a paper-space viewport in the "Ligging:" cell showing model space
        // centered on the section point. ViewHeight = 100 model units ≈ 100 m at Lambert72 scale.
        private const double LiggingViewHeight = 100.0;

        private static void AddLiggingViewport(Layout layout, double modelX, double modelY, List<string> log)
        {
            // Try direct search first, then search inside INSERT blocks (title block references).
            var area = FindLiggingAreaRecursive(layout.AssociatedBlock, out double offX, out double offY, out double scale);
            if (area == null)
            {
                log.Add("Ligging viewport skipped — 'Ligging:' cell not found in layout or its blocks.");
                return;
            }

            var rect = area.Value;
            // Apply INSERT offset and scale to get paper-space coordinates.
            double paperCx = offX + (rect.Left + rect.Width  / 2.0) * scale;
            double paperCy = offY + (rect.Bottom + rect.Height / 2.0) * scale;
            double vpW     = rect.Width  * scale;
            double vpH     = rect.Height * scale;

            var vp = new netDxf.Entities.Viewport
            {
                Center     = new Vector3(paperCx, paperCy, 0),
                Width      = vpW,
                Height     = vpH,
                ViewCenter = new Vector2(modelX, modelY),
                ViewHeight = LiggingViewHeight,
                Status     = netDxf.Entities.ViewportStatusFlags.CurrentlyAlwaysEnabled
                           | netDxf.Entities.ViewportStatusFlags.FastZoom,
            };

            layout.AssociatedBlock.Entities.Add(vp);
            log.Add($"Ligging viewport added at ({paperCx:F1},{paperCy:F1}) " +
                    $"size {vpW:F1}×{vpH:F1} mm, " +
                    $"model center ({modelX:F0},{modelY:F0}), view height {LiggingViewHeight} m.");
        }

        // Searches for the Ligging image area in the block itself, then recursively inside INSERTs.
        // Returns the rect in the found block's local coordinates, plus the INSERT's offset and scale.
        private static TitleBlockWriter.Rect? FindLiggingAreaRecursive(
            Block block, out double offsetX, out double offsetY, out double uniformScale)
        {
            offsetX = 0; offsetY = 0; uniformScale = 1;

            // Direct search in this block.
            var direct = TitleBlockWriter.FindLiggingImageArea(block);
            if (direct != null) return direct;

            // Search inside INSERT entities.
            foreach (var entity in block.Entities)
            {
                if (entity is not Insert ins) continue;
                var found = TitleBlockWriter.FindLiggingImageArea(ins.Block);
                if (found == null) continue;

                // Apply the INSERT's 2-D transform (position + uniform scale).
                double sx = Math.Abs(ins.Scale.X);
                double sy = Math.Abs(ins.Scale.Y);
                offsetX      = ins.Position.X;
                offsetY      = ins.Position.Y;
                uniformScale = (sx + sy) / 2.0;  // uniform scale assumed
                return found;
            }

            return null;
        }

        private static void PatchDateFields(DxfDocument doc, Layout layout, List<string> log)
        {
            string today = DateTime.Today.ToString("dd/MM/yyyy");
            var style = GetFabricomStyle(doc);
            int patched = 0;
            foreach (var entity in layout.AssociatedBlock.Entities)
            {
                if (entity is MText mt && mt.Value.Contains("DesignDate"))
                {
                    mt.Value = mt.Value.Replace("DesignDate", today);
                    if (style != null) mt.Style = style;
                    patched++;
                }
                else if (entity is Text t && t.Value.Contains("DesignDate"))
                { t.Value = t.Value.Replace("DesignDate", today); patched++; }
            }
            if (patched > 0) log.Add($"Date field(s) set to {today} ({patched} entity/entities).");
        }

        private static void PatchAddressField(DxfDocument doc, Layout layout, string address, List<string> log)
        {
            log.Add($"PatchAddress: looking for 'ProjectAddress', address='{address}'.");
            var style = GetFabricomStyle(doc);
            int patched = PatchAddressInEntities(layout.AssociatedBlock.Entities, address, style);
            log.Add(patched > 0
                ? $"Address set to '{address}' ({patched} entity/entities)."
                : "Address placeholder 'ProjectAddress' not found — check template MText.");
        }

        // Replaces cable-type placeholders (e.g. "DB7", "DB2") with formatted lengths.
        // Supported placeholders: DB7, DB2, HDPE, COAX, EXISTING — case-sensitive, whole-word.
        // Cable-length placeholders use the "Length" suffix to avoid colliding with type labels.
        // Template text: DB7Length, DB2Length, HDPELength, COAXLength, EXISTINGLength, totalLength
        private static readonly string[] CablePlaceholders =
            ["DB7", "DB2", "HDPE", "COAX", "EXISTING"];

        private static void PatchCableLengths(
            DxfDocument doc, Layout layout,
            IReadOnlyDictionary<string, double> lengths, List<string> log)
        {
            var style = GetFabricomStyle(doc);
            int patched = 0;

            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            double total = 0;
            foreach (var key in CablePlaceholders)
            {
                if (!lengths.TryGetValue(key, out double m)) continue;
                replacements[$"{key}Length"] = $"{Math.Ceiling(m):0} m";
                total += m;
            }
            if (total > 0)
                replacements["totalLength"] = $"{Math.Ceiling(total):0} m";

            foreach (var (placeholder, formatted) in replacements)
            {
                foreach (var entity in layout.AssociatedBlock.Entities)
                {
                    if (entity is MText mt && mt.Value.Contains(placeholder))
                    { mt.Value = mt.Value.Replace(placeholder, formatted); mt.RectangleWidth = 0; if (style != null) mt.Style = style; patched++; }
                    else if (entity is Text t && t.Value.Contains(placeholder))
                    { t.Value = t.Value.Replace(placeholder, formatted); patched++; }
                }
            }
            if (patched > 0) log.Add($"Cable lengths patched ({patched} entity/entities).");
        }

        private static void PatchDossierField(DxfDocument doc, Layout layout, string dossier, List<string> log)
        {
            var style = GetFabricomStyle(doc);
            int patched = 0;
            foreach (var entity in layout.AssociatedBlock.Entities)
            {
                if (entity is MText mt && mt.Value.Contains("DossierNumber"))
                {
                    mt.Value = mt.Value.Replace("DossierNumber", dossier);
                    mt.RectangleWidth = 0;
                    if (style != null) mt.Style = style;
                    patched++;
                }
                else if (entity is Text t && t.Value.Contains("DossierNumber"))
                { t.Value = t.Value.Replace("DossierNumber", dossier); patched++; }
            }
            if (patched > 0) log.Add($"Dossier number set to '{dossier}'.");
        }

        private static int PatchAddressInEntities(
            IEnumerable<EntityObject> entities, string address, netDxf.Tables.TextStyle? style)
        {
            int patched = 0;
            foreach (var entity in entities)
            {
                if (entity is MText mt && mt.Value.Contains("ProjectAddress"))
                {
                    mt.Value = mt.Value.Replace("ProjectAddress", address);
                    mt.RectangleWidth = 0;  // disable word-wrap — keeps address on one line
                    if (style != null) mt.Style = style;
                    patched++;
                }
                else if (entity is Text t && t.Value.Contains("ProjectAddress"))
                { t.Value = t.Value.Replace("ProjectAddress", address); patched++; }
                else if (entity is Insert ins)
                    patched += PatchAddressInEntities(ins.Block.Entities, address, style);
            }
            return patched;
        }

        // Returns the Fabricom-ISO text style from the document, or null if not present.
        private static netDxf.Tables.TextStyle? GetFabricomStyle(DxfDocument doc)
        {
            foreach (var s in doc.TextStyles)
                if (s.Name.Equals("Fabricom-ISO", StringComparison.OrdinalIgnoreCase))
                    return s;
            return null;
        }

        private static int PatchTextInEntities(
            IEnumerable<EntityObject> entities, string placeholder, string value)
        {
            int patched = 0;
            foreach (var entity in entities)
            {
                if (entity is MText mt && mt.Value.Contains(placeholder))
                { mt.Value = mt.Value.Replace(placeholder, value); patched++; }
                else if (entity is Text t && t.Value.Contains(placeholder))
                { t.Value = t.Value.Replace(placeholder, value); patched++; }
                else if (entity is Insert ins)
                    patched += PatchTextInEntities(ins.Block.Entities, placeholder, value);
            }
            return patched;
        }

        private static void AddPhotoToLayout(
            DxfDocument doc, Layout layout, string imagePath, List<string> log)
        {
            var (pxW, pxH) = ReadJpegDimensions(imagePath);
            if (pxW <= 0 || pxH <= 0) { log.Add("Photo skipped: cannot read image dimensions."); return; }

            double lw = layout.MaxLimit.X - layout.MinLimit.X;
            double lh = layout.MaxLimit.Y - layout.MinLimit.Y;
            if (lw <= 0 || lh <= 0) { log.Add("Photo skipped: invalid layout extents."); return; }

            // Place photo to match the 176.8 × 85 mm strip (58.5% of layout width ≈ drawing area width).
            // Height is proportional to the pixel aspect ratio of the downloaded image.
            double imgW  = lw * 0.585;
            double imgH  = imgW * (double)pxH / pxW;
            double posX  = layout.MinLimit.X + lw * 0.01;
            double posY  = layout.MinLimit.Y + lh * 0.03;

            string defName = Path.GetFileNameWithoutExtension(imagePath);
            if (doc.ImageDefinitions.TryGetValue(defName, out var existing))
                defName = defName + "_" + Guid.NewGuid().ToString("N")[..6];

            // Compute actual print DPI from placed size so AutoCAD plots at true resolution.
            // imgW is in mm; convert to inches before dividing into pixel count.
            double dpiH = pxW / (imgW / 25.4);
            double dpiV = pxH / (imgH / 25.4);
            var imgDef = new ImageDefinition(defName, imagePath,
                pxW, dpiH, pxH, dpiV, ImageResolutionUnits.Inches);
            doc.ImageDefinitions.Add(imgDef);

            var img = new Image(imgDef, new Vector2(posX, posY), imgW, imgH)
            {
                Layer = EnsureLayer(doc, "PHOTO", AciColor.Default.Index),
            };
            layout.AssociatedBlock.Entities.Add(img);

            log.Add($"Photo placed in '{layout.Name}' at ({posX:F1},{posY:F1}) size {imgW:F0}×{imgH:F0} mm.");
        }

        private static (int w, int h) ReadJpegDimensions(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                var buf = new byte[4];
                if (fs.Read(buf, 0, 2) < 2 || buf[0] != 0xFF || buf[1] != 0xD8) return (0, 0);
                while (fs.Position < fs.Length - 8)
                {
                    if (fs.Read(buf, 0, 2) < 2 || buf[0] != 0xFF) return (0, 0);
                    byte marker = buf[1];
                    if (fs.Read(buf, 0, 2) < 2) return (0, 0);
                    int segLen = (buf[0] << 8) | buf[1];
                    if (marker >= 0xC0 && marker <= 0xC3)
                    {
                        fs.ReadByte(); // precision byte
                        if (fs.Read(buf, 0, 4) < 4) return (0, 0);
                        return ((buf[2] << 8) | buf[3], (buf[0] << 8) | buf[1]);
                    }
                    fs.Seek(segLen - 2, SeekOrigin.Current);
                }
            }
            catch { }
            return (0, 0);
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

        private static Polyline2D ClonePolyline2D(Polyline2D src)
        {
            var verts = src.Vertexes.Select(v =>
                new Polyline2DVertex(v.Position) { Bulge = v.Bulge }).ToList();
            return new Polyline2D(verts) { IsClosed = src.IsClosed };
        }

        private static EntityObject? CopyEntity(EntityObject e) => e switch
        {
            Line       l => new Line(l.StartPoint, l.EndPoint),
            Circle     c => new Circle(c.Center, c.Radius),
            Arc        a => new Arc(a.Center, a.Radius, a.StartAngle, a.EndAngle),
            Polyline2D p => ClonePolyline2D(p),
            Text       t => new Text(t.Value, t.Position, t.Height)
                            { Alignment = t.Alignment, Rotation = t.Rotation },
            MText      m => new MText(m.Value, m.Position, m.Height, m.RectangleWidth),
            _            => null,
        };

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
