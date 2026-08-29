using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace ShortsPrep;

public record BassPeak(double TimeSeconds, double Strength01);

/// <summary>
/// Extrait une enveloppe d'énergie des basses (bande ~40-150 Hz) de l'audio via ffmpeg
/// (bandpass + astats, mesuré toutes les 50ms), puis détecte les "coups" (kicks/basses
/// marquées) par une détection de pics simple. Sert à faire réagir le zoom au rythme.
/// Technique validée : le filtre crop/scale de ffmpeg réévalue ses expressions par frame
/// (avec eval=frame pour scale), donc les temps de pics peuvent être injectés directement
/// comme constantes dans l'expression du filtre, sans dépendance externe au rendu.
/// </summary>
public class BassAnalyzer
{
    public async Task<List<BassPeak>> AnalyzePeaksAsync(string audioPath, double sensitivity01)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"shortsprep_rms_{Guid.NewGuid():N}.txt");
        try
        {
            var args =
                $"-i \"{audioPath}\" -af \"aresample=44100,bandpass=f=80:width_type=h:w=100," +
                $"asetnsamples=n=2205,astats=metadata=1:reset=1," +
                $"ametadata=mode=print:key=lavfi.astats.Overall.RMS_level:file='{tempFile}'\" -f null -";

            await RunSilentAsync(FfmpegManager.FfmpegExe, args);

            if (!File.Exists(tempFile)) return new List<BassPeak>();

            var (times, amps) = ParseRmsFile(tempFile);
            return PickPeaks(times, amps, sensitivity01);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    private static (List<double> Times, List<double> Amps) ParseRmsFile(string path)
    {
        var times = new List<double>();
        var amps = new List<double>();

        var lines = File.ReadAllLines(path);
        var timeRegex = new Regex(@"pts_time:([\d\.]+)");
        var valueRegex = new Regex(@"RMS_level=(-?[\d\.]+|-inf|inf|nan)");

        double? pendingTime = null;
        foreach (var line in lines)
        {
            var tMatch = timeRegex.Match(line);
            if (tMatch.Success)
            {
                pendingTime = double.Parse(tMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }
            var vMatch = valueRegex.Match(line);
            if (vMatch.Success && pendingTime.HasValue)
            {
                var raw = vMatch.Groups[1].Value;
                double db = raw is "-inf" or "nan" ? -100.0 : (raw == "inf" ? 0.0 : double.Parse(raw, CultureInfo.InvariantCulture));
                double linear = Math.Pow(10, db / 20.0);
                times.Add(pendingTime.Value);
                amps.Add(linear);
                pendingTime = null;
            }
        }
        return (times, amps);
    }

    private static List<BassPeak> PickPeaks(List<double> times, List<double> amps, double sensitivity01)
    {
        var peaks = new List<BassPeak>();
        if (amps.Count < 3) return peaks;

        // Lissage léger (moyenne mobile sur 3 points) pour réduire le bruit.
        var smoothed = new double[amps.Count];
        for (int i = 0; i < amps.Count; i++)
        {
            double a = amps[Math.Max(0, i - 1)];
            double b = amps[i];
            double c = amps[Math.Min(amps.Count - 1, i + 1)];
            smoothed[i] = (a + b + c) / 3.0;
        }

        double mean = smoothed.Average();
        double std = Math.Sqrt(smoothed.Select(v => (v - mean) * (v - mean)).Average());
        double max = smoothed.Max();

        // Sensibilité 0..1 -> seuil plus bas (plus de pics détectés) quand elle augmente.
        double thresholdFactor = 1.6 - 1.1 * Math.Clamp(sensitivity01, 0, 1); // 1.6 (peu sensible) -> 0.5 (très sensible)
        double threshold = mean + thresholdFactor * std;

        double minGapSeconds = 0.18; // évite de déclencher deux fois le même coup de basse
        double lastPeakTime = -999;

        for (int i = 1; i < smoothed.Length - 1; i++)
        {
            bool isLocalMax = smoothed[i] >= smoothed[i - 1] && smoothed[i] >= smoothed[i + 1];
            if (isLocalMax && smoothed[i] > threshold && times[i] - lastPeakTime >= minGapSeconds)
            {
                double strength = max > mean ? Math.Clamp((smoothed[i] - mean) / (max - mean), 0.2, 1.0) : 0.5;
                peaks.Add(new BassPeak(times[i], strength));
                lastPeakTime = times[i];
            }
        }

        // Garde-fou : si trop de pics (morceau très dense), ne garder que les plus forts
        // pour que l'expression ffmpeg générée reste raisonnable.
        const int maxPeaks = 180;
        if (peaks.Count > maxPeaks)
        {
            peaks = peaks.OrderByDescending(p => p.Strength01).Take(maxPeaks)
                         .OrderBy(p => p.TimeSeconds).ToList();
        }

        return peaks;
    }

    private static async Task RunSilentAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi)!;
        await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
    }
}
