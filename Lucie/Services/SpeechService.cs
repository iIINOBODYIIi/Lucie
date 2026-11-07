using System.Text.Json;
using Lucie.Models;
using NAudio.Wave;
using Vosk;

namespace Lucie.Services;

/// <summary>
/// Reconnaissance vocale hors ligne (Vosk).
///
/// Mode « toujours à l'écoute » : le micro tourne, mais Lucie ne réagit que si elle
/// entend son nom (« Salut Lucie »). La commande peut suivre dans la même phrase
/// (« Salut Lucie, ouvre Discord ») ou juste après le bip.
///
/// Mode « à l'activation » : le micro est coupé. Il ne s'allume qu'après un clic
/// sur le widget ou le raccourci clavier, le temps d'une commande.
///
/// Les événements arrivent sur le fil audio : à renvoyer vers l'interface.
/// </summary>
public sealed class SpeechService : IDisposable
{
    private const float Rate = 16000f;
    private static readonly TimeSpan ListenWindow = TimeSpan.FromSeconds(7);
    private static readonly HashSet<string> Greetings = new() { "salut", "hey", "he", "ok", "okay", "coucou", "dis", "bonjour", "eh", "allo" };

    private readonly SettingsService _settings;
    private readonly object _lock = new();

    private Model? _model;
    private VoskRecognizer? _recognizer;
    private WaveInEvent? _wave;
    private volatile bool _recording;
    private bool _muted;
    private bool _awake;
    private bool _alwaysListen;
    private DateTime _deadline;
    private string[] _name = { "lucie" };

    public event Action? WakeDetected;
    public event Action<string>? CommandHeard;
    public event Action? ListenTimedOut;
    public event Action<double>? LevelChanged;
    public event Action<string>? Error;
    public event Action? Ready;
    /// <summary>Phrase entendue (pour l'afficher dans les paramètres).</summary>
    public event Action<string>? Heard;

    public bool ModelAvailable => _model is not null;

    /// <summary>
    /// Micro coupé : Lucie n'écoute plus son nom. Un clic sur le widget ou le raccourci
    /// permet quand même de lui parler ponctuellement.
    /// </summary>
    public bool Muted
    {
        get => _muted;
        set
        {
            if (_muted == value) return;
            _muted = value;
            bool awake;
            lock (_lock) awake = _awake;
            if (_muted && !awake) StopAudioSoon();
            else if (!_muted && _alwaysListen) StartAudio();
        }
    }
    public bool IsAwake { get { lock (_lock) return _awake; } }
    public string LastHeard { get; private set; } = "";

    /// <summary>Dossier où placer le modèle français.</summary>
    public static string PreferredModelDir { get; } =
        Path.Combine(SettingsService.DataDir, "VoiceModel", "vosk-model-fr");

    public SpeechService(SettingsService settings) => _settings = settings;

    private static string? FindModelDir()
    {
        string[] candidates =
        {
            PreferredModelDir,
            Path.Combine(AppContext.BaseDirectory, "VoiceModel", "vosk-model-fr"),
        };
        return candidates.FirstOrDefault(d => Directory.Exists(Path.Combine(d, "am")) || File.Exists(Path.Combine(d, "conf", "model.conf")));
    }

    /// <summary>(Re)démarre tout — à rappeler si le micro, le nom ou le mode d'écoute change.</summary>
    public void Start()
    {
        StopAudio();

        if (_model is null)
        {
            var dir = FindModelDir();
            if (dir is null)
            {
                Error?.Invoke("Modèle vocal introuvable");
            }
            else
            {
                try
                {
                    Vosk.Vosk.SetLogLevel(-1);
                    _model = new Model(dir);
                }
                catch (Exception ex)
                {
                    Error?.Invoke("Impossible de charger le modèle vocal : " + ex.Message);
                }
            }
        }

        lock (_lock)
        {
            _recognizer?.Dispose();
            _recognizer = _model is null ? null : new VoskRecognizer(_model, Rate);
            _awake = false;
            _alwaysListen = _settings.Current.ListenMode == ListenMode.Always;

            var name = CommandInterpreter.Normalize(_settings.Current.AssistantName);
            _name = (name.Length == 0 ? "lucie" : name).Split(' ');
        }

        try
        {
            _wave = new WaveInEvent
            {
                DeviceNumber = AudioDevices.FindInput(_settings.Current.MicrophoneName),
                WaveFormat = new WaveFormat((int)Rate, 16, 1),
                BufferMilliseconds = 100
            };
            _wave.DataAvailable += OnAudio;
            _wave.RecordingStopped += (_, e) =>
            {
                _recording = false;
                if (e.Exception is not null) Error?.Invoke("Micro coupé : " + e.Exception.Message);
            };
        }
        catch (Exception ex)
        {
            _wave = null;
            Error?.Invoke("Micro indisponible : " + ex.Message);
            return;
        }

        if (_alwaysListen && !_muted) StartAudio();
        if (_model is not null) Ready?.Invoke();
    }

    /// <summary>Écoute tout de suite une commande (clic sur le widget ou raccourci).</summary>
    public void ListenNow()
    {
        lock (_lock)
        {
            if (_recognizer is null)
            {
                Error?.Invoke("Modèle vocal introuvable");
                return;
            }
            _recognizer.Reset();
            _awake = true;
            _deadline = DateTime.UtcNow + ListenWindow;
        }
        StartAudio();
        WakeDetected?.Invoke();
    }

