using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Workflow;

namespace GeoSemanticSat.Engine.Services;

/// <summary>
/// Feature 4: Evidence-Grade Intelligence Report and Data Product Service.
/// Generates auditable, tamper-evident intelligence data products including:
/// - W3C PROV-O compliant GeoJSON with raw raster SHA-256 payload integrity
/// - STAC (SpatioTemporal Asset Catalog) Item Metadata JSON
/// - Responsive Executive Intelligence Briefing (HTML/PDF-ready)
/// - Cryptographic Merkle/SHA-256 Manifest for chain-of-custody verification.
/// </summary>
public class EvidenceReportService
{
    public record EvidencePackageResult(
        string OutputDirectory,
        string GeoJsonPath,
        string StacMetadataPath,
        string BriefingHtmlPath,
        string ManifestSha256Path,
        string MerkleRootHash,
        int ConfirmedChangesCount,
        int TotalChangesCount
    );

    /// <summary>
    /// Compiles a complete evidence data product package for detected changes.
    /// </summary>
    public static EvidencePackageResult GenerateEvidencePackage(
        string outputDirectory,
        string missionName,
        IEnumerable<ChangeRecord> changes,
        SatelliteTile? t1 = null,
        SatelliteTile? t2 = null,
        string analystNotes = "Standard sovereign intelligence review")
    {
        Directory.CreateDirectory(outputDirectory);

        var changeList = new List<ChangeRecord>(changes);
        string timestampStr = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        string t1Sha = t1?.SourceImageSha256 ?? "N/A";
        string t2Sha = t2?.SourceImageSha256 ?? "N/A";

        // 1. W3C PROV-O GeoJSON Export
        string geoJsonFile = Path.Combine(outputDirectory, $"{missionName}_{timestampStr}_evidence.geojson");
        string geoJsonContent = ProvenanceAuditTrail.ExportToGeoJson(changeList, "2.0.0-PROV", t2Sha, 0.05);
        File.WriteAllText(geoJsonFile, geoJsonContent, Encoding.UTF8);

        // 2. STAC Item Metadata JSON
        string stacFile = Path.Combine(outputDirectory, $"{missionName}_{timestampStr}_stac.json");
        string stacContent = GenerateStacMetadata(missionName, changeList, t1, t2);
        File.WriteAllText(stacFile, stacContent, Encoding.UTF8);

        // 3. Executive HTML Briefing (Print/PDF Ready)
        string htmlFile = Path.Combine(outputDirectory, $"{missionName}_{timestampStr}_briefing.html");
        string htmlContent = GenerateBriefingHtml(missionName, changeList, t1, t2, analystNotes);
        File.WriteAllText(htmlFile, htmlContent, Encoding.UTF8);

        // 4. Cryptographic Manifest (SHA-256 chain of custody)
        string manifestFile = Path.Combine(outputDirectory, $"{missionName}_{timestampStr}_manifest.sha256");
        var manifestSb = new StringBuilder();
        var hashes = new List<string>();

        foreach (var file in new[] { geoJsonFile, stacFile, htmlFile })
        {
            byte[] fileBytes = File.ReadAllBytes(file);
            using var sha = SHA256.Create();
            string hash = Convert.ToHexString(sha.ComputeHash(fileBytes)).ToLowerInvariant();
            hashes.Add(hash);
            manifestSb.AppendLine($"{hash}  {Path.GetFileName(file)}");
        }

        // Compute Merkle Root of generated artifacts
        using var rootSha = SHA256.Create();
        string concatenatedHashes = string.Join(":", hashes);
        string merkleRoot = Convert.ToHexString(rootSha.ComputeHash(Encoding.UTF8.GetBytes(concatenatedHashes))).ToLowerInvariant();
        manifestSb.AppendLine($"MERKLE_ROOT  {merkleRoot}");
        manifestSb.AppendLine($"T1_IMAGE_SHA256  {t1Sha}");
        manifestSb.AppendLine($"T2_IMAGE_SHA256  {t2Sha}");
        File.WriteAllText(manifestFile, manifestSb.ToString(), Encoding.UTF8);

        int confirmed = changeList.Count(c => c.ConfirmedByAnalyst);

        return new EvidencePackageResult(
            OutputDirectory: outputDirectory,
            GeoJsonPath: geoJsonFile,
            StacMetadataPath: stacFile,
            BriefingHtmlPath: htmlFile,
            ManifestSha256Path: manifestFile,
            MerkleRootHash: merkleRoot,
            ConfirmedChangesCount: confirmed,
            TotalChangesCount: changeList.Count
        );
    }

