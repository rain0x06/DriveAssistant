using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace FATXTools.Wpf;

internal static class SettingsInsetSmokeRunner
{
    private const string SmokeFlag = "--settings-inset-smoke";
    private const string OutputFlag = "--settings-inset-output";
    private const double TargetInset = 11.0;
    private const double Tolerance = 0.6;

    internal static bool IsRequested(string[] args)
    {
        return args.Any(arg => string.Equals(arg, SmokeFlag, StringComparison.OrdinalIgnoreCase));
    }

    internal static void Run(Application app, string[] args)
    {
        var outputPath = ResolveOutputPath(args);
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var tracePath = Path.Combine(outputDirectory ?? AppContext.BaseDirectory, "settings-inset-smoke.trace.log");
        AppendTrace(tracePath, $"runner_start output={outputPath}");
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var window = new SettingsWindow(AppSettings.Load())
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            Opacity = 0
        };
        AppendTrace(tracePath, "window_created");

        window.Loaded += (_, _) =>
        {
            AppendTrace(tracePath, "window_loaded");
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    AppendTrace(tracePath, "dispatcher_idle_start");
                    var measurements = window.CollectInsetMeasurements();
                    AppendTrace(tracePath, $"measurements={JsonSerializer.Serialize(measurements)}");
                    var textboxFailures = measurements
                        .Where(entry => entry.Key.StartsWith("textbox.", StringComparison.OrdinalIgnoreCase))
                        .Where(entry => Math.Abs(entry.Value - TargetInset) > Tolerance)
                        .ToArray();

                    var payload = new
                    {
                        targetInset = TargetInset,
                        tolerance = Tolerance,
                        measurements,
                        passed = textboxFailures.Length == 0,
                        failures = textboxFailures.Select(f => new
                        {
                            field = f.Key,
                            expected = TargetInset,
                            actual = f.Value
                        }).ToArray()
                    };

                    File.WriteAllText(outputPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                    AppendTrace(tracePath, "output_written");

                    window.Close();
                    AppendTrace(tracePath, $"shutdown code={(textboxFailures.Length == 0 ? 0 : 2)}");
                    app.Shutdown(textboxFailures.Length == 0 ? 0 : 2);
                }
                catch (Exception ex)
                {
                    AppendTrace(tracePath, $"runner_error={ex}");
                    var payload = new
                    {
                        passed = false,
                        error = ex.ToString()
                    };
                    File.WriteAllText(outputPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                    window.Close();
                    app.Shutdown(3);
                }
            }), DispatcherPriority.Background);
        };

        window.Show();
        AppendTrace(tracePath, "window_show_called");
    }

    private static string ResolveOutputPath(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], OutputFlag, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return Path.Combine(Path.GetTempPath(), $"driveassistant-settings-inset-smoke-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.json");
    }

    private static void AppendTrace(string path, string message)
    {
        try
        {
            File.AppendAllText(path, $"{DateTime.Now:O} :: {message}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort tracing only.
        }
    }
}
