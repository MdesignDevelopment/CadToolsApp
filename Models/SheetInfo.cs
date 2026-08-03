using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace CadToolsApp.Models
{
    // Everything a plottable sheet needs that geometry cannot supply: the title-block text
    // fields, the trench-depth variant the template's two cross-section details are annotated
    // with, and the plot scale the "SCHAAL 1/x" note and scale bar claim. Seeded from the DXF
    // filename and today's date, then confirmed/edited in CreatePlansDialog — nothing here is
    // ever written without passing through that dialog.
    public class SheetInfo
    {
        public string Dossiernummer    { get; set; } = "";
        public string Street           { get; set; } = "";
        public string City             { get; set; } = "";
        public string Opgemaakt        { get; set; } = "";
        public string GetekendDoor     { get; set; } = "";
        public string Nagekeken        { get; set; } = "";
        public string UitgevoerdDoor   { get; set; } = "";
        public string BeginDerWerken   { get; set; } = "";
        public string EindeDerWerken   { get; set; } = "";
        public string GeplaatsteLengte { get; set; } = "";
        public string GesloopteLengte  { get; set; } = "";

        // The template's cross-section details are authored at 80cm; 60cm sheets differ only in
        // the depth sentence and the "0,8 m" dimension text, so the variant is a text swap.
        public int TrenchDepthCm { get; set; } = 80;

        // 500 matches every reviewed sample sheet. CreateLayout raises it to the next standard
        // step when the marked plan area cannot fit on the sheet at this scale.
        public int ScaleDenominator { get; set; } = 500;

        // "Glasvezelput (Beton/CTB)" is a count of new pits ("1st"), not a length. Detection
        // from callouts is a starting number the drafter corrects — see CableClassifier.CountNewPits.
        public int PitCount { get; set; }

        public bool FetchLiggingMap { get; set; } = true;

        public string ScaleLabel => $"1/{ScaleDenominator}";

        // Sample project files are named "SW 25246114 - Broekstraat 70, 1860 Meise" or
        // "Beringen, Bogaarsveldstraat 20" — dossier number, street and city are all recoverable
        // from either shape, so the dialog opens pre-filled instead of blank.
        public static SheetInfo FromFileName(string dxfPath)
        {
            var info = new SheetInfo
            {
                Opgemaakt = DateTime.Today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            };

            string name = Path.GetFileNameWithoutExtension(dxfPath ?? "");
            if (string.IsNullOrWhiteSpace(name)) return info;

            // Drop the revision/purpose suffixes drafters append to the project name.
            name = Regex.Replace(name, @"[-_]\s*(Layout|Uitvoeringsplan|Overzicht)[\w.\s]*$", "",
                RegexOptions.IgnoreCase).Trim();

            string rest = name;
            var dossier = Regex.Match(name, @"^\s*(?:SW\s*[_ ]?\s*)?\(?([\d]{5,}|onbekend)\)?\s*-\s*(.+)$",
                RegexOptions.IgnoreCase);
            if (dossier.Success)
            {
                info.Dossiernummer = $"{dossier.Groups[1].Value} - {dossier.Groups[2].Value.Trim()}";
                rest = dossier.Groups[2].Value.Trim();
            }
            else
            {
                info.Dossiernummer = name;
            }

            (info.Street, info.City) = SplitStreetCity(rest);
            return info;
        }

        private static readonly Regex StreetSuffix = new(
            @"(straat|steenweg|laan|weg|plein|dreef|baan|kaai|markt|pad|wijk|park|hof|lei|singel)\b",
            RegexOptions.IgnoreCase);

        private static (string street, string city) SplitStreetCity(string text)
        {
            var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();

            if (parts.Length == 0) return ("", "");
            if (parts.Length == 1) return (CleanStreet(parts[0]), "");

            // Either order occurs in practice ("Broekstraat 70, 1860 Meise" and
            // "Beringen, Bogaarsveldstraat 20") — pick the part that names a street type.
            int streetIdx = Array.FindIndex(parts, p => StreetSuffix.IsMatch(p));
            if (streetIdx < 0) streetIdx = 0;
            int cityIdx = streetIdx == 0 ? parts.Length - 1 : 0;

            return (CleanStreet(parts[streetIdx]), CleanCity(parts[cityIdx]));
        }

        // House numbers ("Broekstraat 70", "Bogaarsveldstraat 20") belong to the address, not
        // to the street name printed under "Vergunningsaanvraag:".
        private static string CleanStreet(string s) =>
            Regex.Replace(s, @"\s+\d+\s*[A-Za-z]?\s*$", "").Trim();

        // Belgian postcodes prefix the municipality ("1860 Meise").
        private static string CleanCity(string s) =>
            Regex.Replace(s, @"^\s*\d{4}\s+", "").Trim();
    }
}
