using System;
using System.Collections.Generic;
using System.Linq;
using GeoSemanticSat.Core.VectorIndex;

namespace GeoSemanticSat.Core.Workflow;

/// <summary>
/// Active Learning and Relevance Feedback Reranker using Rocchio algorithm:
/// Q_new = alpha * Q_orig + (beta / |D_R|) * sum(D_R) - (gamma / |D_NR|) * sum(D_NR)
/// </summary>
public static class RelevanceFeedbackReranker
{
    public const float Alpha = 1.0f; // Weight on original query
    public const float Beta = 0.75f; // Weight on confirmed positives
    public const float Gamma = 0.25f; // Weight on rejected negatives

    public static float[] AdjustQueryVector(
        float[] originalQuery,
        IReadOnlyList<float[]> confirmedEmbeddings,
        IReadOnlyList<float[]> rejectedEmbeddings)
    {
        int dim = originalQuery.Length;
        float[] newQuery = new float[dim];

        // 1. Original query contribution
        for (int i = 0; i < dim; i++)
        {
            newQuery[i] = Alpha * originalQuery[i];
        }

        // 2. Confirmed positive vectors centroid
        if (confirmedEmbeddings.Count > 0)
        {
            float betaNorm = Beta / confirmedEmbeddings.Count;
            foreach (var pos in confirmedEmbeddings)
            {
                for (int i = 0; i < dim; i++)
                {
                    newQuery[i] += betaNorm * pos[i];
                }
            }
        }

        // 3. Rejected negative vectors centroid
        if (rejectedEmbeddings.Count > 0)
        {
            float gammaNorm = Gamma / rejectedEmbeddings.Count;
            foreach (var neg in rejectedEmbeddings)
            {
                for (int i = 0; i < dim; i++)
                {
                    newQuery[i] -= gammaNorm * neg[i];
                }
            }
        }

        // 4. Physical feature preservation (prevent negative variance/energy distortion)
        // Indices 0..15 (reflectance statistics) and 64..79 (texture energy) are non-negative
        for (int i = 0; i < Math.Min(16, dim); i++)
        {
            newQuery[i] = Math.Max(0.0f, newQuery[i]);
        }
        for (int i = 64; i < Math.Min(80, dim); i++)
        {
            newQuery[i] = Math.Max(0.0f, newQuery[i]);
        }

        VectorIndex.VectorIndex.NormalizeInPlace(newQuery);
        return newQuery;
    }
}
