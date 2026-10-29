using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Edge.Feeding;
using UnityEngine;

namespace Edge.Narration
{
    // AI narrator for the EDGE bat. Read-only observer: it samples DoseModel,
    // the bat transform, LampZones and EdgeWeatherSky a few times a second (never
    // per frame), detects meaningful context changes, and asks Gemini for one short
    // documentary line. Any failure (no key, timeout, 429, offline) falls back to an
    // offline template chosen from the same context, so the demo never goes silent.
    // It never writes to the input path (UdpLink / BatFlightController).
    // With an EdgeFeedingGame in the scene it narrates the game instead: each bat has its own comic
    // persona (prompt, offline lines and voice), the cocky light-tolerant bat and the anxious
    // light-shy one, reacting to rounds, moths, energy, night vision and light (real or in game).
    public class EdgeNarrator : MonoBehaviour
    {
        public enum Trigger
        {
            None, Start, EnterLamp, LeaveLamp, Budget75, Budget50, Budget25, Budget10,
            BudgetSpent, WeatherChange, DarkStretch, CrossPath, External, Manual,
            RoundStart, MothEaten, EnergyLow, NightVisionLow, RoundEnd   // feeding game
        }

        [Header("Scene refs (auto-found if empty)")]
        public DoseModel dose;
        public BatFlightController flight;
        public Transform bat;
        public EdgeWeatherSky weather;
        public LampZone[] lamps;
        [Tooltip("Feeding game to narrate (auto-found). Without one, the exhibit narration runs.")]
        public EdgeFeedingGame game;

        [Header("Voice")]
        public NarrationVoiceKind voiceKind = NarrationVoiceKind.MacSay;
        public string macVoice = "Karen";          // en_AU; `say -v ?` lists voices
        [Range(90, 300)] public int macRate = 170;
        public string esp32Host = "192.168.4.1";
        public int esp32Port = 1235;
        [Tooltip("GeminiTts: if TTS fails twice, speak with the Mac voice instead (changes voice mid-session). Off = subtitle-only for that line.")]
        public bool ttsFallbackToMacVoice = false;
        [Tooltip("GeminiTts: min seconds between lines so a text+TTS pair fits the shared 8/min rate limit.")]
        public float ttsMinLineInterval = 15f;

        [Header("Feeding game personas (one voice per bat)")]
        public string macVoiceTolerant = "Rocko (English (UK))";
        public string macVoiceAverse = "Flo (English (UK))";
        [Tooltip("Gemini prebuilt voice, e.g. Fenrir = excitable, Puck = upbeat")] public string ttsVoiceTolerant = "Fenrir";
        public string ttsStyleTolerant = "cocky, loud, hyped-up party animal; fast and grinning, over the top";
        [Tooltip("Gemini prebuilt voice, e.g. Enceladus = breathy, Leda = youthful")] public string ttsVoiceAverse = "Enceladus";
        public string ttsStyleAverse = "jittery, anxious, breathy; dramatic comic panic, then big sighs of relief";
        [Tooltip("Speak after the first moth and then every Nth")] public int mothLineEvery = 4;

        [Header("AI")]
        public bool useGemini = true;
        public GeminiClient gemini = new GeminiClient();

        [Header("Pacing")]
        public float sampleInterval = 0.5f;        // s between state samples
        public float cooldownSeconds = 10f;        // min gap between lines
        public float urgentCooldownSeconds = 5f;   // budget 10% / spent may cut in sooner
        public float darkStretchSeconds = 30f;     // continuous darkness that earns a line
        public float pendingLifetime = 6f;         // stale events are dropped
        public int historySize = 8;                // no repeats of the last N lines
        public bool narrateOnStart = true;
        public bool narratePathCrossing = true;

        [Header("Subtitle")]
        public bool showSubtitle = true;
        public float subtitleSeconds = 6f;

        [Header("Debug (read-only)")]
        [SerializeField] string lastLine = "";
        [SerializeField] string lastSource = "";
        [SerializeField] string lastContext = "";

        // ----- state -----
        INarrationVoice _voice;
        AudioSource _audio;
        readonly List<string> _history = new List<string>();
        float _t0, _lastSpoke = -999f, _subtitleUntil;
        string _subtitle = "";
        int _thresholdsHit;            // bitmask 75/50/25/10/spent
        int _lampInside = -1;          // id of lamp we are in, -1 = dark
        float _darkSince, _lastDarkLine;
        float _prevTotal, _doseRatePctPerMin;
        float _prevSide;
        bool _crossedRecently;
        WeatherType _lastWeather;
        bool _haveWeather;
        Trigger _pending = Trigger.None;
        float _pendingAt;
        string _externalDetail = "";
        string _roundResult = "";
        bool _gameInLight;
        int _requestToken;
        Snapshot _snap;

