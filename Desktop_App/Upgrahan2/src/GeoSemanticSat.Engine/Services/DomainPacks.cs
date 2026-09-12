using System;
using System.Collections.Generic;
using GeoSemanticSat.Core.ChangeDetection;
using GeoSemanticSat.Core.Model;

namespace GeoSemanticSat.Engine.Services;

public enum VerticalDomain
{
    DisasterResponse,
    DefenseInfrastructure,
    MiningEnvironmental,
    UrbanLinearInfrastructure
}

public class DomainIntelligencePack
{
    public required VerticalDomain Domain { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required HashSet<ChangeType> PrimaryChangeTypes { get; init; }
    public required int RecommendedPatchSize { get; init; }
    public required double MinConfidenceThreshold { get; init; }
    public required double MinChangeMagnitude { get; init; }
    public required List<string> SemanticQueryTemplates { get; init; }
    public required List<string> KeySpectralIndices { get; init; }

    public MultiTemporalChangeDetector.ChangeDetectionOptions CreateDetectionOptions()
    {
        return new MultiTemporalChangeDetector.ChangeDetectionOptions(
            PatchSize: RecommendedPatchSize,
            MinConfidence: MinConfidenceThreshold,
            MinChangeMagnitude: MinChangeMagnitude,
            EnableRadiometricNormalization: true,
            EnableJitterSuppression: true,
            EnableQualityMasking: true
        );
    }
}

/// <summary>
/// Feature 3: Vertical Intelligence Packages (Domain Packs).
/// Provides sector-tuned configuration bundles, semantic prompt presets, and calibrated
/// thresholds for Disaster Response, Defense, Mining/Environmental, and Urban Infrastructure.
/// </summary>
public static class DomainPackRegistry
{
    private static readonly Dictionary<VerticalDomain, DomainIntelligencePack> Packs = new()
    {
        [VerticalDomain.DisasterResponse] = new DomainIntelligencePack
        {
            Domain = VerticalDomain.DisasterResponse,
            Name = "Disaster Response & Flood Inundation Pack",
            Description = "Calibrated for rapid flood expansion, reservoir level shifts, and water body inundation with high false-alarm rejection.",
            PrimaryChangeTypes = new HashSet<ChangeType> { ChangeType.WaterExtentVariation },
            RecommendedPatchSize = 16,
            MinConfidenceThreshold = 0.60,
            MinChangeMagnitude = 0.05,
            KeySpectralIndices = new List<string> { "NDWI", "MNDWI" },
            SemanticQueryTemplates = new List<string>
            {
                "submerged residential area flood",
                "riverbank breach inundation",
                "reservoir shrinkage drought",
                "flash flood mudflow debris"
            }
        },

        [VerticalDomain.DefenseInfrastructure] = new DomainIntelligencePack
        {
            Domain = VerticalDomain.DefenseInfrastructure,
            Name = "Defense & Critical Infrastructure Facility Pack",
            Description = "Optimized for runway extensions, revetments, hardened structural construction, and perimeter breaches.",
            PrimaryChangeTypes = new HashSet<ChangeType> { ChangeType.Construction, ChangeType.Clearance, ChangeType.ActivityConcentration },
            RecommendedPatchSize = 16,
            MinConfidenceThreshold = 0.70,
            MinChangeMagnitude = 0.07,
            KeySpectralIndices = new List<string> { "NDBI", "SAR_VV", "NDVI" },
            SemanticQueryTemplates = new List<string>
            {
                "military runway expansion asphalt",
                "hardened aircraft shelter construction",
                "defensive trench fortification excavation",
                "tactical vehicle staging depot"
            }
        },

        [VerticalDomain.MiningEnvironmental] = new DomainIntelligencePack
        {
            Domain = VerticalDomain.MiningEnvironmental,
            Name = "Mining, Forestry & Environmental Compliance Pack",
            Description = "Tuned for tracking illegal open-cast pit mining, deforestation, canopy loss, and tailings pond expansion.",
            PrimaryChangeTypes = new HashSet<ChangeType> { ChangeType.Clearance, ChangeType.Construction },
            RecommendedPatchSize = 32,
            MinConfidenceThreshold = 0.65,
            MinChangeMagnitude = 0.06,
            KeySpectralIndices = new List<string> { "NDVI", "BSI", "EVI" },
            SemanticQueryTemplates = new List<string>
            {
                "illegal open-cast mining pit expansion",
                "forest canopy clearing logging cut",
                "tailings dam water accumulation",
                "riparian zone buffer encroachment"
            }
        },

        [VerticalDomain.UrbanLinearInfrastructure] = new DomainIntelligencePack
        {
            Domain = VerticalDomain.UrbanLinearInfrastructure,
            Name = "Urban & Linear Infrastructure Governance Pack",
            Description = "Optimized for highway cuts, railway corridor grading, right-of-way encroachment, and rapid urban sprawl.",
            PrimaryChangeTypes = new HashSet<ChangeType> { ChangeType.RoadDevelopment, ChangeType.Construction },
            RecommendedPatchSize = 16,
            MinConfidenceThreshold = 0.65,
            MinChangeMagnitude = 0.055,
            KeySpectralIndices = new List<string> { "NDBI", "BSI", "EBBI" },
            SemanticQueryTemplates = new List<string>
            {
                "new highway cutting grading alignment",
                "railway corridor earthworks ballast",
                "informal settlement encroachment buffer",
                "commercial warehouse structural expansion"
            }
        }
    };

    public static DomainIntelligencePack GetPack(VerticalDomain domain) => Packs[domain];

    public static IReadOnlyCollection<DomainIntelligencePack> GetAllPacks() => Packs.Values;
}
