# Lucie — assistant vocal pour Windows

Un petit widget flottant avec un visage qui vit. Dis « Salut Lucie », elle t'écoute, puis exécute ta commande. Elle répond par des sons doux, pas par une voix.

## Ce qu'elle sait faire (V1)

- **Activation à la voix** : « Salut Lucie » (le nom se change dans les paramètres). La reconnaissance tourne hors ligne avec Vosk : rien ne part sur internet.
- **Clic sur le widget** : elle t'écoute aussi, sans mot d'activation.
- **Commandes** : ouvrir une appli ou un lien, taper un raccourci clavier (Discord, Game Bar…), faire une capture d'écran (enregistrée dans `Images\Lucie`), lecture/pause, morceau suivant ou précédent.
- **« Ouvre Spotify »** sans commande dédiée : elle cherche l'appli dans le menu Démarrer.
- **Visage vivant** : clignements au hasard, coups d'œil à gauche et à droite. Elle fait « waouh » et suit la souris quand celle-ci entre dans sa zone, met son casque quand de la musique joue, et s'endort (avec les petits Z) quand le PC est inactif.
- **Mode lecteur** : le widget s'agrandit quand un morceau démarre ou change (titre, artiste, pochette, précédent/pause/suivant), puis se replie au bout de 6 s, sauf si la souris est dessus.
- **Paramètres** (clic droit sur le widget) : nom, micro, sortie audio, position (9 positions ou glisser-déposer), taille, transparence, sommeil, zone de détection de la souris, fenêtre transparente, couleur d'accent, sons, commandes et raccourcis.

## Installation

### 1. Outils

- Windows 10 (1809+) ou Windows 11, PC 64 bits.
- **Visual Studio 2022**, avec la charge de travail **« Développement .NET Desktop »**. Dans les détails, coche **« Windows App SDK C# Templates »** (WinUI 3).

### 2. Le modèle vocal français (obligatoire pour la voix)

1. Va sur https://alphacephei.com/vosk/models.
2. Télécharge **vosk-model-small-fr-0.22** (environ 40 Mo).
3. Décompresse-le et **renomme le dossier** en `vosk-model-fr`.
4. Place-le à l'un de ces deux endroits :
   - `Lucie\VoiceModel\vosk-model-fr\` (dans le projet : il sera copié à côté de l'exe) ;
   - ou `%LocalAppData%\Lucie\VoiceModel\vosk-model-fr\`.

Le dossier doit contenir `am`, `conf`, `graph`… Sans le modèle, l'appli se lance quand même : les paramètres s'ouvrent et expliquent quoi faire.

### 3. Lancer

1. Ouvre `Lucie.sln` dans Visual Studio.
2. Choisis la plateforme **x64** (Vosk n'existe qu'en 64 bits).
3. Appuie sur **F5**. Visual Studio télécharge les paquets NuGet au premier lancement.

### 4. Discord

Discord n'a pas de raccourci pour le micro par défaut. Dans **Discord › Paramètres › Raccourcis clavier**, ajoute :

| Action Discord | Raccourci proposé |
| --- | --- |
| Activer/désactiver le micro | `Ctrl+Maj+M` |
| Activer/désactiver la sourdine | `Ctrl+Maj+D` |

Ce sont ceux déjà configurés dans Lucie. Si tu en choisis d'autres, change-les aussi dans **Paramètres › Commandes et raccourcis**.

L'enregistrement d'écran utilise la Game Bar de Windows (`Win+Alt+R`), qui doit être activée dans les paramètres Windows.

## Écrire un raccourci

Sépare les touches par `+` : `Ctrl+Maj+M`, `Win+Alt+R`, `Ctrl+Shift+F5`, `Alt+Tab`…
Noms reconnus : `Ctrl`, `Maj`/`Shift`, `Alt`, `Win`, `Espace`, `Entrée`, `Tab`, `Échap`, `Suppr`, `F1`…`F24`, les flèches (`Haut`, `Bas`, `Gauche`, `Droite`), `ImprÉcran`, `PlayPause`, `Next`, `Prev`, `Mute`, `VolUp`, `VolDown`.

## Organisation du code

```
Lucie/
├─ App.xaml(.cs)            démarrage, services partagés
├─ Models/AppSettings.cs    réglages + commandes par défaut
├─ Native/Win32.cs          souris, inactivité, clavier, fenêtre
├─ Services/
│  ├─ SpeechService.cs      micro + Vosk (mot d'activation, puis commande)
│  ├─ CommandInterpreter.cs phrase entendue → commande (tolérant)
│  ├─ ActionExecutor.cs     exécute la commande
│  ├─ KeyboardSimulator.cs  « tape » les raccourcis
│  ├─ MediaService.cs       musique en cours (contrôles média Windows)
│  ├─ SoundService.cs       sons doux synthétisés
│  ├─ ScreenCapture.cs      capture d'écran
│  ├─ AppLauncher.cs        trouve une appli dans le menu Démarrer
│  ├─ AudioDevices.cs       liste des micros / sorties
│  └─ SettingsService.cs    %LocalAppData%\Lucie\settings.json
└─ Views/
   ├─ LucieFace.xaml(.cs)   le visage animé
   ├─ WidgetWindow.xaml(.cs) le widget flottant + mode lecteur
   └─ SettingsWindow.xaml(.cs) la page de paramètres
```

## À savoir

- **Le nom doit exister en français** pour que Vosk le reconnaisse (Lucie, Emma, Léo… marchent ; un nom inventé non).
- La zone transparente autour du widget (pour le halo et les Z) capte les clics sur quelques pixels.
- Le micro tourne en continu, mais seul le mot d'activation est analysé tant que Lucie dort.

## Idées pour la suite

- Comprendre des phrases libres avec une IA en ligne (clé API dans les paramètres).
- Icône dans la zone de notification.
- Installeur (MSIX ou setup classique).
- Raccourci clavier global pour parler à Lucie.