        struct Snapshot
        {
            public float budgetLeftPct, dosePct, alongPath, sideOffset, altitude, t, darkFor, nearestDist, cloudC;
            public int nearestId, moths, energyPct;
            public bool insideLamp, onPath, tag, haveWeather;
            public WeatherType weather;
        }

        static readonly string SystemPrompt =
            "You narrate a live nature documentary about a Gould's long-eared bat (Nyctophilus gouldi) " +
            "foraging at a Sydney bushland edge beside a lit footpath. Speak as the bat's close narrator, " +
            "first-person-ish (\"I\"/\"we\" for the bat is fine), hushed and vivid. " +
            "Each reply: ONE sentence, at most 20 words, no quotes, no emoji, no hashtags. " +
            "Ground it in the context JSON: react to the event. Be factual about light pollution " +
            "(artificial light at night disrupts slow, light-shy bats like Nyctophilus; skyglow is amplified " +
            "by cloud; dark corridors matter; dose here is measured in full-moon-equivalents). " +
            "Never invent numbers not in the context. Vary wording; never reuse phrasing from the recent lines.";

        // Feeding game: the same lamps are a party for one bat and a nightmare for the other.
        static readonly string GameRules =
            "You are the voice of a bat in a museum game: a visitor flies you by hand along a lit footpath " +
            "through Sydney bushland at night, catching moths. Reply with ONE short, funny sentence " +
            "(at most 15 words) reacting to the event in the context JSON, in character. " +
            "Keep it PG and kind: tease the lamps and the situation, never the visitor. " +
            "Keep the light facts true: streetlights pull moths out of the bush; light-tolerant bats feast there, " +
            "light-shy bats avoid the light and lose that food. Never invent numbers that are not in the context. " +
            "No quotes, emoji or hashtags. Never repeat or closely paraphrase the recent lines.";

        static readonly string TolerantPersona =
            "You are a Gould's wattled bat: a cocky, loud party animal who LOVES streetlights. " +
            "Every lamp is your all-you-can-eat moth buffet and the night is one big party. Brag and hype yourself up.";

        static readonly string AversePersona =
            "You are a Gould's long-eared bat: a jittery, dramatic introvert who hates light. " +
            "Lamps are blinding spotlights, the dark is your safe space, and it is SO unfair that all the moths " +
            "are partying at the lamps. Panic comically in light, sigh with relief in the dark.";

        string PersonaPrompt() => (Tolerant ? TolerantPersona : AversePersona) + " " + GameRules;

        void Awake()
        {
            if (!dose) dose = FindAnyObjectByType<DoseModel>();
            if (!flight) flight = FindAnyObjectByType<BatFlightController>();
            if (!bat && flight) bat = flight.transform;
            if (!weather) weather = FindAnyObjectByType<EdgeWeatherSky>();
            if (lamps == null || lamps.Length == 0) lamps = FindObjectsByType<LampZone>();
            if (!game) game = FindAnyObjectByType<EdgeFeedingGame>();
            if (game) ApplyPersona(game.variant);
            BuildVoice();
        }

        public void BuildVoice()
        {
            _voice?.Stop();
            var mac = new MacSayVoice(macVoice, macRate);
            switch (voiceKind)
            {
                case NarrationVoiceKind.GeminiTts:
                    if (!_audio) _audio = GetComponent<AudioSource>();
                    if (!_audio) { _audio = gameObject.AddComponent<AudioSource>(); _audio.playOnAwake = false; _audio.spatialBlend = 0f; }
                    _voice = new GeminiTtsVoice(gemini, _audio, mac, ttsFallbackToMacVoice);
                    gemini.BeginSession();
                    break;
                case NarrationVoiceKind.Esp32Speaker: _voice = new Esp32SpeakerVoice(esp32Host, esp32Port); break;
                case NarrationVoiceKind.SubtitleOnly: _voice = new SilentVoice(); break;
                default: _voice = mac; break;
            }
        }

        void OnEnable()
        {
            if (weather) weather.WeatherApplied += OnWeather;
            EdgeLightEffects.RealLightHit += OnRealLight;
            if (game) { game.RoundStarted += OnRoundStarted; game.InsectEaten += OnInsectEaten; game.RoundEnded += OnRoundEnded; }
            StartCoroutine(SampleLoop());
        }

