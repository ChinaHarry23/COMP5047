# EDGE AI narration

A documentary narrator for the Gould's long-eared bat. It plays through the Mac speakers first. Later it can switch to Gemini TTS or to the ESP32 tag.

## Files
- `Scripts/Narration/EdgeNarrator.cs`: watches the game state, decides when to speak, builds the context, and falls back to offline lines. It also draws its own small subtitle bar (EdgeHud is untouched).
- `Scripts/Narration/GeminiClient.cs`: REST client (`generateContent` for text, `interactions` for TTS), the key lookup and the rate limiter.
- `Scripts/Narration/INarrationVoice.cs`: the `INarrationVoice` interface, the `NarrationVoiceKind` enum and `SilentVoice`.
- `Scripts/Narration/MacSayVoice.cs`: runs `/usr/bin/say` without blocking (voice Karen en_AU, rate 170).
- `Scripts/Narration/GeminiTtsVoice.cs`: Gemini 3.8 Flash-Lite TTS. It returns 24 kHz l16 PCM, which plays as an AudioClip. If TTS fails, the line is spoken with `say`.
- `Scripts/Narration/Esp32SpeakerVoice.cs`: stub that sends `SAY <text>` over UDP port 1235. See TODO below.
- `Editor/EdgeNarrationMenu.cs`: the EDGE > Narration menu items.

The narrator only reads from the game. It does not touch UdpLink or BatFlightController, and the IMU path on UDP 1234 is unchanged. Weather comes from the existing `EdgeWeatherSky.WeatherApplied` event and `current`. EdgeWeatherSky was not modified.

