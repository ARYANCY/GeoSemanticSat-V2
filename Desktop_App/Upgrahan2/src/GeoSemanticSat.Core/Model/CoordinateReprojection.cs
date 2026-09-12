using System;

namespace GeoSemanticSat.Core.Model;

/// <summary>
/// Sovereign zero-dependency geodesic and cartographic reprojection engine.
/// Converts projected coordinates (UTM Zones 1-60 North/South, Web Mercator EPSG:3857)
/// to and from WGS84 geographic decimal degrees (EPSG:4326).
/// </summary>
public static class CoordinateReprojection
{
    private const double Wgs84A = 6378137.0; // Semi-major axis in meters
    private const double Wgs84F = 1.0 / 298.257223563; // Flattening
    private const double Wgs84B = Wgs84A * (1.0 - Wgs84F); // Semi-minor axis
    private const double E2 = (Wgs84A * Wgs84A - Wgs84B * Wgs84B) / (Wgs84A * Wgs84A); // First eccentricity squared (~0.00669438)
    private const double EPrime2 = (Wgs84A * Wgs84A - Wgs84B * Wgs84B) / (Wgs84B * Wgs84B); // Second eccentricity squared
    private const double UtmScaleFactorK0 = 0.9996;

    /// <summary>
    /// Checks if an EPSG code represents a projected UTM coordinate system.
    /// WGS 84 / UTM North: 32601 to 32660
    /// WGS 84 / UTM South: 32701 to 32760
    /// </summary>
    public static bool IsUtm(int epsgCode, out int zone, out bool isNorth)
    {
        if (epsgCode >= 32601 && epsgCode <= 32660)
        {
            zone = epsgCode - 32600;
            isNorth = true;
            return true;
        }
        if (epsgCode >= 32701 && epsgCode <= 32760)
        {
            zone = epsgCode - 32700;
            isNorth = false;
            return true;
        }
        zone = 0;
        isNorth = true;
        return false;
    }

    public static bool IsWebMercator(int epsgCode) => epsgCode == 3857 || epsgCode == 900913;

    /// <summary>
    /// Converts projected coordinates (Easting, Northing) to WGS84 (Latitude, Longitude) in degrees.
    /// </summary>
    public static GeoCoordinate ProjectedToWgs84(double x, double y, int epsgCode)
    {
        if (epsgCode == 4326)
            return new GeoCoordinate(y, x);

        if (IsWebMercator(epsgCode))
        {
            double lon = (x / Wgs84A) * (180.0 / Math.PI);
            double lat = (2.0 * Math.Atan(Math.Exp(y / Wgs84A)) - Math.PI / 2.0) * (180.0 / Math.PI);
            return new GeoCoordinate(lat, lon);
        }

        if (IsUtm(epsgCode, out int zone, out bool isNorth))
        {
            return UtmToWgs84(x, y, zone, isNorth);
        }

        // Fallback: If coordinates are in UTM range (x > 100000 or y > 100000) but EPSG wasn't explicitly set,
        // estimate zone from center or treat as UTM zone 43N (NCR/India default)
        if (Math.Abs(x) > 180.0 || Math.Abs(y) > 90.0)
        {
            return UtmToWgs84(x, y, 43, true);
        }

        return new GeoCoordinate(y, x);
    }

    /// <summary>
    /// Converts WGS84 (Latitude, Longitude) to projected coordinates (Easting, Northing).
    /// </summary>
    public static (double X, double Y) Wgs84ToProjected(GeoCoordinate coord, int epsgCode)
    {
        if (epsgCode == 4326)
            return (coord.Longitude, coord.Latitude);

        if (IsWebMercator(epsgCode))
        {
            double x = coord.Longitude * (Math.PI / 180.0) * Wgs84A;
            double latRad = Math.Max(-89.5, Math.Min(89.5, coord.Latitude)) * (Math.PI / 180.0);
            double y = Wgs84A * Math.Log(Math.Tan(Math.PI / 4.0 + latRad / 2.0));
            return (x, y);
        }

        if (IsUtm(epsgCode, out int zone, out bool isNorth))
        {
            return Wgs84ToUtm(coord.Latitude, coord.Longitude, zone, isNorth);
        }

        return (coord.Longitude, coord.Latitude);
    }