        void OnDisable()
        {
            if (weather) weather.WeatherApplied -= OnWeather;
            EdgeLightEffects.RealLightHit -= OnRealLight;
            if (game) { game.RoundStarted -= OnRoundStarted; game.InsectEaten -= OnInsectEaten; game.RoundEnded -= OnRoundEnded; }
            StopAllCoroutines();
            _voice?.Stop();
        }

        void Start()
        {
            _t0 = Time.time;
            _darkSince = Time.time;
            _lastDarkLine = Time.time;
            if (dose) _prevTotal = dose.TotalFmm;
            if (bat) _prevSide = Mathf.Sign(bat.position.x - PathX(bat.position.z));
            if (narrateOnStart && !game) Raise(Trigger.Start);   // the game speaks at each round start instead
            Debug.Log($"[EDGE Narration] voice={voiceKind} gemini={(useGemini ? gemini.model : "off")} key={GeminiClient.KeySource()} lamps={lamps?.Length ?? 0}");
        }

        void OnWeather(WeatherType t)
        {
            if (_haveWeather && t != _lastWeather) Raise(Trigger.WeatherChange);
            _lastWeather = t;
            _haveWeather = true;
        }

        // Light from the real room reaching the bat in the visitor's hand (EdgeLightEffects).
        void OnRealLight(string kind)
        {
            NotifyEvent(kind == "flash"
                ? "a real camera flash from someone in the room just hit the bat"
                : $"someone in the room is shining a real {kind} straight at the bat");
        }

        // ---------------- feeding game ----------------
        void OnRoundStarted(EdgeFeedingGame.Variant v)
        {
            _thresholdsHit = 0;
            _roundResult = "";
            _gameInLight = false;
            _darkSince = _lastDarkLine = Time.time;
            ApplyPersona(v);
            BuildVoice();   // the other bat's voice
            Raise(Trigger.RoundStart);
        }

        void OnInsectEaten(int score)
        {
            if (score == 1 || (mothLineEvery > 0 && score % mothLineEvery == 0)) Raise(Trigger.MothEaten);
        }

        void OnRoundEnded(EdgeFeedingGame.Variant v, int score, string reason)
        {
            _roundResult = reason ?? "";
            Raise(Trigger.RoundEnd);
        }

        bool Tolerant => game && game.variant == EdgeFeedingGame.Variant.LightTolerant;

        void ApplyPersona(EdgeFeedingGame.Variant v)
        {
            bool tolerant = v == EdgeFeedingGame.Variant.LightTolerant;
            macVoice = tolerant ? macVoiceTolerant : macVoiceAverse;
            gemini.ttsVoice = tolerant ? ttsVoiceTolerant : ttsVoiceAverse;
            gemini.ttsStyle = tolerant ? ttsStyleTolerant : ttsStyleAverse;
        }

        void SampleGame()
        {
            float now = Time.time;
            bool playing = game.CurrentState == EdgeFeedingGame.State.Playing;
            var s = new Snapshot { t = now - _t0, nearestId = -1, nearestDist = float.PositiveInfinity };
            s.insideLamp = playing && game.InLight;   // lamps and real light from the room
            s.moths = game.Score;
            s.energyPct = Mathf.RoundToInt(game.Hunger01 * 100f);
            s.budgetLeftPct = game.Budget01 * 100f;
            if (playing)
            {
                if (s.insideLamp && !_gameInLight) Raise(Trigger.EnterLamp);
                else if (!s.insideLamp && _gameInLight) { Raise(Trigger.LeaveLamp); _darkSince = now; }
                CheckThreshold(game.Hunger01 < 0.25f, 32, Trigger.EnergyLow);
                CheckThreshold(game.Preset.lightHurts && game.Budget01 < 0.3f, 64, Trigger.NightVisionLow);
            }
            _gameInLight = s.insideLamp;
            if (s.insideLamp) _darkSince = now;
            s.darkFor = now - _darkSince;
            if (playing && s.darkFor >= darkStretchSeconds && now - _lastDarkLine >= darkStretchSeconds && now - _lastSpoke >= darkStretchSeconds * 0.5f)
            {
                _lastDarkLine = now;
                Raise(Trigger.DarkStretch);
            }
            s.tag = flight && flight.TagActive;
            _snap = s;
        }

        // Public hook for other events (real-room light above, a tag button, ...).
        public void NotifyEvent(string detail)
        {
            _externalDetail = detail ?? "";
            Raise(Trigger.External);
        }

        // Speak now regardless of triggers (used by the editor Test menu / inspector).
        public void SpeakManual(string text)
        {
            if (_voice == null) BuildVoice();
            Deliver(text, "manual");
        }

