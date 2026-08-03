using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CadToolsApp.Models;
using netDxf;
using netDxf.Entities;

namespace CadToolsApp.Services
{
    // Ties scope (DxfService.FindProjectScope), cable identity (LegendReader), and digging-method/
    // stripe-variant evidence (MultiLeaderReader) into a per-polyline classification. Never silently
    // guesses: anything it can't resolve confidently comes back null/flagged so the review dialog
    // can surface it, rather than being folded into a default bucket.
    public static class CableClassifier
    {
        private const string CableLayerPrefix = "PDDucts";
        private const double MinRouteLength = 2.0;      // shorter entities are coupling/symbol stubs, not routes
        private const double RouteClusterTolerance = 2.0; // meters — matches near-coincident double-line duct pairs
        private const double CalloutMaxDistance = 15.0;   // meters from a leader arrowhead to the route it annotates
        public const string MixedMethod = "MIXED";        // nearby callouts disagree on method — needs manual split, never auto-summed

        // Chainage/coordinate/reference callouts that are never type or method evidence.
        private static readonly Regex NoiseCallout = new(
            @"^\s*(KMP\s*\d|N\d{3,}\s|[XY]\s*=\s*\d|Bestaande\s+bak|.*uittredepunt)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex RelevantCallout = new(
            @"plaatsen|leggen|boring|sleuf|doorsteek|droogtrekken|mantelbuis|DB7|DB2|DB1|HDPE|Coax|Zwart",
            RegexOptions.IgnoreCase);

        // A newly placed fibre pit is annotated, not drawn to a convention the tool can measure:
        // the existing-utility layers carry dozens of GVP*/putje symbols imported from the survey,
        // so counting symbols would count the whole street. A callout that names a pit *and* says
        // it is being placed is the only evidence that distinguishes new from existing.
        private static readonly Regex PitWord = new(
            @"betonbak|blaasput|glasvezelput|trekput|wachtput|lasput", RegexOptions.IgnoreCase);
        private static readonly Regex PitPlacementWord = new(
            @"plaats|nieuw|voorzien|te\s+zetten", RegexOptions.IgnoreCase);

        public record ClassifyResult(List<ClassifiedCable> Cables, List<string> Log, int DetectedPitCount);

