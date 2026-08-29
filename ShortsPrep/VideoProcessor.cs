using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShortsPrep;

public enum QualityMode
{
    /// <summary>CRF 16, preset slow : aucune perte visible à l'écran, taille raisonnable.</summary>
    VisuallyLossless,
    /// <summary>CRF 0 (x264) : perte mathématiquement nulle, fichiers énormes.</summary>
    TrueLossless,
    /// <summary>Ne ré-encode pas la vidéo si elle est déjà au bon format/ratio (le plus rapide, zéro perte).</summary>
    CopyWhenPossible
}

public enum LosslessAudioFormat { Wav, Flac }

public enum Orientation { Portrait9x16, Landscape16x9 }

public record VideoInfo(int Width, int Height, double DurationSeconds, string VideoCodec, bool HasAudio);

/// <summary>Réglages du mouvement de caméra / réactivité aux basses pour le mode image + son.</summary>
public record MotionSettings(
    bool KenBurnsEnabled, double KenBurnsIntensity01,
    bool BassReactiveEnabled, double BassSensitivity01)
{
    public static readonly MotionSettings None = new(false, 0, false, 0);
    public bool IsAnyEnabled => KenBurnsEnabled || BassReactiveEnabled;
}

/// <summary>Portion de la source à conserver (recadrage temporel), en secondes.</summary>
public record TrimRange(double StartSeconds, double EndSeconds)
{
    public double Duration => Math.Max(0, EndSeconds - StartSeconds);
}

public class VideoProcessor
{
    private static readonly Regex TimeRegex = new(@"time=(\d+):(\d+):(\d+\.\d+)", RegexOptions.Compiled);
    private readonly BassAnalyzer _bassAnalyzer = new();

    // Format "shorts" (portrait) : limite dure à 60s pour coller aux contraintes des plateformes.
    private const double PortraitMaxDurationSeconds = 60.0;

    /// <summary>Interroge ffprobe pour connaître les caractéristiques du fichier source.</summary>
    public async Task<VideoInfo> ProbeAsync(string inputPath)
    {
        var args = $"-v quiet -print_format json -show_format -show_streams \"{inputPath}\"";
        var json = await RunAndCaptureAsync(FfmpegManager.FfprobeExe, args);
        using var doc = JsonDocument.Parse(json);

        int width = 0, height = 0;
        string videoCodec = "";
        bool hasAudio = false;
        double duration = doc.RootElement.GetProperty("format").GetProperty("duration")
            .GetString() is string d ? double.Parse(d, CultureInfo.InvariantCulture) : 0;

        foreach (var stream in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            var type = stream.GetProperty("codec_type").GetString();
            if (type == "video" && width == 0)
            {
                width = stream.GetProperty("width").GetInt32();
                height = stream.GetProperty("height").GetInt32();
                videoCodec = stream.GetProperty("codec_name").GetString() ?? "";
            }
            else if (type == "audio")
            {
                hasAudio = true;
            }
        }

        return new VideoInfo(width, height, duration, videoCodec, hasAudio);
    }

    /// <summary>
    /// Extrait la piste audio d'origine, intégralement sans perte, dans un fichier séparé
    /// (WAV PCM ou FLAC). C'est la copie "maître" à garder pour un usage musical.
    /// </summary>
    public async Task ExtractLosslessAudioAsync(
        string inputPath, string outputPath, LosslessAudioFormat format,
        IProgress<int>? percentProgress = null)
    {
        var info = await ProbeAsync(inputPath);
        var codecArgs = format == LosslessAudioFormat.Wav ? "-c:a pcm_s24le" : "-c:a flac -compression_level 8";
        var args = $"-y -i \"{inputPath}\" -vn {codecArgs} \"{outputPath}\"";
        await RunAsync(FfmpegManager.FfmpegExe, args, info.DurationSeconds, percentProgress);
    }