        IEnumerator SampleLoop()
        {
            var wait = new WaitForSeconds(Mathf.Max(0.1f, sampleInterval));
            yield return null;
            while (true)
            {
                Sample(Mathf.Max(0.1f, sampleInterval));
                TryNarrate();
                yield return wait;
            }
        }

        void Sample(float dt)
        {
            if (game) { SampleGame(); return; }
            float now = Time.time;
            var s = new Snapshot { t = now - _t0, nearestId = -1, nearestDist = float.PositiveInfinity };

            if (dose)
            {
                float frac = dose.Fraction;
                s.dosePct = frac * 100f;
                s.budgetLeftPct = Mathf.Clamp(100f - s.dosePct, 0f, 100f);
                s.cloudC = dose.CloudC;
                float total = dose.TotalFmm;
                float ratePct = dose.BudgetTonight > 0.001f ? (total - _prevTotal) / dose.BudgetTonight * 100f / dt * 60f : 0f;
                _doseRatePctPerMin = Mathf.Lerp(_doseRatePctPerMin, Mathf.Max(0f, ratePct), 0.35f);
                _prevTotal = total;

                // thresholds fire once each, in order
                CheckThreshold(s.budgetLeftPct <= 75f, 1, Trigger.Budget75);
                CheckThreshold(s.budgetLeftPct <= 50f, 2, Trigger.Budget50);
                CheckThreshold(s.budgetLeftPct <= 25f, 4, Trigger.Budget25);
                CheckThreshold(s.budgetLeftPct <= 10f, 8, Trigger.Budget10);
                CheckThreshold(dose.Exhausted, 16, Trigger.BudgetSpent);
            }

            if (bat)
            {
                Vector3 p = bat.position;
                s.altitude = p.y;
                s.sideOffset = p.x - PathX(p.z);
                s.onPath = Mathf.Abs(s.sideOffset) < 1.5f;
                s.alongPath = AlongPath(p.z);
                float side = Mathf.Sign(s.sideOffset);
                if (side != 0f && _prevSide != 0f && side != _prevSide)
                {
                    _crossedRecently = true;
                    if (narratePathCrossing) Raise(Trigger.CrossPath);
                }
                if (side != 0f) _prevSide = side;

                int inside = -1;
                if (lamps != null)
                {
                    foreach (var l in lamps)
                    {
                        if (!l) continue;
                        Vector3 o = l.measureFrom ? l.measureFrom.position : l.transform.position;
                        float d = Vector3.Distance(p, o);
                        if (d < s.nearestDist) { s.nearestDist = d; s.nearestId = l.id; }
                        if (d < l.radius && inside < 0) inside = l.id;
                    }
                }
                s.insideLamp = inside >= 0;
                if (inside >= 0 && _lampInside < 0) Raise(Trigger.EnterLamp);
                else if (inside < 0 && _lampInside >= 0) { Raise(Trigger.LeaveLamp); _darkSince = now; }
                _lampInside = inside;
            }

            if (s.insideLamp) _darkSince = now;
            s.darkFor = now - _darkSince;
            if (s.darkFor >= darkStretchSeconds && now - _lastDarkLine >= darkStretchSeconds && now - _lastSpoke >= darkStretchSeconds * 0.5f)
            {
                _lastDarkLine = now;
                Raise(Trigger.DarkStretch);
            }

            if (weather && weather.hasWeather)
            {
                s.haveWeather = true;
                s.weather = weather.current;
                if (!_haveWeather) { _lastWeather = weather.current; _haveWeather = true; }
                else if (weather.current != _lastWeather) { OnWeather(weather.current); }
            }
            s.tag = flight && flight.TagActive;
            _snap = s;
        }

        void CheckThreshold(bool cond, int bit, Trigger t)
        {
            if (cond && (_thresholdsHit & bit) == 0)
            {
                _thresholdsHit |= bit;
                Raise(t);
            }
        }

        static int Priority(Trigger t)
        {
            switch (t)
            {
                case Trigger.BudgetSpent: return 100;
                case Trigger.RoundEnd: return 95;
                case Trigger.EnergyLow: return 85;
                case Trigger.NightVisionLow: return 80;
                case Trigger.RoundStart: return 75;
                case Trigger.Budget10: return 90;
                case Trigger.Budget25: return 80;
                case Trigger.Budget50: return 70;
                case Trigger.WeatherChange: return 65;
                case Trigger.Budget75: return 60;
                case Trigger.EnterLamp: return 55;
                case Trigger.External: return 50;
                case Trigger.Start: return 45;
                case Trigger.LeaveLamp: return 40;
                case Trigger.DarkStretch: return 30;
                case Trigger.MothEaten: return 35;
                case Trigger.CrossPath: return 20;
                default: return 0;
            }
        }