## Setup
1. Get a free API key at https://aistudio.google.com/apikey (Google AI Studio → "Create API key").
2. Choose ONE of these:
   - Environment variable (GUI apps don't read your shell rc files):
     `launchctl setenv GEMINI_API_KEY "your-key"`
     Then **quit Unity Hub and Unity completely and reopen them**. The setting is lost on reboot, so run it again after restarting the Mac, or add it to a LaunchAgent.
   - Key file: `printf %s "your-key" > "<project>/UserSettings/gemini_key.txt"`.
     `UserSettings/` is already in `.gitignore`. Unity reads the file on every request, so no restart is needed.
3. In Unity, open the scene and choose **EDGE > Narration > Add Narrator To Open Scene**, then save the scene.
4. **EDGE > Narration > Test Line** checks the speakers (`say`, works in edit mode). **EDGE > Narration > Test Gemini Line** checks the key (uses one API call, and the Console shows OK or the HTTP error).

The key is never stored in a scene, asset or log, and never hardcoded.

## How triggers work
A coroutine takes a sample every 0.5 s (nothing runs per frame). Each sample computes:
- budget left %
- dose rate (% of budget per minute, smoothed)
- nearest lamp, its distance, and whether the bat is inside its light (same radius test as LampZone)
- distance along the path and side of the path (same `PathX` curve as the builders), and whether the bat crossed the path
- altitude, weather and cloud C
- night minutes elapsed, and whether the bat is flown by the tag or the keyboard

Events, highest priority first:

| Priority | Event |
| --- | --- |
| 100 | budget spent |
| 90 | 10% left |
| 80 | 25% left |
| 70 | 50% left |
| 65 | weather change |
| 60 | 75% left |
| 55 | enter lamp |
| 50 | external (`NotifyEvent`, e.g. future torch) |
| 45 | flight start |
| 40 | leave lamp |
| 30 | long dark stretch (30 s unlit) |
| 20 | path crossing |

- Each budget threshold fires once.
- Only one event waits at a time, and a higher-priority event replaces it.
- Waiting events that are not about budget or weather are dropped after 6 s.

Pacing:
- At least **10 s** between lines. 10% left and budget spent may cut in after 5 s.
- The narrator never talks over itself, except for those two urgent events.

When a line is due, the narrator sends Gemini a system prompt and the compact context JSON plus the last 8 lines.
- The system prompt asks for a hushed documentary narrator, a single sentence of ≤20 words, accurate facts about light pollution, and no repeats.
- The reply is cut to about 20 words.
- If the reply repeats a recent line, or the call fails, times out (6 s), hits a rate limit or there is no key, the narrator uses an **offline template for the same event** filled with live values (lamp id, % left, weather). It never repeats one of the last N lines.

## Rate limits and privacy
- Defaults: at least 6 s between calls, at most 8 calls per minute (text and TTS together), 250 calls per session, and a 60 s pause after an HTTP 429. That stays inside free-tier per-minute limits. In practice, with the 10 s cooldown, it's at most about 6 text calls per minute and usually far fewer.
- GeminiTts doubles the calls (text + TTS). Use MacSay for long demos.
- Only game numbers are sent: budget, lamp ids and distances, weather enum, path metres. No audio, video or personal data is sent. Free-tier Google AI Studio requests may be used by Google to improve its products, so don't put personal info in prompts.
- Default text model is `gemini-3.5-flash-lite`. If that returns 404, the client retries with the `gemini-flash-latest` alias. Gemini 2.5 models are restricted to legacy users as of Oct 2026. You can change the model in the inspector under `gemini`.

## Rubric mapping
- **AI API integration**: a live Gemini `generateContent` REST call, with optional Gemini TTS through the Interactions API. It has a proper timeout, error handling, rate limiting and secret handling.
- **Context recognition**: real game state (light budget, dose rate, lamp light, weather, path geometry, time, how the bat is controlled) is turned into structured JSON on meaningful changes, not timed spam.
- **Adaptive responses**: wording comes from the model for each event, avoids recent lines, is ranked by priority with cooldowns, and degrades gracefully to offline lines that still depend on the context.

## Swapping to the ESP32 speaker
Set `voiceKind = Esp32Speaker`. The narration logic doesn't change, because it only calls `INarrationVoice`. Firmware TODO:
- (a) Receive `SAY <text>` and play pre-rendered PCM sent from Unity. Request TTS as `audio/l16` at 16 kHz and send it in chunks of 1400 bytes or less (`PCM <seq> ...`) to an I2S amp (MAX98357A).
- (b) Or flash offline clips to SPIFFS and trigger them with `CLIP <id>`.
- Use port 1235 so the 1234 IMU/dose link stays untouched.

## Testing
- **Without a key**: press Play. The Console shows `key=none` and lines appear as `[offline(no API key)]`, spoken by `say` with subtitles.
- **With a key**: lines show as `[gemini]`. Errors are logged as `offline(HTTP ...)` and the narrator never goes silent.
- **Lamps**: fly into a lamp to trigger enter_lamp, then leave it. Keep flying through lamps to pass the budget thresholds 75/50/25/10/spent.
- **Weather**: select Weather Sky, then use EDGE > Weather > Force > … (or the forceWeather toggle) during Play. The weather_changed event fires.
- **Darkness**: stay out of all lamp light for 30 s to get a long_dark_stretch line.

## Feeding game: two comic bats (30 Oct)
With an `EdgeFeedingGame` in the scene the narrator narrates the game, not the exhibit dose. Each bat has its own persona, prompt, offline lines and voice, switched at every round start:
- **Light-tolerant (Gould's wattled bat):** cocky party animal; lamps are a moth buffet. Gemini voice `Fenrir`, Mac fallback `Rocko (English (UK))`.
- **Light-averse (Gould's long-eared bat):** anxious introvert; lamps are blinding spotlights. Gemini voice `Enceladus`, Mac fallback `Flo (English (UK))`.

Round intro: choosing a bat (start screen, results screen or a switch of bat) holds the bat still with the timer and light paused (`EdgeFeedingGame.introSeconds`, at least 3 s, extended to the clip length) and plays a pre-recorded clip: `Assets/Edge/Audio/Narration/intro_light_tolerant.wav` (Fenrir: "Ohhh yeahhh! Lights on, moths out. Ready to rumble!") and `intro_light_averse.wav` (Enceladus: "Um... it's really bright out there. Do I have to?"). Pre-recorded because a live line takes ~7 s (text ~1-2 s + Gemini TTS ~5 s). R mid-round has no intro.

Pacing: at least 8 s between lines (`cooldownSeconds`, `ttsMinLineInterval`); each line is a text + a TTS call, so `GeminiClient.maxCallsPerMinute` is 16. A 429 from Gemini backs off and the offline line is used.

Events: moth eaten (first, then every `mothLineEvery`), entered/left light (lamps or real room light), long dark stretch, energy below 25%, night vision below 30% (light-averse), real flash/torch from the room (`EdgeLightEffects.RealLightHit`). Nothing is said outside a round: when a round ends, anything queued, being written or still speaking is dropped. Once both bats have played, a scripted explanation (`summaryLine`, or `summaryLineAverseWon` when the long-eared bat ate as many or more) is spoken in a narrator voice (Gemini `Charon`, Mac `Daniel`), or played from `summaryClip` / `summaryClipAverseWon` if recordings are assigned. Context sent to Gemini: event, bat, moths eaten, energy %, night vision % (light-averse), seconds left, in light, dark for (s), plus the real-light detail.

Prompt guardrails (`GameRules`): one sentence, at most 15 words, PG, tease the lamps never the visitor, keep the light facts true, no invented numbers.
