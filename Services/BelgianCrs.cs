using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;

namespace CadToolsApp.Services
{
    // Drawing coordinates in these projects are Belgian Lambert 72 metres; every Google request
    // (Street View panoramas, the Static Maps "Ligging" inset) needs WGS84 degrees. One shared
    // transform so the two features cannot drift onto different datum parameters.
    public static class BelgianCrs
    {
        private const string Lambert72Wkt = @"
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

        private static readonly object Gate = new();
        private static MathTransform? _toWgs84;

        private static MathTransform Transform
        {
            get
            {
                lock (Gate)
                {
                    if (_toWgs84 != null) return _toWgs84;
                    var cs = new CoordinateSystemFactory();
                    var ct = new CoordinateTransformationFactory();
                    _toWgs84 = ct.CreateFromCoordinateSystems(
                        cs.CreateFromWkt(Lambert72Wkt),
                        GeographicCoordinateSystem.WGS84).MathTransform;
                    return _toWgs84;
                }
            }
        }

        public static (double lat, double lon) ToWgs84(double x, double y)
        {
            double[] ll = Transform.Transform(new[] { x, y });
            return (ll[1], ll[0]);
        }

        // Belgian Lambert 72 eastings/northings fall in a narrow band; anything outside it is a
        // drawing in some other coordinate system, where a transformed result would be a
        // plausible-looking point in the wrong country.
        public static bool LooksLikeLambert72(double x, double y) =>
            x > 10_000 && x < 300_000 && y > 10_000 && y < 250_000;
    }
}