    /// <summary>
    /// Convertit la vidéo au format choisi (portrait 9:16 ou paysage 16:9). Les sorties
    /// portrait sont automatiquement limitées à 60s (contrainte shorts/reels/stories).
    /// </summary>
    public async Task ConvertToPortraitAsync(
        string inputPath,
        string outputPath,
        PlatformProfile profile,
        QualityMode quality,
        VideoInfo info,
        Orientation orientation,
        TrimRange? trim = null,
        IProgress<string>? progress = null,
        IProgress<int>? percentProgress = null)
    {
        var (tw, th) = GetDimensions(orientation);
        string videoFilter = BuildAspectFilter(info, tw, th);

        string videoCodecArgs = quality switch
        {
            QualityMode.TrueLossless => "-c:v libx264 -preset veryslow -crf 0 -pix_fmt yuv420p",
            QualityMode.VisuallyLossless => "-c:v libx264 -preset slow -crf 16 -pix_fmt yuv420p",
            QualityMode.CopyWhenPossible when IsAlreadyCompatible(info, tw, th)
                => "-c:v copy",
            _ => "-c:v libx264 -preset slow -crf 16 -pix_fmt yuv420p"
        };

        bool copyingVideo = videoCodecArgs.Contains("copy");
        string filterArgs = copyingVideo ? "" : $"-vf \"{videoFilter}\"";
        string audioArgs = info.HasAudio ? "-c:a aac -b:a 320k -ar 48000" : "-an";

        double sourceDuration = trim?.Duration ?? info.DurationSeconds;
        double effectiveDuration = orientation == Orientation.Portrait9x16
            ? Math.Min(sourceDuration, PortraitMaxDurationSeconds) : sourceDuration;

        string seekArgs = trim is not null ? $"-ss {trim.StartSeconds.ToString(CultureInfo.InvariantCulture)} " : "";
        string durationArgs = $"-t {effectiveDuration.ToString(CultureInfo.InvariantCulture)} ";

        var args = $"-y {seekArgs}-i \"{inputPath}\" {filterArgs} {videoCodecArgs} {audioArgs} {durationArgs}" +
                   $"-movflags +faststart \"{outputPath}\"";

        progress?.Report($"Encodage pour {profile.Name}...");
        await RunAsync(FfmpegManager.FfmpegExe, args, effectiveDuration, percentProgress);
    }

    /// <summary>
    /// Crée une vidéo à partir d'une image fixe et d'une piste audio, avec option de
    /// mouvement de caméra (Ken Burns) et de réaction du zoom aux basses. Les sorties
    /// portrait sont limitées à 60s.
    /// </summary>
    public async Task CreateFromImageAndAudioAsync(
        string imagePath,
        string audioPath,
        string outputPath,
        PlatformProfile profile,
        QualityMode quality,
        Orientation orientation,
        MotionSettings? motion = null,
        TrimRange? trim = null,
        IProgress<string>? progress = null,
        IProgress<int>? percentProgress = null)
    {
        motion ??= MotionSettings.None;
        var imageInfo = await ProbeAsync(imagePath);
        var audioInfo = await ProbeAsync(audioPath);
        var (tw, th) = GetDimensions(orientation);

        double sourceDuration = trim?.Duration ?? audioInfo.DurationSeconds;
        double effectiveDuration = orientation == Orientation.Portrait9x16
            ? Math.Min(sourceDuration, PortraitMaxDurationSeconds) : sourceDuration;

        List<BassPeak> peaks = new();
        if (motion.BassReactiveEnabled)
        {
            progress?.Report("Analyse des basses de l'audio...");
            peaks = await _bassAnalyzer.AnalyzePeaksAsync(audioPath, motion.BassSensitivity01);
            progress?.Report($"{peaks.Count} pics de basses détectés.");
        }

        string videoFilter = motion.IsAnyEnabled
            ? BuildMotionFilter(imageInfo, tw, th, motion, peaks, effectiveDuration)
            : BuildAspectFilter(imageInfo, tw, th);

        string videoCodecArgs = quality == QualityMode.TrueLossless
            ? $"-c:v libx264 -preset veryslow -crf 0{(motion.IsAnyEnabled ? "" : " -tune stillimage")} -pix_fmt yuv420p"
            : $"-c:v libx264 -preset slow -crf 16{(motion.IsAnyEnabled ? "" : " -tune stillimage")} -pix_fmt yuv420p";

        string seekArgs = trim is not null ? $"-ss {trim.StartSeconds.ToString(CultureInfo.InvariantCulture)} " : "";

        var args =
            $"-y -loop 1 -framerate 30 -i \"{imagePath}\" {seekArgs}-i \"{audioPath}\" " +
            $"-vf \"{videoFilter}\" {videoCodecArgs} " +
            $"-c:a aac -b:a 320k -ar 48000 " +
            $"-t {effectiveDuration.ToString(CultureInfo.InvariantCulture)} -movflags +faststart \"{outputPath}\"";

        progress?.Report($"Création de la vidéo image + audio pour {profile.Name}...");
        await RunAsync(FfmpegManager.FfmpegExe, args, effectiveDuration, percentProgress);
    }

