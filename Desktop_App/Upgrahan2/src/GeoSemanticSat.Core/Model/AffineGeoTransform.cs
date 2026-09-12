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
public record AffineGeoTransform(double A, double B, double C, double D, double E, double F, int EpsgCode = 4326)
{
    public static AffineGeoTransform NorthUp(double topLeftLon, double topLeftLat, double pixelSizeLon, double pixelSizeLat, int epsgCode = 4326)
    {
        return new AffineGeoTransform(topLeftLon, pixelSizeLon, 0.0, topLeftLat, 0.0, -pixelSizeLat, epsgCode);
    }

    public GeoCoordinate PixelToGeo(double pixelX, double pixelY)
    {
        double x = A + pixelX * B + pixelY * C;
        double y = D + pixelX * E + pixelY * F;
        return CoordinateReprojection.ProjectedToWgs84(x, y, EpsgCode);
    }

    public (double PixelX, double PixelY) GeoToPixel(GeoCoordinate coord)
    {
        double det = B * F - C * E;
        if (Math.Abs(det) < 1e-12)
            throw new InvalidOperationException("Singular affine transform matrix cannot be inverted.");

        var (projX, projY) = CoordinateReprojection.Wgs84ToProjected(coord, EpsgCode);

        double dx = projX - A;
        double dy = projY - D;

        double pixelX = (dx * F - dy * C) / det;
        double pixelY = (dy * B - dx * E) / det;

        return (pixelX, pixelY);
    }
}
