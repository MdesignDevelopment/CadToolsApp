using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;

namespace CadToolsApp.Services
{
    public class StreetViewService
    {
        // 640 × 267 matches the 199:83 aspect ratio (640 * 83 / 199 ≈ 267)
        private const string IMG_SIZE = "640x267";

        internal static string ApiKey => API_KEY;
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

        // Converts Belgian Lambert 72 (x, y in metres) to WGS84 (lat, lon in degrees).
        public static (double lat, double lon) ToWgs84(double x, double y)
        {
            var csFactory = new CoordinateSystemFactory();
            var ctFactory = new CoordinateTransformationFactory();
            var transform = ctFactory.CreateFromCoordinateSystems(
                csFactory.CreateFromWkt(LAMBERT72_WKT),
                GeographicCoordinateSystem.WGS84);
            double[] ll = transform.MathTransform.Transform(new[] { x, y });
            return (ll[1], ll[0]); // ProjNet returns [lon, lat]
        }

        // Downloads a Street View static image using the heading/pitch/fov from the crop frame.
        // Returns false if there is no coverage at that location.
        public async Task<bool> DownloadImageAsync(
            double lat, double lon, double heading, double pitch, double fov, string savePath)
        {
            string url = $"https://maps.googleapis.com/maps/api/streetview" +
                         $"?size={IMG_SIZE}&location={lat},{lon}" +
                         $"&heading={heading:F0}&pitch={pitch:F1}&fov={fov:F0}&key={API_KEY}";
            try
            {
                byte[] img = await _http.GetByteArrayAsync(url);
                if (img.Length <= 5000) return false; // grey "no coverage" placeholder
                await File.WriteAllBytesAsync(savePath, img);
                return true;
            }
            catch { return false; }
        }
    }
}
