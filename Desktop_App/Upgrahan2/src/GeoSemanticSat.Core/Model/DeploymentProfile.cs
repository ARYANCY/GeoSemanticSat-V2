using GeoSemanticSat.Core.VectorIndex;

namespace GeoSemanticSat.Core.Model;

/// <summary>
/// Deployment tier selected at process start. V1 is the native CPU encoder + GSSV index.
/// V2 enables optional locally staged ONNX foundation weights and the enterprise daemon/API.
/// Neither tier invents capabilities the process cannot actually load.
/// </summary>
public enum DeploymentTier
{
    V1Lightweight,
    V2FullIntelligence
}

public static class DeploymentProfile
{
    public const string NativeEncoderName = "MultiSpectralVisionEncoder+TextQueryEncoder";
    public const string NativeEmbeddingLayout = "SemanticEmbeddingLayout-128-v1";
    public const int EmbeddingDimension = SemanticEmbeddingLayout.Dimension;

    public static DeploymentTier Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DeploymentTier.V1Lightweight;

        return value.Trim().ToUpperInvariant() switch
        {
            "V2" or "FULL" or "V2FULLINTELLIGENCE" => DeploymentTier.V2FullIntelligence,
            _ => DeploymentTier.V1Lightweight
        };
    }

    public static string EncoderDescription(DeploymentTier tier, bool onnxLoaded)
        => tier == DeploymentTier.V2FullIntelligence && onnxLoaded
            ? "Local ONNX foundation encoder projected into " + NativeEmbeddingLayout
            : NativeEncoderName + " (" + NativeEmbeddingLayout + ")";
}
