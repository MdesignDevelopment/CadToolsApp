using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using netDxf;
using netDxf.Blocks;
using netDxf.Entities;
using netDxf.Objects;
using netDxf.Units;

namespace CadToolsApp.Services
{
    // Builds the "Ligging" locator inset: a Google Static Maps roadmap with the project's own
    // routes traced over it, dropped into the title block's Ligging cell. Replaces the step where
    // the drafter screenshots Google Maps, draws the route in red by hand and pastes the result.
    public static class LiggingMapService
    {
        private const string MapLayer = "LIGGING_MAP";
        private const int    MaxRequestEdge = 640;   // Static Maps free-tier limit per side
        private const int    MaxPathPoints = 90;     // keeps the request URL well inside 8192 chars
        private const double MinPointSpacing = 8.0;  // metres — denser vertices are invisible at inset scale
        private const string RouteColour = "0xff0000ff";

        public static (bool ok, List<string> log) AddLiggingMap(
            DxfDocument doc, Block blk, IEnumerable<IReadOnlyList<Vector2>> routes,
            string saveFolder, string baseName)
        {
            var log = new List<string>();

            if (!GoogleApi.TryGetApiKey(out string key, out string keyError))
            {
                log.Add($"Ligging map skipped — {keyError}");
                return (false, log);
            }

            var area = TitleBlockWriter.FindLiggingImageArea(blk);
            if (area == null)
            {
                log.Add("Ligging map skipped — could not locate the 'Ligging:' cell in the title block.");
                return (false, log);
            }

            var paths = BuildPaths(routes, out int totalPoints);
            if (paths.Count == 0)
            {
                log.Add("Ligging map skipped — no route geometry in Belgian Lambert 72 coordinates to trace.");
                return (false, log);
            }

            var rect = area.Value;
            double aspect = rect.Width / rect.Height;
            int reqW = aspect >= 1 ? MaxRequestEdge : (int)Math.Round(MaxRequestEdge * aspect);
            int reqH = aspect >= 1 ? (int)Math.Round(MaxRequestEdge / aspect) : MaxRequestEdge;

            string url = BuildUrl(paths, reqW, reqH, key);
            string file = Path.Combine(saveFolder, $"{Sanitize(baseName)}_ligging.png");

            try
            {
                using var response = GoogleApi.Http.GetAsync(url).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    // Google explains a rejection in the response body ("not authorized to use this
                    // API", "billing not enabled"); the bare status code alone is not actionable.
                    string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    log.Add($"Ligging map skipped — Static Maps returned {(int)response.StatusCode} " +
                            $"{response.StatusCode}: {Summarise(body)}");
                    if ((int)response.StatusCode == 403)
                        log.Add("  The 'Maps Static API' is a separate API from 'Street View Static API' — " +
                                "enable it for this key in the Google Cloud console, then retry.");
                    return (false, log);
                }

                byte[] png = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (png.Length < 5000)
                {
                    log.Add($"Ligging map skipped — Static Maps returned only {png.Length} bytes, not an image.");
                    return (false, log);
                }
                File.WriteAllBytes(file, png);
            }
            catch (Exception ex)
            {
                log.Add($"Ligging map skipped — download failed: {ex.Message}");
                return (false, log);
            }

            // scale=2 doubles the returned pixels for the same map view, so the inset stays sharp
            // when the A0 sheet is plotted full size.
            int pxW = reqW * 2, pxH = reqH * 2;

            int removed = RemovePrevious(blk);
            var layer = TitleBlockWriter.EnsureLayer(doc, MapLayer);
            var definition = new ImageDefinition($"LIGGING_{Sanitize(baseName)}", file,
                pxW, 1.0, pxH, 1.0, ImageResolutionUnits.Unitless);

            blk.Entities.Add(new Image(definition, new Vector3(rect.Left, rect.Bottom, 0),
                rect.Width, rect.Height) { Layer = layer });

            if (removed > 0) log.Add($"Ligging map: replaced {removed} image(s) from a previous run.");
            log.Add($"Ligging map: {totalPoints} route point(s) traced over a {pxW}x{pxH}px roadmap, " +
                    $"placed at {rect.Width:F0}x{rect.Height:F0} in the Ligging cell.");
            log.Add($"Ligging map image saved as: {file}");
            log.Add("Keep that PNG next to the DXF — the sheet references it, it is not embedded.");
            return (true, log);
        }

        private static string BuildUrl(List<string> paths, int w, int h, string key)
        {
            // No center/zoom: Static Maps fits the viewport to the supplied paths, which is
            // exactly the "show where this project is" framing the samples use.
            var sb = new StringBuilder("https://maps.googleapis.com/maps/api/staticmap?");
            sb.Append(CultureInfo.InvariantCulture, $"size={w}x{h}&scale=2&maptype=roadmap&format=png");
            foreach (string p in paths)
                sb.Append("&path=").Append($"color:{RouteColour}|weight:4|").Append(p);
            sb.Append("&key=").Append(key);
            return sb.ToString();
        }

        // One path string per drawn route, vertices thinned so a 2000-vertex survey polyline does
        // not blow the URL length while still following the street.
        private static List<string> BuildPaths(IEnumerable<IReadOnlyList<Vector2>> routes, out int totalPoints)
        {
            var paths = new List<string>();
            totalPoints = 0;

            foreach (var route in routes.OrderByDescending(r => r.Count))
            {
                if (totalPoints >= MaxPathPoints) break;
                var kept = Thin(route, MaxPathPoints - totalPoints);
                if (kept.Count < 2) continue;

                var points = new List<string>(kept.Count);
                foreach (var v in kept)
                {
                    if (!BelgianCrs.LooksLikeLambert72(v.X, v.Y)) return new List<string>();
                    var (lat, lon) = BelgianCrs.ToWgs84(v.X, v.Y);
                    points.Add(string.Format(CultureInfo.InvariantCulture, "{0:F6},{1:F6}", lat, lon));
                }
                paths.Add(string.Join("|", points));
                totalPoints += points.Count;
            }
            return paths;
        }

        private static List<Vector2> Thin(IReadOnlyList<Vector2> route, int budget)
        {
            var kept = new List<Vector2>();
            if (route.Count == 0 || budget < 2) return kept;

            kept.Add(route[0]);
            foreach (var v in route.Skip(1))
            {
                var last = kept[^1];
                if (Math.Sqrt((v.X - last.X) * (v.X - last.X) + (v.Y - last.Y) * (v.Y - last.Y)) >= MinPointSpacing)
                    kept.Add(v);
            }
            if (kept[^1] != route[^1]) kept.Add(route[^1]);

            if (kept.Count <= budget) return kept;

            // Even decimation rather than truncation — a truncated path would draw only the first
            // part of the route and silently misrepresent where the project is.
            var reduced = new List<Vector2>(budget);
            double step = (double)(kept.Count - 1) / (budget - 1);
            for (int i = 0; i < budget; i++) reduced.Add(kept[(int)Math.Round(i * step)]);
            return reduced;
        }

        private static int RemovePrevious(Block blk)
        {
            var stale = blk.Entities
                .Where(e => e.Layer != null && e.Layer.Name.Equals(MapLayer, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var e in stale) blk.Entities.Remove(e);
            return stale.Count;
        }

        // Google's error bodies are short text or a small HTML page; keep the first readable line
        // and never echo back anything long enough to bury the rest of the log.
        private static string Summarise(string body)
        {
            string text = System.Text.RegularExpressions.Regex.Replace(body ?? "", "<[^>]+>", " ");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length <= 200 ? text : text.Substring(0, 200) + "…";
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
            return sb.ToString().Trim();
        }
    }
}