    /// <summary>
    /// Fichier "maître" 100% sans perte : image fixe + audio intégré en FLAC (sans perte,
    /// bit-exact), vidéo encodée en CRF 0. Conteneur .mkv. Pas de limite de durée (archive).
    /// </summary>
    public async Task CreateLosslessMasterAsync(
        string imagePath,
        string audioPath,
        string outputPath,
        Orientation orientation,
        MotionSettings? motion = null,
        IProgress<string>? progress = null,
        IProgress<int>? percentProgress = null)
    {
        motion ??= MotionSettings.None;
        var imageInfo = await ProbeAsync(imagePath);
        var audioInfo = await ProbeAsync(audioPath);
        var (tw, th) = GetDimensions(orientation);

        List<BassPeak> peaks = new();
        if (motion.BassReactiveEnabled)
        {
            progress?.Report("Analyse des basses de l'audio...");
            peaks = await _bassAnalyzer.AnalyzePeaksAsync(audioPath, motion.BassSensitivity01);
        }

        string videoFilter = motion.IsAnyEnabled
            ? BuildMotionFilter(imageInfo, tw, th, motion, peaks, audioInfo.DurationSeconds)
            : BuildAspectFilter(imageInfo, tw, th);

        string tuneArgs = motion.IsAnyEnabled ? "" : " -tune stillimage";

        var args =
            $"-y -loop 1 -framerate 30 -i \"{imagePath}\" -i \"{audioPath}\" " +
            $"-vf \"{videoFilter}\" " +
            $"-c:v libx264 -preset veryslow -crf 0{tuneArgs} -pix_fmt yuv420p " +
            $"-c:a flac -compression_level 8 " +
            $"-shortest \"{outputPath}\"";

        progress?.Report("Encodage du fichier maître (sans perte)...");
        await RunAsync(FfmpegManager.FfmpegExe, args, audioInfo.DurationSeconds, percentProgress);
    }

    /// <summary>
    /// Combine une image et un son en vidéo, exactement selon la commande de référence :
    /// ffmpeg -loop 1 -i image -i son -shortest -c:a copy -strict -2 sortie.mp4
    /// </summary>
    public async Task CreateSimpleFromImageAndAudioAsync(
        string imagePath,
        string audioPath,
        string outputPath,
        IProgress<string>? progress = null,
        IProgress<int>? percentProgress = null)
    {
        var audioInfo = await ProbeAsync(audioPath);
        var args =
            $"-y -loop 1 -i \"{imagePath}\" -i \"{audioPath}\" " +
            $"-shortest -c:a copy -strict -2 \"{outputPath}\"";

        progress?.Report("Combinaison image + audio (commande simple, audio non ré-encodé)...");
        await RunAsync(FfmpegManager.FfmpegExe, args, audioInfo.DurationSeconds, percentProgress);
    }

    private static (int Width, int Height) GetDimensions(Orientation orientation) => orientation switch
    {
        Orientation.Portrait9x16 => (1080, 1920),
        Orientation.Landscape16x9 => (1920, 1080),
        _ => (1080, 1920)
    };

    private static bool IsAlreadyCompatible(VideoInfo info, int targetWidth, int targetHeight)
    {
        bool sameOrientation = (targetHeight > targetWidth) == (info.Height > info.Width);
        bool rightCodec = info.VideoCodec is "h264" or "hevc";
        return sameOrientation && rightCodec;
    }

    /// <summary>
    /// Construit le filtre ffmpeg statique : recadrage centré pour remplir le cadre cible
    /// sans déformer l'image. Fond flou + image centrée si la source est plus étroite.
    /// </summary>
    private static string BuildAspectFilter(VideoInfo info, int tw, int th)
    {
        double targetRatio = (double)tw / th;
        double sourceRatio = (double)info.Width / info.Height;

        if (sourceRatio > targetRatio)
        {
            return $"crop=ih*{tw}/{th}:ih,scale={tw}:{th}:flags=lanczos";
        }
        else if (sourceRatio < targetRatio)
        {
            return
                $"split[bg][fg];" +
                $"[bg]scale={tw}:{th}:force_original_aspect_ratio=increase,crop={tw}:{th},gblur=sigma=20[bg2];" +
                $"[fg]scale={tw}:{th}:force_original_aspect_ratio=decrease[fg2];" +
                $"[bg2][fg2]overlay=(W-w)/2:(H-h)/2:format=auto,format=yuv420p";
        }
        else
        {
            return $"scale={tw}:{th}:flags=lanczos";
        }
    }

