using System;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Processing;
using GeoSemanticSat.Core.VectorIndex;
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

        // 2. Shared semantic axes -> see SemanticEmbeddingLayout.
        // Every value here is bounded and scale-free so it is directly comparable with the
        // text encoder. When NIR/SWIR are absent these fall back to documented visible-band
        // proxies instead of silently collapsing to zero for the whole scene.
        bool hasNir = tile.HasBand(SpectralBand.NIR);
        bool hasSwir = tile.HasBand(SpectralBand.SWIR1);

        float ndvi = hasNir
            ? SpectralIndices.Nd((float)meanN, (float)meanR)
            : SpectralIndices.Nd(2f * (float)meanG, (float)(meanR + meanB));      // Excess Green
        float ndwi = hasNir
            ? SpectralIndices.Nd((float)meanG, (float)meanN)
            : SpectralIndices.Nd((float)meanB, (float)meanR);                     // blue dominance
        float ndbi = (hasSwir && hasNir)
            ? SpectralIndices.Nd((float)meanS, (float)meanN)
            : SpectralIndices.Nd(2f * (float)meanR, (float)(meanG + meanB));      // Excess Red
        float mndwi = hasSwir
            ? SpectralIndices.Nd((float)meanG, (float)meanS)
            : ndwi;
        float bsi = (hasSwir && hasNir)
            ? SpectralIndices.Nd((float)(meanS + meanR), (float)(meanN + meanB))
            : SpectralIndices.Nd((float)(meanR + meanG), 2f * (float)meanB);      // low blue = soil

        embedding[SemanticEmbeddingLayout.Vegetation] = ndvi;
        embedding[SemanticEmbeddingLayout.Water] = ndwi;
        embedding[SemanticEmbeddingLayout.BuiltUp] = ndbi;
        embedding[SemanticEmbeddingLayout.OpenWater] = mndwi;
        embedding[SemanticEmbeddingLayout.BareSoil] = bsi;

        // Interaction axes, rescaled to [-1, 1] so they cannot dominate the semantic subspace.
        embedding[SemanticEmbeddingLayout.StructureNearWater] = Math.Clamp(ndbi * (ndwi + 1.0f) * 0.5f, -1f, 1f);
        embedding[SemanticEmbeddingLayout.ClearedGround] = Math.Clamp((1.0f - ndvi) * bsi * 0.5f, -1f, 1f);
        embedding[SemanticEmbeddingLayout.ManMadeContrast] =
            Math.Clamp((float)Math.Sqrt(varR + varG) * (ndbi + 1.0f), -1f, 1f);

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
        // Bounded so it stays comparable with the other semantic axes: raw gradient sums are
        // unbounded and would swamp the subspace.
        float maxLinear = (float)Math.Max(gradH, Math.Max(gradV, Math.Max(gradD1, gradD2))) / (float)innerCount;
        embedding[SemanticEmbeddingLayout.LinearContinuity] = Math.Clamp(maxLinear, 0f, 1f);

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
        embedding[SemanticEmbeddingLayout.TextureEnergy] = Math.Clamp((float)(textureEnergy / count), 0f, 1f);
        // Localized clusters (vehicles, storage containers); already a fraction in [0, 1].
        embedding[SemanticEmbeddingLayout.ActivityPeaks] = (float)(highFrequencyPeaks / count);

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
