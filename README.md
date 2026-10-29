# COMP5047 EDGE — Unity exhibit

Unity 6 HDRP project for the EDGE path-light exhibit: a bat flown along a lamp-lit bushland path, plus a separate Moonlit Island scene. Firmware for the ESP32 tag lives in `Firmware/`.

The Unity project root is this repository. After a clone, the folder is named `COMP5047`, not `My project`.

## Start here

You need Git, [Git LFS](https://git-lfs.com), and **Unity `6000.5.1f1`** (changeset `0d9463e84828`). A newer or older editor will not match `ProjectSettings/ProjectVersion.txt`. macOS 14+ or Windows 10 21H1+ if you also install the Unity CLI below.

```bash
git lfs install
git clone https://github.com/ChinaHarry23/COMP5047.git
cd COMP5047
git lfs pull
```

Models and textures are stored in Git LFS (about 1 GB). If a `.fbx`, `.png`, or `.jpg` opens as a short text file that starts with `version https://git-lfs.github.com/spec/v1`, LFS did not run. Install it and run `git lfs pull` again before opening Unity.

Open the project from Unity Hub, or with the CLI (install steps are in the next section):

```bash
unity install 6000.5.1f1
unity open .
```

If `unity install` says that version is not in the release list, install that exact changeset:

```bash
unity install 6000.5.1f1 -c 0d9463e84828
```

The first open imports HDRP and the packages in `Packages/manifest.json`. Wait until the Editor is idle. Then open a scene from `Assets/` and press Play. Click the Game view before using the keyboard.

| Scene | Path |
|---|---|
| Bushland (first scene in the build) | `Assets/Edge/Scenes/EdgeBushland.unity` |
| Sydney bushland, feeding game | `Assets/Edge/Scenes/EdgeSydneyBushland.unity` |
| Moonlit Island | `Assets/MoonlitIsland/MoonlitIsland.unity` |

On the bushland scenes, fly with WASD or the arrow keys. In the feeding game, Space or Enter starts a round, 1 and 2 switch variants, R restarts, E pings, and H toggles the debug HUD. Full controls, the ESP32 tag, and calibration are in `Assets/Edge/Docs/FEEDING_GAME.md` and `Assets/Edge/Docs/CONTROLLER_SETUP.md`. Island camera and bat-riding controls are in `Assets/MoonlitIsland/README.txt`.

Firmware build and flash steps are in `Firmware/README.md` (Windows notes in `Firmware/WINDOWS_SETUP.md`). Copy `Firmware/include/secrets.h.example` to `Firmware/include/secrets.h` on your own machine. That file is gitignored. Do not commit Wi-Fi passwords.

Do not commit `Library/`, `Logs/`, `UserSettings/`, `Temp/`, `obj/`, generated `.csproj` files, or `.DS_Store`. Unity recreates those locally.

## Unity CLI

Agents that can run a shell should drive this project with the [Unity CLI](https://docs.unity.com/en-us/unity-cli/use-unity-cli), not the MCP server inside the in-editor AI Assistant package. Unity has deprecated that in-editor server. This repo already depends on `com.unity.pipeline` `0.8.0-exp.1`, which is what `unity command` talks to, and on `com.unity.ai.assistant` `2.20.0-pre.2`. Leave those versions pinned. Do not run `unity pipeline install --force` or `unity pipeline upgrade` unless the group has agreed to change them.

Install the CLI, then confirm it is on your `PATH`.

macOS or Linux:

```bash
curl -fsSL https://unity.com/install.sh | bash
unity --version
```

Homebrew also works: `brew install --cask unity-cli`, then `brew upgrade unity-cli` for updates. If you use both the install script and Homebrew, `which unity` can keep pointing at the script copy.

Windows (PowerShell):

```powershell
irm https://unity.com/install.ps1 | iex
unity --version
```

Reopen the terminal if `unity` is not found. Later updates are `unity self-update`, except for a Homebrew or Linux-package install, which you update with that package manager.

From this project directory, with the Editor open on this project:

```bash
unity status --until-ready --project-path .
unity command script eval "return Application.unityVersion;"
```

The eval result should be `6000.5.1f1`. `unity command` with no arguments lists the command groups the running Editor exposes. If several Editors are open, keep `--project-path` pointed at this clone.

`unity status` can report `STATUS_PIPELINE_LOAD_PENDING` while the project is still importing, or until the Editor window is focused after a package change. Wait, click the Editor, and run `unity status --until-ready` again. If it still cannot connect, run `unity pipeline list`. An Editor in Safe Mode has the project open but will not serve commands until the compile errors are fixed.

## Unity MCP for Claude Code and Codex

`unity mcp` is the supported MCP server. It is built into the `unity` binary and discovers the Editor that has this project open. The client launches it. You do not start `unity mcp` yourself.

Do this once per machine, from the project directory, after `unity --version` works. Use the absolute path of your clone. The folder name has no spaces after a normal clone. If you moved the project, run the configure commands again.

Claude Code:

```bash
unity setup claude
unity mcp configure claude-code --project-path "$(pwd)" --yes
```

`unity setup claude` installs Unity's Claude Code plugin (`unity@claude-plugins-official`), which includes the `unity-cli` skill. Do not also run `unity skill install claude-code`. That would load a second copy. The setup command does not register MCP, so the `unity mcp configure` line is still required. Claude Code's MCP entry is user-wide, not per project, so `--project-path` must be this clone. Start a new Claude Code session in this folder. In a session that is already open, run `/reload-plugins`.

Codex:

```bash
unity setup codex
unity mcp configure codex --local --project-path "$(pwd)" --yes
```

`unity setup codex` adds the plugin from `Unity-Technologies/unity-agent-plugin`. Do not also run `unity skill install codex`. `--local` writes Codex's project config and relaxes its sandbox so `unity mcp` and `unity command` can reach the Editor on localhost. Start a new Codex session. The plugin loads on the next session.

Check what was written:

```bash
unity mcp configure --list
```

If `claude` or `codex` is not on your `PATH`, `unity setup` stops and prints the plugin commands to run inside that app. `unity mcp configure --dry-run` prints the entry it would write.

Until an Editor is open on this project, the MCP server exposes only a `unity-cli-status` tool. Open the project, or run `unity open .`, and the Editor's tools appear without restarting the agent. A modal dialog in Unity can make a capture or command time out. Dismiss the dialog and retry.

Other clients the same CLI can register, if you use them, are listed by `unity mcp configure --list`. Cursor, VS Code, and Codex can take `--local`. Claude Code cannot.
