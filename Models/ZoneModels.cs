using System;
using System.Collections.Generic;
using System.Linq;

namespace CadToolsApp.Models
{
    public enum ZoneType
    {
        Berm, BermTrees, Fietspad, Voetpad, RijbaanFront, RijbaanBack, ParkingFront, ParkingBack
    }

    public static class ZoneMeta
    {
        public static string Label(ZoneType t) => t switch
        {
            ZoneType.Berm         => "Berm",
            ZoneType.BermTrees    => "Berm (Trees)",
            ZoneType.Fietspad     => "Fietspad",
            ZoneType.Voetpad      => "Voetpad",
            ZoneType.RijbaanFront  => "Rijbaan ↑",
            ZoneType.RijbaanBack   => "Rijbaan ↓",
            ZoneType.ParkingFront  => "Parking ↑",
            ZoneType.ParkingBack   => "Parking ↓",
            _                      => t.ToString(),
        };

        public static string DrawingLabel(ZoneType t) => t switch
        {
            ZoneType.Berm         => "Berm",
            ZoneType.BermTrees    => "Berm",
            ZoneType.Fietspad     => "Fietspad",
            ZoneType.Voetpad      => "Voetpad",
            ZoneType.RijbaanFront  => "Rijbaan",
            ZoneType.RijbaanBack   => "Rijbaan",
            ZoneType.ParkingFront  => "Parking",
            ZoneType.ParkingBack   => "Parking",
            _                      => t.ToString(),
        };

        public static string BlockName(ZoneType t) => t switch
        {
            ZoneType.Berm         => "XSEC_BERM",
            ZoneType.BermTrees    => "XSEC_BERM_TREES",
            ZoneType.Fietspad     => "XSEC_FIETSPAD",
            ZoneType.Voetpad      => "XSEC_VOETPAD",
            ZoneType.RijbaanFront  => "XSEC_RIJBAAN_F",
            ZoneType.RijbaanBack   => "XSEC_RIJBAAN_B",
            ZoneType.ParkingFront  => "XSEC_PARKING_F",
            ZoneType.ParkingBack   => "XSEC_PARKING_B",
            _                      => null!,
        };

        public static bool IsWidthFill(ZoneType t) =>
            t == ZoneType.Berm || t == ZoneType.BermTrees;

        public static (double w, double h) IconRef(ZoneType t) => t switch
        {
            ZoneType.Fietspad     => (1.6, 1.2),
            ZoneType.RijbaanFront  => (2.2, 1.4),
            ZoneType.RijbaanBack   => (2.2, 1.4),
            ZoneType.ParkingFront  => (2.2, 1.4),
            ZoneType.ParkingBack   => (2.2, 1.4),
            _                      => (1.0, 1.0),
        };

        public static double DefaultWidth(ZoneType t) => t switch
        {
            ZoneType.Berm         => 1.0,
            ZoneType.BermTrees    => 1.5,
            ZoneType.Fietspad     => 2.0,
            ZoneType.Voetpad      => 1.5,
            ZoneType.RijbaanFront  => 3.0,
            ZoneType.RijbaanBack   => 3.0,
            ZoneType.ParkingFront  => 2.5,
            ZoneType.ParkingBack   => 2.5,
            _                      => 2.0,
        };
    }

    public class LayoutZone
    {
        public ZoneType Type  { get; set; }
        public double   Width { get; set; }

        public override string ToString() =>
            $"{ZoneMeta.Label(Type),-20}  {Width:0.##} m";
    }

    public class SectionInfo
    {
        public int     Id                 { get; }
        public double  Lat                { get; }
        public double  Lon                { get; }
        public double  ModelX             { get; }
        public double  ModelY             { get; }
        public string? ImagePath          { get; set; }
        public string? SelectedPresetName { get; set; }
        public string? StreetName         { get; set; }

        public SectionInfo(int id, double lat, double lon, double modelX = 0, double modelY = 0)
        { Id = id; Lat = lat; Lon = lon; ModelX = modelX; ModelY = modelY; }

        public override string ToString() =>
            string.IsNullOrEmpty(StreetName)
                ? $"Section {Id:D3}"
                : $"Section {Id:D3}  —  {StreetName}";
    }

    public class ZonePreset
    {
        public string Name  { get; }
        public (ZoneType Type, double Width)[] Zones { get; }

        public ZonePreset(string name, params (ZoneType, double)[] zones)
        { Name = name; Zones = zones; }

        public override string ToString() => Name;

        public static ZonePreset? FindClosestMatch(IEnumerable<LayoutZone> design, IEnumerable<ZonePreset> presets)
        {
            var designZones = design?.ToList() ?? new List<LayoutZone>();
            var candidates = presets?.ToList() ?? new List<ZonePreset>();

            if (designZones.Count == 0) return candidates.FirstOrDefault();
            if (candidates.Count == 0) return null;

            ZonePreset? best = null;
            double bestScore = double.MaxValue;

            foreach (var preset in candidates)
            {
                double score = ScoreSequence(designZones, preset.Zones);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = preset;
                }
            }

            return best;
        }

        public static ZonePreset? FindClosestMatch(IEnumerable<LayoutZone> design, params ZonePreset[] presets) =>
            FindClosestMatch(design, (IEnumerable<ZonePreset>)presets);

        private static double ScoreSequence(IReadOnlyList<LayoutZone> customZones, IReadOnlyList<(ZoneType Type, double Width)> presetZones)
        {
            double score = 0;
            int maxCount = Math.Max(customZones.Count, presetZones.Count);

            for (int i = 0; i < maxCount; i++)
            {
                var customIsMissing = i >= customZones.Count;
                var presetIsMissing = i >= presetZones.Count;

                if (customIsMissing || presetIsMissing)
                {
                    score += 25.0;
                    continue;
                }

                var customZone = customZones[i];
                var presetZone = presetZones[i];

                if (NormalizeType(customZone.Type) != NormalizeType(presetZone.Type))
                    score += 12.0;

                score += Math.Abs(customZone.Width - presetZone.Width) * 5.0;
            }

            return score;
        }

        private static ZoneType NormalizeType(ZoneType type) => type switch
        {
            ZoneType.Berm or ZoneType.BermTrees => ZoneType.Berm,
            ZoneType.RijbaanFront or ZoneType.RijbaanBack => ZoneType.RijbaanFront,
            ZoneType.ParkingFront or ZoneType.ParkingBack => ZoneType.ParkingFront,
            _ => type,
        };
    }
}
