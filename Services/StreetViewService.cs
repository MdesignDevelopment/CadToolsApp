using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace CadToolsApp.Services
{
    public class StreetViewService
    {
        // 1280 × 474 — height slightly above 1/3 of width (2.7:1), ~177 × 65 mm in the layout
        private const string IMG_SIZE = "1280x474";

        private static string API_KEY => GoogleApi.ApiKey;
        private static HttpClient _http => GoogleApi.Http;

        // Downloads a Street View static image using the exact panorama ID from the interactive viewer.
        // Using pano= instead of location= guarantees the same panorama is used for both.
        public async Task<bool> DownloadImageAsync(
            string panoId, double heading, double pitch, double fov, string savePath)
        {
            string location = string.IsNullOrEmpty(panoId)
                ? "" : $"&pano={Uri.EscapeDataString(panoId)}";
            string url = $"https://maps.googleapis.com/maps/api/streetview" +
                         $"?size={IMG_SIZE}{location}" +
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
