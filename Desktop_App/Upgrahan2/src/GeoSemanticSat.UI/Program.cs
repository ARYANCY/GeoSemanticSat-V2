using System;
using System.Linq;
using Avalonia;

namespace GeoSemanticSat.UI;

class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Check for explicit CLI invocation or standard CLI commands
        if (args.Length > 0)
        {
            var first = args[0].ToLowerInvariant();
            if (first is "--cli" or "-c")
            {
                var cliArgs = args.Skip(1).ToArray();
                return GeoSemanticSat.Cli.Program.Main(cliArgs);
            }
            if (first is "--help" or "-h" or "help" or "benchmark" or "index" or "search" or "search-change" or "detect")
            {
                return GeoSemanticSat.Cli.Program.Main(args);
            }
        }

        // Headless detection on Linux: if no display server is found, fallback to CLI
        if (OperatingSystem.IsLinux())
        {
            var display = Environment.GetEnvironmentVariable("DISPLAY");
            var wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
            if (string.IsNullOrEmpty(display) && string.IsNullOrEmpty(wayland))
            {
                Console.WriteLine("===============================================================================");
                Console.WriteLine("  GeoSemanticSat - AI Satellite Semantic Retrieval & Change Detection");
                Console.WriteLine("  (No X11 or Wayland display server detected - falling back to CLI mode)");
                Console.WriteLine("===============================================================================");
                Console.WriteLine("To run GUI: set DISPLAY or execute in a desktop session.");
                Console.WriteLine("To run CLI commands: pass --cli <command> (e.g. --cli benchmark, --cli detect)");
                Console.WriteLine();
                return GeoSemanticSat.Cli.Program.Main(args.Length > 0 ? args : new[] { "--help" });
            }
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

