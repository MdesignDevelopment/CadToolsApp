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

        private static readonly string API_KEY = LoadApiKey();

        private static string LoadApiKey()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "config.txt");
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"config.txt not found next to the exe.\n" +
                    $"Create it at: {path}\n" +
                    $"Contents: STREETVIEW_API_KEY=your_key_here");

            foreach (string line in File.ReadAllLines(path))
            {
                if (line.StartsWith("STREETVIEW_API_KEY=", StringComparison.OrdinalIgnoreCase))
                    return line.Substring("STREETVIEW_API_KEY=".Length).Trim();
            }

            throw new InvalidOperationException(
                "config.txt exists but is missing the STREETVIEW_API_KEY= line.");
        }

        private static readonly HttpClient _http = new HttpClient();

        private static readonly string LAMBERT72_WKT = @"
            PROJCS[""Belge 1972 / Belgian Lambert 72"",
                GEOGCS[""Belge 1972"",
                    DATUM[""Reseau_National_Belge_1972"",
                        SPHEROID[""International 1924"",6378388,297],
                        TOWGS84[-106.869,52.2978,-103.724,0.3366,-0.457,1.8422,-1.2747]],
                    PRIMEM[""Greenwich"",0],
                    UNIT[""degree"",0.0174532925199433]],
                PROJECTION[""Lambert_Conformal_Conic_2SP""],
                PARAMETER[""standard_parallel_1"",51.16666723333333],
                PARAMETER[""standard_parallel_2"",49.8333339],
                PARAMETER[""latitude_of_origin"",90],
                PARAMETER[""central_meridian"",4.367486666666666],
                PARAMETER[""false_easting"",150000.013],
                PARAMETER[""false_northing"",5400088.438],
                UNIT[""metre"",1]]";

        public event Action<string>? Progress;

        public void FetchImages(IList<SectionInfo> sections, string saveFolder)
        {
            var csFactory = new CoordinateSystemFactory();
            var ctFactory = new CoordinateTransformationFactory();
            var transform = ctFactory.CreateFromCoordinateSystems(
                csFactory.CreateFromWkt(LAMBERT72_WKT),
                GeographicCoordinateSystem.WGS84);

            int saved = 0, nocover = 0, errors = 0;

            foreach (var sec in sections)
            {
                double[] ll = transform.MathTransform.Transform(
                    new[] { GetSectionX(sec), GetSectionY(sec) });
                double lon = ll[0], lat = ll[1];
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
