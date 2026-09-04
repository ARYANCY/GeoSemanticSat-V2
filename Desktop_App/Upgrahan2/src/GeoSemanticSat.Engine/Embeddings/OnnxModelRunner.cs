using System;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GeoSemanticSat.Engine.Embeddings;

/// <summary>
/// ONNX Runtime Model Runner for staging and running pre-trained remote sensing vision-language models
/// (e.g. RemoteCLIP, SigLIP, MobileCLIP) completely offline on-premises without network access.
/// </summary>
public class OnnxModelRunner : IDisposable
{
    private InferenceSession? _session;
    private readonly string? _modelPath;

    public bool IsModelLoaded => _session != null;

    public OnnxModelRunner(string? modelPath = null)
    {
        _modelPath = modelPath;
        if (!string.IsNullOrEmpty(modelPath) && File.Exists(modelPath))
        {
            try
            {
                var options = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
                };
                _session = new InferenceSession(modelPath, options);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OnnxModelRunner] Could not initialize ONNX session: {ex.Message}. Falling back to native spectral encoder.");
            }
        }
    }

    public float[]? RunInference(float[] inputTensor, int[] dimensions)
    {
        if (_session == null) return null;

        try
        {
            var tensor = new DenseTensor<float>(inputTensor, dimensions);
            var inputs = new NamedOnnxValue[] { NamedOnnxValue.CreateFromTensor("input", tensor) };
            using var results = _session.Run(inputs);
            foreach (var r in results)
            {
                if (r.Value is DenseTensor<float> outTensor)
                {
                    return outTensor.ToArray();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OnnxModelRunner] Inference error: {ex.Message}");
        }

        return null;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
