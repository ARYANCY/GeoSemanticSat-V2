using System;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Processing;
using VectorIndexStore = GeoSemanticSat.Core.VectorIndex.VectorIndex;

namespace GeoSemanticSat.Engine.Embeddings;

/// <summary>
/// Native Multi-Spectral and Multi-Sensor Vision Feature Encoder.
/// Computes dense 128-dimensional normalized embedding vectors from multi-spectral satellite imagery patches.
/// Integrates spectral indices, texture energy (GLCM), multi-scale spatial gradients, and band relationships.
/// </summary>
public class MultiSpectralVisionEncoder
{
    public const int EmbeddingDimension = 128;

    public static float[] EncodePatch(SatelliteTile tile, int startX, int startY, int patchW, int patchH)
    {
        float[] embedding = new float[EmbeddingDimension];
        int w = tile.Width;
        int h = tile.Height;

        var red = tile.GetBandOrFallback(SpectralBand.Red, SpectralBand.Red);
        var green = tile.GetBandOrFallback(SpectralBand.Green, SpectralBand.Red);
        var blue = tile.GetBandOrFallback(SpectralBand.Blue, SpectralBand.Red);
        var nir = tile.GetBandOrFallback(SpectralBand.NIR, SpectralBand.Red);
        var swir = tile.GetBandOrFallback(SpectralBand.SWIR1, SpectralBand.NIR);
        var sarVv = tile.Bands.ContainsKey(SpectralBand.SAR_VV) ? tile.Bands[SpectralBand.SAR_VV] : null;

        // 1. Spectral Band Statistics (Means & Variances) -> [0..15]
        double meanR = 0, meanG = 0, meanB = 0, meanN = 0, meanS = 0, meanSar = 0;
        int count = 0;

        for (int y = startY; y < startY + patchH && y < h; y++)
        {
            for (int x = startX; x < startX + patchW && x < w; x++)
            {
                meanR += red[y, x];
                meanG += green[y, x];
                meanB += blue[y, x];
                meanN += nir[y, x];
                meanS += swir[y, x];
                if (sarVv != null) meanSar += sarVv[y, x];
                count++;
            }
        }
        if (count == 0) count = 1;

        meanR /= count; meanG /= count; meanB /= count; meanN /= count; meanS /= count; meanSar /= count;

        double varR = 0, varG = 0, varB = 0, varN = 0, varS = 0;
        for (int y = startY; y < startY + patchH && y < h; y++)
        {
            for (int x = startX; x < startX + patchW && x < w; x++)
            {
                varR += (red[y, x] - meanR) * (red[y, x] - meanR);
                varG += (green[y, x] - meanG) * (green[y, x] - meanG);
                varB += (blue[y, x] - meanB) * (blue[y, x] - meanB);
                varN += (nir[y, x] - meanN) * (nir[y, x] - meanN);
                varS += (swir[y, x] - meanS) * (swir[y, x] - meanS);
            }
        }
        varR /= count; varG /= count; varB /= count; varN /= count; varS /= count;

        embedding[0] = (float)meanR;
        embedding[1] = (float)meanG;
        embedding[2] = (float)meanB;
        embedding[3] = (float)meanN;
        embedding[4] = (float)meanS;
        embedding[5] = (float)meanSar;
        embedding[6] = (float)Math.Sqrt(varR);
        embedding[7] = (float)Math.Sqrt(varG);
        embedding[8] = (float)Math.Sqrt(varB);
        embedding[9] = (float)Math.Sqrt(varN);
        embedding[10] = (float)Math.Sqrt(varS);

        // 2. Earth Observation Spectral Indices -> [16..31]
        // NDVI: Vegetation
        float ndvi = (float)((meanN + meanR) > 1e-4 ? (meanN - meanR) / (meanN + meanR) : 0);
        // NDWI: Water
        float ndwi = (float)((meanG + meanN) > 1e-4 ? (meanG - meanN) / (meanG + meanN) : 0);
        // NDBI: Built-up / Concrete / Structures
        float ndbi = (float)((meanS + meanN) > 1e-4 ? (meanS - meanN) / (meanS + meanN) : 0);
        // MNDWI: Open Water
        float mndwi = (float)((meanG + meanS) > 1e-4 ? (meanG - meanS) / (meanG + meanS) : 0);
        // Bare Soil Index
        float bsi = (float)(((meanS + meanR) - (meanN + meanB)) / Math.Max(1e-4, (meanS + meanR) + (meanN + meanB)));

        embedding[16] = ndvi;
        embedding[17] = ndwi;
        embedding[18] = ndbi;
        embedding[19] = mndwi;
        embedding[20] = bsi;

        // Interaction terms (e.g. structures near water: NDBI * NDWI)
        embedding[21] = ndbi * (ndwi + 1.0f); // Structures near water / river proximity
        embedding[22] = (1.0f - ndvi) * bsi;  // Cleared / exposed open soil
        embedding[23] = (float)Math.Sqrt(varR + varG) * (ndbi + 1.0f); // High-contrast man-made objects

        // 3. Spatial Gradients & Directional Structural Features -> [32..63]
        double gradSum = 0;
        double gradH = 0, gradV = 0, gradD1 = 0, gradD2 = 0;

        for (int y = startY + 1; y < startY + patchH - 1 && y < h - 1; y++)
        {
            for (int x = startX + 1; x < startX + patchW - 1 && x < w - 1; x++)
            {
                float dx = red[y, x + 1] - red[y, x - 1];
                float dy = red[y + 1, x] - red[y - 1, x];
                float d1 = red[y + 1, x + 1] - red[y - 1, x - 1];
                float d2 = red[y + 1, x - 1] - red[y - 1, x + 1];

                gradH += Math.Abs(dx);
                gradV += Math.Abs(dy);
                gradD1 += Math.Abs(d1);
                gradD2 += Math.Abs(d2);
                gradSum += Math.Sqrt(dx * dx + dy * dy);
            }
        }
        double innerCount = Math.Max(1, (patchW - 2) * (patchH - 2));
        embedding[32] = (float)(gradSum / innerCount);
        embedding[33] = (float)(gradH / innerCount);
        embedding[34] = (float)(gradV / innerCount);
        embedding[35] = (float)(gradD1 / innerCount);
        embedding[36] = (float)(gradD2 / innerCount);

        // Linear continuity (road / runway / perimeter)
        float maxLinear = (float)Math.Max(gradH, Math.Max(gradV, Math.Max(gradD1, gradD2))) / (float)innerCount;
        embedding[37] = maxLinear;

        // 4. Texture & Contrast Energy (GLCM approximations) -> [64..79]
        double textureEnergy = 0;
        double highFrequencyPeaks = 0;
        for (int y = startY; y < startY + patchH && y < h; y++)
        {
            for (int x = startX; x < startX + patchW && x < w; x++)
            {
                float diff = Math.Abs(red[y, x] - (float)meanR);
                textureEnergy += diff * diff;
                if (diff > 0.30f) highFrequencyPeaks++;
            }
        }
        embedding[64] = (float)(textureEnergy / count);
        embedding[65] = (float)(highFrequencyPeaks / count); // Localized clusters (vehicles, storage containers)

        // 5. Multi-quadrant spatial distribution (spatial layout) -> [80..127]
        int halfW = patchW / 2;
        int halfH = patchH / 2;
        // 4 Quadrants: Q1(TL), Q2(TR), Q3(BL), Q4(BR)
        int[] qX = { startX, startX + halfW, startX, startX + halfW };
        int[] qY = { startY, startY, startY + halfH, startY + halfH };

        for (int q = 0; q < 4; q++)
        {
            double qR = 0, qN = 0;
            int qCount = 0;
            for (int y = qY[q]; y < qY[q] + halfH && y < h; y++)
            {
                for (int x = qX[q]; x < qX[q] + halfW && x < w; x++)
                {
                    qR += red[y, x];
                    qN += nir[y, x];
                    qCount++;
                }
            }
            if (qCount > 0)
            {
                embedding[80 + q * 4] = (float)(qR / qCount);
                embedding[81 + q * 4] = (float)(qN / qCount);
                embedding[82 + q * 4] = (float)((qN - qR) / Math.Max(1e-4, qN + qR));
            }
        }

        VectorIndexStore.NormalizeInPlace(embedding);
        return embedding;
    }
}
