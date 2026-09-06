using System;
using System.Collections.Generic;
using System.Text;
using GeoSemanticSat.Core.VectorIndex;
using VectorIndexStore = GeoSemanticSat.Core.VectorIndex.VectorIndex;

namespace GeoSemanticSat.Engine.Embeddings;

/// <summary>
/// Offline natural-language query encoder for remote sensing.
///
/// Writes ONLY to the shared semantic axes declared in SemanticEmbeddingLayout. It previously
/// also wrote into the appearance block (mean red at 0, mean blue at 2, mean NIR at 3, raw
/// gradient at 32). Those dimensions hold absolute reflectance on an unbounded scale that no
/// text query can meaningfully assert, and on RGB imagery index 0 and index 3 held the same
/// number (NIR falls back to Red), so a "+0.38 at 0, -0.43 at 3" coupling cancelled to a net
/// negative and produced negative match scores for built-up queries.
/// </summary>
public class TextQueryEncoder
{
    public const int EmbeddingDimension = SemanticEmbeddingLayout.Dimension;

    private record SemanticConcept(
        string[] Keywords,
        int TargetDimensionIndex,
        float Weight,
        string Category
    );

    private static readonly List<SemanticConcept> Concepts = new()
    {
        // Category 1: Built-up, structures, buildings, facilities
        new(new[] { "structure", "structures", "building", "buildings", "facility", "facilities", "built", "compound", "hangar", "depot" }, SemanticEmbeddingLayout.BuiltUp, 0.85f, "BuiltUp"),
        new(new[] { "concrete", "foundation", "slab", "cement" }, SemanticEmbeddingLayout.BuiltUp, 0.75f, "BuiltUp"),
        new(new[] { "industrial", "warehouse", "factory", "plant", "storage", "tank", "tanks" }, SemanticEmbeddingLayout.TextureEnergy, 0.70f, "BuiltUp"),

        // Category 2: Water, rivers, reservoirs, coast
        new(new[] { "river", "rivers", "stream", "canal", "water", "waterbody", "reservoir", "lake", "coast", "shore" }, SemanticEmbeddingLayout.Water, 0.85f, "Water"),
        new(new[] { "flood", "flooding", "inundation", "wetland" }, SemanticEmbeddingLayout.OpenWater, 0.80f, "Water"),

        // Category 3: Spatial co-occurrence, structures near river or water.
        // Stop words are stripped before matching, so "near a river" and "along the river"
        // reach these keys as "near river" and "along river".
        new(new[] { "near river", "by river", "near water", "riverbank", "coastal", "along river", "beside river", "next river", "near lake", "near canal", "near reservoir", "near coast", "near shore", "near stream" }, SemanticEmbeddingLayout.StructureNearWater, 0.95f, "StructureNearWater"),

        // Category 4: Vehicles, convoys, equipment concentrations
        new(new[] { "vehicle", "vehicles", "truck", "trucks", "convoy", "concentration", "concentrations", "armor", "tanks", "machinery", "equipment" }, SemanticEmbeddingLayout.ActivityPeaks, 0.90f, "Vehicles"),
        new(new[] { "open ground", "field", "clearing", "staging", "assembly" }, SemanticEmbeddingLayout.BareSoil, 0.60f, "Vehicles"),

        // Category 5: Runway, airfield, linear transport infrastructure
        new(new[] { "runway", "airstrip", "airfield", "taxiway", "apron", "tarmac" }, SemanticEmbeddingLayout.LinearContinuity, 0.92f, "Runway"),
        new(new[] { "aircraft", "airplane", "plane", "planes", "helicopter", "helipad" }, SemanticEmbeddingLayout.ActivityPeaks, 0.80f, "Runway"),
        new(new[] { "road", "roads", "highway", "track", "corridor", "paved", "railway", "rail" }, SemanticEmbeddingLayout.LinearContinuity, 0.88f, "Road"),

        // Category 6: Deforestation, land clearance, earthworks
        new(new[] { "clearance", "cleared", "deforestation", "logged", "clearcut", "earthworks", "excavation", "bare" }, SemanticEmbeddingLayout.ClearedGround, 0.90f, "Clearance"),
        new(new[] { "vegetation", "forest", "trees", "canopy", "woodland" }, SemanticEmbeddingLayout.Vegetation, 0.75f, "Vegetation")
    };

