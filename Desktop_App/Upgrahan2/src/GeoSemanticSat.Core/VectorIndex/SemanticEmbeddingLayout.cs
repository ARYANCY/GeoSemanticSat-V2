using System;

namespace GeoSemanticSat.Core.VectorIndex;

/// <summary>
/// Single source of truth for the 128-dimensional embedding layout.
///
/// The vector has two blocks that behave very differently and must not be mixed:
///
///   APPEARANCE BLOCK - written only by MultiSpectralVisionEncoder.
///     0..15   band means and standard deviations (absolute reflectance, unbounded scale)
///     32..36  raw spatial gradient magnitudes
///     80..94  per-quadrant means and layout
///   These describe how a patch *looks*. They carry no cross-modal meaning: a text query
///   cannot state "mean red reflectance = 0.38". Nothing outside the image encoder writes here.
///
///   SEMANTIC AXES - written by BOTH encoders, and the only dimensions that are
///   comparable across modalities. Every axis is a bounded, signed, scale-free quantity.
///
/// Text-to-image similarity is measured on the semantic axes ONLY (see CosineOnSemanticAxes).
/// Comparing full 128-dim vectors made the score depend mostly on the appearance block, which
/// the text side never populates - that is what produced near-noise and negative match scores.
/// Image-to-image similarity still uses the full vector, where every dimension is meaningful.
/// </summary>
public static class SemanticEmbeddingLayout
{
    public const int Dimension = 128;

    // --- Shared semantic axes (bounded, cross-modal) ---
    public const int Vegetation = 16;          // NDVI, or visible-band proxy
    public const int Water = 17;               // NDWI
    public const int BuiltUp = 18;             // NDBI
    public const int OpenWater = 19;           // MNDWI
    public const int BareSoil = 20;            // BSI
    public const int StructureNearWater = 21;  // built-up / water interaction
    public const int ClearedGround = 22;       // vegetation loss over exposed soil
    public const int ManMadeContrast = 23;     // high-contrast man-made structure
    public const int LinearContinuity = 37;    // road / runway / perimeter directional continuity
    public const int TextureEnergy = 64;       // GLCM-style contrast energy
    public const int ActivityPeaks = 65;       // localized high-frequency peak density

    /// <summary>Dimensions both encoders write. Cross-modal comparison uses these only.</summary>
    public static readonly int[] SemanticAxes =
    {
        Vegetation, Water, BuiltUp, OpenWater, BareSoil,
        StructureNearWater, ClearedGround, ManMadeContrast,
        LinearContinuity, TextureEnergy, ActivityPeaks
    };

    /// <summary>
    /// Cosine similarity restricted to the semantic axes, each side renormalized over that
    /// subspace so the result is a true cosine in [-1, 1] and not diluted by dimensions one
    /// side can never populate.
    /// </summary>
    public static double CosineOnSemanticAxes(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;

        foreach (int i in SemanticAxes)
        {
            if (i >= a.Length || i >= b.Length) continue;
            double va = a[i];
            double vb = b[i];
            dot += va * vb;
            normA += va * va;
            normB += vb * vb;
        }

        if (normA < 1e-12 || normB < 1e-12) return 0.0;
        return Math.Clamp(dot / (Math.Sqrt(normA) * Math.Sqrt(normB)), -1.0, 1.0);
    }
}

/// <summary>Which similarity metric a search should use.</summary>
public enum SimilarityMode
{
    /// <summary>Full 128-dim cosine. Correct for image-to-image comparison.</summary>
    FullVector,

    /// <summary>Cosine over the shared semantic axes. Correct for text-to-image comparison.</summary>
    SemanticAxes
}
