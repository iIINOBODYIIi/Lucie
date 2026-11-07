# Lucie — assistant vocal pour Windows

Un petit widget flottant avec un visage qui vit. Dis « Salut Lucie », elle t'écoute, puis exécute ta commande. Elle répond par des sons doux, pas par une voix.

## Ce qu'elle sait faire

- **Activation à la voix** : « Salut Lucie » (le nom se change dans les paramètres). Tu peux enchaîner d'une traite : « Salut Lucie, ouvre Discord ». La reconnaissance tourne hors ligne avec Vosk : rien ne part sur internet.
- **Deux modes d'écoute** : toujours prête (elle réagit à son nom), ou seulement quand tu l'actives (micro coupé le reste du temps).
- **Clic sur le widget** ou **raccourci clavier** (Ctrl+Alt+L par défaut) : elle t'écoute tout de suite. Refais-le pour annuler.
- **Applications** : « Ouvre Cider », « Montre-moi Discord »… Si l'appli est déjà ouverte, même cachée, elle est remise au premier plan. Tu ajoutes tes applis (.exe) dans les paramètres.
- **Musique** : lance la musique, pause, suivante, précédente. **Volume** : monte / baisse / coupe le son.
- **Raccourcis** : Discord (micro, sourdine), enregistrement d'écran (Game Bar), capture d'écran (Images\Lucie).
- **Visage vivant** : elle respire, cligne des yeux au hasard et regarde à gauche et à droite. Quand la souris approche, elle fait « waouh » et la suit des yeux. Avec de la musique, elle met son casque, et quand le PC est inactif, elle s'endort avec les petits Z.
- **Mode lecteur** : quand un morceau démarre ou change, le widget s'étire en gélule avec la pochette ronde, le titre, et précédent/pause/suivant.
- **Widget** : une gélule en verre dépoli. Pour la déplacer, reste appuyé dessus et bouge la souris.

## Toutes les phrases (par défaut)

| Tu dis… | Lucie… |
| --- | --- |
| « Ouvre / lance / montre-moi / affiche *appli* » | ouvre l'appli ou la met devant |
| « Ouvre le navigateur / internet / google » | ouvre Google |
| « Lance la musique / mets la musique / reprends la musique / lecture » | lance la lecture |
| « Mets pause / pause / arrête la musique » | met en pause |
| « Musique suivante / chanson suivante / suivant » | passe au morceau suivant |
| « Musique précédente / chanson précédente / précédent » | revient au morceau précédent |
| « Monte le son / plus fort » — « Baisse le son / moins fort » | règle le volume |
| « Coupe le son / remets le son » | coupe ou remet le son |
| « Fais une capture / capture d'écran » | fait une capture d'écran |
| « Lance l'enregistrement / arrête l'enregistrement » | Win+Alt+R (Game Bar) |
| « Coupe mon micro / active mon micro » | Ctrl+Maj+M (Discord) |
| « Mets-moi en sourdine / enlève la sourdine » | Ctrl+Maj+D (Discord) |

Tout se modifie dans **Paramètres › Commandes et raccourcis**. Sépare plusieurs façons de dire la même chose par `/`. La **dernière phrase entendue** y est affichée : pratique pour voir comment Lucie comprend un mot (par exemple « Cider » entendu « cidre »), puis l'ajouter comme variante.

## Installation

### 1. Outils

- Windows 10 (1809+) ou Windows 11, PC 64 bits.
- **Visual Studio 2022**, avec la charge de travail **« Développement .NET Desktop »**. Dans les détails, coche **« Windows App SDK C# Templates »** (WinUI 3).

### 2. Le modèle vocal français (obligatoire pour la voix)

**Méthode simple** : lance Lucie. Les paramètres s'ouvrent avec un bandeau orange : clique sur **« Télécharger automatiquement »**. Elle télécharge et installe le modèle (~40 Mo) toute seule.

**Méthode manuelle** (si le téléchargement automatique échoue) :

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

## Mettre l'appli en ligne

1. Double-clique sur **`Publier.bat`** (à côté de `Lucie.sln`).
2. Il crée `publish\Lucie-Windows-x64.zip`, une version qui marche sans Visual Studio et sans rien installer.
3. Mets ce zip en ligne (par exemple dans une « Release » GitHub).
4. Pour l'utiliser, on dézippe le fichier et on lance `Lucie.exe`. Le modèle vocal se télécharge au premier lancement.

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
