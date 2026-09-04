using System;

namespace GeoSemanticSat.Core.Model;

/// <summary>
/// Represents GDAL/GeoTIFF 6-parameter affine geotransformation:
/// X_geo = A + x * B + y * C
/// Y_geo = D + x * E + y * F
/// In standard north-up rasters:
/// A = TopLeftX (MinLon / Easting)
/// B = PixelResolutionX (dx > 0)
/// C = 0 (rotation)
/// D = TopLeftY (MaxLat / Northing)
/// E = 0 (rotation)
/// F = -PixelResolutionY (dy < 0, negative for north-up)
/// </summary>
public record AffineGeoTransform(double A, double B, double C, double D, double E, double F)
{
    public static AffineGeoTransform NorthUp(double topLeftLon, double topLeftLat, double pixelSizeLon, double pixelSizeLat)
    {
        return new AffineGeoTransform(topLeftLon, pixelSizeLon, 0.0, topLeftLat, 0.0, -pixelSizeLat);
    }

    public GeoCoordinate PixelToGeo(double pixelX, double pixelY)
    {
        double lon = A + pixelX * B + pixelY * C;
        double lat = D + pixelX * E + pixelY * F;
        return new GeoCoordinate(lat, lon);
    }

    public (double PixelX, double PixelY) GeoToPixel(GeoCoordinate coord)
    {
        double det = B * F - C * E;
        if (Math.Abs(det) < 1e-12)
            throw new InvalidOperationException("Singular affine transform matrix cannot be inverted.");

        double dx = coord.Longitude - A;
        double dy = coord.Latitude - D;

        double pixelX = (dx * F - dy * C) / det;
        double pixelY = (dy * B - dx * E) / det;

        return (pixelX, pixelY);
    }
}
