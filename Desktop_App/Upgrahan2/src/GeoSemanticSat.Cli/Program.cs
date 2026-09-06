using System;
using System.IO;
using System.Linq;
using GeoSemanticSat.Core.ChangeDetection;
using GeoSemanticSat.Core.Clustering;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Raster;
using GeoSemanticSat.Core.VectorIndex;
using GeoSemanticSat.Core.Workflow;
using GeoSemanticSat.Engine.Retrieval;

using GeoSemanticSat.Engine.Benchmark;

namespace GeoSemanticSat.Cli;

public class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintUsage();
            return 0;
        }

        string command = args[0].ToLowerInvariant();

        try
        {
            switch (command)
            {
                case "benchmark":
                    string benchOut = args.Length > 1 ? args[1] : Path.Combine(Directory.GetCurrentDirectory(), "benchmark_results");
                    BenchmarkRunner.Run(benchOut);
                    return 0;

                case "index":
                    if (args.Length < 3)
                    {
                        Console.WriteLine("Usage: dotnet run --project src/GeoSemanticSat.Cli -- index <inputDirectory> <outputIndexFile>");
                        return 1;
                    }
                    RunIndex(args[1], args[2]);
                    return 0;

                case "search":
                    if (args.Length < 3)
                    {
                        Console.WriteLine("Usage: dotnet run --project src/GeoSemanticSat.Cli -- search <indexFile> \"<queryText>\" [topK]");
                        return 1;
                    }
                    int topK = args.Length > 3 ? int.Parse(args[3]) : 5;
                    RunSearch(args[1], args[2], topK);
                    return 0;

                case "search-change":
                    RunSearchChange(args);
                    return 0;

                case "detect":
                    if (args.Length < 4)
                    {
                        Console.WriteLine("Usage: dotnet run --project src/GeoSemanticSat.Cli -- detect <tileT1.tif> <tileT2.tif> <outGeoJson>");
                        return 1;
                    }
                    RunDetect(args[1], args[2], args[3]);
                    return 0;

                default:
                    Console.WriteLine($"Unknown command: {command}");
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error executing command '{command}': {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    private static void RunIndex(string inputDir, string outputIndex)
    {
        Console.WriteLine($"Indexing GeoTIFF rasters from: {inputDir}");
        var files = Directory.GetFiles(inputDir, "*.tif", SearchOption.AllDirectories);
        var index = new VectorIndex(128);
        var engine = new SemanticSearchEngine(index);

        int totalPatches = 0;
        foreach (var f in files)
        {
            var tile = GeoTiffReader.Read(f);
            int count = engine.IngestTile(tile);
            totalPatches += count;
            Console.WriteLine($"  Indexed: {Path.GetFileName(f)} -> {count} patches.");
        }

        index.SaveIndex(outputIndex);
        Console.WriteLine($"Saved vector index with {totalPatches} patches to: {outputIndex}");
    }

    private static void RunSearch(string indexFile, string query, int topK)
    {
        var index = VectorIndex.LoadIndex(indexFile);
        var engine = new SemanticSearchEngine(index);
        var results = engine.SearchByText(query, topK);

        Console.WriteLine($"Search results for: \"{query}\" (Top {topK}):");
        int rank = 1;
        foreach (var r in results)
        {
            Console.WriteLine($"  {rank++}. Patch: {r.Patch.PatchId} | Sim: {r.SimilarityScore:F4} | Bounds: {r.Patch.Bounds} | Date: {r.Patch.Timestamp:yyyy-MM-dd}");
        }
    }

    private static void RunDetect(string t1Path, string t2Path, string outGeoJson)
    {
        var t1 = GeoTiffReader.Read(t1Path);
        var t2 = GeoTiffReader.Read(t2Path);

        Console.WriteLine($"Running multi-temporal change detection ({t1.TileId} -> {t2.TileId})...");
        var changes = MultiTemporalChangeDetector.DetectChanges(t1, t2);
        Console.WriteLine($"Found {changes.Count} detected changes.");

        ProvenanceAuditTrail.SaveGeoJson(outGeoJson, changes);
        Console.WriteLine($"Exported GeoJSON provenance to: {outGeoJson}");
    }

    private static void RunSearchChange(string[] args)
    {
        double? lat = null;
        double? lon = null;
        double? radius = null;
        ChangeType? changeType = null;
        DateTime? startDate = null;
        DateTime? endDate = null;
        string? keyword = null;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--lat" && i + 1 < args.Length) lat = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            else if (args[i] == "--lon" && i + 1 < args.Length) lon = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            else if (args[i] == "--radius-km" && i + 1 < args.Length) radius = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            else if (args[i] == "--type" && i + 1 < args.Length && Enum.TryParse<ChangeType>(args[++i], true, out var ct)) changeType = ct;
            else if (args[i] == "--start" && i + 1 < args.Length) startDate = DateTime.Parse(args[++i]);
            else if (args[i] == "--end" && i + 1 < args.Length) endDate = DateTime.Parse(args[++i]);
            else if (args[i] == "--keyword" && i + 1 < args.Length) keyword = args[++i];
        }

        Console.WriteLine($"Executing Advanced Change Search:");
        if (lat.HasValue && lon.HasValue) Console.WriteLine($"  Target Coordinate: ({lat.Value:F5}°N, {lon.Value:F5}°E), Radius: {radius ?? 10.0} km");
        if (changeType.HasValue) Console.WriteLine($"  Target Change Type: {changeType.Value}");
        if (startDate.HasValue || endDate.HasValue) Console.WriteLine($"  Time Window: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");

        string t1Tif = Path.Combine(Directory.GetCurrentDirectory(), "benchmark_results", "scene_t1.tif");
        string t3Tif = Path.Combine(Directory.GetCurrentDirectory(), "benchmark_results", "scene_t3.tif");
        List<ChangeRecord> changes;

        if (File.Exists(t1Tif) && File.Exists(t3Tif))
        {
            var t1 = GeoTiffReader.Read(t1Tif, SensorPlatform.Sentinel2_Optical, new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc));
            var t3 = GeoTiffReader.Read(t3Tif, SensorPlatform.Sentinel2_Optical, new DateTime(2024, 3, 20, 0, 0, 0, DateTimeKind.Utc));
            changes = MultiTemporalChangeDetector.DetectChanges(t1, t3);
        }
        else
        {
            // Run benchmark to stage scenes
            BenchmarkRunner.Run(Path.Combine(Directory.GetCurrentDirectory(), "benchmark_results"));
            var t1 = GeoTiffReader.Read(t1Tif, SensorPlatform.Sentinel2_Optical, new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc));
            var t3 = GeoTiffReader.Read(t3Tif, SensorPlatform.Sentinel2_Optical, new DateTime(2024, 3, 20, 0, 0, 0, DateTimeKind.Utc));
            changes = MultiTemporalChangeDetector.DetectChanges(t1, t3);
        }

        var searchEngine = new ChangeSearchEngine(changes);

        GeoCoordinate? center = (lat.HasValue && lon.HasValue) ? new GeoCoordinate(lat.Value, lon.Value) : null;
        var criteria = new ChangeSearchCriteria(
            Center: center,
            RadiusKm: radius ?? (center.HasValue ? 15.0 : null),
            TargetChangeType: changeType,
            StartDate: startDate,
            EndDate: endDate,
            Keyword: keyword
        );

        var results = searchEngine.Search(criteria, topK: 10);
        Console.WriteLine($"\nFound {results.Count} matching change sites:");
        if (results.Count == 0)
        {
            Console.WriteLine("  [!] NO SATELLITE COVERAGE OR ZERO DETECTIONS FOR QUERIED SPATIOTEMPORAL CRITERIA.");
            Console.WriteLine("      Active Indexed Footprint: New Delhi AOI (28.5844°N - 28.6100°N, 77.2000°E - 77.2256°E)");
            Console.WriteLine("      Available Acquisition Epochs: 2024-01-10 to 2024-04-25");
            if (center.HasValue)
            {
                double distKm = center.Value.DistanceToKm(new GeoCoordinate(28.6050, 77.2080));
                if (distKm > (radius ?? 15.0) + 15.0)
                {
                    Console.WriteLine($"      Notice: Requested coordinate ({center.Value.Latitude:F4}°N, {center.Value.Longitude:F4}°E) is {distKm:F0} km away from indexed coverage.");
                    Console.WriteLine("      To ingest GeoTIFF imagery for this region: GeoSemanticSat index <raster-directory> <index.bin>");
                }
            }
            if (startDate.HasValue && startDate.Value > new DateTime(2024, 4, 25) || endDate.HasValue && endDate.Value < new DateTime(2024, 1, 10))
            {
                Console.WriteLine("      Notice: Requested observation dates lie completely outside the archive epoch time series.");
            }
        }
        else
        {
            int rank = 1;
            foreach (var r in results)
            {
                Console.WriteLine($"  #{rank++} [{r.Record.Type}] Candidate {r.Record.Id[..8]} | Rel: {r.RelevanceScore:F3} | Conf: {(r.Record.Confidence * 100):F1}% | Dist: {r.DistanceKm:F2} km | Center: {r.Record.Center} | Date: {r.Record.TimestampT2:yyyy-MM-dd}");
            }
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("GeoSemanticSat CLI - Semantic Retrieval & Multi-Temporal Change Analysis");
        Console.WriteLine("Usage:");
        Console.WriteLine("  benchmark [outputDir]                                Run full automated evaluation suite");
        Console.WriteLine("  index <inputDir> <outputIndex.bin>                   Index a directory of GeoTIFF scenes");
        Console.WriteLine("  search <index.bin> \"<query>\" [topK]                  Execute semantic natural language search");
        Console.WriteLine("  detect <t1.tif> <t2.tif> <output.geojson>            Execute change analysis between epochs");
        Console.WriteLine("  search-change [--lat X] [--lon Y] [--radius-km R] [--type Construction] [--start YYYY-MM-DD] [--end YYYY-MM-DD]");
    }
}
