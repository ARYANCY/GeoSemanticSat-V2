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
        string Category,
        string Description
    );

    private static readonly List<SemanticConcept> Concepts = new()
    {
        // Category 1: Built-up, structures, buildings, facilities
        new(new[] { "structure", "structures", "building", "buildings", "facility", "facilities", "built", "compound", "hangar", "depot", "complex", "construction", "settlement" }, SemanticEmbeddingLayout.BuiltUp, 0.90f, "BuiltUp", "Buildings & Built-Up Structures"),
        new(new[] { "concrete", "foundation", "slab", "cement", "roof", "rooftop" }, SemanticEmbeddingLayout.BuiltUp, 0.80f, "BuiltUp", "Concrete & Foundations"),
        new(new[] { "industrial", "warehouse", "factory", "plant", "storage", "tank", "tanks", "substation", "refinery", "silo" }, SemanticEmbeddingLayout.TextureEnergy, 0.80f, "BuiltUp", "Industrial Infrastructure & Tanks"),

        // Category 2: Water, rivers, reservoirs, coast
        new(new[] { "river", "rivers", "stream", "canal", "water", "waterbody", "reservoir", "lake", "coast", "shore", "pond", "sea", "ocean" }, SemanticEmbeddingLayout.Water, 0.90f, "Water", "Waterbody, River & Lakes"),
        new(new[] { "flood", "flooding", "inundation", "wetland", "marsh", "water variation", "high water" }, SemanticEmbeddingLayout.OpenWater, 0.85f, "Water", "Flooding & Wetlands"),

        // Category 3: Spatial co-occurrence, structures near river or water
        new(new[] { "near river", "by river", "near water", "riverbank", "coastal", "along river", "beside river", "next river", "near lake", "near canal", "near reservoir", "near coast", "near shore", "near stream", "along water", "by water" }, SemanticEmbeddingLayout.StructureNearWater, 0.98f, "StructureNearWater", "Structures near Waterbody/River"),

        // Category 4: Vehicles, convoys, equipment concentrations, staging
        new(new[] { "vehicle", "vehicles", "truck", "trucks", "car", "cars", "van", "vans", "convoy", "convoys", "concentration", "concentrations", "armor", "armored", "tanks", "tank", "machinery", "equipment", "heavy equipment", "cranes", "motor pool", "parked", "parking", "fleet" }, SemanticEmbeddingLayout.ActivityPeaks, 0.95f, "Vehicles", "Vehicle & Equipment Concentrations"),
        new(new[] { "open ground", "open field", "bare ground", "bare soil", "dirt field", "field", "clearing", "staging", "assembly", "staging area", "unpaved", "ground" }, SemanticEmbeddingLayout.BareSoil, 0.75f, "Vehicles", "Open Ground & Staging Area"),

        // Category 5: Runway, airfield, linear transport infrastructure
        new(new[] { "runway", "runways", "airstrip", "airfield", "airport", "taxiway", "apron", "tarmac" }, SemanticEmbeddingLayout.LinearContinuity, 0.95f, "Runway", "Airfield, Runway & Taxiways"),
        new(new[] { "aircraft", "airplane", "plane", "planes", "helicopter", "helipad" }, SemanticEmbeddingLayout.ActivityPeaks, 0.85f, "Runway", "Aircraft & Helipads"),
        new(new[] { "road", "roads", "highway", "expressway", "freeway", "track", "corridor", "paved", "paving", "railway", "railroad", "rail", "asphalt" }, SemanticEmbeddingLayout.LinearContinuity, 0.90f, "Road", "Paved Roads & Transport Corridors"),

        // Category 6: Deforestation, land clearance, earthworks
        new(new[] { "clearance", "cleared", "deforestation", "logged", "logging", "clearcut", "tree loss", "tree clearing", "earthworks", "excavation", "quarry", "digging", "bare" }, SemanticEmbeddingLayout.ClearedGround, 0.95f, "Clearance", "Land Clearance & Deforestation"),
        new(new[] { "vegetation", "forest", "trees", "canopy", "woodland", "jungle", "plants", "crop", "crops", "agriculture", "farm", "greenery" }, SemanticEmbeddingLayout.Vegetation, 0.85f, "Vegetation", "Vegetation & Forest Canopy")
    };

    /// <summary>
    /// Words dropped before matching so multi-word concept keys survive natural phrasing.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "of", "at", "in", "on", "to", "and", "or", "with", "some", "any", "new", "newly", "large", "small", "many", "showing", "find", "search", "looking", "for"
    };

    private const char Space = ' ';

    /// <summary>
    /// Lowercases, strips punctuation, drops stop words, and collapses whitespace.
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

    /// <summary>
    /// Generates a human-friendly explanation of the detected NLP semantic intent.
    /// </summary>
    public static string ExplainQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "AI Finding: Ready to search";

        string normalized = Normalize(query);
        var matchedDescriptions = new List<string>();

        foreach (var concept in Concepts)
        {
            foreach (var kw in concept.Keywords)
            {
                if (ContainsPhrase(normalized, kw))
                {
                    if (!matchedDescriptions.Contains(concept.Description))
                    {
                        matchedDescriptions.Add(concept.Description);
                    }
                    break;
                }
            }
        }

        if (matchedDescriptions.Count > 0)
        {
            return $"AI Semantic Intent: {string.Join(" + ", matchedDescriptions)}";
        }

        return $"AI Finding: Keyword search for '{query.Trim()}'";
    }

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
                        embedding[SemanticEmbeddingLayout.ManMadeContrast] += 0.55f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.Vegetation] -= 0.30f * concept.Weight;
                        break;
                    case "Water":
                        embedding[SemanticEmbeddingLayout.OpenWater] += 0.50f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.BareSoil] -= 0.30f * concept.Weight;
                        break;
                    case "StructureNearWater":
                        embedding[SemanticEmbeddingLayout.BuiltUp] += 0.70f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.Water] += 0.70f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.ManMadeContrast] += 0.50f * concept.Weight;
                        break;
                    case "Vehicles":
                        embedding[SemanticEmbeddingLayout.ActivityPeaks] += 0.95f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.TextureEnergy] += 0.80f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.BareSoil] += 0.65f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.ManMadeContrast] += 0.60f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.Water] -= 0.35f * concept.Weight;
                        break;
                    case "Runway":
                    case "Road":
                        embedding[SemanticEmbeddingLayout.LinearContinuity] += 0.90f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.BuiltUp] += 0.55f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.ManMadeContrast] += 0.45f * concept.Weight;
                        break;
                    case "Clearance":
                        embedding[SemanticEmbeddingLayout.Vegetation] -= 0.80f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.BareSoil] += 0.85f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.ClearedGround] += 0.90f * concept.Weight;
                        break;
                    case "Vegetation":
                        embedding[SemanticEmbeddingLayout.Vegetation] += 0.75f * concept.Weight;
                        embedding[SemanticEmbeddingLayout.BareSoil] -= 0.40f * concept.Weight;
                        break;
                }
                break;
            }
        }

        // Fallback for vocabulary outside the lexicon: deterministic projection onto the semantic axes.
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