    /// <summary>Arrête d'écouter la commande en cours.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _awake = false;
            _recognizer?.Reset();
        }
        if (!_alwaysListen || _muted) StopAudioSoon();
    }

    private void OnAudio(object? sender, WaveInEventArgs e)
    {
        LevelChanged?.Invoke(ComputeLevel(e.Buffer, e.BytesRecorded));

        Action? after = null;
        bool finished = false;

        lock (_lock)
        {
            if (_recognizer is null) return;
            var now = DateTime.UtcNow;

            if (_recognizer.AcceptWaveform(e.Buffer, e.BytesRecorded))
            {
                var words = Words(Field(_recognizer.Result(), "text"));
                int wake = FindName(words);

                if (_awake)
                {
                    var command = AfterName(words, wake);
                    if (command.Length > 0)
                    {
                        _awake = false;
                        finished = true;
                        after = () => { Report(command); CommandHeard?.Invoke(command); };
                    }
                    else
                    {
                        _deadline = now + ListenWindow; // il a juste dit « Salut Lucie » : on attend la suite
                    }
                }
                else if (_alwaysListen && !_muted && wake >= 0)
                {
                    var command = AfterName(words, wake);
                    if (command.Length > 0)
                    {
                        // « Salut Lucie, ouvre Discord » d'une traite
                        finished = true;
                        after = () => { WakeDetected?.Invoke(); Report(command); CommandHeard?.Invoke(command); };
                    }
                    else
                    {
                        _awake = true;
                        _deadline = now + ListenWindow;
                        after = () => WakeDetected?.Invoke();
                    }
                }
            }
            else
            {
                var partial = Words(Field(_recognizer.PartialResult(), "partial"));
                if (!_awake && _alwaysListen && !_muted && FindName(partial) >= 0)
                {
                    // Réaction immédiate dès qu'on entend son nom
                    _awake = true;
                    _deadline = now + ListenWindow + TimeSpan.FromSeconds(1);
                    after = () => WakeDetected?.Invoke();
                }
                else if (_awake && partial.Length > 0)
                {
                    // Tu es en train de parler : on te laisse finir.
                    var min = now + TimeSpan.FromSeconds(2);
                    if (_deadline < min) _deadline = min;
                }
            }

            if (_awake && now > _deadline)
            {
                _awake = false;
                finished = true;
                var words = Words(Field(_recognizer.FinalResult(), "text"));
                var command = AfterName(words, FindName(words));
                after = command.Length > 0
                    ? () => { Report(command); CommandHeard?.Invoke(command); }
                    : () => ListenTimedOut?.Invoke();
            }
        }

        after?.Invoke();
        if (finished && (!_alwaysListen || _muted)) StopAudioSoon();
    }

    private void Report(string text)
    {
        LastHeard = text;
        Heard?.Invoke(text);
    }

    // ───── Repérer son nom ─────

    private int FindName(string[] words)
    {
        for (int i = 0; i + _name.Length <= words.Length; i++)
        {
            bool exact = true;
            for (int j = 0; j < _name.Length; j++)
                if (words[i + j] != _name[j]) { exact = false; break; }
            if (exact) return i;

            // Nom presque reconnu (« lucy », « luci »…) : accepté s'il suit « salut », « hey »…
            if (_name.Length == 1 && _name[0].Length >= 4 && i > 0 && Greetings.Contains(words[i - 1])
                && CommandInterpreter.Levenshtein(words[i], _name[0]) <= 1)
                return i;
        }
        return -1;
    }

    private string AfterName(string[] words, int nameIndex)
    {
        if (nameIndex < 0)
        {
            // Pas de nom (après le bip) : « bonjour » tout seul reste une vraie phrase.
            var all = string.Join(' ', words.SkipWhile(Greetings.Contains));
            return all.Length > 0 ? all : string.Join(' ', words);
        }

        var after = string.Join(' ', words.Skip(nameIndex + _name.Length).SkipWhile(Greetings.Contains));
        if (after.Length > 0) return after;

        // « Salut, comment tu vas Lucie ? » : la phrase est avant son nom.
        return string.Join(' ', words.Take(nameIndex).SkipWhile(Greetings.Contains));
    }

    private static string[] Words(string text) =>
        CommandInterpreter.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    // ───── Micro ─────

    private void StartAudio()
    {
        if (_wave is null || _recording) return;
        try
        {
            _wave.StartRecording();
            _recording = true;
        }
        catch (Exception ex)
        {
            Error?.Invoke("Micro indisponible : " + ex.Message);
        }
    }

    private void StopAudioSoon()
    {
        var wave = _wave;
        Task.Run(() =>
        {
            try { wave?.StopRecording(); } catch { }
        });
    }

    private void StopAudio()
    {
        if (_wave is null) return;
        _wave.DataAvailable -= OnAudio;
        try { _wave.StopRecording(); } catch { }
        _wave.Dispose();
        _wave = null;
        _recording = false;
    }

    private static double ComputeLevel(byte[] buffer, int bytes)
    {
        if (bytes < 2) return 0;
        double sum = 0;
        int samples = bytes / 2;
        for (int i = 0; i + 1 < bytes; i += 2)
        {
            short s = BitConverter.ToInt16(buffer, i);
            sum += (double)s * s;
        }
        double rms = Math.Sqrt(sum / samples) / short.MaxValue;
        return Math.Min(1.0, rms * 5);
    }

    private static string Field(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(name, out var v) ? (v.GetString() ?? "").Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    public void Dispose()
    {
        StopAudio();
        lock (_lock)
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
        _model?.Dispose();
        _model = null;
    }
}
