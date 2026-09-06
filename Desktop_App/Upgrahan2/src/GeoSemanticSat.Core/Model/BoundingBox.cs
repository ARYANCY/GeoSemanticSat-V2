using System;

namespace GeoSemanticSat.Core.Model;

public readonly record struct BoundingBox(double MinLon, double MinLat, double MaxLon, double MaxLat)
{
    public double WidthLon => MaxLon - MinLon;
    public double HeightLat => MaxLat - MinLat;
    public GeoCoordinate Center => new((MinLat + MaxLat) / 2.0, (MinLon + MaxLon) / 2.0);

    public bool Contains(GeoCoordinate coord)
    {
        return coord.Longitude >= MinLon && coord.Longitude <= MaxLon &&
               coord.Latitude >= MinLat && coord.Latitude <= MaxLat;
    }

    public bool Intersects(BoundingBox other)
    {
        return !(other.MinLon > MaxLon || other.MaxLon < MinLon ||
                 other.MinLat > MaxLat || other.MaxLat < MinLat);
    }

    /// <summary>
    /// True surface area of the box on the WGS84 ellipsoid, in square metres.
    ///
    /// Replaces the previous estimate, patchSize^2 * gsd * (gsd * cos(lat)), which applied a
    /// latitude correction to a ground sampling distance already expressed in ground metres,
    /// double-counting the convergence of the meridians, and used a spherical cosine while
    /// the documentation claimed an ellipsoidal computation. Math.Max(0.2, cos) also silently
    /// floored the correction beyond 78.5 degrees instead of staying correct.
    ///
    /// Closed-form authalic integral between two parallels:
    ///   A = (lambda2 - lambda1) * a^2 (1 - e^2) / 2 * [ q(phi2) - q(phi1) ]
    ///   q(phi) = sin(phi) / (1 - e^2 sin^2 phi) + (1/(2e)) ln((1 + e sin phi)/(1 - e sin phi))
    /// </summary>
    public double AreaSquareMetres()
    {
        const double a = 6378137.0;               // WGS84 semi-major axis
        const double f = 1.0 / 298.257223563;     // WGS84 flattening
        double e2 = f * (2.0 - f);
        double e = Math.Sqrt(e2);

        double lon1 = Math.Min(MinLon, MaxLon) * Math.PI / 180.0;
        double lon2 = Math.Max(MinLon, MaxLon) * Math.PI / 180.0;
        double lat1 = Math.Clamp(Math.Min(MinLat, MaxLat), -90.0, 90.0) * Math.PI / 180.0;
        double lat2 = Math.Clamp(Math.Max(MinLat, MaxLat), -90.0, 90.0) * Math.PI / 180.0;

        double Q(double phi)
        {
            double sinPhi = Math.Sin(phi);
            double denom = 1.0 - e2 * sinPhi * sinPhi;
            return sinPhi / denom + (1.0 / (2.0 * e)) * Math.Log((1.0 + e * sinPhi) / (1.0 - e * sinPhi));
        }

        return Math.Abs((lon2 - lon1) * a * a * (1.0 - e2) / 2.0 * (Q(lat2) - Q(lat1)));
    }

    public override string ToString() => $"[Lon: {MinLon:F5} to {MaxLon:F5}, Lat: {MinLat:F5} to {MaxLat:F5}]";
}
