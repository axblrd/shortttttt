namespace ShortsPrep;

/// <summary>
/// Spécifications recommandées par plateforme pour le format "short" portrait.
/// Sert uniquement à valider/adapter la sortie ; le traitement vise toujours
/// la qualité maximale (voir VideoProcessor.QualityMode).
/// </summary>
public record PlatformProfile(
    string Name,
    int Width,
    int Height,
    double MaxDurationSeconds,
    string PublishUrl,
    string PublishHint
);

public static class PlatformProfiles
{
    public static readonly PlatformProfile TikTok = new(
        Name: "TikTok",
        Width: 1080, Height: 1920,
        MaxDurationSeconds: 600,
        PublishUrl: "https://www.tiktok.com/upload?lang=fr",
        PublishHint: "Le fichier traité s'ouvre dans l'Explorateur : glisse-le dans la page d'upload TikTok."
    );

    public static readonly PlatformProfile InstagramReels = new(
        Name: "Instagram Reels",
        Width: 1080, Height: 1920,
        MaxDurationSeconds: 900,
        PublishUrl: "https://www.instagram.com/",
        PublishHint: "Instagram Web ne permet pas toujours l'upload de Reels : privilégie l'app mobile " +
                     "(le fichier est copié dans un dossier facile à retrouver depuis ton téléphone via OneDrive/Drive)."
    );

    public static readonly PlatformProfile YouTubeShorts = new(
        Name: "YouTube Shorts",
        Width: 1080, Height: 1920,
        MaxDurationSeconds: 180,
        PublishUrl: "https://studio.youtube.com/channel/UC/videos/upload",
        PublishHint: "S'ouvre dans YouTube Studio : dépose le fichier sur la page d'upload."
    );

    public static readonly List<PlatformProfile> All = new() { TikTok, InstagramReels, YouTubeShorts };
}
