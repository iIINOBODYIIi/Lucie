using System.Text.Json;
using NAudio.Wave;
using Vosk;

namespace Lucie.Services;

/// <summary>
/// Écoute le micro en continu, hors ligne (Vosk).
/// 1. Mode veille : ne cherche que « Salut &lt;nom&gt; » (grammaire très réduite, léger).
/// 2. Mode écoute : transcrit la phrase suivante et la renvoie comme commande.
/// Les événements arrivent sur le fil audio : à renvoyer vers l'interface.
/// </summary>
public sealed class SpeechService : IDisposable
{
    private const float Rate = 16000f;
    private static readonly TimeSpan ListenWindow = TimeSpan.FromSeconds(7);

    private readonly SettingsService _settings;
    private readonly object _lock = new();

    private Model? _model;
    private VoskRecognizer? _wake;
    private VoskRecognizer? _command;
    private WaveInEvent? _wave;
    private bool _listening;
    private DateTime _deadline;
    private string _name = "lucie";

    public event Action? WakeDetected;
    public event Action<string>? CommandHeard;
    public event Action? ListenTimedOut;
    public event Action<double>? LevelChanged;
    public event Action<string>? Error;

    public bool ModelAvailable => _model is not null;

    /// <summary>Dossier où placer le modèle français (voir README).</summary>
    public static string PreferredModelDir { get; } =
        Path.Combine(SettingsService.DataDir, "VoiceModel", "vosk-model-fr");

    private static string? FindModelDir()
    {
        string[] candidates =
        {
            PreferredModelDir,
            Path.Combine(AppContext.BaseDirectory, "VoiceModel", "vosk-model-fr"),
        };
        return candidates.FirstOrDefault(d => Directory.Exists(Path.Combine(d, "am")) || File.Exists(Path.Combine(d, "conf", "model.conf")));
    }

    public SpeechService(SettingsService settings) => _settings = settings;

    /// <summary>(Re)démarre le micro et la reconnaissance — à rappeler si le micro ou le nom change.</summary>
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

        BuildRecognizers();

        try
        {
            _wave = new WaveInEvent
            {
                DeviceNumber = AudioDevices.FindInput(_settings.Current.MicrophoneName),
                WaveFormat = new WaveFormat((int)Rate, 16, 1),
                BufferMilliseconds = 100
            };
            _wave.DataAvailable += OnAudio;
            _wave.StartRecording();
        }
        catch (Exception ex)
        {
            _wave?.Dispose();
            _wave = null;
            Error?.Invoke("Micro indisponible : " + ex.Message);
        }
    }

    /// <summary>Passe directement en écoute (clic sur le widget).</summary>
    public void ListenNow()
    {
        lock (_lock)
        {
            if (_command is null)
            {
                Error?.Invoke("Modèle vocal introuvable");
                return;
            }
            _command.Reset();
            _listening = true;
            _deadline = DateTime.UtcNow + ListenWindow;
        }
        WakeDetected?.Invoke();
    }

    private void BuildRecognizers()
    {
        lock (_lock)
        {
            _wake?.Dispose();
            _command?.Dispose();
            _wake = _command = null;
            _listening = false;
            if (_model is null) return;

            _name = CommandInterpreter.Normalize(_settings.Current.AssistantName);
            if (_name.Length == 0) _name = "lucie";

            var grammar = JsonSerializer.Serialize(new[]
            {
                $"salut {_name}", $"hey {_name}", $"ok {_name}", $"coucou {_name}", _name, "[unk]"
            });
            _wake = new VoskRecognizer(_model, Rate, grammar);
            _command = new VoskRecognizer(_model, Rate);
        }
    }

    private void OnAudio(object? sender, WaveInEventArgs e)
    {
        LevelChanged?.Invoke(ComputeLevel(e.Buffer, e.BytesRecorded));

        Action? after = null;
        lock (_lock)
        {
            if (_wake is null || _command is null) return;

            if (!_listening)
            {
                bool final = _wake.AcceptWaveform(e.Buffer, e.BytesRecorded);
                string text = Field(final ? _wake.Result() : _wake.PartialResult(), final ? "text" : "partial");
                if (ContainsName(text))
                {
                    _wake.Reset();
                    _command.Reset();
                    _listening = true;
                    _deadline = DateTime.UtcNow + ListenWindow;
                    after = () => WakeDetected?.Invoke();
                }
            }
            else
            {
                if (_command.AcceptWaveform(e.Buffer, e.BytesRecorded))
                {
                    string text = Field(_command.Result(), "text");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        _listening = false;
                        _wake.Reset();
                        after = () => CommandHeard?.Invoke(text);
                    }
                }
                else if (Field(_command.PartialResult(), "partial").Length > 0)
                {
                    // Tu es en train de parler : on te laisse finir.
                    var min = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                    if (_deadline < min) _deadline = min;
                }

                if (_listening && DateTime.UtcNow > _deadline)
                {
                    _listening = false;
                    string text = Field(_command.FinalResult(), "text");
                    _wake.Reset();
                    after = string.IsNullOrWhiteSpace(text)
                        ? () => ListenTimedOut?.Invoke()
                        : () => CommandHeard?.Invoke(text);
                }
            }
        }
        after?.Invoke();
    }

    private bool ContainsName(string text) =>
        text.Length > 0 && $" {text} ".Contains($" {_name} ", StringComparison.Ordinal);

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

    private void StopAudio()
    {
        if (_wave is null) return;
        _wave.DataAvailable -= OnAudio;
        try { _wave.StopRecording(); } catch { }
        _wave.Dispose();
        _wave = null;
    }

    public void Dispose()
    {
        StopAudio();
        lock (_lock)
        {
            _wake?.Dispose();
            _command?.Dispose();
            _wake = _command = null;
        }
        _model?.Dispose();
        _model = null;
    }
}