        static bool Sticky(Trigger t) => t == Trigger.BudgetSpent || t == Trigger.Budget10 || t == Trigger.Budget25 ||
                                         t == Trigger.Budget50 || t == Trigger.Budget75 || t == Trigger.WeatherChange ||
                                         t == Trigger.RoundStart || t == Trigger.RoundEnd;

        void Raise(Trigger t)
        {
            if (_pending == Trigger.None || Priority(t) >= Priority(_pending))
            {
                _pending = t;
                _pendingAt = Time.time;
            }
        }

        void TryNarrate()
        {
            if (_pending == Trigger.None) return;
            float now = Time.time;
            // the feeding game only talks during a round, plus its round-over line
            if (game && game.CurrentState != EdgeFeedingGame.State.Playing && _pending != Trigger.RoundEnd) { _pending = Trigger.None; return; }
            if (!Sticky(_pending) && now - _pendingAt > pendingLifetime) { _pending = Trigger.None; return; }
            bool urgent = _pending == Trigger.BudgetSpent || _pending == Trigger.Budget10 ||
                          _pending == Trigger.RoundStart || _pending == Trigger.RoundEnd;
            float cd = urgent ? urgentCooldownSeconds : cooldownSeconds;
            if (voiceKind == NarrationVoiceKind.GeminiTts && useGemini) cd = Mathf.Max(cd, ttsMinLineInterval);
            if (now - _lastSpoke < cd) return;
            if (!urgent && _voice != null && _voice.IsSpeaking) return;

            Trigger t = _pending;
            _pending = Trigger.None;
            _lastSpoke = now;   // reserve the slot even while the request is in flight
            Narrate(t);
        }

        void Narrate(Trigger t)
        {
            string ctx = BuildContext(t);
            lastContext = ctx;
            string offline = OfflineLine(t);
            int tok = ++_requestToken;

            if (useGemini && gemini.CanCall(out string why))
            {
                gemini.GenerateLine(game ? PersonaPrompt() : SystemPrompt, BuildUserPrompt(ctx), line =>
                {
                    if (tok != _requestToken || !this) return;
                    line = Clamp20(line);
                    if (line.Length < 4 || IsRepeat(line)) Deliver(offline, "offline(repeat)");
                    else Deliver(line, "gemini");
                }, err =>
                {
                    if (tok != _requestToken || !this) return;
                    Debug.LogWarning($"[EDGE Narration] Gemini failed ({err}); offline line used");
                    Deliver(offline, "offline(" + err + ")");
                });
            }
            else
            {
                string reason = useGemini ? WhyNot() : "gemini off";
                Deliver(offline, "offline(" + reason + ")");
            }
        }

        string WhyNot() { gemini.CanCall(out string why); return why ?? "?"; }

        void Deliver(string line, string source)
        {
            lastLine = line;
            lastSource = source;
            _lastSpoke = Time.time;
            _history.Add(line);
            while (_history.Count > Mathf.Max(1, historySize)) _history.RemoveAt(0);
            _subtitle = line;
            _subtitleUntil = Time.time + subtitleSeconds;
            if (showSubtitle && Edge.UI.EdgeUiView.Instance) Edge.UI.EdgeUiView.Instance.ShowSubtitle(line, subtitleSeconds);
            Debug.Log($"[EDGE Narration] [{source}] {line}");
            _voice?.Speak(line);
        }

        // ---------------- context ----------------
        string BuildContext(Trigger t)
        {
            if (game) return BuildGameContext(t);
            var s = _snap;
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(256);
            sb.Append("{\"event\":\"").Append(EventName(t)).Append('"');
            if (t == Trigger.External && _externalDetail.Length > 0) sb.Append(",\"detail\":").Append(GeminiClient.Q(_externalDetail));
            sb.Append(",\"budget_left_pct\":").Append(Mathf.RoundToInt(s.budgetLeftPct));
            sb.Append(",\"dose_rate_pct_per_min\":").Append(_doseRatePctPerMin.ToString("0.0", ci));
            sb.Append(",\"in_lamp_light\":").Append(s.insideLamp ? "true" : "false");
            if (s.nearestId >= 0) sb.Append(",\"nearest_lamp\":{\"id\":").Append(s.nearestId).Append(",\"dist_m\":").Append(s.nearestDist.ToString("0", ci)).Append('}');
            sb.Append(",\"dark_for_s\":").Append(Mathf.RoundToInt(s.darkFor));
            sb.Append(",\"weather\":\"").Append(s.haveWeather ? s.weather.ToString() : "unknown").Append('"');
            sb.Append(",\"cloud_c\":").Append(s.cloudC.ToString("0.00", ci));
            sb.Append(",\"along_path_m\":").Append(Mathf.RoundToInt(s.alongPath));
            sb.Append(",\"where\":\"").Append(s.onPath ? "over the footpath" : (s.sideOffset > 0 ? "lamp side of path" : "bush side of path")).Append('"');
            sb.Append(",\"crossed_path_recently\":").Append(_crossedRecently ? "true" : "false");
            sb.Append(",\"altitude_m\":").Append(s.altitude.ToString("0", ci));
            sb.Append(",\"night_minutes_elapsed\":").Append(Mathf.RoundToInt(s.t * EdgeConfig.TimeCompression / 60f));
            sb.Append(",\"control\":\"").Append(s.tag ? "tag" : "keyboard").Append('"');
            sb.Append(",\"torch\":\"none\"");
            sb.Append('}');
            _crossedRecently = false;
            return sb.ToString();
        }

