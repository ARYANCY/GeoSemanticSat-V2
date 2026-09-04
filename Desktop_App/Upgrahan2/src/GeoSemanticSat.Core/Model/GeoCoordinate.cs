using System;

namespace GeoSemanticSat.Core.Model;

public readonly record struct GeoCoordinate(double Latitude, double Longitude, double Altitude = 0)
{
    public const double EarthRadiusKm = 6371.0;

    /// <summary>
    /// Computes distance in kilometers between two geographic coordinates using the Haversine formula.
    /// </summary>
    public double DistanceToKm(GeoCoordinate other)
    {
        double lat1Rad = DegreesToRadians(Latitude);
        double lon1Rad = DegreesToRadians(Longitude);
        double lat2Rad = DegreesToRadians(other.Latitude);
        double lon2Rad = DegreesToRadians(other.Longitude);

        double deltaLat = lat2Rad - lat1Rad;
        double deltaLon = lon2Rad - lon1Rad;

        double a = Math.Sin(deltaLat / 2.0) * Math.Sin(deltaLat / 2.0) +
                   Math.Cos(lat1Rad) * Math.Cos(lat2Rad) *
                   Math.Sin(deltaLon / 2.0) * Math.Sin(deltaLon / 2.0);

        double c = 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
        return EarthRadiusKm * c;
    }

    public double DistanceToMeters(GeoCoordinate other) => DistanceToKm(other) * 1000.0;

    private static double DegreesToRadians(double deg) => deg * (Math.PI / 180.0);

    public override string ToString() => $"({Latitude:F6}°N, {Longitude:F6}°E)";
}
