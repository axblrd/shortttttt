using System.Windows;
using System.Windows.Threading;

namespace ShortsPrep;

public partial class TrimWindow : Window
{
    private readonly DispatcherTimer _previewStopTimer = new();
    private bool _updatingFromCode;

    /// <summary>Résultat validé par l'utilisateur (null si annulé).</summary>
    public TrimRange? Result { get; private set; }

    public TrimWindow(string mediaPath)
    {
        InitializeComponent();
        Media.Source = new Uri(mediaPath);
        Media.Position = TimeSpan.Zero;
        Media.Play();
        Media.Pause();

        _previewStopTimer.Tick += (_, _) =>
        {
            _previewStopTimer.Stop();
            Media.Pause();
        };
    }

    private void Media_MediaOpened(object sender, RoutedEventArgs e)
    {
        var total = Media.NaturalDuration.HasTimeSpan ? Media.NaturalDuration.TimeSpan.TotalSeconds : 0;
        if (total <= 0) total = 1;

        _updatingFromCode = true;
        StartSlider.Maximum = total;
        EndSlider.Maximum = total;
        StartSlider.Value = 0;
        EndSlider.Value = total;
        _updatingFromCode = false;

        UpdateLabels();
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
        Media.Stop();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        Media.Stop();
        DialogResult = false;
        Close();
    }
}
