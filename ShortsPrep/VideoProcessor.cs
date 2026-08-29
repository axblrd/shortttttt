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

public class VideoProcessor
{
    private static readonly Regex TimeRegex = new(@"time=(\d+):(\d+):(\d+\.\d+)", RegexOptions.Compiled);

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
        // -vn : pas de vidéo. Codec PCM (wav) ou FLAC : compression sans perte, bit-exact.
        var codecArgs = format == LosslessAudioFormat.Wav ? "-c:a pcm_s24le" : "-c:a flac -compression_level 8";
        var args = $"-y -i \"{inputPath}\" -vn {codecArgs} \"{outputPath}\"";
        await RunAsync(FfmpegManager.FfmpegExe, args, info.DurationSeconds, percentProgress);
    }

    /// <summary>
    /// Convertit la vidéo au format choisi (portrait 9:16 ou paysage 16:9), en visant
    /// la meilleure qualité possible, tout en réintégrant l'audio d'origine sans le
    /// ré-encoder inutilement plus d'une fois (copy du flux audio quand le conteneur le permet).
    /// </summary>
    public async Task ConvertToPortraitAsync(
        string inputPath,
        string outputPath,
        PlatformProfile profile,
        QualityMode quality,
        VideoInfo info,
        Orientation orientation,
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

        var args = $"-y -i \"{inputPath}\" {filterArgs} {videoCodecArgs} {audioArgs} " +
                   $"-movflags +faststart \"{outputPath}\"";

        progress?.Report($"Encodage pour {profile.Name}...");
        await RunAsync(FfmpegManager.FfmpegExe, args, info.DurationSeconds, percentProgress);
    }

    /// <summary>
    /// Crée une vidéo à partir d'une image fixe et d'une piste audio : la durée de la
    /// vidéo est exactement calée sur la durée de l'audio.
    /// </summary>
    public async Task CreateFromImageAndAudioAsync(
        string imagePath,
        string audioPath,
        string outputPath,
        PlatformProfile profile,
        QualityMode quality,
        Orientation orientation,
        IProgress<string>? progress = null,
        IProgress<int>? percentProgress = null)
    {
        var imageInfo = await ProbeAsync(imagePath);
        var audioInfo = await ProbeAsync(audioPath);
        var (tw, th) = GetDimensions(orientation);
        string videoFilter = BuildAspectFilter(imageInfo, tw, th);

        string videoCodecArgs = quality == QualityMode.TrueLossless
            ? "-c:v libx264 -preset veryslow -crf 0 -tune stillimage -pix_fmt yuv420p"
            : "-c:v libx264 -preset slow -crf 16 -tune stillimage -pix_fmt yuv420p";

        var args =
            $"-y -loop 1 -i \"{imagePath}\" -i \"{audioPath}\" " +
            $"-vf \"{videoFilter}\" {videoCodecArgs} " +
            $"-c:a aac -b:a 320k -ar 48000 " +
            $"-shortest -movflags +faststart \"{outputPath}\"";

        progress?.Report($"Création de la vidéo image + audio pour {profile.Name}...");
        await RunAsync(FfmpegManager.FfmpegExe, args, audioInfo.DurationSeconds, percentProgress);
    }

    /// <summary>
    /// Fichier "maître" 100% sans perte : image fixe + audio intégré en FLAC (sans perte,
    /// bit-exact), vidéo encodée en CRF 0. Conteneur .mkv.
    /// </summary>
    public async Task CreateLosslessMasterAsync(
        string imagePath,
        string audioPath,
        string outputPath,
        Orientation orientation,
        IProgress<string>? progress = null,
        IProgress<int>? percentProgress = null)
    {
        var imageInfo = await ProbeAsync(imagePath);
        var audioInfo = await ProbeAsync(audioPath);
        var (tw, th) = GetDimensions(orientation);
        string videoFilter = BuildAspectFilter(imageInfo, tw, th);

        var args =
            $"-y -loop 1 -i \"{imagePath}\" -i \"{audioPath}\" " +
            $"-vf \"{videoFilter}\" " +
            $"-c:v libx264 -preset veryslow -crf 0 -tune stillimage -pix_fmt yuv420p " +
            $"-c:a flac -compression_level 8 " +
            $"-shortest \"{outputPath}\"";

        progress?.Report("Encodage du fichier maître (sans perte)...");
        await RunAsync(FfmpegManager.FfmpegExe, args, audioInfo.DurationSeconds, percentProgress);
    }

    /// <summary>
    /// Combine une image et un son en vidéo, exactement selon la commande de référence :
    /// ffmpeg -loop 1 -i image -i son -shortest -c:a copy -strict -2 sortie.mp4
    /// Aucun ré-encodage de l'audio (copie brute du flux d'origine, bit pour bit).
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
    /// Construit le filtre ffmpeg : recadrage centré pour remplir le cadre cible sans
    /// déformer l'image. Fond flou + image centrée si la source est plus étroite que la cible.
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
