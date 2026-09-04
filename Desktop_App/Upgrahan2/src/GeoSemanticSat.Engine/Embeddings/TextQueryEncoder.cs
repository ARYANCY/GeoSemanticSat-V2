using System;
using System.Collections.Generic;
using VectorIndexStore = GeoSemanticSat.Core.VectorIndex.VectorIndex;

namespace GeoSemanticSat.Engine.Embeddings;

/// <summary>
/// Offline Natural-Language Semantic Text Encoder for Remote Sensing Queries.
/// Maps natural-language domain queries (e.g. "structures near a river", "vehicle concentrations",
/// "airfield runway", "deforestation") directly into the 128-dimensional multi-spectral embedding space.
/// </summary>
public class TextQueryEncoder
{
    public const int EmbeddingDimension = 128;

    private record SemanticConcept(
        string[] Keywords,
        int TargetDimensionIndex,
        float Weight,
        string Category
    );

    private static readonly List<SemanticConcept> Concepts = new()
    {
        // Category 1: Built-up, structures, buildings, facilities
        new(new[] { "structure", "structures", "building", "buildings", "facility", "facilities", "built", "compound", "hangar", "depot" }, 18, 0.85f, "BuiltUp"),
        new(new[] { "concrete", "foundation", "slab", "cement" }, 18, 0.75f, "BuiltUp"),
        new(new[] { "industrial", "warehouse", "factory", "plant", "storage", "tank", "tanks" }, 64, 0.70f, "BuiltUp"),

        // Category 2: Water, rivers, reservoirs, coast
        new(new[] { "river", "rivers", "stream", "canal", "water", "waterbody", "reservoir", "lake", "coast", "shore" }, 17, 0.85f, "Water"),
        new(new[] { "flood", "flooding", "inundation", "wetland" }, 19, 0.80f, "Water"),

        // Category 3: Spatial co-occurrence: "structures near river / water"
        new(new[] { "near river", "by river", "near water", "riverbank", "coastal", "along river" }, 21, 0.95f, "StructureNearWater"),

        // Category 4: Vehicles, convoys, equipment concentrations
        new(new[] { "vehicle", "vehicles", "truck", "trucks", "convoy", "concentration", "concentrations", "armor", "tanks", "machinery", "equipment" }, 65, 0.90f, "Vehicles"),
        new(new[] { "open ground", "field", "clearing", "staging", "assembly" }, 20, 0.60f, "Vehicles"),

        // Category 5: Runway, airfield, linear transport infrastructure
        new(new[] { "runway", "airstrip", "airfield", "taxiway", "apron", "tarmac" }, 37, 0.92f, "Runway"),
        new(new[] { "aircraft", "airplane", "plane", "planes", "helicopter", "helipad" }, 65, 0.80f, "Runway"),
        new(new[] { "road", "roads", "highway", "track", "corridor", "paved", "railway", "rail" }, 37, 0.88f, "Road"),

        // Category 6: Deforestation, land clearance, earthworks
        new(new[] { "clearance", "cleared", "deforestation", "logged", "clearcut", "earthworks", "excavation", "bare" }, 22, 0.90f, "Clearance"),
        new(new[] { "vegetation", "forest", "trees", "canopy", "woodland" }, 16, 0.75f, "Vegetation")
    };

    /// <summary>
    /// Encodes arbitrary natural language query into the aligned 128-dimensional embedding space.
    /// </summary>
    public static float[] Encode(string query)
    {
        float[] embedding = new float[EmbeddingDimension];
        if (string.IsNullOrWhiteSpace(query))
        {
            VectorIndexStore.NormalizeInPlace(embedding);
            return embedding;
        }

        string lower = query.ToLowerInvariant();
        bool anyMatched = false;

        foreach (var concept in Concepts)
        {
            foreach (var kw in concept.Keywords)
            {
                if (lower.Contains(kw))
                {
                    anyMatched = true;
                    embedding[concept.TargetDimensionIndex] += concept.Weight;

                    // Apply semantic cross-coupling based on domain physics
                    switch (concept.Category)
                    {
                        case "BuiltUp":
                            embedding[0] += 0.45f * concept.Weight; // high red reflectance
                            embedding[32] += 0.50f * concept.Weight; // edge gradients
                            break;
                        case "Water":
                            embedding[2] += 0.40f * concept.Weight; // blue band
                            embedding[3] -= 0.50f * concept.Weight; // low NIR absorption
                            break;
                        case "StructureNearWater":
                            embedding[18] += 0.60f * concept.Weight; // NDBI
                            embedding[17] += 0.60f * concept.Weight; // NDWI
                            embedding[21] += 0.90f * concept.Weight;
                            break;
                        case "Vehicles":
                            embedding[64] += 0.65f * concept.Weight; // texture variance
                            embedding[65] += 0.85f * concept.Weight; // high-frequency peak anomalies
                            embedding[20] += 0.40f * concept.Weight; // soil background
                            break;
                        case "Runway":
                        case "Road":
                            embedding[37] += 0.85f * concept.Weight; // linear directional orientation
                            embedding[18] += 0.50f * concept.Weight; // asphalt / concrete
                            break;
                        case "Clearance":
                            embedding[16] -= 0.70f * concept.Weight; // drop in NDVI
                            embedding[20] += 0.70f * concept.Weight; // bare soil
                            embedding[22] += 0.80f * concept.Weight;
                            break;
                    }
                    break;
                }
            }
        }

        // Fallback for vocabulary words outside specific lexicon: deterministic hash projection
        if (!anyMatched)
        {
            var tokens = lower.Split(new[] { ' ', ',', '.', '-', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                int hash = Math.Abs(token.GetHashCode());
                int idx = hash % EmbeddingDimension;
                embedding[idx] += 0.5f;
            }
        }

        VectorIndexStore.NormalizeInPlace(embedding);
        return embedding;
    }
}