    /// <summary>
    /// Words dropped before matching so multi-word concept keys survive natural phrasing.
    /// "structures near a river" previously failed to match the key "near river", the single
    /// highest-weight concept in the lexicon, because matching was a raw substring test.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "of", "at", "in", "on", "to", "and", "or", "with", "some", "any", "new", "newly"
    };

    private const char Space = ' ';

    /// <summary>
    /// Lowercases, strips punctuation, drops stop words, and collapses whitespace.
    /// The returned string is space-padded so keyword matching can be whole-word: a raw
    /// Contains() matched "plane" inside "planet" and "rail" inside "guardrail".
    /// </summary>
    internal static string Normalize(string query)
    {
        var cleaned = new StringBuilder(query.Length);
        foreach (char c in query.ToLowerInvariant())
        {
            cleaned.Append(char.IsLetterOrDigit(c) ? c : Space);
        }

        var kept = new List<string>();
        foreach (var token in cleaned.ToString().Split(Space, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!StopWords.Contains(token)) kept.Add(token);
        }

        return kept.Count == 0 ? " " : " " + string.Join(" ", kept) + " ";
    }

    private static bool ContainsPhrase(string paddedQuery, string keyword)
        => paddedQuery.Contains(" " + keyword + " ", StringComparison.Ordinal);

    /// <summary>Encodes a natural language query into the shared semantic axes.</summary>
    public static float[] Encode(string query)
    {
        float[] embedding = new float[EmbeddingDimension];
        if (string.IsNullOrWhiteSpace(query))
        {
            return embedding;
        }

        string normalized = Normalize(query);
        bool anyMatched = false;

        foreach (var concept in Concepts)
        {
            foreach (var kw in concept.Keywords)
            {
                if (!ContainsPhrase(normalized, kw)) continue;

                anyMatched = true;
                embedding[concept.TargetDimensionIndex] += concept.Weight;

                // Cross-coupling stays inside the semantic axes: every dimension touched here
                // is one the image encoder also populates on the same bounded [-1, 1] scale.
                switch (concept.Category)
                {
                    case "BuiltUp":
                        embedding[SemanticEmbeddingLayout.ManMadeContrast] += 0.50f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.Vegetation] -= 0.25f * concept.Weight;
                        break;
                    case "Water":
                        embedding[SemanticEmbeddingLayout.OpenWater] += 0.45f * concept.Weight;
                        break;
                    case "StructureNearWater":
                        embedding[SemanticEmbeddingLayout.BuiltUp] += 0.60f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.Water] += 0.60f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.ManMadeContrast] += 0.40f * concept.Weight;
                        break;
                    case "Vehicles":
                        embedding[SemanticEmbeddingLayout.TextureEnergy] += 0.65f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.ActivityPeaks] += 0.85f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.BareSoil] += 0.40f * concept.Weight;
                        break;
                    case "Runway":
                    case "Road":
                        embedding[SemanticEmbeddingLayout.LinearContinuity] += 0.85f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.BuiltUp] += 0.50f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.ManMadeContrast] += 0.40f * concept.Weight;
                        break;
                    case "Clearance":
                        embedding[SemanticEmbeddingLayout.Vegetation] -= 0.70f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.BareSoil] += 0.70f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.ClearedGround] += 0.80f * concept.Weight;
                        break;
                    case "Vegetation":
                        embedding[SemanticEmbeddingLayout.Vegetation] += 0.60f * concept.Weight;
                        break;
                }
                break;
            }
        }

        // Fallback for vocabulary outside the lexicon: deterministic projection onto the
        // semantic axes. Projecting across all 128 dimensions put most of the mass on
        // appearance dimensions the image encoder scales completely differently, so an
        // unknown word produced an arbitrary score rather than a weak one.
        if (!anyMatched)
        {
            foreach (var token in normalized.Split(Space, StringSplitOptions.RemoveEmptyEntries))
            {
                int axis = SemanticEmbeddingLayout.SemanticAxes[
                    StableHash(token) % SemanticEmbeddingLayout.SemanticAxes.Length];
                embedding[axis] += 0.5f;
            }
        }

        VectorIndexStore.NormalizeInPlace(embedding);
        return embedding;
    }

    /// <summary>
    /// FNV-1a 32-bit hash, masked to a non-negative int.
    /// string.GetHashCode() is randomized per process in .NET, which made out-of-lexicon
    /// queries return a different ranking on every application launch and made persisted
    /// indices and PROV-O provenance exports irreproducible. Masking the sign bit also
    /// avoids the Math.Abs(int.MinValue) OverflowException the previous code could throw.
    /// </summary>
    public static int StableHash(string token)
    {
        unchecked
        {
            uint hash = 2166136261u;
            foreach (char c in token)
            {
                hash ^= c;
                hash *= 16777619u;
            }
            return (int)(hash & 0x7FFFFFFF);
        }
    }
}
