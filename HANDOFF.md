# Handoff: You Are the Bat (Unity exhibit)

**Date:** 30 Oct 2026
**Branch:** `feature/perf-echo`, on top of `main` (`0c80be6`), pushed to GitHub
**Final demo:** Week 13 (2–6 Nov)

This covers the Unity side of the exhibit: what changed in this round of work, how to run and test it, and what's left. The game is the two-bat feeding game in `Assets/Edge/Scenes/EdgeSydneyBushland.unity`.

> **Pushed, not merged.** The branch is on GitHub (`origin/feature/perf-echo`) but not in `main` yet. Open a pull request into `main` to merge it.

---

## 1. Quick start

1. Install Git LFS, then clone and run `git lfs pull`. Models, textures and the narrator `.wav` clips are stored in LFS.
2. Use Unity **6000.5.1f1** (changeset `0d9463e84828`) with HDRP. A Personal licence is fine.
3. Open `Assets/Edge/Scenes/EdgeSydneyBushland.unity`, press Play, and click the Game view so it receives keyboard input.
4. **Gemini key** (for the AI narrator): put the key alone, on one line, in `UserSettings/gemini_key.txt`. That folder is git-ignored, so **never commit the key**. Check it with the menu item **EDGE > Narration > Test Gemini Line**. Without a key the narrator still works, using its built-in lines and the Mac voice.

**Keys:**

| Key | Action |
|---|---|
| WASD / arrows | Fly |
| 1 / 2 | Choose the light-tolerant / light-averse bat |
| Space / Enter | Start or replay |
| R | Restart the round |
| E | Echolocate |
| C | Switch between first- and third-person camera |
| H | Debug line |
| F | Frame rate |
| Z | Zero the controller |

---

## 2. What was done

### Performance: about 10 fps → 60 fps
- **Cause:** the 73 canopy trees (`island_tree_02`, `tree_small_02`) were drawn at full detail every frame. They made up about 98% of all triangles (131M per frame). Unity's automatic Mesh LOD selection always picked level 0 for them.
- **Fix:** `meshLodSelectionBias = 2` on those trees, which brings it down to 39M triangles. The scene builder applies it (`EdgeSceneBuilder.ApplyCanopyLodBias`), and so does `unity command edge_upgrade_scene`.
- **Ruled out:** removing the single-level cull LODGroups made no difference.

### Two bats tuned
- **Mode 1 (light-tolerant) isn't blinding any more.** Three things stacked up:
  1. The `EdgeLightEffects` overlays. They blend in linear colour space, so even a 0.7% white layer turns the night scene grey. The phone-flash overlay also had a hard 25% floor.
  2. The lamp-head emissive bloom.
  3. The lamps' spot lights in the volumetric fog.
  - **Now:** mode 1's light-effect scale is 0 (no glare, shake or vibration), and the flash follows each bat's scale. The lamps look faint through two new preset fields, `lampBrightness` (0.35) and `lampGlow` (0.01, applied to the head bloom and the lit fog). These are visual only; the light and dose maths don't read them.
- **Mode 2 (light-averse) used to end within seconds,** because the bat starts under Lamp_1.
  - Added a 4 s light grace at the start (`lightGraceSeconds`).
  - Energy drain halved: `hungerDrainPerSec` 0.012.
  - The light budget was raised (`budgetFmm`, now 60 after playtesting).
  - New start hint: "Leave the lit path and hunt moths in the dark."
- **Turning:** full tilt gives 122°/s (was 70°/s), so a 180° turn takes about 1.5 s. The bat also slows to 80% in a hard bank. Values on `BatFlightController`: `turnGain` 1.3, `maxYawRate` 140, `hardTurnSpeed` 0.8.
- **Round length:** 45 s for both bats (`roundSeconds`, set during playtesting).

### Echolocation
- On for both bats. It fires only on E or the controller button (no auto-ping).
- The highlight lasts 1.5 s, reaches 20 m and starts at 4× the moth's size. The ping is louder (0.3).

### Bat outline (`EdgeBatOutline.cs`, added automatically at Play)
- Third person: a glowing outline around the 3D bat.
- First person: rim-lit wing silhouettes in the bottom corners. Tune them on the component: `wingScale`, `rimEmissive`, `outlineWidth`, `flapDegrees`.

### Real light from the controller (`EdgeLightEffects.cs`)
- It learns the room's normal light from `LUX`.
  - A sudden jump means a **phone flash**: white-out and a strong buzz.
  - Light that stays up for more than 0.5 s means a **held torch**: torch glare and a pulsing buzz, both scaled by brightness.
