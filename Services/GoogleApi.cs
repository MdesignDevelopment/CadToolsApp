using System;
using System.IO;
using System.Net.Http;

namespace CadToolsApp.Services
{
    // Shared Google API plumbing: one key, one HttpClient. Street View and the Static Maps
    // "Ligging" inset both bill against the same key, so both read it from the same place.
    public static class GoogleApi
    {
        private const string KeyLine = "STREETVIEW_API_KEY=";

        private static string? _key;

        public static readonly HttpClient Http = new();

        public static string ApiKey => _key ??= LoadApiKey();

        // Kept out of the constructor path so a missing key surfaces as a handled error on the
        // first network-using action rather than crashing the whole tool at startup.
        public static bool TryGetApiKey(out string key, out string error)
        {
            try { key = ApiKey; error = ""; return true; }
            catch (Exception ex) { key = ""; error = ex.Message; return false; }
        }

        private static string LoadApiKey()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "config.txt");
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"config.txt not found next to the exe.\n" +
                    $"Create it at: {path}\n" +
                    $"Contents: {KeyLine}your_key_here");

            foreach (string line in File.ReadAllLines(path))
            {
                if (line.StartsWith(KeyLine, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(KeyLine.Length).Trim();
            }

            throw new InvalidOperationException(
                $"config.txt exists but is missing the {KeyLine} line.");
        }
    }
}
