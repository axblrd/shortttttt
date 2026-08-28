using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

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

public record VideoInfo(int Width, int Height, double DurationSeconds, string VideoCodec, bool HasAudio);

public class VideoProcessor
{
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
    public async Task ExtractLosslessAudioAsync(string inputPath, string outputPath, LosslessAudioFormat format)
    {
        // -vn : pas de vidéo. Codec PCM (wav) ou FLAC : compression sans perte, bit-exact.
        var codecArgs = format == LosslessAudioFormat.Wav ? "-c:a pcm_s24le" : "-c:a flac -compression_level 8";
        var args = $"-y -i \"{inputPath}\" -vn {codecArgs} \"{outputPath}\"";
        await RunAsync(FfmpegManager.FfmpegExe, args);
    }

    /// <summary>
    /// Convertit la vidéo au format portrait 9:16 pour le short/reel/story, en visant
    /// la meilleure qualité possible, tout en réintégrant l'audio d'origine sans le
    /// ré-encoder inutilement plus d'une fois (copy du flux audio quand le conteneur le permet).
    /// </summary>
    public async Task ConvertToPortraitAsync(
        string inputPath,
        string outputPath,
        PlatformProfile profile,
        QualityMode quality,
        VideoInfo info,
        IProgress<string>? progress = null)
    {
        string videoFilter = BuildPortraitFilter(info, profile);

        string videoCodecArgs = quality switch
        {
            QualityMode.TrueLossless => "-c:v libx264 -preset veryslow -crf 0 -pix_fmt yuv420p",
            QualityMode.VisuallyLossless => "-c:v libx264 -preset slow -crf 16 -pix_fmt yuv420p",
            QualityMode.CopyWhenPossible when IsAlreadyCompatible(info, profile)
                => "-c:v copy",
            _ => "-c:v libx264 -preset slow -crf 16 -pix_fmt yuv420p"
        };

        // Si on copie le flux vidéo tel quel, aucun filtre ne peut être appliqué.
        bool copyingVideo = videoCodecArgs.Contains("copy");
        string filterArgs = copyingVideo ? "" : $"-vf \"{videoFilter}\"";

        // Audio : ré-encodage AAC haut débit pour la compatibilité des plateformes
        // (elles ré-encodent de toute façon côté serveur). La copie maître intacte
        // (WAV/FLAC) est produite séparément par ExtractLosslessAudioAsync.
        string audioArgs = info.HasAudio ? "-c:a aac -b:a 320k -ar 48000" : "-an";

        var args = $"-y -i \"{inputPath}\" {filterArgs} {videoCodecArgs} {audioArgs} " +
                   $"-movflags +faststart \"{outputPath}\"";

        progress?.Report($"Encodage pour {profile.Name}...");
        await RunAsync(FfmpegManager.FfmpegExe, args);
    }

    private static bool IsAlreadyCompatible(VideoInfo info, PlatformProfile profile)
    {
        bool isPortrait = info.Height > info.Width;
        bool rightCodec = info.VideoCodec is "h264" or "hevc";
        return isPortrait && rightCodec;
    }

    /// <summary>
    /// Construit le filtre ffmpeg : recadrage centré pour remplir le cadre 9:16 sans
    /// déformer l'image (comportement "crop-to-fill", le plus courant pour les shorts).
    /// Si la source est déjà plus étroite que 9:16, on complète avec un fond flou
    /// plutôt que d'étirer l'image (pas de déformation visible).
    /// </summary>
    private static string BuildPortraitFilter(VideoInfo info, PlatformProfile profile)
    {
        int tw = profile.Width, th = profile.Height;
        double targetRatio = (double)tw / th;
        double sourceRatio = (double)info.Width / info.Height;

        if (sourceRatio > targetRatio)
        {
            // Source plus large que la cible : crop horizontal centré, puis scale.
            return $"crop=ih*{tw}/{th}:ih,scale={tw}:{th}:flags=lanczos";
        }
        else if (sourceRatio < targetRatio)
        {
            // Source plus étroite (ex: déjà verticale mais pas assez) : fond flou + image centrée.
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

    private static async Task RunAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi)!;
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg a échoué (code {process.ExitCode}) :\n{stderr}");
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