        public static ClassifyResult Classify(DxfDocument doc, string dxfFilePath, string? cableMapConfigPath = null)
        {
            var log = new List<string>();

            var scope = DxfService.FindProjectScope(doc);
            if (scope == null)
                log.Add("WARNING: no clipped viewport found — could not determine project scope; using the full Model space (leftover/duplicate geometry from other revisions may be included).");
            else
                log.Add($"Project scope: X=[{scope.Value.MinX:F1},{scope.Value.MaxX:F1}] Y=[{scope.Value.MinY:F1},{scope.Value.MaxY:F1}]");

            var legend = LegendReader.BuildTypeMap(doc, cableMapConfigPath);
            log.Add($"Cable type legend: {legend.Source}");
            if (legend.UnresolvedLabels.Count > 0)
                log.Add($"WARNING: legend could not resolve: {string.Join(", ", legend.UnresolvedLabels)} — those cable types will show as unresolved below.");

            var allCallouts = MultiLeaderReader.ReadCallouts(dxfFilePath)
                .Where(c => !NoiseCallout.IsMatch(c.Text))
                .ToList();

            var callouts = allCallouts.Where(c => RelevantCallout.IsMatch(c.Text)).ToList();
            log.Add($"Callout annotations usable for classification: {callouts.Count}");

            // Counted over all callouts, not just the cable-relevant ones — "Plaatsing Betonbak:
            // UUB_MODULAR_CONCRETE_..." names no duct type and would be filtered out above.
            int pitCount = CountNewPits(allCallouts, scope);
            log.Add($"New fibre pits detected from callouts: {pitCount} — confirm or correct this count before writing.");

            var modelLayout = doc.Layouts.First(l => !l.IsPaperSpace);
            var pdductLayers = doc.Layers
                .Where(l => l.Name.StartsWith(CableLayerPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(l => l.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var allCandidates = modelLayout.AssociatedBlock.Entities.OfType<Polyline2D>()
                .Where(p => pdductLayers.Contains(p.Layer.Name))
                .ToList();

            var tooShort = 0;
            var candidates = new List<Polyline2D>();
            foreach (var p in allCandidates)
            {
                if (DxfService.PolylineLength(p) < MinRouteLength) { tooShort++; continue; }
                candidates.Add(p);
            }
            if (tooShort > 0)
                log.Add($"Excluded {tooShort} entities shorter than {MinRouteLength}m (coupling/symbol markers, not measurable routes).");

            if (scope != null)
            {
                var box = scope.Value;
                var inScope = new List<Polyline2D>();
                var boundary = new List<Polyline2D>();
                var outOfScope = 0;
                foreach (var p in candidates)
                {
                    bool allIn = p.Vertexes.All(v => box.Contains(v.Position.X, v.Position.Y));
                    bool anyIn = p.Vertexes.Any(v => box.Contains(v.Position.X, v.Position.Y));
                    if (allIn) inScope.Add(p);
                    else if (anyIn) boundary.Add(p);
                    else outOfScope++;
                }
                log.Add($"In scope: {inScope.Count}, near boundary (flagged): {boundary.Count}, excluded as out-of-scope/leftover: {outOfScope}.");
                candidates = inScope.Concat(boundary).ToList();

                var routeGroups = ClusterRoutes(candidates);
                var result = candidates.Select(p => ClassifyOne(p, legend.TypeMap, callouts, routeGroups, boundary.Contains(p))).ToList();
                return new ClassifyResult(result, log, pitCount);
            }
            else
            {
                var routeGroups = ClusterRoutes(candidates);
                var result = candidates.Select(p => ClassifyOne(p, legend.TypeMap, callouts, routeGroups, false)).ToList();
                return new ClassifyResult(result, log, pitCount);
            }
        }

        private static int CountNewPits(List<CalloutAnnotation> callouts, DxfService.ScopeBox? scope)
        {
            return callouts.Count(c =>
                PitWord.IsMatch(c.Text) &&
                PitPlacementWord.IsMatch(c.Text) &&
                (scope == null || c.AnchorPoints.Any(a => scope.Value.Contains(a.X, a.Y))));
        }

        private static ClassifiedCable ClassifyOne(
            Polyline2D poly,
            Dictionary<(short, string), string> typeMap,
            List<CalloutAnnotation> callouts,
            Dictionary<Polyline2D, string> routeGroups,
            bool nearBoundary)
        {
            double length = DxfService.PolylineLength(poly);
            var (color, linetype) = DxfService.ResolveColorLinetype(poly);
            typeMap.TryGetValue((color, linetype), out string? baseType);
            bool isExisting = baseType == LegendReader.ExistingCableType;

            // A long route can pass near several distinct callouts (a generic "Te plaatsen 2xDB7"
            // placement note near one end, a "Lijnboring 2xDB7" method note elsewhere, a stripe-
            // color note elsewhere still) — collect all of them within range rather than only the
            // single nearest, or evidence that exists but isn't closest to the route gets lost.
            var matched = callouts
                .Select(c => (callout: c, dist: c.AnchorPoints.Min(a => DistanceToPolyline(poly, a))))
                .Where(x => x.dist < CalloutMaxDistance)
                .OrderBy(x => x.dist)
                .Select(x => x.callout)
                .ToList();

            // A single drawn route can genuinely mix techniques (mostly trenched, but bored under
            // one specific crossing) — when the nearby callouts disagree on method, don't silently
            // pick a winner, flag it so the total gets split manually during review instead.
            var distinctMethods = matched.Select(c => ParseMethod(c.Text)).Where(m => m != null).Distinct().ToList();
            string? method = distinctMethods.Count switch
            {
                0 => null,
                1 => distinctMethods[0],
                _ => MixedMethod
            };
            string? stripe = matched.Select(c => ParseStripe(c.Text)).FirstOrDefault(s => s != null);
            string evidence = matched.Count > 0
                ? string.Join(" | ", matched.Select(c => c.Text.Replace('\n', ' ')))
                : "(no nearby callout found)";

            return new ClassifiedCable(
                poly.Handle, length,
                isExisting ? null : baseType,
                stripe, method,
                routeGroups[poly],
                evidence, isExisting, nearBoundary);
        }

        // Leader-arrowhead-to-nearest-point-on-route distance — far more reliable than nearest-
        // text-to-centroid (prior investigation: about half of centroid-based matches locked onto
        // irrelevant chainage/coordinate labels instead of the real callout).
        private static double DistanceToPolyline(Polyline2D poly, Vector3 point)
        {
            double best = double.MaxValue;
            var verts = poly.Vertexes;
            for (int i = 0; i < verts.Count - 1; i++)
            {
                double d = DistanceToSegment(
                    verts[i].Position.X, verts[i].Position.Y,
                    verts[i + 1].Position.X, verts[i + 1].Position.Y,
                    point.X, point.Y);
                if (d < best) best = d;
            }
            return best;
        }

        private static double DistanceToSegment(double ax, double ay, double bx, double by, double px, double py)
        {
            double dx = bx - ax, dy = by - ay;
            double lenSq = dx * dx + dy * dy;
            double t = lenSq == 0 ? 0 : ((px - ax) * dx + (py - ay) * dy) / lenSq;
            t = Math.Clamp(t, 0, 1);
            double cx = ax + t * dx, cy = ay + t * dy;
            double ex = px - cx, ey = py - cy;
            return Math.Sqrt(ex * ex + ey * ey);
        }

        private static string? ParseMethod(string text)
        {
            if (Regex.IsMatch(text, @"mantelboring.*?110|mantelbuis.*?110|110mm.*?mantel", RegexOptions.IgnoreCase)) return "MANTEL110";
            if (Regex.IsMatch(text, @"mantelboring.*?125|mantelbuis.*?125|125mm.*?mantel", RegexOptions.IgnoreCase)) return "MANTEL125";
            if (Regex.IsMatch(text, @"mantelboring.*?200|mantelbuis.*?200|200mm.*?mantel", RegexOptions.IgnoreCase)) return "MANTEL200";
            if (Regex.IsMatch(text, @"mantelboring|mantelbuis", RegexOptions.IgnoreCase)) return "MANTEL125"; // casing mentioned, no diameter caught
            if (Regex.IsMatch(text, @"lijnboring", RegexOptions.IgnoreCase)) return "LIJNBORING";
            if (Regex.IsMatch(text, @"handboring", RegexOptions.IgnoreCase)) return "HANDBORING";
            if (Regex.IsMatch(text, @"droogtrekken", RegexOptions.IgnoreCase)) return "DROOGTREK";
            if (Regex.IsMatch(text, @"doorsteek", RegexOptions.IgnoreCase)) return "DOORSTEEK";
            // Per the drawing's own title-block legend: "Aanleg in open sleuf ... tenzij anders
            // vermeld" (installed in open trench unless otherwise stated) — a placement callout
            // with no special method IS documented evidence of Sleuf, not a silent guess.
            if (Regex.IsMatch(text, @"plaatsen|leggen|sleuf", RegexOptions.IgnoreCase)) return "SLEUF";
            return null;
        }

        private static string? ParseStripe(string text)
        {
            if (Regex.IsMatch(text, @"\bGR\b|grijs|grijze", RegexOptions.IgnoreCase)) return "GREY";
            if (Regex.IsMatch(text, @"\b(OR|OG)\b|oranje", RegexOptions.IgnoreCase)) return "ORANGE";
            if (Regex.IsMatch(text, @"\bGN\b|groen", RegexOptions.IgnoreCase)) return "GREEN";
            return null;
        }

        // Groups near-coincident polylines (matching endpoints within tolerance, either direction)
        // into one physical route — a "2xDB7" run is commonly drawn as two parallel duct lines
        // sharing one trench, and the digging-method total must count that trench once, not twice.
        private static Dictionary<Polyline2D, string> ClusterRoutes(List<Polyline2D> candidates)
        {
            var groupOf = new Dictionary<Polyline2D, int>();
            int nextGroup = 0;

            (Vector2 start, Vector2 end) Ends(Polyline2D p) =>
                (p.Vertexes[0].Position, p.Vertexes[^1].Position);

            bool Near(Vector2 a, Vector2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) < RouteClusterTolerance;

            for (int i = 0; i < candidates.Count; i++)
            {
                if (groupOf.ContainsKey(candidates[i])) continue;
                groupOf[candidates[i]] = nextGroup;
                var (s1, e1) = Ends(candidates[i]);

                for (int j = i + 1; j < candidates.Count; j++)
                {
                    if (groupOf.ContainsKey(candidates[j])) continue;
                    var (s2, e2) = Ends(candidates[j]);
                    bool sameDir = Near(s1, s2) && Near(e1, e2);
                    bool reversed = Near(s1, e2) && Near(e1, s2);
                    if (sameDir || reversed)
                        groupOf[candidates[j]] = nextGroup;
                }
                nextGroup++;
            }

            return candidates.ToDictionary(p => p, p => $"R{groupOf[p]}");
        }
    }
}
