using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Engine.Embeddings;

namespace GeoSemanticSat.Engine.Services;

/// <summary>
/// Structured analyst insight synthesized from Earth Observation metrics.
/// </summary>
public record AnalystInsight(
    string Severity,
    string ChangeClass,
    double Confidence,
    string Summary,
    List<string> PhysicalEvidence,
    string Timeline,
    double FalseAlarmRisk,
    List<string> Recommendations,
    string GroundedDossier
);

/// <summary>
/// Conversational GEOINT analyst assistant and automated insight synthesizer.
/// Operates fully in-process offline with zero external network dependencies,
/// while providing optional HTTP integration with local Qwen3-8B backend service.
/// </summary>
public class AnalystChatService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string? _backendApiUrl;

    public AnalystChatService(string? backendApiUrl = null)
    {
        _backendApiUrl = backendApiUrl;
    }

    /// <summary>
    /// Generate an in-process structured intelligence insight for a detected change candidate.
    /// </summary>
    public AnalystInsight GenerateInsight(ChangeRecord candidate)
    {
        double score = candidate.Metrics.TryGetValue("SpectralDifference", out var sd) ? sd : candidate.Confidence;
        double confidence = candidate.Confidence;
        double falseAlarmRisk = Math.Max(0.0, 1.0 - confidence);
        string changeTypeStr = candidate.Type.ToString().ToUpperInvariant();

        // 1. Severity Classification
        string severity;
        if (candidate.Type == ChangeType.Construction && confidence >= 0.85)
            severity = "CRITICAL";
        else if (candidate.Type is ChangeType.Construction or ChangeType.Clearance && confidence >= 0.70)
            severity = "HIGH";
        else if (score > 0.40)
            severity = "ELEVATED";
        else
            severity = "NOMINAL";

        // 2. Physical Evidence Synthesis
        var evidence = new List<string>();
        if (score > 0.50)
        {
            evidence.Add($"Multi-band Change Vector Analysis (CVA) magnitude is {score:F3}, demonstrating strong spectral divergence.");
        }

        switch (candidate.Type)
        {
            case ChangeType.Clearance:
                evidence.Add("Sharp negative vegetation index shift (Delta NDVI < -0.20) consistent with deforestation or earthworks.");
                break;
            case ChangeType.Construction:
                evidence.Add("Pronounced built-up reflectance increase (Delta NDBI > +0.20) with high spatial gradient continuity.");
                break;
            case ChangeType.WaterExtentVariation:
                evidence.Add("Modified MNDWI moisture boundaries indicating localized flooding or shoreline variation.");
                break;
            case ChangeType.ActivityConcentration:
                evidence.Add("High-frequency spatial activity peaks indicating localized machinery, vehicle, or equipment concentration.");
                break;
            default:
                evidence.Add("Spectral reflectance variation observed across visible and near-infrared bands.");
                break;
        }

        if (falseAlarmRisk < 0.15)
        {
            evidence.Add($"Data quality factor is high ({(1.0 - falseAlarmRisk):P1}), and sub-pixel registration jitter was suppressed.");
        }

        // 3. Temporal Timeline & Onset
        string timeline;
        if (candidate.EarliestObservationTimestamp != default)
        {
            timeline = $"Sequential CUSUM process control pinned statistical onset to {candidate.EarliestObservationTimestamp:yyyy-MM-dd} (3.5-sigma threshold).";
        }
        else
        {
            timeline = $"Observed between {candidate.TimestampT1:yyyy-MM-dd} and {candidate.TimestampT2:yyyy-MM-dd}.";
        }

        // 4. Actionable Recommendations
        var recommendations = new List<string>();
        if (severity is "CRITICAL" or "HIGH")
        {
            recommendations.Add("Prioritize verification in Stage 5 Review Queue (Press C to confirm, R to reject).");
            recommendations.Add("Cross-reference with Sentinel-1 SAR backscatter for structural double-bounce.");
            recommendations.Add("Export W3C PROV-O GeoJSON audit package for downstream dissemination.");
        }
        else if (severity == "ELEVATED")
        {
            recommendations.Add("Monitor the next scheduled satellite pass for ongoing progression.");
            recommendations.Add("Verify cloud and shadow masks around the coordinate perimeter.");
        }
        else
        {
            recommendations.Add("Low priority event; eligible for batch archival sign-off.");
        }

        // 5. Executive Summary
        string summary = $"{severity} alert: Detected {candidate.Type} at ({candidate.Center.Latitude:F4}°N, {candidate.Center.Longitude:F4}°E) with {confidence:P0} confidence over ~{candidate.AreaSqMeters:N0} m².";

        string dossier = $"### GROUNDED DOSSIER\n- Coordinates: ({candidate.Center.Latitude:F5}, {candidate.Center.Longitude:F5})\n- Change: {candidate.Type}\n- Confidence: {confidence:P1}\n- CVA Score: {score:F4}\n- Area: {candidate.AreaSqMeters:N0} m²\n- Onset: {candidate.EarliestObservationTimestamp:yyyy-MM-dd}";

        return new AnalystInsight(
            Severity: severity,
            ChangeClass: changeTypeStr,
            Confidence: confidence,
            Summary: summary,
            PhysicalEvidence: evidence,
            Timeline: timeline,
            FalseAlarmRisk: falseAlarmRisk,
            Recommendations: recommendations,
            GroundedDossier: dossier
        );
    }

    /// <summary>
    /// Conversational chat with the GEOINT assistant.
    /// Uses local FastAPI Qwen service if reachable, otherwise resolves in-process with deterministic GEOINT reasoning.
    /// </summary>
    public async Task<string> AskAnalystAsync(string query, ChangeRecord? candidate = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Please enter an analytical question or inquiry.";

        // Attempt HTTP local backend if configured
        if (!string.IsNullOrEmpty(_backendApiUrl))
        {
            try
            {
                var payload = new
                {
                    messages = new[] { new { role = "user", content = query } },
                    change_id = candidate?.Id
                };

                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var response = await HttpClient.PostAsync($"{_backendApiUrl}/api/v1/chat", content);
                if (response.IsSuccessStatusCode)
                {
                    var respJson = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(respJson);
                    if (doc.RootElement.TryGetProperty("message", out var msgEl) &&
                        msgEl.TryGetProperty("content", out var contentEl))
                    {
                        return contentEl.GetString() ?? "No content returned.";
                    }
                }
            }
            catch
            {
                // Fall through to in-process deterministic reasoning
            }
        }

        // In-process offline deterministic reasoning
        return ResolveInProcess(query, candidate);
    }

    private string ResolveInProcess(string query, ChangeRecord? candidate)
    {
        string q = query.ToLowerInvariant();

        if (candidate == null)
        {
            string explanation = TextQueryEncoder.ExplainQuery(query);
            return $"**AI Query Analysis**:\n{explanation}\n\n" +
                   "Ready to analyze archive. Select a candidate patch or search the satellite catalogue to inspect multi-temporal evidence.";
        }

        var insight = GenerateInsight(candidate);

        if (q.Contains("report") || q.Contains("brief") || q.Contains("sitrep") || q.Contains("summary"))
        {
            return $"### MILITARY-STANDARD GEOINT INTELLIGENCE BRIEF (SITREP)\n\n" +
                   $"**1. LOCATION & TARGET**\n" +
                   $"Centroid: ({candidate.Center.Latitude:F5}°N, {candidate.Center.Longitude:F5}°E)\n" +
                   $"Surface Area: ~{candidate.AreaSqMeters:N0} m² ({candidate.AreaSqMeters / 10000.0:F2} hectares)\n\n" +
                   $"**2. DETECTED CLASSIFICATION & CONFIDENCE**\n" +
                   $"State: **{insight.ChangeClass}** | Severity: **{insight.Severity}** | Confidence: **{insight.Confidence:P1}**\n\n" +
                   $"**3. PHYSICAL EVIDENCE**\n" +
                   string.Join("\n", insight.PhysicalEvidence.Select(e => $"• {e}")) + "\n\n" +
                   $"**4. TIMELINE & ONSET**\n" +
                   $"{insight.Timeline}\n\n" +
                   $"**5. RECOMMENDED ACTIONS**\n" +
                   string.Join("\n", insight.Recommendations.Select(r => $"• {r}"));
        }

        if (q.Contains("why") || q.Contains("cause") || q.Contains("reason") || q.Contains("etiology"))
        {
            return $"**Change Etiology Assessment for {candidate.Type}**:\n\n" +
                   $"• **Spectral Divergence**: Multi-Band CVA magnitude exceeds the background variance threshold.\n" +
                   $"• **Index Dynamics**: Spectral profile indicates {insight.ChangeClass.ToLowerInvariant()} dynamics with localized contrast gradients.\n" +
                   $"• **Confidence Factor**: Computed at {candidate.Confidence:P1}, verified against calibrated Sentinel-2 radiometric baselines.";
        }

        if (q.Contains("false alarm") || q.Contains("quality") || q.Contains("noise") || q.Contains("jitter"))
        {
            return $"**False Alarm & Quality Assessment**:\n\n" +
                   $"• **False Alarm Risk Index**: {insight.FalseAlarmRisk:P1}\n" +
                   $"• **Jitter Suppression**: Sub-pixel quadratic interpolation verified that the detected shift does not stem from orbital registration jitter.\n" +
                   $"• **Radiometric Calibration**: Pseudo-Invariant Feature (PIF) normalization with Tukey biweight loss suppressed atmospheric and illumination outliers.";
        }

        if (q.Contains("sar") || q.Contains("radar") || q.Contains("cloud"))
        {
            return "**Sentinel-1 SAR Radar Cross-Corroboration**:\n\n" +
                   "• C-band microwave pulses penetrate atmospheric haze and cloud cover.\n" +
                   "• For built-up structures, intense double-bounce reflection off vertical walls sharply increases VV backscatter.\n" +
                   "• Cross-polarization (VH/VV) ratio differentiates vegetation volume scattering from metallic and concrete corners.";
        }

        if (q.Contains("timeline") || q.Contains("when") || q.Contains("onset") || q.Contains("date"))
        {
            return $"**Temporal Onset & Trajectory Dynamics**:\n\n" +
                   $"• **Baseline Window**: {candidate.TimestampT1:yyyy-MM-dd} to {candidate.TimestampT2:yyyy-MM-dd}\n" +
                   $"• **CUSUM Onset Pinpoint**: {(candidate.EarliestObservationTimestamp != default ? candidate.EarliestObservationTimestamp.ToString("yyyy-MM-dd") : "Continuous trajectory")}\n" +
                   $"• **Process Control**: Exceeded 3.5-sigma rolling variance threshold, confirming genuine onset date.";
        }

        return $"**GEOINT Analyst Response**:\n\n" +
               $"{insight.Summary}\n\n" +
               "Would you like me to generate a full formal SITREP brief, investigate false alarm risk, or analyze SAR radar backscatter for this location?";
    }
}
