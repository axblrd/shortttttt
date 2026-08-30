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
    private string? _simpleOutputPath;
    private TrimRange? _videoTrim;
    private TrimRange? _audioTrim;

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

    private void OrientationRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (PublishBorder is null) return;
        // Les pages de publication (TikTok/Insta/YouTube Shorts) n'ont de sens qu'au format portrait.
        PublishBorder.Visibility = OrientationPortrait.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MotionCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (KenBurnsIntensityPanel is null || BassSensitivityPanel is null) return;
        KenBurnsIntensityPanel.Visibility = KenBurnsCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        BassSensitivityPanel.Visibility = BassReactiveCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private MotionSettings GetMotionSettings() => new(
        KenBurnsEnabled: KenBurnsCheck.IsChecked == true,
        KenBurnsIntensity01: KenBurnsIntensitySlider.Value / 100.0,
        BassReactiveEnabled: BassReactiveCheck.IsChecked == true,
        BassSensitivity01: BassSensitivitySlider.Value / 100.0);

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
            _videoTrim = null;
            VideoTrimText.Text = "";
            TrimVideoButton.IsEnabled = true;
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
            _audioTrim = null;
            AudioTrimText.Text = "";
            TrimAudioButton.IsEnabled = true;
        }
    }

    // --- Éditeur de recadrage temporel ---

    private void TrimVideoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFile is null) return;
        try
        {
            var trimWindow = new TrimWindow(_selectedFile) { Owner = this };
            if (trimWindow.ShowDialog() == true && trimWindow.Result is not null)
            {
                _videoTrim = trimWindow.Result;
                VideoTrimText.Text = $"{_videoTrim.StartSeconds:F1}s -> {_videoTrim.EndSeconds:F1}s ({_videoTrim.Duration:F1}s)";
            }
        }
        catch (Exception ex)
        {
            Log("Impossible d'ouvrir l'éditeur de recadrage : " + ex.Message);
            Log("Le format vidéo n'est peut-être pas lisible par le lecteur intégré à Windows. " +
                "Essaie de convertir le fichier une première fois, ou traite-le sans recadrage.");
        }
    }

    private void TrimAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAudio is null) return;
        try
        {
            var trimWindow = new TrimWindow(_selectedAudio) { Owner = this };
            if (trimWindow.ShowDialog() == true && trimWindow.Result is not null)
            {
                _audioTrim = trimWindow.Result;
                AudioTrimText.Text = $"{_audioTrim.StartSeconds:F1}s -> {_audioTrim.EndSeconds:F1}s ({_audioTrim.Duration:F1}s)";
            }
        }
        catch (Exception ex)
        {
            Log("Impossible d'ouvrir l'éditeur de recadrage : " + ex.Message);
        }
    }

    private void ChooseOutputButton_Click(object sender, RoutedEventArgs e)
    {
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

    // --- Mode "commande simple" : nommer puis démarrer séparément ---

    private void ChooseSimpleOutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedImage is null || _selectedAudio is null)
        {
            Log("Sélectionne d'abord une image et un son.");
            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Title = "Nommer la vidéo à créer",
            Filter = "Vidéo MP4|*.mp4",
            FileName = Path.GetFileNameWithoutExtension(_selectedAudio) + ".mp4",
            InitialDirectory = string.IsNullOrEmpty(_customOutputDir)
                ? Path.GetDirectoryName(_selectedAudio)
                : _customOutputDir
        };
        if (saveDialog.ShowDialog() != true) return;

        _simpleOutputPath = saveDialog.FileName;
        SimpleOutputText.Text = _simpleOutputPath;
        StartSimpleCombineButton.IsEnabled = true;
    }

    private async void StartSimpleCombineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedImage is null || _selectedAudio is null || _simpleOutputPath is null)
        {
            Log("Choisis d'abord une image, un son, puis le nom du fichier de sortie.");
            return;
        }

        SetBusy(true, "Combinaison image + audio...");
        try
        {
            Log($"Combinaison de {Path.GetFileName(_selectedImage)} + {Path.GetFileName(_selectedAudio)}...");
            await _processor.CreateSimpleFromImageAndAudioAsync(
                _selectedImage, _selectedAudio, _simpleOutputPath,
                new Progress<string>(Log), MakePercentProgress());
            Log($"  -> {_simpleOutputPath}");

            _lastOutputDir = Path.GetDirectoryName(_simpleOutputPath);
            FinishAndOpen(_lastOutputDir!, _simpleOutputPath, new List<PlatformProfile>());
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

    // --- Pipelines principaux ---

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

    /// <summary>Crée un IProgress&lt;int&gt; qui met à jour la barre + le texte de pourcentage.</summary>
    private IProgress<int> MakePercentProgress() => new Progress<int>(percent =>
    {
        ProgressBarCtrl.Value = percent;
        ProgressPercentText.Text = $"{percent}%";
    });

    private void SetBusy(bool busy, string status)
    {
        ProcessButton.IsEnabled = !busy;
        StartSimpleCombineButton.IsEnabled = !busy && _simpleOutputPath is not null;
        OpenOutputButton.IsEnabled = !busy && _lastOutputDir is not null;
        ProgressBarCtrl.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressPercentText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressBarCtrl.Value = 0;
        ProgressPercentText.Text = "0%";
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

    /// <summary>Plateformes cochées pour l'ouverture post-génération (uniquement pertinent en portrait).</summary>
    private List<PlatformProfile> GetPlatformsToOpen(Orientation orientation)
    {
        var list = new List<PlatformProfile>();
        if (orientation != Orientation.Portrait9x16) return list;
        if (TikTokCheck.IsChecked == true) list.Add(PlatformProfiles.TikTok);
        if (InstagramCheck.IsChecked == true) list.Add(PlatformProfiles.InstagramReels);
        if (YoutubeCheck.IsChecked == true) list.Add(PlatformProfiles.YouTubeShorts);
        return list;
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
            StatusText.Text = "Extraction de l'audio d'origine...";
            Log("Extraction de l'audio d'origine sans perte...");
            await _processor.ExtractLosslessAudioAsync(inputFile, audioOut, format, MakePercentProgress());
            Log($"  -> {audioOut}");
        }

        var quality = QualityTrueLossless.IsChecked == true ? QualityMode.TrueLossless
            : QualityCopy.IsChecked == true ? QualityMode.CopyWhenPossible
            : QualityMode.VisuallyLossless;
        var orientation = OrientationLandscape.IsChecked == true
            ? Orientation.Landscape16x9 : Orientation.Portrait9x16;
        var motion = GetMotionSettings();

        if (_videoTrim is not null)
            Log($"Recadrage temporel : {_videoTrim.StartSeconds:F1}s -> {_videoTrim.EndSeconds:F1}s");
        if (orientation == Orientation.Portrait9x16)
            Log("Format portrait (short) : durée limitée à 60s max.");
        if (motion.IsAnyEnabled)
            Log($"Mouvement activé : Ken Burns={motion.KenBurnsEnabled}, Réactif aux basses={motion.BassReactiveEnabled}");

        var outFile = Path.Combine(outputDir, orientation == Orientation.Portrait9x16 ? "short.mp4" : "video_paysage.mp4");
        StatusText.Text = "Encodage...";
        Log("Encodage en cours...");
        await _processor.ConvertToPortraitAsync(inputFile, outFile, quality, info, orientation, motion,
            _videoTrim, new Progress<string>(Log), MakePercentProgress());
        Log($"  -> {outFile}");

        FinishAndOpen(outputDir, outFile, GetPlatformsToOpen(orientation));
    }

    private async Task RunImageAudioPipelineAsync(string imagePath, string audioPath)
    {
        var outputDir = ResolveOutputDir(audioPath, "_shorts");
        _lastOutputDir = outputDir;

        Log($"Image : {Path.GetFileName(imagePath)} — Son : {Path.GetFileName(audioPath)}");
        if (_audioTrim is not null)
            Log($"Recadrage temporel du son : {_audioTrim.StartSeconds:F1}s -> {_audioTrim.EndSeconds:F1}s");

        if (ExtractAudioCheck.IsChecked == true)
        {
            var format = AudioWav.IsChecked == true ? LosslessAudioFormat.Wav : LosslessAudioFormat.Flac;
            var ext = format == LosslessAudioFormat.Wav ? "wav" : "flac";
            var audioOut = Path.Combine(outputDir, $"audio_original.{ext}");
            StatusText.Text = "Copie sans perte du son d'origine...";
            Log("Copie sans perte du son d'origine...");
            await _processor.ExtractLosslessAudioAsync(audioPath, audioOut, format, MakePercentProgress());
            Log($"  -> {audioOut}");
        }

        var quality = QualityTrueLossless.IsChecked == true ? QualityMode.TrueLossless : QualityMode.VisuallyLossless;
        var orientation = OrientationLandscape.IsChecked == true
            ? Orientation.Landscape16x9 : Orientation.Portrait9x16;
        var motion = GetMotionSettings();

        if (motion.IsAnyEnabled)
            Log($"Mouvement activé : Ken Burns={motion.KenBurnsEnabled} (intensité {motion.KenBurnsIntensity01:P0}), " +
                $"Réactif aux basses={motion.BassReactiveEnabled} (sensibilité {motion.BassSensitivity01:P0})");
        if (orientation == Orientation.Portrait9x16)
            Log("Format portrait (short) : durée limitée à 60s max.");

        string? firstOutFile = null;

        if (FlacMasterCheck.IsChecked == true)
        {
            var masterOut = Path.Combine(outputDir, "master_sans_perte.mkv");
            StatusText.Text = "Génération du fichier maître 100% sans perte...";
            Log("Génération du fichier maître (vidéo CRF 0 + audio FLAC intégré)...");
            await _processor.CreateLosslessMasterAsync(imagePath, audioPath, masterOut, orientation, motion,
                new Progress<string>(Log), MakePercentProgress());
            Log($"  -> {masterOut}");
            firstOutFile ??= masterOut;
        }

        var outFile = Path.Combine(outputDir, orientation == Orientation.Portrait9x16 ? "short.mp4" : "video_paysage.mp4");
        StatusText.Text = "Génération de la vidéo...";
        Log("Génération de la vidéo (image + son)...");
        await _processor.CreateFromImageAndAudioAsync(imagePath, audioPath, outFile, quality, orientation,
            motion, _audioTrim, new Progress<string>(Log), MakePercentProgress());
        Log($"  -> {outFile}");
        firstOutFile ??= outFile;

        FinishAndOpen(outputDir, firstOutFile, GetPlatformsToOpen(orientation));
    }

    private void FinishAndOpen(string outputDir, string? fileToPreview, List<PlatformProfile> platformsOpened)
    {
        Log("Terminé. Ouverture du dossier de sortie...");
        Process.Start("explorer.exe", outputDir);

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