        string BuildGameContext(Trigger t)
        {
            var s = _snap;
            var sb = new StringBuilder(200);
            sb.Append("{\"event\":\"").Append(EventName(t)).Append('"');
            if (t == Trigger.External && _externalDetail.Length > 0) sb.Append(",\"detail\":").Append(GeminiClient.Q(_externalDetail));
            if (t == Trigger.RoundEnd && _roundResult.Length > 0) sb.Append(",\"result\":").Append(GeminiClient.Q(_roundResult));
            sb.Append(",\"bat\":\"").Append(Tolerant ? "Gould's wattled bat (light-tolerant)" : "Gould's long-eared bat (light-shy)").Append('"');
            sb.Append(",\"moths_eaten\":").Append(s.moths);
            sb.Append(",\"energy_pct\":").Append(s.energyPct);
            if (game.Preset.lightHurts) sb.Append(",\"night_vision_pct\":").Append(Mathf.RoundToInt(s.budgetLeftPct));
            sb.Append(",\"seconds_left\":").Append(Mathf.Max(0, Mathf.RoundToInt(game.TimeLeft)));
            sb.Append(",\"in_light\":").Append(s.insideLamp ? "true" : "false");
            sb.Append(",\"dark_for_s\":").Append(Mathf.RoundToInt(s.darkFor));
            sb.Append('}');
            return sb.ToString();
        }

        string BuildUserPrompt(string ctx)
        {
            var sb = new StringBuilder();
            sb.Append("Context: ").Append(ctx).Append('\n');
            if (_history.Count > 0)
            {
                sb.Append("Recent lines (do not repeat or paraphrase closely):\n");
                foreach (var h in _history) sb.Append("- ").Append(h).Append('\n');
            }
            sb.Append("Write the next narration line.");
            return sb.ToString();
        }

        static string EventName(Trigger t)
        {
            switch (t)
            {
                case Trigger.Start: return "flight_begins";
                case Trigger.EnterLamp: return "entered_lamp_light";
                case Trigger.LeaveLamp: return "left_lamp_light";
                case Trigger.Budget75: return "light_budget_75pct_left";
                case Trigger.Budget50: return "light_budget_50pct_left";
                case Trigger.Budget25: return "light_budget_25pct_left";
                case Trigger.Budget10: return "light_budget_10pct_left";
                case Trigger.BudgetSpent: return "light_budget_spent";
                case Trigger.WeatherChange: return "weather_changed";
                case Trigger.DarkStretch: return "long_dark_stretch";
                case Trigger.CrossPath: return "crossed_footpath";
                case Trigger.External: return "external_event";
                case Trigger.RoundStart: return "round_starts";
                case Trigger.MothEaten: return "caught_a_moth";
                case Trigger.EnergyLow: return "energy_running_low";
                case Trigger.NightVisionLow: return "night_vision_almost_gone";
                case Trigger.RoundEnd: return "round_over";
                default: return "manual";
            }
        }

