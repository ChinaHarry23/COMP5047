using System.Text;
using Edge.Feeding;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Edge.EditorTools
{
    // Unity CLI commands for EDGE (com.unity.pipeline). With the project open in the Editor:
    //   unity command edge_status                      what the open scene has, and how agents are tuned
    //   unity command edge_upgrade_scene               add/wire the new runtime pieces and save the scene
    //   unity command editor_play                      (built in) enter Play mode, then:
    //   unity command edge_feeding --action averse     start a round as the light-averse bat (or tolerant/restart)
    //   unity command edge_light --kind flash          preview a light effect (flash | torch | bike | lamp | none)
    //   unity command screenshot --output shot.png     (built in) capture the Game view
    // `unity list` shows them with their arguments.
    static class EdgeCliCommands
    {
        static T Find<T>() where T : Object => Object.FindAnyObjectByType<T>();
        static T[] FindAll<T>() where T : Object => Object.FindObjectsByType<T>();

        [CliCommand("edge_status", "EDGE: report the open scene - bat, lamps, feeding game, light effects, pedestrians (clip, foot-sync), cyclists (seat, lean)", Tags = new[] { "edge" })]
        public static string Status()
        {
            var sb = new StringBuilder();
            var scene = EditorSceneManager.GetActiveScene();
            sb.AppendLine($"scene {scene.name} ({(EditorApplication.isPlaying ? "playing" : "edit mode")})");
            var bat = Find<BatFlightController>();
            sb.AppendLine(bat ? $"bat {bat.name} at {bat.transform.position} tag {(bat.TagActive ? "streaming" : "not streaming")}" : "bat: none (no BatFlightController)");
            sb.AppendLine($"lamps {FindAll<LampZone>().Length}, beams {EdgeBeam.All.Count} (torches/bike lights exist in Play mode)");

            var game = Find<EdgeFeedingGame>();
            if (game)
            {
                sb.Append($"feeding game: {game.CurrentState} variant {game.variant}");
                if (EditorApplication.isPlaying)
                    sb.Append($" score {game.Score} rivals {game.RivalScore} insects at lamps {game.LampShare01 * 100f:F0}% light {game.LightSeconds:F0}s");
                sb.AppendLine();
            }
            else sb.AppendLine("feeding game: none");

            var fx = Object.FindAnyObjectByType<EdgeLightEffects>(FindObjectsInactive.Include);
            sb.AppendLine(fx ? $"light effects{(fx.enabled && fx.gameObject.activeInHierarchy ? "" : " [INACTIVE - effects off]")}: scale {fx.scale:F2} haze {fx.Haze01:F2} beam {fx.Beam01:F2} flash {fx.Flash01:F2} real '{fx.RealClass}'"
                             : "light effects: none in the scene (one is created automatically in Play mode; edge_upgrade_scene adds it)");

            foreach (var p in FindAll<EdgePedestrians>())
            {
                sb.AppendLine($"pedestrians {p.name}: {(p.agents != null ? p.agents.Length : 0)} agents, smoothLocomotion {p.smoothLocomotion}, torches {p.torchShare:P0} of walkers");
                if (EditorApplication.isPlaying) sb.Append(p.Describe());
            }
            foreach (var c in FindAll<EdgeCyclist>())
                sb.AppendLine("cyclist " + (EditorApplication.isPlaying ? c.Describe() : $"{c.name} seatRider {c.seatRider} headlight {c.headlight} ik-pass {IkPass(c.GetComponent<Animator>())}"));
            var sitters = FindAll<EdgeBenchSitter>();
            int photographers = 0;
            foreach (var s in sitters) if (s.takesFlashPhotos) photographers++;
            sb.AppendLine($"bench sitters {sitters.Length} ({photographers} take flash photos)");
            return sb.ToString();
        }

        static string IkPass(Animator an)
        {
            var ac = an ? an.runtimeAnimatorController as AnimatorController : null;
            return ac && ac.layers.Length > 0 ? (ac.layers[0].iKPass ? "on" : "OFF") : "?";
        }

        [CliCommand("edge_upgrade_scene", "EDGE: add and wire EdgeLightEffects, make sure cyclist controllers have IK Pass, then save the open scene", Tags = new[] { "edge" })]
        public static string UpgradeScene([CliArg("save", "Save the scene afterwards (default true)")] bool save = true)
        {
            if (EditorApplication.isPlaying) return "Exit Play mode first (unity command editor_stop).";
            var sb = new StringBuilder();
            var scene = EditorSceneManager.GetActiveScene();

            var fx = Object.FindAnyObjectByType<EdgeLightEffects>(FindObjectsInactive.Include);   // an inactive one counts
            if (!fx)
            {
                var go = new GameObject("EdgeLightEffects");
                Undo.RegisterCreatedObjectUndo(go, "Add EdgeLightEffects");
                fx = go.AddComponent<EdgeLightEffects>();
                sb.AppendLine("added EdgeLightEffects");
            }
            Undo.RecordObject(fx, "Wire EdgeLightEffects");
            if (!fx.flight) fx.flight = Find<BatFlightController>();
            if (!fx.cams) fx.cams = Find<CameraToggle>();
            if (!fx.link) fx.link = fx.flight && fx.flight.link ? fx.flight.link : Find<UdpLink>();
            if (fx.lamps == null || fx.lamps.Length == 0) fx.lamps = FindAll<LampZone>();
            sb.AppendLine($"EdgeLightEffects wired: bat {(fx.flight ? fx.flight.name : "-")}, cameras {(fx.cams ? fx.cams.name : "-")}, link {(fx.link ? fx.link.name : "-")}, lamps {fx.lamps.Length}");
            if (!fx.enabled || !fx.gameObject.activeInHierarchy)
                sb.AppendLine($"WARNING: '{fx.name}' is inactive or disabled, so all light effects are off. Enable it if that is not intended.");

            foreach (var c in FindAll<EdgeCyclist>())
            {
                var ac = c.GetComponent<Animator>().runtimeAnimatorController as AnimatorController;
                if (!ac || ac.layers.Length == 0) { sb.AppendLine($"{c.name}: no AnimatorController asset (IK Pass not checked)"); continue; }
                var layers = ac.layers;
                if (layers[0].iKPass) continue;
                layers[0].iKPass = true;
                ac.layers = layers;
                EditorUtility.SetDirty(ac);
                sb.AppendLine($"{c.name}: enabled IK Pass on '{ac.name}' (needed for the seated rider pose)");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            if (save)
            {
                AssetDatabase.SaveAssets();
                sb.AppendLine(EditorSceneManager.SaveScene(scene) ? $"saved {scene.path}" : $"could not save {scene.path}");
            }
            return sb.ToString();
        }

        [CliCommand("edge_feeding", "EDGE (Play mode): control the feeding game - tolerant | averse | restart | end", Tags = new[] { "edge" })]
        public static string Feeding([CliArg("action", "tolerant, averse, restart or end", Required = true)] string action)
        {
            if (!EditorApplication.isPlaying) return "Enter Play mode first (unity command editor_play).";
            var game = Find<EdgeFeedingGame>();
            if (!game) return "No EdgeFeedingGame in the scene.";
            switch ((action ?? "").ToLowerInvariant())
            {
                case "tolerant": case "1": game.SetVariant(EdgeFeedingGame.Variant.LightTolerant); break;
                case "averse": case "2": game.SetVariant(EdgeFeedingGame.Variant.LightAverse); break;
                case "restart": game.Restart(); break;
                case "end": game.EndRound("Ended from the CLI."); break;
                default: return $"Unknown action '{action}'. Use tolerant, averse, restart or end.";
            }
            return $"{game.CurrentState} as {game.Preset.displayName}";
        }

        [CliCommand("edge_light", "EDGE (Play mode): preview what a light does to the bat - flash | torch | bike | lamp | none", Tags = new[] { "edge" })]
        public static string LightEffect(
            [CliArg("kind", "flash, torch, bike, lamp or none", Required = true)] string kind,
            [CliArg("strength", "0..1 (default 1)")] float strength = 1f,
            [CliArg("seconds", "How long a torch/bike/lamp lasts (default 3)")] float seconds = 3f)
        {
            if (!EditorApplication.isPlaying) return "Enter Play mode first (unity command editor_play).";
            var fx = EdgeLightEffects.Instance;
            if (!fx) return "No EdgeLightEffects (is there a BatFlightController in the scene?).";
            kind = (kind ?? "none").ToLowerInvariant();
            if (kind == "flash") fx.Flash(strength);
            else fx.SetRealClass(kind, strength, seconds);
            return $"{kind} at {strength:F2} for {(kind == "flash" ? fx.flashFade : seconds):F1}s (effect scale {fx.scale:F2})";
        }
    }
}
