using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace CadToolsApp.Services
{
    public class StreetViewService
    {
        // 640 × 267 matches the 199:83 aspect ratio (640 * 83 / 199 ≈ 267)
        private const string IMG_SIZE = "640x267";

        private static string API_KEY => GoogleApi.ApiKey;
        private static HttpClient _http => GoogleApi.Http;

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