        // ---------------- offline fallback ----------------
        static readonly Dictionary<Trigger, string[]> Templates = new Dictionary<Trigger, string[]>
        {
            { Trigger.Start, new[] {
                "Dusk at the bushland edge. I slip out low, keeping to the shadows beside the path.",
                "Night falls over Sydney's bush. Every lamp ahead is a cost I must pay.",
                "Out I go, a Gould's long-eared bat, hunting moths along the dark edge." } },
            { Trigger.EnterLamp, new[] {
                "Lamp {lamp}. The glare floods my eyes; budget left: {left} percent.",
                "Into the light of lamp {lamp}. For a slow, light-shy bat, this is exposed ground.",
                "Amber glare from lamp {lamp}. Insects gather here, but so does danger.",
                "Lamp {lamp} burns through my night vision. Each second here spends my dark budget." } },
            { Trigger.LeaveLamp, new[] {
                "Back into shadow. My eyes slowly recover in the dark.",
                "Out of the glare. Darkness is where a long-eared bat belongs.",
                "Leaving the lamp behind. The bush closes around me, cool and black." } },
            { Trigger.Budget75, new[] {
                "A quarter of tonight's light budget is gone already.",
                "Seventy-five percent left. The lamps are adding up faster than the moon ever would." } },
            { Trigger.Budget50, new[] {
                "Half my light budget spent. The path lights are taking their toll.",
                "Fifty percent left. Each lamp is a full moon's worth of glare, compressed." } },
            { Trigger.Budget25, new[] {
                "Only a quarter of my budget remains. I am starting to lose my bearings.",
                "Twenty-five percent left. The edge is becoming harder to read." } },
            { Trigger.Budget10, new[] {
                "Ten percent left. Too much light, and my flight begins to wander.",
                "Almost spent. One more lamp could leave me disoriented." } },
            { Trigger.BudgetSpent, new[] {
                "Budget spent. I can still fly, but the night no longer makes sense.",
                "Too much light tonight. My eyes are saturated, my map is gone." } },
            { Trigger.WeatherChange, new[] {
                "The weather turns: {weather}. Cloud reflects the city's glow back down onto the bush.",
                "Now {weather}. Low cloud brightens urban skies; clear nights stay darker.",
                "Weather shift to {weather}. The sky's glow changes how far the lamps reach." } },
            { Trigger.DarkStretch, new[] {
                "A long stretch of true darkness. This is the corridor bats like me need.",
                "Dark, quiet, safe. A rare unlit run along the edge.",
                "No glare for a while now. Moths are easy to hear out here." } },
            { Trigger.CrossPath, new[] {
                "Across the footpath, a bright gap in the bush I must cross quickly.",
                "Over the path. Lit gaps like this break the dark corridor in two." } },
            { Trigger.External, new[] {
                "Something changes in the night around me.",
                "A new light moves through the bush. I hold still.",
                "Light from the people around me. The moths blur, and I lose my way.",
                "A sudden glare. For a light-shy bat, even one torch shrinks the night." } },
        };

        // Feeding game personas: the cocky light-tolerant bat and the anxious light-shy one.
        static readonly Dictionary<Trigger, string[]> TolerantTemplates = new Dictionary<Trigger, string[]>
        {
            { Trigger.RoundStart, new[] {
                "Lights on, moths out, party time! Gould's wattled bat has entered the chat.",
                "Streetlights? More like buffet lights. Let's eat!" } },
            { Trigger.MothEaten, new[] {
                "Moth number {moths}! Too easy. Thank you, streetlights!",
                "Crunch! {moths} down. This lamp buffet never closes.",
                "Another one! {moths} moths and I haven't even broken a sweat." } },
            { Trigger.EnterLamp, new[] {
                "Ooh, spotlight! Finally the crowd can see me.",
                "Into the light, where the snacks are. Love it here." } },
            { Trigger.LeaveLamp, new[] {
                "Out of the light? Boring. Where's the next lamp?",
                "Dark bit. Quick, back to the buffet." } },
            { Trigger.DarkStretch, new[] {
                "It's so dark out here. Who turned off the party?",
                "No lamps, no moths, no fun. Take me back to the lights." } },
            { Trigger.EnergyLow, new[] {
                "Energy at {energy} percent. Even party animals need snacks.",
                "Running on empty here. Find me a lamp, stat!" } },
            { Trigger.External, new[] {
                "A spotlight from the crowd? I knew I was famous.",
                "Real lights, real fans. I was born for this." } },
            { Trigger.RoundEnd, new[] {
                "{moths} moths! Streetlights are the best thing that ever happened to me.",
                "Party's over. {moths} moths eaten. Same time tomorrow, lamps?" } },
        };

