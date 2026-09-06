using System;
using System.Collections.Generic;
using System.IO;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Processing;

namespace GeoSemanticSat.Core.Raster;

public enum VisualRenderMode
{
    TrueColorRGB,
    FalseColorInfrared,       // NIR (B8), Red (B4), Green (B3)
    SWIR_GeologicalMoisture,  // SWIR1 (B11), NIR (B8), Red (B4)
    NDVI_Heatmap,             // Vegetation biomass density
    NDWI_WaterMap,            // Water delineation
    NDBI_BuiltUpUrban,        // Concrete, built structures
    SAR_MicrowaveSimulation,  // C-band radar roughness & corner reflections
    ThermalRadiance,          // Longwave thermal radiance heat signatures
    ChangeOverlay             // Visual diff overlay
}

/// <summary>
/// Cross-Platform Satellite Imagery & Change Heatmap Raster Visualizer.
/// Generates 32-bit BGRA pixel buffers and standard BMP streams directly from multi-spectral bands
/// for rendering in Avalonia UI without external dependencies.
/// </summary>
public static class RasterVisualizer
{
    /// <summary>
    /// Renders a patch or full tile into a standard BMP MemoryStream readable by Avalonia Bitmap.
    /// </summary>
    public static MemoryStream RenderTileToBmpStream(
        SatelliteTile tile,
        int startX = 0,
        int startY = 0,
        int width = -1,
        int height = -1,
        VisualRenderMode mode = VisualRenderMode.TrueColorRGB)
    {
        if (width <= 0) width = tile.Width - startX;
        if (height <= 0) height = tile.Height - startY;

        width = Math.Clamp(width, 1, tile.Width - startX);
        height = Math.Clamp(height, 1, tile.Height - startY);

        byte[] bgra = RenderToBgraBytes(tile, startX, startY, width, height, mode);
        return CreateBmpStream(bgra, width, height);
    }

    /// <summary>
    /// Generates a composite Change Heatmap visualizing baseline T1 overlaid with color-coded detected changes.
    /// If selectedChange is provided, other candidates are drawn subtly while the active candidate is prominently highlighted.
    /// </summary>
    public static MemoryStream RenderChangeHeatmapBmpStream(
        SatelliteTile t1,
        SatelliteTile t2,
        IReadOnlyList<ChangeRecord> changes,
        ChangeRecord? selectedChange = null,
        int width = -1,
        int height = -1)
    {
        if (width <= 0) width = t1.Width;
        if (height <= 0) height = t1.Height;

        width = Math.Clamp(width, 1, t1.Width);
        height = Math.Clamp(height, 1, t1.Height);

        // Start with True Color background of T1
        byte[] bgra = RenderToBgraBytes(t1, 0, 0, width, height, VisualRenderMode.TrueColorRGB);

        // Apply change highlights
        foreach (var change in changes)
        {
            var (px1, py1) = t1.Transform.GeoToPixel(new GeoCoordinate(change.Bounds.MaxLat, change.Bounds.MinLon));
            var (px2, py2) = t1.Transform.GeoToPixel(new GeoCoordinate(change.Bounds.MinLat, change.Bounds.MaxLon));

            int x0 = Math.Clamp((int)Math.Min(px1, px2), 0, width - 1);
            int y0 = Math.Clamp((int)Math.Min(py1, py2), 0, height - 1);
            int x1 = Math.Clamp((int)Math.Max(px1, px2), 0, width - 1);
            int y1 = Math.Clamp((int)Math.Max(py1, py2), 0, height - 1);

            bool isSelected = selectedChange != null && change.Id == selectedChange.Id;
            var (r, g, b) = isSelected ? ((byte)0, (byte)210, (byte)255) : GetColorRgbForChangeType(change.Type);

            double fillAlpha = isSelected ? 0.35 : 0.20;
            double bgAlpha = 1.0 - fillAlpha;

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int idx = (y * width + x) * 4;
                    bool isBorder = (x == x0 || x == x1 || y == y0 || y == y1);

                    if (isBorder)
                    {
                        bgra[idx + 0] = b;
                        bgra[idx + 1] = g;
                        bgra[idx + 2] = r;
                        bgra[idx + 3] = 255;
                    }
                    else
                    {
                        bgra[idx + 0] = (byte)(bgra[idx + 0] * bgAlpha + b * fillAlpha);
                        bgra[idx + 1] = (byte)(bgra[idx + 1] * bgAlpha + g * fillAlpha);
                        bgra[idx + 2] = (byte)(bgra[idx + 2] * bgAlpha + r * fillAlpha);
                    }
                }
            }