    /// <summary>
    /// Construit le filtre ffmpeg avec mouvement : l'image est d'abord mise à l'échelle en
    /// "surplus" (overscan 1.35x) pour toujours couvrir le cadre final, puis un zoom
    /// dynamique (Ken Burns + pulsations sur les basses) et un léger travelling sont
    /// appliqués frame par frame (scale...eval=frame + crop avec expressions en "t"),
    /// avant un recadrage final au format cible. Technique validée : crop/scale réévaluent
    /// leurs expressions par image quand eval=frame est actif, sans dépendance externe.
    /// Note : contrairement à BuildAspectFilter, ce mode recadre toujours pour remplir le
    /// cadre (pas de fond flou) — nécessaire pour garder une marge de mouvement sûre.
    /// </summary>
    private static string BuildMotionFilter(
        VideoInfo info, int tw, int th, MotionSettings motion, List<BassPeak> peaks, double totalDuration)
    {
        const double overscan = 1.35;
        const double zoomCap = 1.30; // reste sous l'overscan pour ne jamais montrer de bord vide
        const double kenBurnsMaxGrowth = 0.10; // +10% de zoom continu max sur toute la durée
        const double panMaxFraction = 0.05;    // travelling max = 5% de la largeur/hauteur cible
        const double pulseDecaySeconds = 0.15;

        int ow = (int)Math.Round(tw * overscan);
        int oh = (int)Math.Round(th * overscan);
        double safeDuration = Math.Max(totalDuration, 0.1);

        // Étape 1 : l'image couvre toujours le cadre "overscan" (recadrage centré, sans déformation).
        var chain = new StringBuilder();
        chain.Append($"scale={ow}:{oh}:force_original_aspect_ratio=increase,crop={ow}:{oh}");

        // Étape 2 : zoom dynamique = Ken Burns continu + pulsations sur les pics de basses.
        string kenBurnsTerm = motion.KenBurnsEnabled
            ? $"({kenBurnsMaxGrowth.ToString(CultureInfo.InvariantCulture)}*{motion.KenBurnsIntensity01.ToString(CultureInfo.InvariantCulture)}*(t/{safeDuration.ToString(CultureInfo.InvariantCulture)}))"
            : "0";

        var pulseTerms = new StringBuilder("0");
        if (motion.BassReactiveEnabled)
        {
            double ampScale = 0.06 + 0.14 * Math.Clamp(motion.BassSensitivity01, 0, 1);
            foreach (var peak in peaks)
            {
                double amp = ampScale * peak.Strength01;
                pulseTerms.Append(
                    $"+({amp.ToString("F4", CultureInfo.InvariantCulture)}*max(0,1-abs(t-{peak.TimeSeconds.ToString("F3", CultureInfo.InvariantCulture)})/{pulseDecaySeconds.ToString(CultureInfo.InvariantCulture)}))");
            }
        }

        string zoomExpr = $"min({zoomCap.ToString(CultureInfo.InvariantCulture)},1+{kenBurnsTerm}+({pulseTerms}))";
        chain.Append($",scale=w='iw*({zoomExpr})':h='ih*({zoomExpr})':eval=frame");

        // Étape 3 : léger travelling (pan) continu si Ken Burns actif, puis recadrage final.
        double panAmpX = motion.KenBurnsEnabled ? tw * panMaxFraction * motion.KenBurnsIntensity01 : 0;
        double panAmpY = motion.KenBurnsEnabled ? th * (panMaxFraction * 0.6) * motion.KenBurnsIntensity01 : 0;
        string panX = panAmpX > 0
            ? $"+{panAmpX.ToString("F2", CultureInfo.InvariantCulture)}*sin(2*PI*t/{safeDuration.ToString(CultureInfo.InvariantCulture)})"
            : "";
        string panY = panAmpY > 0
            ? $"+{panAmpY.ToString("F2", CultureInfo.InvariantCulture)}*cos(2*PI*t/{(safeDuration * 1.3).ToString(CultureInfo.InvariantCulture)})"
            : "";

        chain.Append($",crop={tw}:{th}:x='(iw-ow)/2{panX}':y='(ih-oh)/2{panY}'");

        return chain.ToString();
    }

    /// <summary>
    /// Lance ffmpeg et suit sa progression en temps réel en parsant les lignes "time=" de
    /// sa sortie stderr, comparées à la durée totale attendue, pour reporter un pourcentage.
    /// </summary>
    private static async Task RunAsync(
        string exe, string args,
        double? totalDurationSeconds = null,
        IProgress<int>? percentProgress = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderrLog = new StringBuilder();

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderrLog.AppendLine(e.Data);

            if (totalDurationSeconds is > 0 && percentProgress is not null)
            {
                var m = TimeRegex.Match(e.Data);
                if (m.Success)
                {
                    double h = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    double min = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    double sec = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    double current = h * 3600 + min * 60 + sec;
                    int percent = (int)Math.Clamp(current / totalDurationSeconds.Value * 100.0, 0, 100);
                    percentProgress.Report(percent);
                }
            }
        };

        process.Start();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg a échoué (code {process.ExitCode}) :\n{stderrLog}");

        percentProgress?.Report(100);
    }

    private static async Task<string> RunAndCaptureAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi)!;
        string stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return stdout;
    }
}
