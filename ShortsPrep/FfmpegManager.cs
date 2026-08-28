using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace ShortsPrep;

/// <summary>
/// Gère le binaire FFmpeg local : vérifie s'il est présent, récupère la dernière
/// build statique Windows (via les builds "BtbN/FFmpeg-Builds", GPL full, gratuites
/// et mises à jour en continu) et la met à jour si une nouvelle version existe.
/// </summary>
public class FfmpegManager
{
    private static readonly string RootDir =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg");

    public static string FfmpegExe => Path.Combine(RootDir, "ffmpeg.exe");
    public static string FfprobeExe => Path.Combine(RootDir, "ffprobe.exe");

    private const string LatestReleaseApi =
        "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest";

    // Build statique Windows 64-bit, GPL "full" (inclut tous les codecs, x264/x265/etc.)
    private const string AssetNameHint = "win64-gpl.zip";

    public bool IsInstalled => File.Exists(FfmpegExe) && File.Exists(FfprobeExe);

    /// <summary>
    /// Vérifie la présence de FFmpeg et propose/effectue la mise à jour vers la
    /// dernière version disponible. Retourne un rapport texte pour affichage UI.
    /// </summary>
    public async Task<string> EnsureUpToDateAsync(IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(RootDir);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ShortsPrep-App");

        progress?.Report("Vérification de la dernière version de FFmpeg...");

        string? downloadUrl = null;
        string? remoteTag = null;

        try
        {
            var json = await http.GetStringAsync(LatestReleaseApi);
            using var doc = JsonDocument.Parse(json);
            remoteTag = doc.RootElement.GetProperty("tag_name").GetString();

            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.Contains(AssetNameHint, StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            if (IsInstalled)
            {
                progress?.Report($"Impossible de vérifier une mise à jour ({ex.Message}). " +
                                  "Utilisation de la version déjà installée.");
                return "offline-existing";
            }
            throw new InvalidOperationException(
                "Pas de connexion internet et FFmpeg n'est pas encore installé. " +
                "Connecte-toi une première fois pour le télécharger.", ex);
        }

        var versionFile = Path.Combine(RootDir, "version.txt");
        var currentTag = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : null;

        if (IsInstalled && currentTag == remoteTag)
        {
            progress?.Report($"FFmpeg déjà à jour ({remoteTag}).");
            return "up-to-date";
        }

        if (downloadUrl is null)
            throw new InvalidOperationException("Build FFmpeg introuvable dans la release GitHub.");

        progress?.Report($"Téléchargement de FFmpeg {remoteTag}...");

        var zipPath = Path.Combine(Path.GetTempPath(), "ffmpeg_latest.zip");
        await using (var stream = await http.GetStreamAsync(downloadUrl))
        await using (var file = File.Create(zipPath))
        {
            await stream.CopyToAsync(file);
        }

        progress?.Report("Extraction...");

        var extractDir = Path.Combine(Path.GetTempPath(), "ffmpeg_extract_" + Guid.NewGuid());
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        // Les archives BtbN contiennent un sous-dossier bin/ avec ffmpeg.exe et ffprobe.exe
        var binDir = Directory.GetDirectories(extractDir, "bin", SearchOption.AllDirectories)
            .FirstOrDefault() ?? throw new InvalidOperationException("Dossier bin introuvable dans l'archive FFmpeg.");

        foreach (var exe in new[] { "ffmpeg.exe", "ffprobe.exe" })
        {
            File.Copy(Path.Combine(binDir, exe), Path.Combine(RootDir, exe), overwrite: true);
        }

        File.WriteAllText(versionFile, remoteTag ?? "unknown");

        Directory.Delete(extractDir, recursive: true);
        File.Delete(zipPath);

        progress?.Report($"FFmpeg mis à jour vers {remoteTag}.");
        return "updated";
    }

    public string GetInstalledVersionLabel()
    {
        var versionFile = Path.Combine(RootDir, "version.txt");
        return File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "non installé";
    }
}
