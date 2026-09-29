using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace CadToolsApp.Services
{
    public class GeocodingService
    {
        private static string     API_KEY => GoogleApi.ApiKey;
        private static System.Net.Http.HttpClient _http => GoogleApi.Http;

        // Returns "StreetName, Locality" at the given coordinates (e.g. "Wouwerstraat, Heist-op-den-Berg").
        // For Belgian addresses, prefers the sub-municipality (deelgemeente) over the main municipality.
        // Returns (address, error) — error is non-empty when API call fails or returns non-OK status.
        public async Task<(string Address, string Error)> GetStreetNameAsync(double lat, double lon)
        {
            string url = $"https://maps.googleapis.com/maps/api/geocode/json" +
                         $"?latlng={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                         $"{lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}&key={API_KEY}";
            try
            {
                string json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
                if (status != "OK")
                {
                    string msg = root.TryGetProperty("error_message", out var em)
                        ? em.GetString() ?? "" : "";
                    return ("", $"Geocoding API status={status} {msg}".Trim());
                }

                var results = root.GetProperty("results");
                if (results.GetArrayLength() == 0)
                    return ("", "Geocoding returned 0 results.");

                if (!results[0].TryGetProperty("address_components", out var components))
                    return ("", "No address_components in result.");

                string route       = "";
                string sublocality = "";
                string locality    = "";

                foreach (var comp in components.EnumerateArray())
                {
                    if (!comp.TryGetProperty("types", out var types)) continue;
                    string name = comp.GetProperty("long_name").GetString() ?? "";
                    foreach (var type in types.EnumerateArray())
                    {
                        switch (type.GetString())
                        {
                            case "route":               route       = name; break;
                            case "sublocality_level_1":
                            case "sublocality":         sublocality = name; break;
                            case "locality":            locality    = name; break;
                        }
                    }
                }

                string place = !string.IsNullOrEmpty(sublocality) ? sublocality : locality;
                string address = string.IsNullOrEmpty(route) ? place
                               : string.IsNullOrEmpty(place) ? route
                               : $"{route}, {place}";
                return (address, "");
            }
            catch (Exception ex)
            {
                return ("", $"Geocoding exception: {ex.Message}");
            }
        }
    }
}