- `RealGameLux` adds to the lamp light at the bat, so in mode 2 a torch drains night vision and energy like a lamp. Set how hard it hits with `realGameLux`.
- The room level isn't re-learned while a torch is held, for up to 30 s.
- The H debug line shows `lux x (real y)`.
- **Gotcha:** if the torch is already on when the controller starts sending, it's learned as the room's normal light.
- **Test tool:** `python3 Firmware/tools/fake_light.py` (type `f` + Enter for a flash, `t` for a torch on/off), or run it with `--scenario demo`.

### AI narrator (`Narration/EdgeNarrator.cs`): comic two-bat personas
- **When there's an `EdgeFeedingGame` in the scene, it narrates the game.**
  - **Mode 1:** a cocky party bat, voiced by Gemini **Fenrir**.
  - **Mode 2:** an anxious long-eared bat, voiced by Gemini **Enceladus**.
  - Each has its own style instruction, and the Mac `say` voice is the backup (Rocko / Flo).
- **Events it reacts to:** moths eaten (the first, then every 4th), entering and leaving light, long dark stretches, energy below 25%, night vision below 30%, and real flashes and torches. It only talks during a round. When a round ends, it drops anything queued, being written or still speaking.
- **End of visit:** once both bats have played, a scripted explanation plays in a calm narrator voice (Gemini **Charon**, Mac **Daniel**): `summaryLine`, or `summaryLineAverseWon` if the long-eared bat ate as many or more. Assign `summaryClip` / `summaryClipAverseWon` on the Narrator to use recordings instead.
- **What it sends Gemini:** the event, which bat, moths eaten, energy %, night vision %, seconds left, whether it's in light, and how long it's been dark.
- **Prompt guardrails** (`GameRules`): one sentence of 15 words or fewer, PG, teases the lamps and never the visitor, light facts stay true, no invented numbers.
  - Personas: `TolerantPersona` and `AversePersona`.
  - Offline lines: `TolerantTemplates` and `AverseTemplates`.
- **Bat intro:** choosing a bat holds it still, with the timer and light damage paused, for at least 3 s (`introSeconds`, extended to the clip length). A **pre-recorded** line plays instantly from `Assets/Edge/Audio/Narration/` ("Ohhh yeahhh! … Ready to rumble!" / "Um… it's really bright out there. Do I have to?"). R mid-round skips the intro.
- **Latency, measured:** text takes 1–2 s and Gemini TTS takes 5–5.7 s, so a live line plays about 7 s after its moment. There's at least 8 s between lines, and `maxCallsPerMinute` is 16.
- **The Gemini free tier allows 10 TTS requests a day.** After that, a "per day" 429 switches the session to the Mac voice with no retry. Text generation keeps working, since it has its own quota. Turn on billing to raise the limit.

### Wording
One set of terms everywhere: **moths eaten / energy / night vision**.
- End reasons: "Out of energy: not enough moths.", "Night vision gone: too much light, back to the roost.", "Night's over."
- The comparison line starts with "Same night. Unequal feast."

### Fixes
- `EdgeFeedingBuzz`: float rounding could read past the end of the echo delay buffer (`IndexOutOfRangeException` on the audio thread).

---

## 3. Commits (`feature/perf-echo`)

| Commit | What |
|---|---|
| `5628287` | Canopy LOD bias (performance), bat-call audio fix, bat outline |
| `5521039` | Bat tuning, echolocation, real light from the controller, `fake_light.py` |
| `62d9cd0` | Comic two-bat narrator, per-voice TTS cache, narrator added to the scene |
| `80ca6b8` | Bat intro clips, 8 s gaps, TTS daily-quota handling |
| `f6431e1` | Wording; intro clips and pacing wired into the scene; docs |
| `2b12a5f` | Narrator goes quiet at round end; scripted explanation after both bats |

Before each commit: Unity compiled with 0 errors, and the firmware simulator passed (`cd Firmware/sim && make test`: 5 suites, 29 scenarios). **The project has no Unity tests**, so gameplay was checked by hand in Play mode. Unity rewrites `.vscode/settings.json` on its own; don't commit it.

---

## 4. What's left

**Unity, no hardware needed**
1. **Vibration rules:**
   - Remove the buzz on tree hits (`BatFlightController.bumpVib`).
   - Optionally remove the buzz on eating a moth (`EdgeFeedingGame.eatVibLevel`); the team decided to skip this for now.
   - Only the long-eared bat should vibrate, with strength following in-game and real light.
2. **End screen** "Same night. Unequal feast.": both bats side by side, showing moths eaten, energy left, and % of time pushed into the dark (`UI/EdgeFeedingHud.cs`).
3. **Echolocation from the controller button.** The game side is easy once the message format is agreed (see section 5).
4. **Exhibit build:**
   - Make `EdgeSydneyBushland` the first scene in Build Settings; it's currently `EdgeBushland`.
   - Build a standalone macOS app.
   - Check fullscreen on the projector.
