using System;
using System.Collections.Generic;
using System.Linq;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.VectorIndex;
using GeoSemanticSat.Core.Workflow;
using GeoSemanticSat.Engine.Embeddings;

namespace GeoSemanticSat.Engine.Retrieval;

/// <summary>
/// Semantic and Multimodal Retrieval Engine.
/// Supports natural language text queries, image-to-image patch similarity,
/// spatiotemporal filtering, and active learning relevance feedback.
/// </summary>
public class SemanticSearchEngine
{
    private readonly VectorIndex _index;
    private readonly OnnxModelRunner _onnxRunner;

    public VectorIndex Index => _index;

    public SemanticSearchEngine(VectorIndex index, string? onnxModelPath = null)
    {
        _index = index;
        _onnxRunner = new OnnxModelRunner(onnxModelPath);
    }

    /// <summary>
    /// Natural Language Free-Text Search over Satellite Tiles.
    /// </summary>
    public List<SearchResult> SearchByText(string textQuery, int topK = 10, SearchFilter? filter = null)
    {
        float[] queryEmbedding = TextQueryEncoder.Encode(textQuery);
        return _index.Search(queryEmbedding, topK, filter);
    }

    /// <summary>
    /// Spatiotemporal Search centered at Latitude/Longitude within a specified radius (km).
    /// </summary>
    public List<SearchResult> SearchByLocation(
        GeoCoordinate center,
        double radiusKm,
        string? textQuery = null,
        int topK = 10,
        DateTime? startDate = null,
        DateTime? endDate = null,
        SensorPlatform? platform = null)
    {
        float[] queryEmbedding = !string.IsNullOrWhiteSpace(textQuery)
            ? TextQueryEncoder.Encode(textQuery)
            : new float[_index.VectorDimension];

        // If no text query provided, uniform vector to allow pure spatial proximity ranking
        if (string.IsNullOrWhiteSpace(textQuery))
        {
            Array.Fill(queryEmbedding, 1.0f / (float)Math.Sqrt(_index.VectorDimension));
        }

        var filter = new SearchFilter(
            CenterCoordinate: center,
            RadiusKm: radiusKm,
            StartDate: startDate,
            EndDate: endDate,
            Platform: platform
        );

        return _index.Search(queryEmbedding, topK, filter);
    }

    /// <summary>
    /// Image-to-Image / Tile-to-Tile Search using a reference patch.
    /// </summary>
    public List<SearchResult> SearchByImagePatch(TilePatch queryPatch, int topK = 10, SearchFilter? filter = null)
    {
        return _index.Search(queryPatch.EmbeddingVector, topK, filter);
    }

    /// <summary>
    /// Re-ranks results using analyst feedback (confirmed relevant vs. rejected irrelevant).
    /// </summary>
    public List<SearchResult> SearchWithFeedback(
        string textQuery,
        IReadOnlyList<TilePatch> confirmedPatches,
        IReadOnlyList<TilePatch> rejectedPatches,
        int topK = 10,
        SearchFilter? filter = null)
    {
        float[] baseQuery = TextQueryEncoder.Encode(textQuery);
        var posEmbeds = confirmedPatches.Select(p => p.EmbeddingVector).ToList();
        var negEmbeds = rejectedPatches.Select(p => p.EmbeddingVector).ToList();

        float[] adjustedQuery = RelevanceFeedbackReranker.AdjustQueryVector(baseQuery, posEmbeds, negEmbeds);
        return _index.Search(adjustedQuery, topK, filter);
    }

    /// <summary>
    /// Extracts patches from a satellite tile and indexes them incrementally.
    /// </summary>
    public int IngestTile(SatelliteTile tile, int patchSize = 64)
    {
        int count = 0;
        int w = tile.Width;
        int h = tile.Height;

        for (int y = 0; y <= h - patchSize; y += patchSize)
        {
            for (int x = 0; x <= w - patchSize; x += patchSize)
            {
                var topLeft = tile.Transform.PixelToGeo(x, y);
                var bottomRight = tile.Transform.PixelToGeo(x + patchSize, y + patchSize);
                var bounds = new BoundingBox(
                    Math.Min(topLeft.Longitude, bottomRight.Longitude),
                    Math.Min(topLeft.Latitude, bottomRight.Latitude),
                    Math.Max(topLeft.Longitude, bottomRight.Longitude),
                    Math.Max(topLeft.Latitude, bottomRight.Latitude)
                );

                float[] embedding = MultiSpectralVisionEncoder.EncodePatch(tile, x, y, patchSize, patchSize);

                var patch = new TilePatch
                {
                    PatchId = $"{tile.TileId}_p_{x}_{y}",
                    ParentTileId = tile.TileId,
                    Platform = tile.Platform,
                    Timestamp = tile.AcquisitionTimestamp,
                    Bounds = bounds,
                    PixelX = x,
                    PixelY = y,
                    PatchWidth = patchSize,
                    PatchHeight = patchSize,
                    EmbeddingVector = embedding,
                    QualityScore = 1.0 - (tile.CloudCoverPercentage / 100.0)
                };

                _index.Add(patch);
                count++;
            }
        }

        return count;
    }
}
