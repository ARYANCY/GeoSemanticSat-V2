using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using GeoSemanticSat.Core.Model;

namespace GeoSemanticSat.Core.VectorIndex;

public record SearchResult(TilePatch Patch, double SimilarityScore, double Distance);

public record SearchFilter(
    BoundingBox? BoundingBox = null,
    GeoCoordinate? CenterCoordinate = null,
    double? RadiusKm = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    SensorPlatform? Platform = null,
    double MinQuality = 0.3
);

/// <summary>
/// High-performance Vector Index with Incremental Ingestion and Compact Binary Persistence.
/// Supports multi-modal cosine similarity search with spatiotemporal constraints.
/// </summary>
public class VectorIndex
{
    private readonly List<TilePatch> _patches = new();
    private readonly System.Threading.ReaderWriterLockSlim _rwLock = new();
    public int VectorDimension { get; private set; }

    public int Count
    {
        get
        {
            _rwLock.EnterReadLock();
            try { return _patches.Count; }
            finally { _rwLock.ExitReadLock(); }
        }
    }

    public VectorIndex(int vectorDimension = 128)
    {
        VectorDimension = vectorDimension;
    }

    /// <summary>
    /// Incrementally adds a patch to the index without rebuilding existing embeddings.
    /// </summary>
    public void Add(TilePatch patch)
    {
        if (patch.EmbeddingVector.Length != VectorDimension)
        {
            throw new ArgumentException($"Embedding dimension mismatch. Expected {VectorDimension}, got {patch.EmbeddingVector.Length}");
        }

        // Normalize vector for cosine distance
        NormalizeInPlace(patch.EmbeddingVector);

        _rwLock.EnterWriteLock();
        try
        {
            _patches.Add(patch);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Batch adds patches incrementally.
    /// </summary>
    public void AddRange(IEnumerable<TilePatch> patches)
    {
        _rwLock.EnterWriteLock();
        try
        {
            foreach (var p in patches)
            {
                if (p.EmbeddingVector.Length != VectorDimension)
                    throw new ArgumentException($"Embedding dimension mismatch. Expected {VectorDimension}, got {p.EmbeddingVector.Length}");
                NormalizeInPlace(p.EmbeddingVector);
                _patches.Add(p);
            }
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    public IReadOnlyList<TilePatch> GetAllPatches()
    {
        _rwLock.EnterReadLock();
        try
        {
            return _patches.ToList();
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Top-K nearest neighbor search by cosine similarity with spatiotemporal filters.
    /// Supports high-throughput concurrent parallel readers.
    /// </summary>
    public List<SearchResult> Search(float[] queryVector, int topK = 10, SearchFilter? filter = null)
    {
        if (queryVector.Length != VectorDimension)
            throw new ArgumentException($"Query vector dimension mismatch: {queryVector.Length} vs {VectorDimension}");

        float[] normQuery = (float[])queryVector.Clone();
        NormalizeInPlace(normQuery);

        var results = new List<SearchResult>();

        _rwLock.EnterReadLock();
        try
        {
            for (int i = 0; i < _patches.Count; i++)
            {
                var patch = _patches[i];

                // Apply Spatio-Temporal Filters
                if (filter != null)
                {
                    if (filter.BoundingBox.HasValue && !filter.BoundingBox.Value.Intersects(patch.Bounds))
                        continue;

                    if (filter.CenterCoordinate.HasValue && filter.RadiusKm.HasValue)
                    {
                        double distKm = patch.Bounds.Center.DistanceToKm(filter.CenterCoordinate.Value);
                        if (distKm > filter.RadiusKm.Value)
                            continue;
                    }

                    if (filter.StartDate.HasValue && patch.Timestamp < filter.StartDate.Value)
                        continue;

                    if (filter.EndDate.HasValue && patch.Timestamp > filter.EndDate.Value)
                        continue;

                    if (filter.Platform.HasValue && patch.Platform != filter.Platform.Value)
                        continue;

                    if (patch.QualityScore < filter.MinQuality)
                        continue;
                }

                // SIMD Cosine similarity = dot product of normalized vectors
                double sim = DotProduct(normQuery, patch.EmbeddingVector);
                double dist = 1.0 - sim;
                results.Add(new SearchResult(patch, Math.Clamp(sim, -1.0, 1.0), Math.Max(0.0, dist)));
            }
        }
        finally
        {
            _rwLock.ExitReadLock();
        }

        return results.OrderByDescending(r => r.SimilarityScore).Take(topK).ToList();
    }

    public static double DotProduct(float[] a, float[] b)
    {
        int length = a.Length;
        int simdim = Vector<float>.Count;
        int i = 0;
        float sum = 0f;

        if (Vector.IsHardwareAccelerated && length >= simdim)
        {
            Vector<float> acc = Vector<float>.Zero;
            for (; i <= length - simdim; i += simdim)
            {
                var va = new Vector<float>(a, i);
                var vb = new Vector<float>(b, i);
                acc += va * vb;
            }
            sum = Vector.Dot(acc, Vector<float>.One);
        }

        for (; i < length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    public static void NormalizeInPlace(float[] v)
    {
        double normSq = 0.0;
        for (int i = 0; i < v.Length; i++)
        {
            normSq += v[i] * v[i];
        }
        if (normSq > 1e-12)
        {
            float invNorm = (float)(1.0 / Math.Sqrt(normSq));
            for (int i = 0; i < v.Length; i++)
            {
                v[i] *= invNorm;
            }
        }
    }

    /// <summary>
    /// Compact binary serialization for fast offline on-premises loading without network dependencies.
    /// </summary>
    public void SaveIndex(string filePath)
    {
        _rwLock.EnterReadLock();
        try
        {
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);

            // Header: Magic "GSSV"
            bw.Write((byte)'G'); bw.Write((byte)'S'); bw.Write((byte)'S'); bw.Write((byte)'V');
            bw.Write((int)1); // version
            bw.Write(VectorDimension);
            bw.Write(_patches.Count);

            foreach (var p in _patches)
            {
                bw.Write(p.PatchId ?? string.Empty);
                bw.Write(p.ParentTileId ?? string.Empty);
                bw.Write((int)p.Platform);
                bw.Write(p.Timestamp.ToBinary());
                bw.Write(p.Bounds.MinLon);
                bw.Write(p.Bounds.MinLat);
                bw.Write(p.Bounds.MaxLon);
                bw.Write(p.Bounds.MaxLat);
                bw.Write(p.PixelX);
                bw.Write(p.PixelY);
                bw.Write(p.PatchWidth);
                bw.Write(p.PatchHeight);
                bw.Write(p.QualityScore);
                bw.Write(p.HasCloudOrShadow);

                for (int i = 0; i < VectorDimension; i++)
                {
                    bw.Write(p.EmbeddingVector[i]);
                }
            }
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Loads or incrementally appends an existing index file from disk.
    /// </summary>
    public static VectorIndex LoadIndex(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Index file not found: {filePath}");

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);

        byte[] magic = br.ReadBytes(4);
        if (magic[0] != 'G' || magic[1] != 'S' || magic[2] != 'S' || magic[3] != 'V')
            throw new InvalidDataException("Invalid index binary header.");

        int version = br.ReadInt32();
        int dim = br.ReadInt32();
        int count = br.ReadInt32();

        var index = new VectorIndex(dim);

        for (int i = 0; i < count; i++)
        {
            var p = new TilePatch
            {
                PatchId = br.ReadString(),
                ParentTileId = br.ReadString(),
                Platform = (SensorPlatform)br.ReadInt32(),
                Timestamp = DateTime.FromBinary(br.ReadInt64()),
                Bounds = new BoundingBox(br.ReadDouble(), br.ReadDouble(), br.ReadDouble(), br.ReadDouble()),
                PixelX = br.ReadInt32(),
                PixelY = br.ReadInt32(),
                PatchWidth = br.ReadInt32(),
                PatchHeight = br.ReadInt32(),
                QualityScore = br.ReadDouble(),
                HasCloudOrShadow = br.ReadBoolean(),
                EmbeddingVector = new float[dim]
            };

            for (int d = 0; d < dim; d++)
            {
                p.EmbeddingVector[d] = br.ReadSingle();
            }

            index.Add(p);
        }

        return index;
    }
}
