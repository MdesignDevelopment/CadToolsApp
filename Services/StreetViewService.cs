using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using CadToolsApp.Models;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;

namespace CadToolsApp.Services
{
    public class StreetViewService
    {
        private const string IMG_SIZE = "640x640";
        private const int    IMG_FOV  = 120;

        private static string API_KEY => GoogleApi.ApiKey;
        private static HttpClient _http => GoogleApi.Http;

        public event Action<string>? Progress;

        public void FetchImages(IList<SectionInfo> sections, string saveFolder)
        {
            int saved = 0, nocover = 0, errors = 0;

            foreach (var sec in sections)
            {
                var (lat, lon) = BelgianCrs.ToWgs84(GetSectionX(sec), GetSectionY(sec));
                double hdg = GetHeading(lat, lon);
                double h1 = (hdg + 90)  % 360;
                double h2 = (hdg + 270) % 360;

                foreach (var (sfx, h) in new[] { ("_left", h1), ("_right", h2) })
                {
                    string file = Path.Combine(saveFolder, $"section_{sec.Id:D3}{sfx}.jpg");
                    string url  = $"https://maps.googleapis.com/maps/api/streetview" +
                                  $"?size={IMG_SIZE}&location={lat},{lon}" +
                                  $"&heading={h:F0}&fov={IMG_FOV}&pitch=0&key={API_KEY}";
                    try
                    {
                        byte[] img = _http.GetByteArrayAsync(url).GetAwaiter().GetResult();
                        if (img.Length > 5000) { File.WriteAllBytes(file, img); saved++; }
                        else nocover++;
                    }
                    catch (Exception ex) { Progress?.Invoke($"  ERR: {ex.Message}"); errors++; }
                }
                Progress?.Invoke($"  Section {sec.Id:D3} — done");
            }

            Progress?.Invoke($"Done — {saved} saved, {nocover} no coverage, {errors} errors.");
        }

        private static double GetSectionX(SectionInfo sec) => _sectionCoords.TryGetValue(sec.Id, out var c) ? c.x : 0;
        private static double GetSectionY(SectionInfo sec) => _sectionCoords.TryGetValue(sec.Id, out var c) ? c.y : 0;

        // Coordinates are stored by DxfService after scanning, then passed here.
        private static readonly Dictionary<int, (double x, double y)> _sectionCoords = new();

        public static void StoreSectionCoords(int id, double x, double y) =>
            _sectionCoords[id] = (x, y);

        private double GetHeading(double lat, double lon)
        {
            try
            {
                string url = $"https://maps.googleapis.com/maps/api/streetview/metadata" +
                             $"?location={lat},{lon}&key={API_KEY}";
                string json = _http.GetStringAsync(url).GetAwaiter().GetResult();
                using JsonDocument d = JsonDocument.Parse(json);
                JsonElement r = d.RootElement;
                if (r.GetProperty("status").GetString() != "OK") return 0;
                JsonElement loc = r.GetProperty("location");
                double pLat = loc.GetProperty("lat").GetDouble();
                double pLon = loc.GetProperty("lng").GetDouble();
                double b = Math.Atan2(pLon - lon, pLat - lat) * 180.0 / Math.PI;
                return (b + 360) % 360;
            }
            catch { return 0; }
        }
    }
}
