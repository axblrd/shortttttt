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
    private string? _selectedImage;
    private string? _selectedAudio;

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

    private void ModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        // Les panneaux peuvent ne pas encore exister lors du chargement initial du XAML.
        if (VideoModePanel is null || ImageAudioModePanel is null) return;

        bool videoMode = ModeVideoRadio.IsChecked == true;
        VideoModePanel.Visibility = videoMode ? Visibility.Visible : Visibility.Collapsed;
        ImageAudioModePanel.Visibility = videoMode ? Visibility.Collapsed : Visibility.Visible;
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

    private void BrowseImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.webp|Tous les fichiers|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            _selectedImage = dialog.FileName;
            SelectedImageText.Text = Path.GetFileName(_selectedImage);
        }
    }

    private void BrowseAudioButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Fichiers audio|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg|Tous les fichiers|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            _selectedAudio = dialog.FileName;
            SelectedAudioText.Text = Path.GetFileName(_selectedAudio);
        }
    }

    private async void ProcessButton_Click(object sender, RoutedEventArgs e)
    {
        bool imageAudioMode = ModeImageAudioRadio.IsChecked == true;

        if (!imageAudioMode && _selectedFile is null)
        {
            Log("Sélectionne d'abord une vidéo.");
            return;
        }
        if (imageAudioMode && (_selectedImage is null || _selectedAudio is null))
        {
            Log("Sélectionne une image et un son.");
            return;
        }

        ProcessButton.IsEnabled = false;
        try
        {
            if (imageAudioMode)
                await RunImageAudioPipelineAsync(_selectedImage!, _selectedAudio!);
            else
                await RunPipelineAsync(_selectedFile!);
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

    private async Task RunImageAudioPipelineAsync(string imagePath, string audioPath)
    {
        var outputDir = Path.Combine(
            Path.GetDirectoryName(audioPath) ?? Environment.CurrentDirectory,
            Path.GetFileNameWithoutExtension(audioPath) + "_shorts");
        Directory.CreateDirectory(outputDir);

        Log($"Image : {Path.GetFileName(imagePath)} — Son : {Path.GetFileName(audioPath)}");

        // Copie maître de l'audio, intacte, à part (même logique que le mode vidéo)
        if (ExtractAudioCheck.IsChecked == true)
        {
            var format = AudioWav.IsChecked == true ? LosslessAudioFormat.Wav : LosslessAudioFormat.Flac;
            var ext = format == LosslessAudioFormat.Wav ? "wav" : "flac";
            var audioOut = Path.Combine(outputDir, $"audio_original.{ext}");
            Log("Copie sans perte du son d'origine...");
            await _processor.ExtractLosslessAudioAsync(audioPath, audioOut, format);
            Log($"  -> {audioOut}");
        }

        var quality = QualityTrueLossless.IsChecked == true ? QualityMode.TrueLossless : QualityMode.VisuallyLossless;
        var orientation = OrientationLandscape.IsChecked == true
            ? Orientation.Landscape16x9 : Orientation.Portrait9x16;

        var selected = new List<PlatformProfile>();
        if (TikTokCheck.IsChecked == true) selected.Add(PlatformProfiles.TikTok);
        if (InstagramCheck.IsChecked == true) selected.Add(PlatformProfiles.InstagramReels);
        if (YoutubeCheck.IsChecked == true) selected.Add(PlatformProfiles.YouTubeShorts);

        foreach (var profile in selected)
        {
            var outFile = Path.Combine(outputDir, $"{profile.Name.Replace(" ", "_")}.mp4");
            Log($"Génération de la vidéo (image + son) pour {profile.Name}...");
            await _processor.CreateFromImageAndAudioAsync(imagePath, audioPath, outFile, profile, quality, orientation,
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
        var orientation = OrientationLandscape.IsChecked == true
            ? Orientation.Landscape16x9 : Orientation.Portrait9x16;

        var selected = new List<PlatformProfile>();
        if (TikTokCheck.IsChecked == true) selected.Add(PlatformProfiles.TikTok);
        if (InstagramCheck.IsChecked == true) selected.Add(PlatformProfiles.InstagramReels);
        if (YoutubeCheck.IsChecked == true) selected.Add(PlatformProfiles.YouTubeShorts);

        foreach (var profile in selected)
        {
            var outFile = Path.Combine(outputDir, $"{profile.Name.Replace(" ", "_")}.mp4");
            Log($"Préparation pour {profile.Name}...");
            await _processor.ConvertToPortraitAsync(inputFile, outFile, profile, quality, info, orientation,
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
