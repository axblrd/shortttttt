using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ShortsPrep;

public partial class MainWindow : Window
{
    private readonly FfmpegManager _ffmpeg = new();
    private readonly VideoProcessor _processor = new();
    private string? _selectedFile;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var progress = new Progress<string>(Log);
        try
        {
            await _ffmpeg.EnsureUpToDateAsync(progress);
        }
        catch (Exception ex)
        {
            Log("Erreur FFmpeg : " + ex.Message);
        }
        FfmpegVersionText.Text = $"  (ffmpeg : {_ffmpeg.GetInstalledVersionLabel()})";
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Fichiers vidéo|*.mp4;*.mov;*.mkv;*.avi;*.webm|Tous les fichiers|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            _selectedFile = dialog.FileName;
            SelectedFileText.Text = Path.GetFileName(_selectedFile);
        }
    }

    private async void ProcessButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFile is null)
        {
            Log("Sélectionne d'abord une vidéo.");
            return;
        }

        ProcessButton.IsEnabled = false;
        try
        {
            await RunPipelineAsync(_selectedFile);
        }
        catch (Exception ex)
        {
            Log("ERREUR : " + ex.Message);
        }
        finally
        {
            ProcessButton.IsEnabled = true;
        }
    }

    private async Task RunPipelineAsync(string inputFile)
    {
        var outputDir = Path.Combine(
            Path.GetDirectoryName(inputFile) ?? Environment.CurrentDirectory,
            Path.GetFileNameWithoutExtension(inputFile) + "_shorts");
        Directory.CreateDirectory(outputDir);

        Log($"Analyse de {Path.GetFileName(inputFile)}...");
        var info = await _processor.ProbeAsync(inputFile);
        Log($"Source : {info.Width}x{info.Height}, {info.DurationSeconds:F1}s, codec {info.VideoCodec}, audio : {info.HasAudio}");

        // 1) Copie audio maître sans perte, à part, pour un usage musical
        if (ExtractAudioCheck.IsChecked == true && info.HasAudio)
        {
            var format = AudioWav.IsChecked == true ? LosslessAudioFormat.Wav : LosslessAudioFormat.Flac;
            var ext = format == LosslessAudioFormat.Wav ? "wav" : "flac";
            var audioOut = Path.Combine(outputDir, $"audio_original.{ext}");
            Log("Extraction de l'audio d'origine sans perte...");
            await _processor.ExtractLosslessAudioAsync(inputFile, audioOut, format);
            Log($"  -> {audioOut}");
        }

        // 2) Une vidéo par plateforme sélectionnée
        var quality = QualityTrueLossless.IsChecked == true ? QualityMode.TrueLossless
            : QualityCopy.IsChecked == true ? QualityMode.CopyWhenPossible
            : QualityMode.VisuallyLossless;

        var selected = new List<PlatformProfile>();
        if (TikTokCheck.IsChecked == true) selected.Add(PlatformProfiles.TikTok);
        if (InstagramCheck.IsChecked == true) selected.Add(PlatformProfiles.InstagramReels);
        if (YoutubeCheck.IsChecked == true) selected.Add(PlatformProfiles.YouTubeShorts);

        foreach (var profile in selected)
        {
            var outFile = Path.Combine(outputDir, $"{profile.Name.Replace(" ", "_")}.mp4");
            Log($"Préparation pour {profile.Name}...");
            await _processor.ConvertToPortraitAsync(inputFile, outFile, profile, quality, info,
                new Progress<string>(Log));
            Log($"  -> {outFile}");
        }

        Log("Terminé. Ouverture du dossier de sortie et des pages de publication...");
        Process.Start("explorer.exe", outputDir);

        foreach (var profile in selected)
        {
            Process.Start(new ProcessStartInfo(profile.PublishUrl) { UseShellExecute = true });
            Log($"[{profile.Name}] {profile.PublishHint}");
        }
    }

    private void Log(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogText.Text += message + Environment.NewLine;
        });
    }
}