            if (isSelected)
            {
                // Draw tactical corner brackets around selected candidate
                int clen = 5;
                DrawCornerBrackets(bgra, width, height, Math.Max(0, x0 - 2), Math.Max(0, y0 - 2), Math.Min(width - 1, x1 + 2), Math.Min(height - 1, y1 + 2), clen, 255, 255, 255);
            }
        }

        return CreateBmpStream(bgra, width, height);
    }

    public static byte[] RenderToBgraBytes(
        SatelliteTile tile,
        int startX,
        int startY,
        int w,
        int h,
        VisualRenderMode mode)
    {
        byte[] bgra = new byte[w * h * 4];

        var red = tile.GetBandOrFallback(SpectralBand.Red, SpectralBand.Red);
        var green = tile.GetBandOrFallback(SpectralBand.Green, SpectralBand.Red);
        var blue = tile.GetBandOrFallback(SpectralBand.Blue, SpectralBand.Red);
        var nir = tile.GetBandOrFallback(SpectralBand.NIR, SpectralBand.Red);
        var swir = tile.GetBandOrFallback(SpectralBand.SWIR1, SpectralBand.Red);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int srcX = Math.Clamp(startX + x, 0, tile.Width - 1);
                int srcY = Math.Clamp(startY + y, 0, tile.Height - 1);
                int outIdx = (y * w + x) * 4;

                byte rByte, gByte, bByte;

                switch (mode)
                {
                    case VisualRenderMode.FalseColorInfrared:
                        // Standard NIR false color: NIR -> Red, Red -> Green, Green -> Blue
                        rByte = (byte)Math.Clamp(nir[srcY, srcX] * 255.0f, 0, 255);
                        gByte = (byte)Math.Clamp(red[srcY, srcX] * 255.0f, 0, 255);
                        bByte = (byte)Math.Clamp(green[srcY, srcX] * 255.0f, 0, 255);
                        break;

                    case VisualRenderMode.SWIR_GeologicalMoisture:
                        // SWIR-NIR-Red: Penetrates haze/smoke, highlights disturbed soil and concrete
                        rByte = (byte)Math.Clamp(swir[srcY, srcX] * 255.0f, 0, 255);
                        gByte = (byte)Math.Clamp(nir[srcY, srcX] * 255.0f, 0, 255);
                        bByte = (byte)Math.Clamp(red[srcY, srcX] * 255.0f, 0, 255);
                        break;

                    case VisualRenderMode.NDVI_Heatmap:
                        // NDVI Color scale: green for vegetation, brown for soil, blue for water
                        float ndvi = (nir[srcY, srcX] + red[srcY, srcX]) > 1e-4f
                            ? (nir[srcY, srcX] - red[srcY, srcX]) / (nir[srcY, srcX] + red[srcY, srcX])
                            : 0f;
                        if (ndvi > 0.3f)
                        {
                            rByte = (byte)(40 + (1.0f - ndvi) * 100);
                            gByte = (byte)Math.Clamp(ndvi * 255.0f, 120, 255);
                            bByte = 40;
                        }
                        else if (ndvi < 0.0f)
                        {
                            rByte = 20; gByte = 80; bByte = 220; // Water
                        }
                        else
                        {
                            rByte = 180; gByte = 140; bByte = 90; // Bare earth
                        }
                        break;

                    case VisualRenderMode.NDWI_WaterMap:
                        // Normalized Difference Water Index
                        float ndwi = (green[srcY, srcX] + nir[srcY, srcX]) > 1e-4f
                            ? (green[srcY, srcX] - nir[srcY, srcX]) / (green[srcY, srcX] + nir[srcY, srcX])
                            : 0f;
                        if (ndwi > 0.05f)
                        {
                            rByte = 14;
                            gByte = (byte)Math.Clamp(140 + ndwi * 115, 140, 255);
                            bByte = 240; // Vibrant Cyan-Blue water
                        }
                        else
                        {
                            rByte = (byte)Math.Clamp(30 + red[srcY, srcX] * 40, 0, 70);
                            gByte = (byte)Math.Clamp(30 + green[srcY, srcX] * 40, 0, 70);
                            bByte = (byte)Math.Clamp(35 + blue[srcY, srcX] * 40, 0, 80);
                        }
                        break;

                    case VisualRenderMode.NDBI_BuiltUpUrban:
                        // Normalized Difference Built-up Index (Concrete, Asphalt, Structures)
                        float ndbi = (swir[srcY, srcX] + nir[srcY, srcX]) > 1e-4f
                            ? (swir[srcY, srcX] - nir[srcY, srcX]) / (swir[srcY, srcX] + nir[srcY, srcX])
                            : 0f;
                        if (ndbi > 0.02f)
                        {
                            // Fiery orange-red highlight for concrete/structures
                            rByte = (byte)Math.Clamp(210 + ndbi * 45, 210, 255);
                            gByte = (byte)Math.Clamp(50 + (1.0f - ndbi) * 130, 20, 180);
                            bByte = 20;
                        }
                        else
                        {
                            // Cool slate background
                            rByte = 16;
                            gByte = 22;
                            bByte = 38;
                        }
                        break;

                    case VisualRenderMode.SAR_MicrowaveSimulation:
                        // Simulates Sentinel-1 C-Band radar backscatter (roughness / metallic double-bounce)
                        float waterCheck = (green[srcY, srcX] - nir[srcY, srcX]);
                        if (waterCheck > 0.12f)
                        {
                            rByte = 0; gByte = 5; bByte = 10; // Specular water is radar black
                        }
                        else
                        {
                            float roughness = (swir[srcY, srcX] * 0.65f + red[srcY, srcX] * 0.35f);
                            if (roughness > 0.32f)
                            {
                                // Built concrete/metal corner reflectors: glowing radar white/cyan
                                rByte = (byte)Math.Clamp(roughness * 255.0f, 180, 255);
                                gByte = 255;
                                bByte = 240;
                            }
                            else
                            {
                                // Ground/vegetation diffuse return (CRT phosphor green)
                                rByte = 18;
                                gByte = (byte)Math.Clamp(roughness * 210.0f, 35, 150);
                                bByte = 28;
                            }
                        }
                        break;

                    case VisualRenderMode.ThermalRadiance:
                        // Simulates Thermal Infrared (TIR) longwave surface heat emission
                        float heat = swir[srcY, srcX] * 0.70f + red[srcY, srcX] * 0.30f;
                        if (heat > 0.40f)
                        {
                            // High thermal emission (active facility, metal, engine heat)
                            rByte = 255;
                            gByte = (byte)Math.Clamp(180 + (heat - 0.4f) * 125, 180, 255);
                            bByte = (byte)Math.Clamp((heat - 0.4f) * 200, 0, 255);
                        }
                        else if (heat > 0.20f)
                        {
                            // Moderate heat
                            rByte = (byte)Math.Clamp(120 + (heat - 0.2f) * 600, 120, 255);
                            gByte = (byte)Math.Clamp((heat - 0.2f) * 400, 0, 150);
                            bByte = (byte)Math.Clamp(150 - (heat - 0.2f) * 500, 20, 150);
                        }
                        else
                        {
                            // Cold / vegetative moisture absorption
                            rByte = (byte)Math.Clamp(heat * 300, 0, 60);
                            gByte = 10;
                            bByte = (byte)Math.Clamp(30 + heat * 400, 30, 110);
                        }
                        break;

                    case VisualRenderMode.TrueColorRGB:
                    default:
                        rByte = (byte)Math.Clamp(red[srcY, srcX] * 255.0f, 0, 255);
                        gByte = (byte)Math.Clamp(green[srcY, srcX] * 255.0f, 0, 255);
                        bByte = (byte)Math.Clamp(blue[srcY, srcX] * 255.0f, 0, 255);
                        break;
                }

                bgra[outIdx + 0] = bByte;
                bgra[outIdx + 1] = gByte;
                bgra[outIdx + 2] = rByte;
                bgra[outIdx + 3] = 255; // Alpha
            }
        }

        return bgra;
    }

    /// <summary>
    /// Creates a valid uncompressed 32-bit BMP stream from BGRA byte array.
    /// Works with zero external libraries and loads natively in Avalonia Bitmap.
    /// </summary>
    public static MemoryStream CreateBmpStream(byte[] bgra, int width, int height)
    {
        var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, System.Text.Encoding.Default, leaveOpen: true);

        int pixelDataSize = width * height * 4;
        int fileSize = 54 + pixelDataSize;

        // BITMAPFILEHEADER (14 bytes)
        bw.Write((byte)'B');
        bw.Write((byte)'M');
        bw.Write(fileSize);
        bw.Write((ushort)0);
        bw.Write((ushort)0);
        bw.Write(54); // Offset to pixel data

        // BITMAPINFOHEADER (40 bytes)
        bw.Write(40); // header size
        bw.Write(width);
        bw.Write(-height); // negative for top-down bitmap
        bw.Write((ushort)1); // planes
        bw.Write((ushort)32); // 32 bits per pixel (BGRA)
        bw.Write(0); // BI_RGB (uncompressed)
        bw.Write(pixelDataSize);
        bw.Write(2835); // horizontal resolution (~72 DPI)
        bw.Write(2835); // vertical resolution
        bw.Write(0);
        bw.Write(0);

        bw.Write(bgra);
        ms.Seek(0, SeekOrigin.Begin);
        return ms;
    }

    private static (byte R, byte G, byte B) GetColorRgbForChangeType(ChangeType type) => type switch
    {
        ChangeType.Construction => (239, 68, 68),    // Red
        ChangeType.Clearance => (245, 158, 11),      // Amber
        ChangeType.WaterExtentVariation => (14, 165, 233), // Cyan / Water
        ChangeType.RoadDevelopment => (168, 85, 247),// Purple
        ChangeType.ActivityConcentration => (249, 115, 22), // Orange
        _ => (255, 255, 255)
    };

    public record FocusedChangeInspection(
        MemoryStream T1Stream,
        MemoryStream T2Stream,
        MemoryStream OverlayStream,
        int CropX,
        int CropY,
        int CropWidth,
        int CropHeight,
        double PeakMagnitude,
        double MeanMagnitude
    );

    /// <summary>
    /// Generates high-detail focused crops (T1, T2, and Change Vector Heatmap Overlay with tactical reticle)
    /// for a specific detected change candidate.
    /// The heatmap is rendered using continuous Change Vector Analysis (CVA) or specific spectral indices
    /// with standard remote sensing colormaps, showing real physical change gradients rather than arbitrary flat blocks.
    /// </summary>
    public static FocusedChangeInspection RenderFocusedSite(
        SatelliteTile t1,
        SatelliteTile t2,
        ChangeRecord change,
        VisualRenderMode mode = VisualRenderMode.TrueColorRGB,
        string heatmapType = "CVA",
        int padding = 28,
        int scale = 2)
    {
        var (px1, py1) = t1.Transform.GeoToPixel(new GeoCoordinate(change.Bounds.MaxLat, change.Bounds.MinLon));
        var (px2, py2) = t1.Transform.GeoToPixel(new GeoCoordinate(change.Bounds.MinLat, change.Bounds.MaxLon));

        int minX = (int)Math.Min(px1, px2);
        int minY = (int)Math.Min(py1, py2);
        int maxX = (int)Math.Max(px1, px2);
        int maxY = (int)Math.Max(py1, py2);

        int cropX = Math.Clamp(minX - padding, 0, t1.Width - 1);
        int cropY = Math.Clamp(minY - padding, 0, t1.Height - 1);
        int cropW = Math.Clamp((maxX + padding) - cropX, 16, t1.Width - cropX);
        int cropH = Math.Clamp((maxY + padding) - cropY, 16, t1.Height - cropY);

        byte[] rawT1 = RenderToBgraBytes(t1, cropX, cropY, cropW, cropH, mode);
        byte[] rawT2 = RenderToBgraBytes(t2, cropX, cropY, cropW, cropH, mode);

        // Render continuous spectral difference / CVA magnitude heatmap
        byte[] rawOverlay = new byte[cropW * cropH * 4];

        // Start with a darkened, contrast-enhanced T2 background to provide physical terrain context
        for (int i = 0; i < rawOverlay.Length; i += 4)
        {
            rawOverlay[i + 0] = (byte)(rawT2[i + 0] * 0.42);
            rawOverlay[i + 1] = (byte)(rawT2[i + 1] * 0.42);
            rawOverlay[i + 2] = (byte)(rawT2[i + 2] * 0.42);
            rawOverlay[i + 3] = 255;
        }

        var sharedBands = t1.Bands.Keys.Where(b => t2.Bands.ContainsKey(b)).ToList();
        var ndvi1 = SpectralIndices.ComputeNDVI(t1);
        var ndvi2 = SpectralIndices.ComputeNDVI(t2);
        var ndbi1 = SpectralIndices.ComputeNDBI(t1);
        var ndbi2 = SpectralIndices.ComputeNDBI(t2);
        var ndwi1 = SpectralIndices.ComputeNDWI(t1);
        var ndwi2 = SpectralIndices.ComputeNDWI(t2);
        var bsi1  = SpectralIndices.ComputeBSI(t1);
        var bsi2  = SpectralIndices.ComputeBSI(t2);

        double peakMag = 0.0;
        double sumMag = 0.0;
        int patchPixelCount = 0;

        int relX0 = Math.Clamp(minX - cropX, 0, cropW - 1);
        int relY0 = Math.Clamp(minY - cropY, 0, cropH - 1);
        int relX1 = Math.Clamp(maxX - cropX, 0, cropW - 1);
        int relY1 = Math.Clamp(maxY - cropY, 0, cropH - 1);

        for (int y = 0; y < cropH; y++)
        {
            int sy = Math.Clamp(cropY + y, 0, t1.Height - 1);
            for (int x = 0; x < cropW; x++)
            {
                int sx = Math.Clamp(cropX + x, 0, t1.Width - 1);
                int idx = (y * cropW + x) * 4;

                double cvaDist = 0.0;
                foreach (var b in sharedBands)
                {
                    double diff = t2.Bands[b][sy, sx] - t1.Bands[b][sy, sx];
                    cvaDist += diff * diff;
                }
                cvaDist = Math.Sqrt(cvaDist);

                if (x >= relX0 && x <= relX1 && y >= relY0 && y <= relY1)
                {
                    patchPixelCount++;
                    sumMag += cvaDist;
                    if (cvaDist > peakMag) peakMag = cvaDist;
                }

                // Choose pixel delta based on selected heatmap visualization mode
                float deltaVal;
                byte hmR, hmG, hmB;
                float blendAlpha = 0.0f;

                switch (heatmapType.ToUpperInvariant())
                {
                    case "NDBI":
                        deltaVal = ndbi2[sy, sx] - ndbi1[sy, sx];
                        if (deltaVal > 0.06f)
                        {
                            float norm = Math.Clamp((deltaVal - 0.06f) / 0.40f, 0f, 1f);
                            (hmR, hmG, hmB) = ((byte)(240 + norm * 15), (byte)(110 + norm * 120), (byte)(20));
                            blendAlpha = Math.Clamp(0.40f + norm * 0.50f, 0.4f, 0.90f);
                        }
                        else { hmR = hmG = hmB = 0; }
                        break;

                    case "NDVI":
                        deltaVal = ndvi2[sy, sx] - ndvi1[sy, sx];
                        if (deltaVal < -0.08f)
                        {
                            // Vegetation destruction / deforestation: crimson to fire-amber
                            float norm = Math.Clamp((-deltaVal - 0.08f) / 0.40f, 0f, 1f);
                            (hmR, hmG, hmB) = ((byte)(235 + norm * 20), (byte)(50 + norm * 100), (byte)30);
                            blendAlpha = Math.Clamp(0.40f + norm * 0.50f, 0.4f, 0.90f);
                        }
                        else if (deltaVal > 0.10f)
                        {
                            // Re-greening / vegetation growth: emerald
                            float norm = Math.Clamp((deltaVal - 0.10f) / 0.40f, 0f, 1f);
                            (hmR, hmG, hmB) = (30, (byte)(180 + norm * 70), 50);
                            blendAlpha = Math.Clamp(0.40f + norm * 0.45f, 0.4f, 0.85f);
                        }
                        else { hmR = hmG = hmB = 0; }
                        break;

                    case "NDWI":
                        deltaVal = ndwi2[sy, sx] - ndwi1[sy, sx];
                        if (deltaVal > 0.08f)
                        {
                            // Water expansion: electric cyan/blue
                            float norm = Math.Clamp((deltaVal - 0.08f) / 0.40f, 0f, 1f);
                            (hmR, hmG, hmB) = (10, (byte)(180 + norm * 60), (byte)(235 + norm * 20));
                            blendAlpha = Math.Clamp(0.45f + norm * 0.45f, 0.45f, 0.90f);
                        }
                        else if (deltaVal < -0.08f)
                        {
                            // Water drying: warm earthy tan
                            float norm = Math.Clamp((-deltaVal - 0.08f) / 0.40f, 0f, 1f);
                            (hmR, hmG, hmB) = ((byte)(190 + norm * 40), (byte)(130 + norm * 40), 50);
                            blendAlpha = Math.Clamp(0.40f + norm * 0.40f, 0.4f, 0.80f);
                        }
                        else { hmR = hmG = hmB = 0; }
                        break;

                    case "BSI":
                        deltaVal = bsi2[sy, sx] - bsi1[sy, sx];
                        if (deltaVal > 0.08f)
                        {
                            // Bare soil / excavation disturbance: golden ochre to rust
                            float norm = Math.Clamp((deltaVal - 0.08f) / 0.40f, 0f, 1f);
                            (hmR, hmG, hmB) = ((byte)(220 + norm * 35), (byte)(130 + norm * 50), 20);
                            blendAlpha = Math.Clamp(0.40f + norm * 0.50f, 0.4f, 0.90f);
                        }
                        else { hmR = hmG = hmB = 0; }
                        break;

                    case "CVA":
                    default:
                        // Multi-band Euclidean Change Vector Analysis magnitude with Turbo colormap
                        if (cvaDist > 0.06)
                        {
                            float norm = Math.Clamp((float)((cvaDist - 0.06) / 0.45), 0f, 1f);
                            (hmR, hmG, hmB) = TurboColormap(norm);
                            blendAlpha = Math.Clamp(0.35f + norm * 0.55f, 0.35f, 0.90f);
                        }
                        else { hmR = hmG = hmB = 0; }
                        break;
                }

                if (blendAlpha > 0.01f)
                {
                    rawOverlay[idx + 0] = (byte)(rawOverlay[idx + 0] * (1f - blendAlpha) + hmB * blendAlpha);
                    rawOverlay[idx + 1] = (byte)(rawOverlay[idx + 1] * (1f - blendAlpha) + hmG * blendAlpha);
                    rawOverlay[idx + 2] = (byte)(rawOverlay[idx + 2] * (1f - blendAlpha) + hmR * blendAlpha);
                }
            }
        }

        // Draw delicate tactical perimeter around the candidate patch (subtle 1px dashed frame)
        for (int x = relX0; x <= relX1; x++)
        {
            if ((x % 3) != 0)
            {
                SetPixel(rawOverlay, cropW, cropH, x, relY0, 0, 210, 255, 180);
                SetPixel(rawOverlay, cropW, cropH, x, relY1, 0, 210, 255, 180);
            }
        }
        for (int y = relY0; y <= relY1; y++)
        {
            if ((y % 3) != 0)
            {
                SetPixel(rawOverlay, cropW, cropH, relX0, y, 0, 210, 255, 180);
                SetPixel(rawOverlay, cropW, cropH, relX1, y, 0, 210, 255, 180);
            }
        }

        // Draw crisp tactical corner brackets (5px long in vibrant cyan)
        DrawCornerBrackets(rawOverlay, cropW, cropH, relX0, relY0, relX1, relY1, 5, 0, 210, 255);

        // Center tactical reticle with a 4px central gap so the center pixel remains visible
        int midX = (relX0 + relX1) / 2;
        int midY = (relY0 + relY1) / 2;
        for (int i = 3; i <= 6; i++)
        {
            SetPixel(rawOverlay, cropW, cropH, midX - i, midY, 255, 255, 255);
            SetPixel(rawOverlay, cropW, cropH, midX + i, midY, 255, 255, 255);
            SetPixel(rawOverlay, cropW, cropH, midX, midY - i, 255, 255, 255);
            SetPixel(rawOverlay, cropW, cropH, midX, midY + i, 255, 255, 255);
        }

        // Upsample to high resolution for crisp, pixel-perfect rendering
        byte[] upT1 = UpsampleBgra(rawT1, cropW, cropH, scale);
        byte[] upT2 = UpsampleBgra(rawT2, cropW, cropH, scale);
        byte[] upOverlay = UpsampleBgra(rawOverlay, cropW, cropH, scale);

        double meanMag = patchPixelCount > 0 ? sumMag / patchPixelCount : 0.0;

        return new FocusedChangeInspection(
            CreateBmpStream(upT1, cropW * scale, cropH * scale),
            CreateBmpStream(upT2, cropW * scale, cropH * scale),
            CreateBmpStream(upOverlay, cropW * scale, cropH * scale),
            cropX, cropY, cropW, cropH,
            peakMag, meanMag
        );
    }

    public record FocusedIndexHeatmaps(
        MemoryStream NdviStream,
        MemoryStream NdbiStream,
        MemoryStream NdwiStream,
        MemoryStream BsiStream,
        int CropX, int CropY, int CropWidth, int CropHeight
    );

    /// <summary>
    /// Generates per-candidate calibrated spectral index difference heatmaps (NDVI, NDBI, NDWI, BSI).
    /// Each heatmap uses standard remote-sensing colour ramps with clear physical semantics:
    /// - ΔNDVI: Vegetation loss/clearing in Hot Amber/Red, growth in Emerald
    /// - ΔNDBI: Built-up concrete in Fiery Flame-Orange
    /// - ΔNDWI: Water expansion in Electric Cyan/Blue, drying in Tan
    /// - ΔBSI: Soil disturbance in Golden Ochre / Terracotta
    /// </summary>
    public static FocusedIndexHeatmaps RenderFocusedIndexHeatmaps(
        SatelliteTile t1,
        SatelliteTile t2,
        ChangeRecord change,
        int padding = 20,
        int scale = 2)
    {
        var (px1, py1) = t1.Transform.GeoToPixel(new GeoCoordinate(change.Bounds.MaxLat, change.Bounds.MinLon));
        var (px2, py2) = t1.Transform.GeoToPixel(new GeoCoordinate(change.Bounds.MinLat, change.Bounds.MaxLon));

        int minX = (int)Math.Min(px1, px2);
        int minY = (int)Math.Min(py1, py2);
        int maxX = (int)Math.Max(px1, px2);
        int maxY = (int)Math.Max(py1, py2);

        int cropX = Math.Clamp(minX - padding, 0, t1.Width - 1);
        int cropY = Math.Clamp(minY - padding, 0, t1.Height - 1);
        int cropW = Math.Clamp((maxX + padding) - cropX, 16, t1.Width - cropX);
        int cropH = Math.Clamp((maxY + padding) - cropY, 16, t1.Height - cropY);

        var ndvi1 = SpectralIndices.ComputeNDVI(t1);
        var ndvi2 = SpectralIndices.ComputeNDVI(t2);
        var ndbi1 = SpectralIndices.ComputeNDBI(t1);
        var ndbi2 = SpectralIndices.ComputeNDBI(t2);
        var ndwi1 = SpectralIndices.ComputeNDWI(t1);
        var ndwi2 = SpectralIndices.ComputeNDWI(t2);
        var bsi1  = SpectralIndices.ComputeBSI(t1);
        var bsi2  = SpectralIndices.ComputeBSI(t2);

        byte[] rawNdvi = new byte[cropW * cropH * 4];
        byte[] rawNdbi = new byte[cropW * cropH * 4];
        byte[] rawNdwi = new byte[cropW * cropH * 4];
        byte[] rawBsi  = new byte[cropW * cropH * 4];

        for (int y = 0; y < cropH; y++)
        {
            int sy = Math.Clamp(cropY + y, 0, t1.Height - 1);
            for (int x = 0; x < cropW; x++)
            {
                int sx = Math.Clamp(cropX + x, 0, t1.Width - 1);
                int idx = (y * cropW + x) * 4;

                // 1. ΔNDVI: Vegetation loss/gain
                float dNdvi = ndvi2[sy, sx] - ndvi1[sy, sx];
                if (dNdvi < -0.08f)
                {
                    float norm = Math.Clamp((-dNdvi - 0.08f) / 0.40f, 0f, 1f);
                    rawNdvi[idx + 0] = (byte)20;
                    rawNdvi[idx + 1] = (byte)(50 + norm * 80);
                    rawNdvi[idx + 2] = (byte)(220 + norm * 35); // Crimson to Amber
                    rawNdvi[idx + 3] = 255;
                }
                else if (dNdvi > 0.08f)
                {
                    float norm = Math.Clamp((dNdvi - 0.08f) / 0.40f, 0f, 1f);
                    rawNdvi[idx + 0] = (byte)40;
                    rawNdvi[idx + 1] = (byte)(160 + norm * 85); // Emerald Green
                    rawNdvi[idx + 2] = (byte)30;
                    rawNdvi[idx + 3] = 255;
                }
                else
                {
                    rawNdvi[idx + 0] = 30; rawNdvi[idx + 1] = 24; rawNdvi[idx + 2] = 18; rawNdvi[idx + 3] = 255; // Neutral dark
                }

                // 2. ΔNDBI: Built-up concrete
                float dNdbi = ndbi2[sy, sx] - ndbi1[sy, sx];
                if (dNdbi > 0.06f)
                {
                    float norm = Math.Clamp((dNdbi - 0.06f) / 0.40f, 0f, 1f);
                    rawNdbi[idx + 0] = (byte)15;
                    rawNdbi[idx + 1] = (byte)(100 + norm * 130); // Golden Yellow to Flame
                    rawNdbi[idx + 2] = (byte)(235 + norm * 20);
                    rawNdbi[idx + 3] = 255;
                }
                else
                {
                    rawNdbi[idx + 0] = 30; rawNdbi[idx + 1] = 24; rawNdbi[idx + 2] = 18; rawNdbi[idx + 3] = 255;
                }

                // 3. ΔNDWI: Water extent
                float dNdwi = ndwi2[sy, sx] - ndwi1[sy, sx];
                if (dNdwi > 0.06f)
                {
                    float norm = Math.Clamp((dNdwi - 0.06f) / 0.40f, 0f, 1f);
                    rawNdwi[idx + 0] = (byte)(225 + norm * 30); // Vibrant Cyan-Blue
                    rawNdwi[idx + 1] = (byte)(160 + norm * 60);
                    rawNdwi[idx + 2] = (byte)10;
                    rawNdwi[idx + 3] = 255;
                }
                else if (dNdwi < -0.06f)
                {
                    float norm = Math.Clamp((-dNdwi - 0.06f) / 0.40f, 0f, 1f);
                    rawNdwi[idx + 0] = 40;
                    rawNdwi[idx + 1] = (byte)(110 + norm * 40);
                    rawNdwi[idx + 2] = (byte)(170 + norm * 50); // Drying earth
                    rawNdwi[idx + 3] = 255;
                }
                else
                {
                    rawNdwi[idx + 0] = 30; rawNdwi[idx + 1] = 24; rawNdwi[idx + 2] = 18; rawNdwi[idx + 3] = 255;
                }

                // 4. ΔBSI: Bare soil index
                float dBsi = bsi2[sy, sx] - bsi1[sy, sx];
                if (dBsi > 0.06f)
                {
                    float norm = Math.Clamp((dBsi - 0.06f) / 0.40f, 0f, 1f);
                    rawBsi[idx + 0] = (byte)20;
                    rawBsi[idx + 1] = (byte)(125 + norm * 50); // Golden Ochre / Rust
                    rawBsi[idx + 2] = (byte)(215 + norm * 40);
                    rawBsi[idx + 3] = 255;
                }
                else
                {
                    rawBsi[idx + 0] = 30; rawBsi[idx + 1] = 24; rawBsi[idx + 2] = 18; rawBsi[idx + 3] = 255;
                }
            }
        }

        // Draw tactical corner brackets around candidate bounds
        int relX0 = Math.Clamp(minX - cropX, 0, cropW - 1);
        int relY0 = Math.Clamp(minY - cropY, 0, cropH - 1);
        int relX1 = Math.Clamp(maxX - cropX, 0, cropW - 1);
        int relY1 = Math.Clamp(maxY - cropY, 0, cropH - 1);

        DrawCornerBrackets(rawNdvi, cropW, cropH, relX0, relY0, relX1, relY1, 4, 255, 255, 255);
        DrawCornerBrackets(rawNdbi, cropW, cropH, relX0, relY0, relX1, relY1, 4, 255, 255, 255);
        DrawCornerBrackets(rawNdwi, cropW, cropH, relX0, relY0, relX1, relY1, 4, 255, 255, 255);
        DrawCornerBrackets(rawBsi,  cropW, cropH, relX0, relY0, relX1, relY1, 4, 255, 255, 255);

        return new FocusedIndexHeatmaps(
            CreateBmpStream(UpsampleBgra(rawNdvi, cropW, cropH, scale), cropW * scale, cropH * scale),
            CreateBmpStream(UpsampleBgra(rawNdbi, cropW, cropH, scale), cropW * scale, cropH * scale),
            CreateBmpStream(UpsampleBgra(rawNdwi, cropW, cropH, scale), cropW * scale, cropH * scale),
            CreateBmpStream(UpsampleBgra(rawBsi,  cropW, cropH, scale), cropW * scale, cropH * scale),
            cropX, cropY, cropW, cropH
        );
    }

    /// <summary>
    /// Generates a crisp, scientific Multi-Spectral Signature Reflectance Chart (280x140 BMP)
    /// comparing T1 Baseline vs T2 Target reflectance across Blue (B2), Green (B3), Red (B4), NIR (B8), SWIR1 (B11).
    /// Transparently visualizes the physical basis of the AI verdict for analyst verification.
    /// </summary>
    public static MemoryStream RenderSpectralProfileChart(
        SatelliteTile t1,
        SatelliteTile t2,
        ChangeRecord change,
        int chartW = 280,
        int chartH = 135)
    {
        byte[] bgra = new byte[chartW * chartH * 4];

        // Dark card background (#0D111A)
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i + 0] = 0x1A; bgra[i + 1] = 0x11; bgra[i + 2] = 0x0D; bgra[i + 3] = 255;
        }

        // Get pixel bounds of change in tile
        var (px1, py1) = t1.Transform.GeoToPixel(new GeoCoordinate(change.Bounds.MaxLat, change.Bounds.MinLon));
        var (px2, py2) = t1.Transform.GeoToPixel(new GeoCoordinate(change.Bounds.MinLat, change.Bounds.MaxLon));

        int x0 = Math.Clamp((int)Math.Min(px1, px2), 0, t1.Width - 1);
        int y0 = Math.Clamp((int)Math.Min(py1, py2), 0, t1.Height - 1);
        int x1 = Math.Clamp((int)Math.Max(px1, px2), 0, t1.Width - 1);
        int y1 = Math.Clamp((int)Math.Max(py1, py2), 0, t1.Height - 1);

        var bands = new[] { SpectralBand.Blue, SpectralBand.Green, SpectralBand.Red, SpectralBand.NIR, SpectralBand.SWIR1 };
        float[] meanT1 = new float[bands.Length];
        float[] meanT2 = new float[bands.Length];

        int pixelCount = Math.Max(1, (x1 - x0 + 1) * (y1 - y0 + 1));

        for (int b = 0; b < bands.Length; b++)
        {
            var b1 = t1.GetBandOrFallback(bands[b], SpectralBand.Red);
            var b2 = t2.GetBandOrFallback(bands[b], SpectralBand.Red);

            double sum1 = 0, sum2 = 0;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    sum1 += b1[y, x];
                    sum2 += b2[y, x];
                }
            }
            meanT1[b] = (float)(sum1 / pixelCount);
            meanT2[b] = (float)(sum2 / pixelCount);
        }

        // Plot Geometry
        int plotLeft = 40;
        int plotRight = chartW - 20;
        int plotTop = 22;
        int plotBottom = chartH - 25;
        int plotW = plotRight - plotLeft;
        int plotH = plotBottom - plotTop;

        // Grid lines at reflectance 0.0, 0.2, 0.4, 0.6
        float maxReflectance = 0.70f;
        for (float refVal = 0.0f; refVal <= 0.60f; refVal += 0.20f)
        {
            int yPos = plotBottom - (int)((refVal / maxReflectance) * plotH);
            for (int x = plotLeft; x <= plotRight; x++)
            {
                SetPixel(bgra, chartW, chartH, x, yPos, 0x33, 0x41, 0x55, 100);
            }
        }

        // X-positions for each of the 5 bands
        int[] bandX = new int[bands.Length];
        for (int i = 0; i < bands.Length; i++)
        {
            bandX[i] = plotLeft + (int)((i + 0.5f) / bands.Length * plotW);
            // Tick line
            for (int y = plotTop; y <= plotBottom; y++)
            {
                SetPixel(bgra, chartW, chartH, bandX[i], y, 0x1E, 0x29, 0x3B, 80);
            }
        }

        // Compute Y positions
        int[] yPosT1 = new int[bands.Length];
        int[] yPosT2 = new int[bands.Length];
        for (int i = 0; i < bands.Length; i++)
        {
            yPosT1[i] = Math.Clamp(plotBottom - (int)((Math.Clamp(meanT1[i], 0f, maxReflectance) / maxReflectance) * plotH), plotTop, plotBottom);
            yPosT2[i] = Math.Clamp(plotBottom - (int)((Math.Clamp(meanT2[i], 0f, maxReflectance) / maxReflectance) * plotH), plotTop, plotBottom);
        }

        // Draw translucent shaded polygon between T1 and T2 curves
        for (int i = 0; i < bands.Length - 1; i++)
        {
            int xA = bandX[i];
            int xB = bandX[i + 1];
            for (int x = xA; x <= xB; x++)
            {
                float t = (float)(x - xA) / Math.Max(1, xB - xA);
                int yA = (int)(yPosT1[i] * (1f - t) + yPosT1[i + 1] * t);
                int yB = (int)(yPosT2[i] * (1f - t) + yPosT2[i + 1] * t);

                int minY = Math.Min(yA, yB);
                int maxY = Math.Max(yA, yB);
                for (int y = minY; y <= maxY; y++)
                {
                    // Delta shading (translucent amber)
                    int idx = (y * chartW + x) * 4;
                    bgra[idx + 0] = (byte)(bgra[idx + 0] * 0.70 + 20 * 0.30);
                    bgra[idx + 1] = (byte)(bgra[idx + 1] * 0.70 + 160 * 0.30);
                    bgra[idx + 2] = (byte)(bgra[idx + 2] * 0.70 + 245 * 0.30);
                }
            }
        }

        // Draw connecting lines for T1 (Cyan #00D2FF) and T2 (Coral Red #F87171)
        for (int i = 0; i < bands.Length - 1; i++)
        {
            DrawLine(bgra, chartW, chartH, bandX[i], yPosT1[i], bandX[i + 1], yPosT1[i + 1], 0, 210, 255);
            DrawLine(bgra, chartW, chartH, bandX[i], yPosT2[i], bandX[i + 1], yPosT2[i + 1], 248, 113, 113);
        }

        // Draw data points
        for (int i = 0; i < bands.Length; i++)
        {
            DrawCircle(bgra, chartW, chartH, bandX[i], yPosT1[i], 3, 0, 210, 255, filled: true);
            DrawRect(bgra, chartW, chartH, bandX[i] - 2, yPosT2[i] - 2, bandX[i] + 2, yPosT2[i] + 2, 248, 113, 113, filled: true);
        }

        // Header Legend indicators
        // Cyan circle for T1 Baseline
        DrawCircle(bgra, chartW, chartH, plotLeft + 10, 10, 3, 0, 210, 255, filled: true);
        // Coral square for T2 Target
        DrawRect(bgra, chartW, chartH, plotLeft + 95, 8, plotLeft + 101, 14, 248, 113, 113, filled: true);

        return CreateBmpStream(bgra, chartW, chartH);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Graphics & Colormap Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    public static (byte R, byte G, byte B) TurboColormap(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (t < 0.25f)
        {
            float f = t / 0.25f;
            return ((byte)(30 + f * 10), (byte)(40 + f * 140), (byte)(130 + f * 95));
        }
        else if (t < 0.50f)
        {
            float f = (t - 0.25f) / 0.25f;
            return ((byte)(40 + f * 50), (byte)(180 + f * 45), (byte)(225 - f * 160));
        }
        else if (t < 0.75f)
        {
            float f = (t - 0.50f) / 0.25f;
            return ((byte)(90 + f * 155), (byte)(225 - f * 65), (byte)(65 - f * 45));
        }
        else
        {
            float f = (t - 0.75f) / 0.25f;
            return ((byte)(245 - f * 20), (byte)(160 - f * 130), (byte)(20 + f * 20));
        }
    }

    private static byte[] UpsampleBgra(byte[] src, int srcW, int srcH, int scale)
    {
        if (scale <= 1) return src;
        int dstW = srcW * scale;
        int dstH = srcH * scale;
        byte[] dst = new byte[dstW * dstH * 4];

        for (int y = 0; y < dstH; y++)
        {
            int sy = y / scale;
            for (int x = 0; x < dstW; x++)
            {
                int sx = x / scale;
                int srcIdx = (sy * srcW + sx) * 4;
                int dstIdx = (y * dstW + x) * 4;
                dst[dstIdx + 0] = src[srcIdx + 0];
                dst[dstIdx + 1] = src[srcIdx + 1];
                dst[dstIdx + 2] = src[srcIdx + 2];
                dst[dstIdx + 3] = src[srcIdx + 3];
            }
        }
        return dst;
    }

    private static void DrawCornerBrackets(byte[] bgra, int w, int h, int x0, int y0, int x1, int y1, int len, byte r, byte g, byte b, byte a = 255)
    {
        x0 = Math.Clamp(x0, 0, w - 1);
        y0 = Math.Clamp(y0, 0, h - 1);
        x1 = Math.Clamp(x1, 0, w - 1);
        y1 = Math.Clamp(y1, 0, h - 1);

        // Top-left
        for (int x = x0; x <= Math.Min(x1, x0 + len); x++) SetPixel(bgra, w, h, x, y0, r, g, b, a);
        for (int y = y0; y <= Math.Min(y1, y0 + len); y++) SetPixel(bgra, w, h, x0, y, r, g, b, a);

        // Top-right
        for (int x = Math.Max(x0, x1 - len); x <= x1; x++) SetPixel(bgra, w, h, x, y0, r, g, b, a);
        for (int y = y0; y <= Math.Min(y1, y0 + len); y++) SetPixel(bgra, w, h, x1, y, r, g, b, a);

        // Bottom-left
        for (int x = x0; x <= Math.Min(x1, x0 + len); x++) SetPixel(bgra, w, h, x, y1, r, g, b, a);
        for (int y = Math.Max(y0, y1 - len); y <= y1; y++) SetPixel(bgra, w, h, x0, y, r, g, b, a);

        // Bottom-right
        for (int x = Math.Max(x0, x1 - len); x <= x1; x++) SetPixel(bgra, w, h, x, y1, r, g, b, a);
        for (int y = Math.Max(y0, y1 - len); y <= y1; y++) SetPixel(bgra, w, h, x1, y, r, g, b, a);
    }

    private static void SetPixel(byte[] bgra, int w, int h, int x, int y, byte r, byte g, byte b, byte a = 255)
    {
        if (x < 0 || x >= w || y < 0 || y >= h) return;
        int idx = (y * w + x) * 4;
        bgra[idx + 0] = b;
        bgra[idx + 1] = g;
        bgra[idx + 2] = r;
        bgra[idx + 3] = a;
    }

    private static void DrawLine(byte[] bgra, int w, int h, int x0, int y0, int x1, int y1, byte r, byte g, byte b, byte a = 255)
    {
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        while (true)
        {
            SetPixel(bgra, w, h, x0, y0, r, g, b, a);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    private static void DrawCircle(byte[] bgra, int w, int h, int cx, int cy, int radius, byte r, byte g, byte b, byte a = 255, bool filled = false)
    {
        for (int y = cy - radius; y <= cy + radius; y++)
        {
            for (int x = cx - radius; x <= cx + radius; x++)
            {
                int distSq = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                if (filled ? distSq <= radius * radius : Math.Abs(distSq - radius * radius) <= radius)
                {
                    SetPixel(bgra, w, h, x, y, r, g, b, a);
                }
            }
        }
    }

    private static void DrawRect(byte[] bgra, int w, int h, int x0, int y0, int x1, int y1, byte r, byte g, byte b, byte a = 255, bool filled = false)
    {
        int minX = Math.Min(x0, x1);
        int maxX = Math.Max(x0, x1);
        int minY = Math.Min(y0, y1);
        int maxY = Math.Max(y0, y1);

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (filled || x == minX || x == maxX || y == minY || y == maxY)
                {
                    SetPixel(bgra, w, h, x, y, r, g, b, a);
                }
            }
        }
    }
}
