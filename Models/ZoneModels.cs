using System;
using System.Collections.Generic;

namespace CadToolsApp.Models
{
    public enum ZoneType
    {
        Berm, BermTrees, Fietspad, Voetpad, RijbaanFront, RijbaanBack, Parking
    }

    public static class ZoneMeta
    {
        public static string Label(ZoneType t) => t switch
        {
            ZoneType.Berm         => "Berm",
            ZoneType.BermTrees    => "Berm (Trees)",
            ZoneType.Fietspad     => "Fietspad",
            ZoneType.Voetpad      => "Voetpad",
            ZoneType.RijbaanFront => "Rijbaan ↑",
            ZoneType.RijbaanBack  => "Rijbaan ↓",
            ZoneType.Parking      => "Parking",
            _                     => t.ToString(),
        };

        public static string DrawingLabel(ZoneType t) => t switch
        {
            ZoneType.Berm         => "Berm",
            ZoneType.BermTrees    => "Berm",
            ZoneType.Fietspad     => "Fietspad",
            ZoneType.Voetpad      => "Voetpad",
            ZoneType.RijbaanFront => "Rijbaan",
            ZoneType.RijbaanBack  => "Rijbaan",
            ZoneType.Parking      => "Parking",
            _                     => t.ToString(),
        };

        public static string BlockName(ZoneType t) => t switch
        {
            ZoneType.Berm         => "XSEC_BERM",
            ZoneType.BermTrees    => "XSEC_BERM_TREES",
            ZoneType.Fietspad     => "XSEC_FIETSPAD",
            ZoneType.Voetpad      => "XSEC_VOETPAD",
            ZoneType.RijbaanFront => "XSEC_RIJBAAN_F",
            ZoneType.RijbaanBack  => "XSEC_RIJBAAN_B",
            ZoneType.Parking      => "XSEC_PARKING",
            _                     => null!,
        };

        public static bool IsWidthFill(ZoneType t) =>
            t == ZoneType.Berm || t == ZoneType.BermTrees;

        public static (double w, double h) IconRef(ZoneType t) => t switch
        {
            ZoneType.Fietspad     => (1.6, 1.2),
            ZoneType.RijbaanFront => (2.2, 1.4),
            ZoneType.RijbaanBack  => (2.2, 1.4),
            ZoneType.Parking      => (1.0, 1.4),
            _                     => (1.0, 1.0),
        };

        public static double DefaultWidth(ZoneType t) => t switch
        {
            ZoneType.Berm         => 1.0,
            ZoneType.BermTrees    => 1.5,
            ZoneType.Fietspad     => 2.0,
            ZoneType.Voetpad      => 1.5,
            ZoneType.RijbaanFront => 3.0,
            ZoneType.RijbaanBack  => 3.0,
            ZoneType.Parking      => 2.5,
            _                     => 2.0,
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
        public int     Id        { get; }
        public double  Lat       { get; }
        public double  Lon       { get; }
        public string? ImagePath { get; set; }

        public SectionInfo(int id, double lat, double lon)
        { Id = id; Lat = lat; Lon = lon; }

        public override string ToString() => $"Section {Id:D3}";
    }

    public class ZonePreset
    {
        public string Name  { get; }
        public (ZoneType Type, double Width)[] Zones { get; }

        public ZonePreset(string name, params (ZoneType, double)[] zones)
        { Name = name; Zones = zones; }

        public override string ToString() => Name;
    }
}
