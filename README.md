# AI Assistant · Cowork Main

A local Windows .NET 8/WPF development agent that routes Unity and Blender work through separate controlled execution domains.

Main now contains the Ship-v1 Unity/UI improvements plus the standardized Blender MCP path.

## Runtime domains

### Unity Cowork Agent V2

Unity requests use an inspect -> design -> execute -> observe -> correct loop. Persistent gameplay code is written as normal MonoBehaviour scripts, normal scene work uses deterministic bridge actions, failed batches are transactional/idempotent, and correction passes use fresh Unity state instead of blind retries.

Bridge responses are interpreted semantically, so harmless JSON fields such as `error: null` or `errors: []` do not create false task failures.

## Blender MCP

Blender prompts are routed through the official Blender Lab MCP server and use Groq Qwen 3.6 27B only.

Requirements:

- Blender 5.1 or newer with the official MCP add-on enabled.
- Start MCP Server in the add-on preferences.
- `uvx` available on PATH.

The desktop app launches the official server from:

```text
git+https://projects.blender.org/lab/blender_mcp.git#subdirectory=mcp
```

The default model is `qwen/qwen3.6-27b`. To override it:

```powershell
[Environment]::SetEnvironmentVariable("GROQ_BLENDER_MODEL","qwen/qwen3.6-27b","User")
```

If `uvx` is not on PATH, set its full executable path:

```powershell
[Environment]::SetEnvironmentVariable("BLENDER_MCP_COMMAND","C:\\Users\\YOUR_NAME\\.local\\bin\\uvx.exe","User")
```


### Controlled Blender V3 fallback

Blender requests use a controlled headless pipeline:

1. Probe the installed Blender version.
2. Ask a free-first model for version-compatible scene-construction Python.
3. Safety-scan generated code.
4. Run Blender with `--background --factory-startup`.
5. Host code saves `.blend` and exports FBX/GLB.
6. Capture Python traceback/output and verify both expected files.
7. If the first run fails, perform one bounded repair pass using the failed script + Blender log.
8. Optionally copy the exported model to `Assets/AI_Generated/Models` in the configured Unity project.

If the official MCP process is unavailable, the host-owned Blender V3 pipeline remains available as a controlled fallback. It probes Blender, generates a semantic plan, builds the scene deterministically, verifies the output, and can hand off the result to Unity. Blender **3.6 LTS and Blender 4.x** are supported fallback targets.

## Free-first provider routing

Initial implementation passes:

1. OpenRouter free router (`openrouter/free`)
2. Gemini (`gemini-3.7-flash` by default)
3. Groq (`openai/gpt-oss-120b` by default)

Correction/repair passes prefer Groq -> Gemini -> OpenRouter. Rate-limit cooldowns are remembered and blind 429 retries are blocked.

Model IDs can be overridden with environment variables.

## Required environment keys

At least one provider key must be configured; all three are recommended for fallback coverage:

```powershell
setx OPENROUTER_API_KEY "your-key"
setx GEMINI_API_KEY "your-key"
setx GROQ_API_KEY "your-key"
```

Optional model overrides:

```powershell
setx OPENROUTER_MODEL "openrouter/free"
setx GEMINI_MODEL "gemini-3.7-flash"
setx GROQ_MODEL "openai/gpt-oss-120b"
setx GEMINI_REASONING_EFFORT "high"
```

Restart AI Assistant after changing user environment variables.

## App settings

Open **Settings** in the WPF app and configure:

- Unity project root: local `AIIntegrationProject` clone
- Blender executable, for example `C:\Program Files\Blender Foundation\Blender 5.2\blender.exe`
- Blender workspace, default `C:\BlenderProjects`

The app stores runtime settings in `%LOCALAPPDATA%\AI Assistant\settings.json`. API keys remain environment variables and are not stored in that file.

## Commands

```text
/agent <Unity implementation or repair request>
/plan <Unity inspect/plan-only request>
/blender <3D asset request>
```

Destructive/high-impact work is held behind the explicit `APPROVE` / `CANCEL` risk gate.

## UI behavior

The conversation shows user prompts and final assistant results. Internal model/tool activity does **not** create chat bubbles; the current operation is shown in the status area and Live Inspector telemetry instead.

## Validation

GitHub Actions restores and builds the Windows .NET 8 project on every push to `main` and `beta/ship-v1`.

See `BETA_RELEASE.md` for the full SHIP test matrix and known boundaries.
