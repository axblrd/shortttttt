# ShortsPrep

Logiciel Windows (WPF / .NET 8) pour préparer une même vidéo au format portrait
(short/reel/story) pour TikTok, Instagram Reels et YouTube Shorts, avec FFmpeg
maintenu à jour automatiquement.

## Ce qu'il fait

0. **Deux modes en entrée** : soit tu pars d'une vidéo existante, soit tu choisis
   une **image fixe + un son** — la vidéo générée dure alors exactement la durée
   du son (utile pour poster un morceau avec une pochette/visuel statique). En
   mode image + son, un **fichier maître 100% sans perte** peut aussi être généré
   (vidéo CRF 0 + audio FLAC intégré, conteneur `.mkv`) — c'est le fichier à
   garder pour toi, à ne surtout pas envoyer tel quel sur TikTok/Instagram (voir
   plus bas pourquoi).
1. **Vérifie et met à jour FFmpeg** au lancement (dernière build statique Windows
   depuis les releases GitHub officielles de BtbN/FFmpeg-Builds — gratuites, à
   jour en continu, licence GPL "full" avec tous les codecs).
2. **Extrait l'audio d'origine sans aucune perte** dans un fichier séparé
   (WAV 24-bit ou FLAC) — la copie "maître" à garder pour un usage musical.
3. **Convertit la vidéo en 9:16** pour chaque plateforme cochée :
   - recadrage centré si la source est plus large que 9:16 (pas de déformation),
   - fond flou + image centrée si la source est plus étroite,
   - 3 modes de qualité : visuellement sans perte (CRF 16, recommandé), 100%
     sans perte (CRF 0, fichiers énormes), ou copie du flux si déjà compatible.
4. **Ouvre le dossier de sortie** et les pages d'upload de chaque plateforme
   pour publier en un ou deux clics (semi-automatique — voir limites ci-dessous).

## Build (sur Windows)

Prérequis : [.NET 8 SDK](https://dotnet.microsoft.com/download) (Windows).

```powershell
cd ShortsPrep
dotnet build -c Release
dotnet run
```

Ou ouvre `ShortsPrep.csproj` dans Visual Studio 2022 et lance en F5.
FFmpeg se télécharge automatiquement au premier lancement (~100 Mo).

## Pourquoi "semi-automatique" et pas 100% automatique

Un upload **entièrement automatisé** (sans aucun clic) vers TikTok/Instagram/
YouTube demande d'utiliser leurs API officielles, avec des contraintes réelles :

- **YouTube Shorts** : API Google Cloud accessible, upload automatisable — la
  V2 la plus simple à ajouter si tu veux aller plus loin.
- **TikTok Content Posting API** : nécessite une validation d'app par TikTok
  (dossier de review, délais, refus possible pour un usage personnel/petit compte).
- **Instagram Reels (Graph API)** : nécessite un compte Business/Creator +
  app Meta validée — process assez lourd pour un usage perso.

Le logiciel prépare donc tout (fichier prêt, bon format, bonne qualité) et ouvre
directement la bonne page — il ne reste plus qu'à glisser le fichier.

## Sur la question du "zéro perte audio"

Important à savoir : TikTok, Instagram et YouTube **ré-encodent toujours l'audio
en AAC compressé côté serveur**, quoi que tu leur envoies — c'est une limite des
plateformes elles-mêmes, aucun logiciel ne peut l'éviter. Ce que ShortsPrep
garantit :
- la copie WAV/FLAC extraite en local est bit-exacte par rapport à l'original —
  c'est ta version "maître" à conserver ou réutiliser ailleurs (DAW, SoundCloud, etc.) ;
  SoundCloud, lui, accepte bien du FLAC/WAV en upload direct, sans transcodage destructeur imposé en amont ;
- l'audio intégré dans le MP4 envoyé aux plateformes est encodé en AAC 320kbps
  (le maximum utile — au-delà, l'oreille ne fait plus la différence et les
  plateformes replafonnent de toute façon).

**Sur le fichier maître `.mkv` (FLAC + CRF 0)** : ce format n'est pas accepté
par TikTok/Instagram/YouTube Shorts (ils veulent du .mp4, H.264/AAC). Il sert
uniquement d'archive personnelle sans aucune perte, ou pour être réencodé plus
tard sans repartir de zéro. Pour l'upload, utilise les fichiers `.mp4` générés
à côté.

## Idées pour la suite (V2)

- Upload automatique réel vers YouTube Shorts via l'API Google.
- Watermark / recadrage ajustable à la souris (aperçu avant traitement).
- File d'attente pour traiter plusieurs vidéos d'un coup.
- Version Android (nécessiterait une réécriture — FFmpeg via `FFmpegKit`,
  interface en Kotlin/Jetpack Compose ; projet distinct du WPF).