        static readonly Dictionary<Trigger, string[]> AverseTemplates = new Dictionary<Trigger, string[]>
        {
            { Trigger.RoundStart, new[] {
                "Okay. Deep breath. Long-eared bat, out into the... oh no, lamps everywhere.",
                "Why is the whole path lit up? I just want a quiet dinner." } },
            { Trigger.MothEaten, new[] {
                "Got one! Moth number {moths}, caught in the lovely, lovely dark.",
                "{moths} moths. Small wins, but no spotlights involved.",
                "A dark-bush moth! Rare, delicious, and nowhere near a lamp." } },
            { Trigger.EnterLamp, new[] {
                "Too bright, too bright! I can't see a thing!",
                "Spotlight! Everyone's looking! Abort, abort!",
                "My dinner is literally standing under a spotlight. Rude." } },
            { Trigger.LeaveLamp, new[] {
                "Phew. Darkness. My eyes thank you.",
                "Back in the shadows. Nobody saw that." } },
            { Trigger.DarkStretch, new[] {
                "Ahh, proper darkness. If only the moths hung out here too.",
                "Quiet, dark, safe. This is my happy place." } },
            { Trigger.EnergyLow, new[] {
                "Energy {energy} percent. All the food is at the lamps. So unfair.",
                "So hungry. So many moths. All of them in the light." } },
            { Trigger.NightVisionLow, new[] {
                "My night vision is nearly gone. Too many lamps, not enough dark.",
                "Seeing spots. Lots of spots. Need dark, now." } },
            { Trigger.External, new[] {
                "Who's shining that at me?! Put it down, I'm trying to eat!",
                "A flash?! My eyes! Why would you do that?" } },
            { Trigger.RoundEnd, new[] {
                "{moths} moths. The lamps got the rest. Typical.",
                "Night over. Same moths, same lamps, much less dinner for me." } },
        };

        string OfflineLine(Trigger t)
        {
            var set = game ? (Tolerant ? TolerantTemplates : AverseTemplates) : Templates;
            if (!set.TryGetValue(t, out var arr) && !Templates.TryGetValue(t, out arr)) arr = set[Trigger.DarkStretch];
            var s = _snap;
            int start = Random.Range(0, arr.Length);
            string pick = null;
            for (int k = 0; k < arr.Length; k++)
            {
                string cand = Fill(arr[(start + k) % arr.Length], s);
                if (!IsRepeat(cand)) { pick = cand; break; }
            }
            return pick ?? Fill(arr[start], s);
        }

        static string Fill(string tpl, Snapshot s)
        {
            return tpl.Replace("{lamp}", s.nearestId >= 0 ? s.nearestId.ToString() : "ahead")
                      .Replace("{moths}", s.moths.ToString())
                      .Replace("{energy}", s.energyPct.ToString())
                      .Replace("{left}", Mathf.RoundToInt(s.budgetLeftPct).ToString())
                      .Replace("{weather}", s.haveWeather ? Pretty(s.weather) : "changing");
        }

        static string Pretty(WeatherType w)
        {
            switch (w)
            {
                case WeatherType.ClearNight: return "clear night";
                case WeatherType.PartlyCloudy: return "partly cloudy";
                case WeatherType.Overcast: return "overcast";
                case WeatherType.LightRain: return "light rain";
                case WeatherType.Rain: return "rain";
                case WeatherType.Thunderstorm: return "thunderstorm";
            }
            return w.ToString();
        }

        bool IsRepeat(string line)
        {
            string n = Norm(line);
            foreach (var h in _history) if (Norm(h) == n) return true;
            return false;
        }

        static string Norm(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        static string Clamp20(string line)
        {
            line = line.Replace('\n', ' ').Replace("\"", "").Replace("*", "").Trim();
            var w = line.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (w.Length <= 22) return line;
            return string.Join(" ", w, 0, 20).TrimEnd(',', ';', ':') + ".";
        }

        // ---------------- path geometry (same curve as EdgeSceneBuilder.PathX) ----------------
        public static float PathX(float z) => 4.5f * Mathf.Sin(z * 0.021f + 0.6f) + 1.8f * Mathf.Sin(z * 0.057f + 2.1f);

        static float AlongPath(float z)
        {
            const float z0 = -100f, step = 1f;
            float zc = Mathf.Clamp(z, z0, 100f), d = 0f, px = PathX(z0);
            for (float zz = z0 + step; zz <= zc; zz += step)
            {
                float x = PathX(zz);
                d += Mathf.Sqrt(step * step + (x - px) * (x - px));
                px = x;
            }
            return d;
        }

        // ---------------- subtitle (own small overlay; EdgeHud untouched) ----------------
        GUIStyle _style;
        void OnGUI()
        {
            if (!showSubtitle || Time.time > _subtitleUntil || string.IsNullOrEmpty(_subtitle)) return;
            var ui = Edge.UI.EdgeUiView.Instance;
            if (ui && ui.isActiveAndEnabled) return;   // shown by the themed UI
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true, fontSize = 18 };
                _style.normal.textColor = Color.white;
            }
            float w = Mathf.Min(760f, Screen.width - 40f);
            var r = new Rect((Screen.width - w) * 0.5f, Screen.height - 110f, w, 56f);
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(r.x + 12f, r.y, r.width - 24f, r.height), _subtitle, _style);
        }
    }
}
