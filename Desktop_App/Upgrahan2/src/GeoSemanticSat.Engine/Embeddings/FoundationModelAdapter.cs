using System;
using System.Collections.Generic;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Processing;
using GeoSemanticSat.Core.VectorIndex;
using VectorIndexStore = GeoSemanticSat.Core.VectorIndex.VectorIndex;

namespace GeoSemanticSat.Engine.Embeddings;

/// <summary>
/// Pretrained Geospatial Foundation Model kinds supported by UpaGraha / GeoSemanticSat.
/// </summary>
public enum FoundationModelKind
{
    NativeBaseline,
    TerraMind,          // ibm-esa-geospatial/TerraMind-1.0-base
    SatMaePP,           // BiliSakura/SATMAE-PP-transformers
    GfmComposition,     // GFM_Composition_Pretraining (SAR + Optical)
    PrithviTemporal     // ibm-nasa-geospatial/Prithvi-EO-2.0-600M-TL
}

/// <summary>
/// C# Offline Adapter for Pretrained Geospatial Foundation Models.
/// Bridges ONNX runtime outputs with the single source of truth 128-dimensional
/// vector space defined in <see cref="SemanticEmbeddingLayout"/>.
/// </summary>
public class FoundationModelAdapter : IDisposable
{
    private readonly FoundationModelKind _modelKind;
    private readonly OnnxModelRunner? _onnxRunner;

    public FoundationModelKind ModelKind => _modelKind;
    public bool IsNeuralActive => _onnxRunner != null && _onnxRunner.IsModelLoaded;

    public FoundationModelAdapter(FoundationModelKind modelKind = FoundationModelKind.NativeBaseline, string? onnxPath = null)
    {
        _modelKind = modelKind;
        if (!string.IsNullOrEmpty(onnxPath) && System.IO.File.Exists(onnxPath))
        {
            _onnxRunner = new OnnxModelRunner(onnxPath);
        }
    }

    /// <summary>
    /// Encode satellite imagery patch using the active foundation model architecture.
    /// Falls back to <see cref="MultiSpectralVisionEncoder.EncodePatch"/> if neural weights are not staged.
    /// </summary>
    public float[] EncodePatch(SatelliteTile tile, int startX, int startY, int patchW, int patchH)
    {
        // 1. If ONNX weights are loaded, run neural inference
        if (IsNeuralActive && _onnxRunner != null)
        {
            float[]? neuralOutput = RunNeuralInference(tile, startX, startY, patchW, patchH);
            if (neuralOutput != null && neuralOutput.Length >= SemanticEmbeddingLayout.Dimension)
            {
                VectorIndexStore.NormalizeInPlace(neuralOutput);
                return neuralOutput;
            }
        }

        // 2. High-Fidelity offline native spectral foundation mapping
        float[] embedding = MultiSpectralVisionEncoder.EncodePatch(tile, startX, startY, patchW, patchH);

        // Apply model-specific compositional weighting
        switch (_modelKind)
        {
            case FoundationModelKind.TerraMind:
                // Boost cross-modal semantic axes
                embedding[SemanticEmbeddingLayout.StructureNearWater] *= 1.1f;
                embedding[SemanticEmbeddingLayout.LinearContinuity] *= 1.05f;
                break;

            case FoundationModelKind.SatMaePP:
                // Grouped multi-spectral balance
                embedding[SemanticEmbeddingLayout.Vegetation] *= 1.05f;
                embedding[SemanticEmbeddingLayout.TextureEnergy] *= 1.1f;
                break;

            case FoundationModelKind.GfmComposition:
                // If SAR is present, emphasize high-contrast microwave scattering
                if (tile.HasBand(SpectralBand.SAR_VV))
                {
                    embedding[SemanticEmbeddingLayout.ManMadeContrast] *= 1.2f;
                    embedding[SemanticEmbeddingLayout.ActivityPeaks] *= 1.15f;
                }
                break;

            case FoundationModelKind.PrithviTemporal:
                // Emphasize onset dynamics
                embedding[SemanticEmbeddingLayout.ClearedGround] *= 1.1f;
                embedding[SemanticEmbeddingLayout.BuiltUp] *= 1.05f;
                break;

            default:
                break;
        }

        VectorIndexStore.NormalizeInPlace(embedding);
        return embedding;
    }

    private float[]? RunNeuralInference(SatelliteTile tile, int startX, int startY, int patchW, int patchH)
    {
        if (_onnxRunner == null) return null;

        var red = tile.GetBandOrFallback(SpectralBand.Red, SpectralBand.Red);
        var green = tile.GetBandOrFallback(SpectralBand.Green, SpectralBand.Red);
        var blue = tile.GetBandOrFallback(SpectralBand.Blue, SpectralBand.Red);
        var nir = tile.GetBandOrFallback(SpectralBand.NIR, SpectralBand.Red);

        // Prepare flat tensor [1, 4, patchH, patchW]
        float[] tensorData = new float[4 * patchH * patchW];
        int idx = 0;
        float[][,] bands = new float[][,] { red, green, blue, nir };

        for (int b = 0; b < 4; b++)
        {
            for (int y = startY; y < startY + patchH; y++)
            {
                for (int x = startX; x < startX + patchW; x++)
                {
                    int clampedY = Math.Clamp(y, 0, tile.Height - 1);
                    int clampedX = Math.Clamp(x, 0, tile.Width - 1);
                    tensorData[idx++] = bands[b][clampedY, clampedX];
                }
            }
        }

        return _onnxRunner.RunInference(tensorData, new int[] { 1, 4, patchH, patchW });
    }

    public void Dispose()
    {
        _onnxRunner?.Dispose();
    }
}
