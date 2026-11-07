<div align="center">

<img src="docs/icon.png" width="128" alt="Lucie icon">

# Lucie

**A tiny voice assistant that lives on your Windows desktop.**

Say *« Salut Lucie »*, then tell her what to do. She opens your apps, controls your music, mutes you on Discord and takes screenshots, and answers with soft little sounds instead of a voice.

[**⬇ Download the latest version**](https://github.com/iIINOBODYIIi/Lucie/releases/latest)

Windows 10 / 11 · 64-bit · free · works offline

<br>

<img src="docs/banner.png" width="100%" alt="Lucie's moods: idle, wow, music, asleep">

</div>

---

## ✨ What she can do

- **🎙️ Wake word**: say *« Salut Lucie »* and she's listening. You can say it all in one go: *« Salut Lucie, ouvre Discord »*.
- **🫧 Hover menu**: move your mouse over Lucie to mute the mic, type a command or open the settings.
- **🖱️ Click or shortcut**: click the widget or press **Ctrl + Alt + L** (works in games too) to talk without the wake word.
- **🪟 Apps and their shortcuts**: *« Ouvre Cider »*, *« Montre-moi Discord »*. If the app is already open, even hidden in the tray, Lucie brings it to the front. Each app has its own voice shortcuts (*« Coupe mon micro »* → `Ctrl+Shift+M` in Discord). Record the keys in one click, then test them.
- **🎵 Music and volume**: play, pause, next, previous, volume up, down or mute. Works with Spotify, Apple Music, Cider, your browser, and more.
- **🎧 Live player**: when a song starts, the widget stretches to show the cover, title and controls, then tucks itself away.
- **🎮 Discord and Game Bar**: mute your mic, deafen yourself, start a screen recording, take a screenshot.
- **👀 She feels alive**: she breathes, blinks, glances around, says *“wow”* and follows your mouse when it gets close, puts her headphones on when music plays, and falls asleep with little Z's when your PC is idle.
- **🔒 Private**: speech recognition runs **100 % offline** on your PC. Nothing is sent to the internet.

## 🚀 Getting started

1. **Download** `Lucie-Windows-x64.zip` from the [latest release](https://github.com/iIINOBODYIIi/Lucie/releases/latest).
2. **Extract it** (right-click › *Extract All…*) somewhere it will stay, for example `Documents\Lucie`.
3. **Run `Lucie.exe`.**
   Windows may show a blue *“Windows protected your PC”* screen because the app isn't signed. Click **More info** › **Run anyway**.
4. On first launch, the settings open: click **Download automatically** to get the French voice model (~40 MB).
5. Say **« Salut Lucie »** 🎤

> 💡 Right-click the widget for the menu: **Talk to Lucie**, **Settings**, **Quit**.
> Hold the widget and move your mouse to place it anywhere on the screen.

## 🗣️ What to say

> Lucie understands **French**: the voice model is French. Every phrase can be changed in **Settings › Commands & shortcuts**, and you can add your own.

| Say… | Lucie… |
| --- | --- |
| « Ouvre / lance / montre-moi *app* » | opens the app, or brings it to the front |
| « Ouvre le navigateur » | opens your web browser |
| « Lance la musique » · « Mets pause » | plays / pauses the music |
| « Musique suivante » · « Musique précédente » | next / previous track |
| « Monte le son » · « Baisse le son » · « Coupe le son » | volume up / down / mute |
| « Fais une capture » | takes a screenshot (saved in *Pictures › Lucie*) |
| « Lance l'enregistrement » | starts / stops screen recording (Xbox Game Bar) |
| « Coupe mon micro » | toggles your Discord microphone |
| « Mets-moi en sourdine » | toggles Discord deafen |

### Discord setup

Discord has no default shortcut for mute and deafen. In **Discord › Settings › Keybinds**, add:

| Discord action | Shortcut |
| --- | --- |
| Toggle Mute | `Ctrl + Shift + M` |
| Toggle Deafen | `Ctrl + Shift + D` |

## ⚙️ Settings

- **General**: her name (anyone can rename her), listening mode (*always ready* or *only when I activate her*), the talk shortcut, start with Windows.
- **Audio devices**: choose your microphone and output, with a live mic level meter.
- **Widget**: position (9 spots or drag-and-drop), size, transparency, sleep delay, mouse detection zone, live music player.
- **Look**: transparent settings window, accent colour.
- **Sounds**: on/off, volume, three sound themes.
- **Apps & shortcuts**: one card per app: the names you call it (e.g. `Cider / cidre`), the program, and its keyboard shortcuts with a **record** and a **test** button.
- **General commands**: music, volume, screenshot, links and Windows shortcuts, plus **the last sentence Lucie heard** to help you fine-tune.

## ❓ FAQ

<details>
<summary><b>Lucie doesn't react when I talk</b></summary>

- Check **Settings › Audio devices**: the mic level bar should move when you speak. If not, pick the right microphone.
- Make sure Windows allows desktop apps to use the microphone (*Settings › Privacy › Microphone*).
- Look at **the last sentence heard** in *Commands & shortcuts* to see how Lucie understood you.
</details>

<details>
<summary><b>She doesn't understand an app's name</b></summary>

The voice model may hear it differently (e.g. *Cider* → *cidre*). In **Settings › Apps**, add the way she hears it after a `/`: `Cider / cidre`.
</details>

<details>
<summary><b>Can I rename her?</b></summary>

Yes, in **Settings › General**. Pick a common first name so the voice model knows it well.
</details>

<details>
<summary><b>Where are my settings stored?</b></summary>

In `%LocalAppData%\Lucie`. To start fresh, quit Lucie and delete `settings.json` there.
</details>

---

## 🛠️ For developers

Built with **C# / WinUI 3** (Windows App SDK), **Vosk** for offline speech recognition and **NAudio** for audio.

<details>
<summary><b>Build from source</b></summary>

1. Install **Visual Studio 2022** with the **.NET desktop development** workload and **Windows App SDK C# Templates**.
2. Open `Lucie.sln`, select the **x64** platform, press **F5**.
3. To produce a release zip locally, run `Publier.bat`.

Releases are built automatically by GitHub Actions (`.github/workflows/publier.yml`): **Actions › Publier Lucie › Run workflow**.
</details>

<details>
<summary><b>Project layout</b></summary>

```
Lucie/
├─ App.xaml(.cs)              startup, shared services, crash report
├─ Assets/                    app icon
├─ Models/AppSettings.cs      settings, default commands and apps
├─ Native/Win32.cs            window shape, focus, mouse, idle time, hotkeys
├─ Services/
│  ├─ SpeechService.cs        microphone + Vosk (wake word, then command)
│  ├─ CommandInterpreter.cs   sentence → command or app
│  ├─ ActionExecutor.cs       runs a command
│  ├─ AppLauncher.cs          opens an app or brings it to the front
│  ├─ KeyboardSimulator.cs    sends keyboard shortcuts
│  ├─ HotkeyService.cs        global talk shortcut
│  ├─ MediaService.cs         now playing (Windows media controls)
│  ├─ SoundService.cs         soft synthesized sounds
│  ├─ ModelDownloader.cs      downloads the voice model
│  └─ …
└─ Views/
   ├─ LucieFace.xaml(.cs)     the animated face
   ├─ WidgetWindow.xaml(.cs)  the floating capsule + music player
   └─ SettingsWindow.xaml(.cs)
```
</details>
