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
    private string? _customOutputDir;
    private string? _lastOutputDir;

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
            Log("Vérifie ta connexion internet, puis relance l'application.");
        }
        FfmpegVersionText.Text = $"  (ffmpeg : {_ffmpeg.GetInstalledVersionLabel()})";
    }

    private void ModeRadio_Changed(object sender, RoutedEventArgs e)
    {
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

    private void ChooseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        // OpenFolderDialog est disponible nativement depuis .NET 8 (WPF).
        var dialog = new OpenFolderDialog { Title = "Choisir le dossier de sortie" };
        if (dialog.ShowDialog() == true)
        {
            _customOutputDir = dialog.FolderName;
            OutputDirText.Text = _customOutputDir;
        }
    }

    private void OpenOutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutputDir is not null && Directory.Exists(_lastOutputDir))
            Process.Start("explorer.exe", _lastOutputDir);
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

        SetBusy(true, "Traitement en cours...");
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
            SetBusy(false, "Terminé.");
        }
    }

    private void SetBusy(bool busy, string status)
    {
        ProcessButton.IsEnabled = !busy;
        OpenOutputButton.IsEnabled = !busy && _lastOutputDir is not null;
        ProgressBarCtrl.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = status;
    }

    private string ResolveOutputDir(string sourcePath, string suffix)
    {
        if (!string.IsNullOrEmpty(_customOutputDir))
        {
            Directory.CreateDirectory(_customOutputDir);
            return _customOutputDir;
        }
        var dir = Path.Combine(
            Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory,
            Path.GetFileNameWithoutExtension(sourcePath) + suffix);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task RunPipelineAsync(string inputFile)
    {
        var outputDir = ResolveOutputDir(inputFile, "_shorts");
        _lastOutputDir = outputDir;

        Log($"Analyse de {Path.GetFileName(inputFile)}...");
        var info = await _processor.ProbeAsync(inputFile);
        Log($"Source : {info.Width}x{info.Height}, {info.DurationSeconds:F1}s, codec {info.VideoCodec}, audio : {info.HasAudio}");

        if (ExtractAudioCheck.IsChecked == true && info.HasAudio)
        {
            var format = AudioWav.IsChecked == true ? LosslessAudioFormat.Wav : LosslessAudioFormat.Flac;
            var ext = format == LosslessAudioFormat.Wav ? "wav" : "flac";
            var audioOut = Path.Combine(outputDir, $"audio_original.{ext}");
            Log("Extraction de l'audio d'origine sans perte...");
            await _processor.ExtractLosslessAudioAsync(inputFile, audioOut, format);
            Log($"  -> {audioOut}");
        }

        var quality = QualityTrueLossless.IsChecked == true ? QualityMode.TrueLossless
            : QualityCopy.IsChecked == true ? QualityMode.CopyWhenPossible
            : QualityMode.VisuallyLossless;
        var orientation = OrientationLandscape.IsChecked == true
            ? Orientation.Landscape16x9 : Orientation.Portrait9x16;

        var selected = new List<PlatformProfile>();
        if (TikTokCheck.IsChecked == true) selected.Add(PlatformProfiles.TikTok);
        if (InstagramCheck.IsChecked == true) selected.Add(PlatformProfiles.InstagramReels);
        if (YoutubeCheck.IsChecked == true) selected.Add(PlatformProfiles.YouTubeShorts);

        string? firstOutFile = null;
        foreach (var profile in selected)
        {
            var outFile = Path.Combine(outputDir, $"{profile.Name.Replace(" ", "_")}.mp4");
            firstOutFile ??= outFile;
            StatusText.Text = $"Encodage pour {profile.Name}...";
            Log($"Préparation pour {profile.Name}...");
            await _processor.ConvertToPortraitAsync(inputFile, outFile, profile, quality, info, orientation,
                new Progress<string>(Log));
            Log($"  -> {outFile}");
        }

        FinishAndOpen(outputDir, firstOutFile, selected);
    }

    private async Task RunImageAudioPipelineAsync(string imagePath, string audioPath)
    {
        var outputDir = ResolveOutputDir(audioPath, "_shorts");
        _lastOutputDir = outputDir;

        Log($"Image : {Path.GetFileName(imagePath)} — Son : {Path.GetFileName(audioPath)}");

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

        string? firstOutFile = null;

        // Fichier maître 100% sans perte : image + audio FLAC intégré, vidéo CRF 0, conteneur .mkv
        // (le FLAC n'est fiable dans un conteneur que dans .mkv, pas garanti en .mp4 selon les lecteurs).
        if (FlacMasterCheck.IsChecked == true)
        {
            var masterOut = Path.Combine(outputDir, "master_sans_perte.mkv");
            StatusText.Text = "Génération du fichier maître 100% sans perte...";
            Log("Génération du fichier maître (vidéo CRF 0 + audio FLAC intégré)...");
            await _processor.CreateLosslessMasterAsync(imagePath, audioPath, masterOut, orientation,
                new Progress<string>(Log));
            Log($"  -> {masterOut}");
            firstOutFile ??= masterOut;
        }

        var selected = new List<PlatformProfile>();
        if (TikTokCheck.IsChecked == true) selected.Add(PlatformProfiles.TikTok);
        if (InstagramCheck.IsChecked == true) selected.Add(PlatformProfiles.InstagramReels);
        if (YoutubeCheck.IsChecked == true) selected.Add(PlatformProfiles.YouTubeShorts);

        foreach (var profile in selected)
        {
            var outFile = Path.Combine(outputDir, $"{profile.Name.Replace(" ", "_")}.mp4");
            firstOutFile ??= outFile;
            StatusText.Text = $"Génération pour {profile.Name}...";
            Log($"Génération de la vidéo (image + son) pour {profile.Name}...");
            await _processor.CreateFromImageAndAudioAsync(imagePath, audioPath, outFile, profile, quality, orientation,
                new Progress<string>(Log));
            Log($"  -> {outFile}");
        }

        FinishAndOpen(outputDir, firstOutFile, selected);
    }

    private void FinishAndOpen(string outputDir, string? fileToPreview, List<PlatformProfile> platformsOpened)
    {
        Log("Terminé. Ouverture du dossier de sortie...");
        Process.Start("explorer.exe", outputDir);

        // Ouvre directement le premier fichier généré dans le lecteur vidéo par défaut,
        // pour pouvoir le visualiser immédiatement sans avoir à le chercher.
        if (fileToPreview is not null && File.Exists(fileToPreview))
        {
            Log($"Lecture de {Path.GetFileName(fileToPreview)}...");
            Process.Start(new ProcessStartInfo(fileToPreview) { UseShellExecute = true });
        }

        foreach (var profile in platformsOpened)
        {
            Process.Start(new ProcessStartInfo(profile.PublishUrl) { UseShellExecute = true });
            Log($"[{profile.Name}] {profile.PublishHint}");
        }

        OpenOutputButton.IsEnabled = true;
    }

    private void Log(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogText.Text += message + Environment.NewLine;
            LogScroll.ScrollToEnd();
        });
    }
}