    public static GeoCoordinate UtmToWgs84(double easting, double northing, int zone, bool isNorth)
    {
        double x = easting - 500000.0;
        double y = isNorth ? northing : northing - 10000000.0;

        double m = y / UtmScaleFactorK0;
        double mu = m / (Wgs84A * (1.0 - E2 / 4.0 - 3.0 * E2 * E2 / 64.0 - 5.0 * E2 * E2 * E2 / 256.0));

        double e1 = (1.0 - Math.Sqrt(1.0 - E2)) / (1.0 + Math.Sqrt(1.0 - E2));
        double j1 = 3.0 * e1 / 2.0 - 27.0 * e1 * e1 * e1 / 32.0;
        double j2 = 21.0 * e1 * e1 / 16.0 - 55.0 * Math.Pow(e1, 4) / 32.0;
        double j3 = 151.0 * e1 * e1 * e1 / 96.0;
        double j4 = 1097.0 * Math.Pow(e1, 4) / 512.0;

        double fp = mu + j1 * Math.Sin(2.0 * mu) + j2 * Math.Sin(4.0 * mu) + j3 * Math.Sin(6.0 * mu) + j4 * Math.Sin(8.0 * mu);

        double c1 = EPrime2 * Math.Cos(fp) * Math.Cos(fp);
        double t1 = Math.Tan(fp) * Math.Tan(fp);
        double r1 = Wgs84A * (1.0 - E2) / Math.Pow(1.0 - E2 * Math.Sin(fp) * Math.Sin(fp), 1.5);
        double n1 = Wgs84A / Math.Sqrt(1.0 - E2 * Math.Sin(fp) * Math.Sin(fp));

        double d = x / (n1 * UtmScaleFactorK0);

        // Latitude
        double latFact1 = n1 * Math.Tan(fp) / r1;
        double latFact2 = d * d / 2.0;
        double latFact3 = (5.0 + 3.0 * t1 + 10.0 * c1 - 4.0 * c1 * c1 - 9.0 * EPrime2) * Math.Pow(d, 4) / 24.0;
        double latFact4 = (61.0 + 90.0 * t1 + 298.0 * c1 + 45.0 * t1 * t1 - 252.0 * EPrime2 - 3.0 * c1 * c1) * Math.Pow(d, 6) / 720.0;
        double latRad = fp - latFact1 * (latFact2 - latFact3 + latFact4);

        // Longitude
        double lonFact1 = d;
        double lonFact2 = (1.0 + 2.0 * t1 + c1) * Math.Pow(d, 3) / 6.0;
        double lonFact3 = (5.0 - 2.0 * c1 + 28.0 * t1 - 3.0 * c1 * c1 + 8.0 * EPrime2 + 24.0 * t1 * t1) * Math.Pow(d, 5) / 120.0;
        double lonRad = (lonFact1 - lonFact2 + lonFact3) / Math.Cos(fp);

        double centralMeridian = (zone - 1) * 6 - 180 + 3;
        double lat = latRad * (180.0 / Math.PI);
        double lon = centralMeridian + lonRad * (180.0 / Math.PI);

        return new GeoCoordinate(lat, lon);
    }

    public static (double Easting, double Northing) Wgs84ToUtm(double lat, double lon, int zone, bool isNorth)
    {
        double latRad = lat * (Math.PI / 180.0);
        double lonRad = lon * (Math.PI / 180.0);
        double centralMeridian = ((zone - 1) * 6 - 180 + 3) * (Math.PI / 180.0);

        double n = Wgs84A / Math.Sqrt(1.0 - E2 * Math.Sin(latRad) * Math.Sin(latRad));
        double t = Math.Tan(latRad) * Math.Tan(latRad);
        double c = EPrime2 * Math.Cos(latRad) * Math.Cos(latRad);
        double a = Math.Cos(latRad) * (lonRad - centralMeridian);

        double m = Wgs84A * ((1.0 - E2 / 4.0 - 3.0 * E2 * E2 / 64.0 - 5.0 * E2 * E2 * E2 / 256.0) * latRad
            - (3.0 * E2 / 8.0 + 3.0 * E2 * E2 / 32.0 + 45.0 * E2 * E2 * E2 / 1024.0) * Math.Sin(2.0 * latRad)
            + (15.0 * E2 * E2 / 256.0 + 45.0 * E2 * E2 * E2 / 1024.0) * Math.Sin(4.0 * latRad)
            - (35.0 * E2 * E2 * E2 / 3072.0) * Math.Sin(6.0 * latRad));

        double easting = UtmScaleFactorK0 * n * (a + (1.0 - t + c) * Math.Pow(a, 3) / 6.0
            + (5.0 - 18.0 * t + t * t + 72.0 * c - 58.0 * EPrime2) * Math.Pow(a, 5) / 120.0) + 500000.0;

        double northing = UtmScaleFactorK0 * (m + n * Math.Tan(latRad) * (a * a / 2.0
            + (5.0 - t + 9.0 * c + 4.0 * c * c) * Math.Pow(a, 4) / 24.0
            + (61.0 - 58.0 * t + t * t + 600.0 * c - 330.0 * EPrime2) * Math.Pow(a, 6) / 720.0));

        if (!isNorth) northing += 10000000.0;

        return (easting, northing);
    }
}
