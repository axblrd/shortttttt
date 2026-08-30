using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ShortsPrep;

public partial class TrimWindow : Window
{
    private readonly DispatcherTimer _previewStopTimer = new();
    private bool _updatingFromCode;
    private bool _mediaReady;

    /// <summary>Résultat validé par l'utilisateur (null si annulé).</summary>
    public TrimRange? Result { get; private set; }

    public TrimWindow(string mediaPath)
    {
        InitializeComponent();

        _previewStopTimer.Tick += (_, _) =>
        {
            _previewStopTimer.Stop();
            Media.Pause();
        };

        Loaded += (_, _) => OpenMedia(mediaPath);
    }

    private void OpenMedia(string mediaPath)
    {
        try
        {
            // Chemin absolu obligatoire pour Uri ; évite les erreurs sur chemins relatifs/réseau.
            var fullPath = Path.GetFullPath(mediaPath);
            Media.Source = new Uri(fullPath, UriKind.Absolute);
        }
        catch (Exception ex)
        {
            StatusMessage("Impossible de charger le fichier : " + ex.Message);
        }
    }

    private void Media_MediaOpened(object sender, RoutedEventArgs e)
    {
        _mediaReady = true;
        var total = Media.NaturalDuration.HasTimeSpan ? Media.NaturalDuration.TimeSpan.TotalSeconds : 0;
        if (total <= 0) total = 1;

        _updatingFromCode = true;
        StartSlider.Maximum = total;
        EndSlider.Maximum = total;
        StartSlider.Value = 0;
        EndSlider.Value = total;
        _updatingFromCode = false;

        UpdateLabels();
        // Affiche une première image (frame à 0s) sans lancer la lecture.
        Media.Play();
        Media.Pause();
    }

    private void Media_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        StatusMessage("Ce fichier ne peut pas être prévisualisé par le lecteur Windows " +
                      "(codec non supporté). Tu peux quand même valider une plage approximative " +
                      "en te basant sur la durée, ou annuler et traiter le fichier sans recadrage.");
        // On permet quand même de choisir une plage à l'aveugle si on connaît au moins la durée
        // via le fichier lui-même n'étant pas lisible ici ; les sliders restent utilisables avec
        // une durée par défaut de 60s si rien de mieux n'est disponible.
        if (!_mediaReady)
        {
            _updatingFromCode = true;
            StartSlider.Maximum = 3600;
            EndSlider.Maximum = 3600;
            EndSlider.Value = 60;
            _updatingFromCode = false;
            UpdateLabels();
        }
    }

    private void StatusMessage(string text)
    {
        DurationLabel.Text = text;
    }

    private void StartSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingFromCode) return;
        if (StartSlider.Value >= EndSlider.Value)
            StartSlider.Value = Math.Max(0, EndSlider.Value - 0.5);
        UpdateLabels();
    }

    private void EndSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingFromCode) return;
        if (EndSlider.Value <= StartSlider.Value)
            EndSlider.Value = Math.Min(EndSlider.Maximum, StartSlider.Value + 0.5);
        UpdateLabels();
    }

    private void UpdateLabels()
    {
        StartLabel.Text = FormatTime(StartSlider.Value);
        EndLabel.Text = FormatTime(EndSlider.Value);
        DurationLabel.Text = $"Durée sélectionnée : {(EndSlider.Value - StartSlider.Value):F1}s";
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 100}";
    }

    private void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_mediaReady)
        {
            StatusMessage("Aperçu indisponible pour ce fichier (codec non supporté par le lecteur Windows).");
            return;
        }

        Media.Position = TimeSpan.FromSeconds(StartSlider.Value);
        Media.Play();

        var previewLength = Math.Min(EndSlider.Value - StartSlider.Value, 15); // aperçu limité à 15s
        _previewStopTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.2, previewLength));
        _previewStopTimer.Stop();
        _previewStopTimer.Start();
    }

    private void ValidateButton_Click(object sender, RoutedEventArgs e)
    {
        Result = new TrimRange(StartSlider.Value, EndSlider.Value);
        try { Media.Stop(); } catch { /* ignore */ }
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        try { Media.Stop(); } catch { /* ignore */ }
        DialogResult = false;
        Close();
    }
}