5. **Playtest:** narrator jokes and timing; mode 2's glare near lamps (very strong, by design); whether the canopy looks too thin at LOD bias 2.
6. **Optional:** clear the deprecation warnings (`FindFirstObjectByType` and similar).

**Hardware (team)**
- **The controller firmware isn't in this repo.** `Firmware/platformio.ini` expects `src/tag/`; the code is in the team's `Final Project/rev3/firmware/src/tag`. Push it.
- Wire the vibration motor (an LED stand-in on D3 today) and the echolocation button.
- Check the light threshold against the projector glow (`luxFlashMin` 150 lx, `luxFlashRatio` 3×).
- Narrator through the bat's speaker (`Esp32SpeakerVoice` is a stub), or keep the laptop speakers. Decide soon.

---

## 5. Messages between controller and game (UDP over Wi-Fi)

| Direction | Message | Status in Unity |
|---|---|---|
| bat → game, port 1234 | `ORI roll pitch yaw gx gy gz btn seq` at about 100 Hz | Done (`EdgeTagInput`) |
| bat → game | `LUX <lux>` at about 10 Hz | Done: flash and held torch (`EdgeLightEffects`) |
| bat → game | `CLS <none\|lamp\|bike\|torch\|flash> [conf]` | Done, optional (for a future classifier) |
| bat → game | `HELLO EDGE-TAG paused <seq>` at 1 Hz | Done |
| game → bat | `VIB <0..1>` | Done (`BatFlightController`) |
| game → bat | `LAMP <id> <proximity>` | Done (`LampZone`) |
| game → bat, port 1235 | `SAY <text>` | Stub only |
| bat → game | **echolocation button** | **Not defined.** `btn` in `ORI` is D2 (pause / zero). |

Quick check without hardware: UDP `ORI` packets sent to `127.0.0.1:1234` steer the bat. Unity zeroes the tilt on the first packet, so send level first.

---

## 6. Working with the Unity CLI (gotchas)

- **Old command names.** The project pins `com.unity.pipeline 0.8.0-exp.1`, which uses older, flat command names: `eval`, `editor_status`, `editor_play`, `editor_stop`, `capture_game_view`, `console`, `recompile`, `menu`, plus the project's own `edge_status`, `edge_feeding`, `edge_light` and `edge_upgrade_scene`. `unity command script eval` doesn't exist. Don't run `unity pipeline upgrade` unless the group agrees.
- **Option order:** options go **before** the command name, e.g. `unity command --timeout 300 open_scene --path …`.
- **Screenshots:** `capture_game_view --save_path` always writes into `Assets/` (as `Assets/Temp/…`). Move the file out and delete the `.meta` files and folder afterwards. A capture taken while the editor is paused doesn't get written.
- **Background throttling:** when the Unity window isn't in front, the editor runs at about 10 fps, and edit-mode web requests can time out. Measure frame rate in the window with F. To freeze a frame for comparisons, use `Time.timeScale = 0` (it keeps rendering) rather than pausing, and **check `EditorApplication.isPaused` is false** before trusting any test.
- **Live edits to preset assets** (`Assets/Edge/Feeding/*.asset`) made during Play **are kept** after you stop. Scene component changes made during Play aren't.

---

## 7. Key files

| Area | File |
|---|---|
| Feeding game, presets | `Assets/Edge/Scripts/Feeding/EdgeFeedingGame.cs`, `EdgeFeedingPreset.cs`, `Assets/Edge/Feeding/Feeding_Light*.asset` |
| Flight | `Assets/Edge/Scripts/BatFlightController.cs` |
| Light effects and real light | `Assets/Edge/Scripts/EdgeLightEffects.cs` |
| Narrator | `Assets/Edge/Scripts/Narration/EdgeNarrator.cs`, `GeminiClient.cs`, `GeminiTtsVoice.cs` |
| Bat outline | `Assets/Edge/Scripts/EdgeBatOutline.cs` |
| HUD and end screen | `Assets/Edge/Scripts/UI/EdgeFeedingHud.cs` |
| Scene builder and CLI commands | `Assets/Edge/Editor/EdgeSceneBuilder.cs`, `Assets/Edge/Scripts/Editor/EdgeCliCommands.cs` |
| Docs | `Assets/Edge/Docs/FEEDING_GAME.md`, `NARRATION_NOTES.md`, `CONTROLLER_SETUP.md` |
| Test tool | `Firmware/tools/fake_light.py` |
