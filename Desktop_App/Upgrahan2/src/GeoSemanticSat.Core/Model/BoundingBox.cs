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

    public override string ToString() => $"[Lon: {MinLon:F5} to {MaxLon:F5}, Lat: {MinLat:F5} to {MaxLat:F5}]";
}