    private static string GenerateStacMetadata(string missionName, List<ChangeRecord> changes, SatelliteTile? t1, SatelliteTile? t2)
    {
        double minLon = changes.Count > 0 ? changes.Min(c => c.Bounds.MinLon) : 77.0;
        double minLat = changes.Count > 0 ? changes.Min(c => c.Bounds.MinLat) : 28.0;
        double maxLon = changes.Count > 0 ? changes.Max(c => c.Bounds.MaxLon) : 77.5;
        double maxLat = changes.Count > 0 ? changes.Max(c => c.Bounds.MaxLat) : 28.5;

        var stac = new Dictionary<string, object>
        {
            ["stac_version"] = "1.0.0",
            ["stac_extensions"] = new[] { "https://stac-extensions.github.io/processing/v1.1.0/schema.json" },
            ["type"] = "Feature",
            ["id"] = $"UPAGRAHA-{missionName}-{Guid.NewGuid():N}",
            ["bbox"] = new[] { minLon, minLat, maxLon, maxLat },
            ["geometry"] = new
            {
                type = "Polygon",
                coordinates = new[]
                {
                    new[]
                    {
                        new[] { minLon, minLat },
                        new[] { maxLon, minLat },
                        new[] { maxLon, maxLat },
                        new[] { minLon, maxLat },
                        new[] { minLon, minLat }
                    }
                }
            },
            ["properties"] = new Dictionary<string, object>
            {
                ["title"] = $"UPAGRAHA Intelligence Product: {missionName}",
                ["description"] = "Evidence-grade multi-temporal change detection and semantic attribution record.",
                ["datetime"] = (t2?.AcquisitionTimestamp ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["start_datetime"] = (t1?.AcquisitionTimestamp ?? DateTime.UtcNow.AddDays(-14)).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["end_datetime"] = (t2?.AcquisitionTimestamp ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["processing:software"] = "UPAGRAHA / GeoSemanticSat Sovereign Engine v2.0",
                ["processing:facility"] = "Air-Gapped Sovereign Facility",
                ["upagraha:total_changes"] = changes.Count,
                ["upagraha:confirmed_changes"] = changes.Count(c => c.ConfirmedByAnalyst),
                ["upagraha:t1_sha256"] = t1?.SourceImageSha256 ?? "N/A",
                ["upagraha:t2_sha256"] = t2?.SourceImageSha256 ?? "N/A"
            },
            ["assets"] = new Dictionary<string, object>
            {
                ["evidence_geojson"] = new
                {
                    href = "./evidence.geojson",
                    type = "application/geo+json",
                    roles = new[] { "data", "change-vectors" }
                }
            }
        };

        return JsonSerializer.Serialize(stac, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string GenerateBriefingHtml(string missionName, List<ChangeRecord> changes, SatelliteTile? t1, SatelliteTile? t2, string notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <title>UPAGRAHA Intelligence Briefing</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; margin: 30px; background: #0f172a; color: #f8fafc; }");
        sb.AppendLine("    .header { border-bottom: 2px solid #38bdf8; padding-bottom: 15px; margin-bottom: 25px; }");
        sb.AppendLine("    h1 { margin: 0; color: #38bdf8; font-size: 24px; text-transform: uppercase; letter-spacing: 1px; }");
        sb.AppendLine("    .badge { display: inline-block; padding: 4px 10px; border-radius: 4px; font-size: 12px; font-weight: bold; background: #0369a1; color: white; margin-top: 6px; }");
        sb.AppendLine("    .grid { display: grid; grid-template-columns: repeat(4, 1fr); gap: 15px; margin-bottom: 25px; }");
        sb.AppendLine("    .card { background: #1e293b; border: 1px solid #334155; border-radius: 8px; padding: 15px; }");
        sb.AppendLine("    .card-label { font-size: 11px; text-transform: uppercase; color: #94a3b8; margin-bottom: 5px; }");
        sb.AppendLine("    .card-value { font-size: 20px; font-weight: bold; color: #f1f5f9; }");
        sb.AppendLine("    table { width: 100%; border-collapse: collapse; margin-top: 15px; background: #1e293b; border-radius: 8px; overflow: hidden; }");
        sb.AppendLine("    th, td { padding: 12px 16px; text-align: left; border-bottom: 1px solid #334155; font-size: 13px; }");
        sb.AppendLine("    th { background: #0f172a; color: #94a3b8; font-weight: 600; text-transform: uppercase; font-size: 11px; }");
        sb.AppendLine("    .type-tag { padding: 3px 8px; border-radius: 4px; font-size: 11px; font-weight: 600; }");
        sb.AppendLine("    .construction { background: #b45309; color: #fef3c7; }");
        sb.AppendLine("    .clearance { background: #15803d; color: #dcfce7; }");
        sb.AppendLine("    .water { background: #0369a1; color: #e0f2fe; }");
        sb.AppendLine("    .road { background: #6d28d9; color: #ede9fe; }");
        sb.AppendLine("    .footer { margin-top: 40px; font-size: 11px; color: #64748b; border-top: 1px solid #334155; padding-top: 15px; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        sb.AppendLine("  <div class=\"header\">");
        sb.AppendLine($"    <h1>UPAGRAHA Sovereign EO Intelligence Briefing: {missionName}</h1>");
        sb.AppendLine("    <div class=\"badge\">AIR-GAPPED SOVEREIGN RECORD // STRICT PROVENANCE PRESERVED</div>");
        sb.AppendLine("  </div>");

        sb.AppendLine("  <div class=\"grid\">");
        sb.AppendLine($"    <div class=\"card\"><div class=\"card-label\">Total Change Detections</div><div class=\"card-value\">{changes.Count}</div></div>");
        sb.AppendLine($"    <div class=\"card\"><div class=\"card-label\">Confirmed by Analyst</div><div class=\"card-value\">{changes.Count(c => c.ConfirmedByAnalyst)}</div></div>");
        sb.AppendLine($"    <div class=\"card\"><div class=\"card-label\">Baseline Epoch (T1)</div><div class=\"card-value\" style=\"font-size:14px;\">{t1?.AcquisitionTimestamp:yyyy-MM-dd HH:mm} UTC</div></div>");
        sb.AppendLine($"    <div class=\"card\"><div class=\"card-label\">Target Epoch (T2)</div><div class=\"card-value\" style=\"font-size:14px;\">{t2?.AcquisitionTimestamp:yyyy-MM-dd HH:mm} UTC</div></div>");
        sb.AppendLine("  </div>");

        sb.AppendLine("  <div class=\"card\" style=\"margin-bottom: 25px;\">");
        sb.AppendLine("    <div class=\"card-label\">Analyst Assessment Notes</div>");
        sb.AppendLine($"    <div style=\"font-size: 14px; color: #e2e8f0; margin-top: 5px;\">{notes}</div>");
        sb.AppendLine("  </div>");

        sb.AppendLine("  <table>");
        sb.AppendLine("    <thead><tr><th>ID</th><th>Type</th><th>Confidence</th><th>Area (m²)</th><th>Bounding Coordinates</th><th>Status</th></tr></thead>");
        sb.AppendLine("    <tbody>");

        foreach (var c in changes)
        {
            string typeClass = c.Type switch
            {
                ChangeType.Construction => "construction",
                ChangeType.Clearance => "clearance",
                ChangeType.WaterExtentVariation => "water",
                ChangeType.RoadDevelopment => "road",
                _ => "type-tag"
            };

            string statusText = c.ConfirmedByAnalyst ? "<span style='color:#4ade80;'>CONFIRMED</span>" : (c.RejectedByAnalyst ? "<span style='color:#f87171;'>REJECTED</span>" : "<span style='color:#94a3b8;'>PENDING</span>");

            sb.AppendLine($"    <tr><td>{c.Id}</td><td><span class=\"type-tag {typeClass}\">{c.Type}</span></td><td>{(c.Confidence * 100):F0}%</td><td>{c.AreaSqMeters:N0}</td><td>{c.Bounds.MinLat:F4}°N, {c.Bounds.MinLon:F4}°E</td><td>{statusText}</td></tr>");
        }

        sb.AppendLine("    </tbody>");
        sb.AppendLine("  </table>");

        sb.AppendLine("  <div class=\"footer\">");
        sb.AppendLine($"    Generated by UPAGRAHA / GeoSemanticSat v2.0 on {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC.<br>");
        sb.AppendLine($"    T1 SHA-256: <code>{t1?.SourceImageSha256 ?? "N/A"}</code> | T2 SHA-256: <code>{t2?.SourceImageSha256 ?? "N/A"}</code><br>");
        sb.AppendLine("    Certified Air-Gapped Decision Support Artifact — Zero External Telemetry.");
        sb.AppendLine("  </div>");

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }
}
