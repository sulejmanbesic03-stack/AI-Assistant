# AI Assistant · Cowork Main

A local Windows .NET 8/WPF development agent that routes Unity and Blender work through separate controlled execution domains.

Main contains the Ship-v1 Unity/UI improvements plus the official Blender MCP client path.

## Runtime domains

### Unity Cowork Agent V2

Unity requests use an inspect -> design -> execute -> observe -> correct loop. Persistent gameplay code is written as normal MonoBehaviour scripts, normal scene work uses deterministic bridge actions, failed batches are transactional/idempotent, and correction passes use fresh Unity state instead of blind retries.

Bridge responses are interpreted semantically, so harmless JSON fields such as `error: null` or `errors: []` do not create false task failures.

## Blender MCP

Blender work uses the official Blender Lab MCP server. AI Assistant is the MCP client/orchestrator: it starts the official `blender-mcp` process over stdio, discovers its tools, sends the tool schemas to Groq, and forwards each model tool call to the MCP server. The Blender add-on is the server's local connection to the running Blender instance. The Unity bridge is used only after a verified FBX export; it is not a replacement for Blender MCP.

Natural-language requests are classified by a small Groq intent pass. A request such as “napravi muški survival character i pošalji ga u Unity” therefore enters the Blender → Unity pipeline without relying on a hardcoded keyword combination. The original request is passed to Blender unchanged in meaning; the pipeline does not impose a humanoid, gender, or visual style.

Requirements:

- Blender 5.1 or newer with the official MCP add-on enabled.
- Start MCP Server in the add-on preferences.
- `uvx` available on PATH.

The desktop app launches the official server from:

```text
git+https://projects.blender.org/lab/blender_mcp.git@4309a39646e644261624bfcd2bca669b343b7621#subdirectory=mcp
```

The server revision is pinned so a future upstream change cannot silently break a production build. Upgrade it deliberately after testing the matching Blender add-on. AI Assistant also constrains the temporary `uvx` environment to `mcp>=1.2,<2`, matching the official server source revision's `mcp.server.fastmcp` import.

The Blender MCP provider uses direct Groq only:

1. `qwen/qwen3.6-27b` primary
2. `openai/gpt-oss-120b` fallback

OpenRouter is not in the Blender MCP path. To override the models:

```powershell
[Environment]::SetEnvironmentVariable("GROQ_BLENDER_MODEL","qwen/qwen3.6-27b","User")
[Environment]::SetEnvironmentVariable("GROQ_BLENDER_FALLBACK_MODEL","openai/gpt-oss-120b","User")
[Environment]::SetEnvironmentVariable("GROQ_ROUTER_MODEL","openai/gpt-oss-120b","User")
```

If `uvx` is not on PATH, set its full executable path:

```powershell
[Environment]::SetEnvironmentVariable("BLENDER_MCP_COMMAND","C:\\Users\\YOUR_NAME\\.local\\bin\\uvx.exe","User")
```


## General provider routing

Unity Agent V2 keeps its own provider routing and compatibility behavior. The Blender MCP path above is intentionally isolated so an OpenRouter rate limit or malformed OpenRouter tool history cannot corrupt a Blender MCP run. Model IDs can be overridden with environment variables.

## Required environment keys

For Blender MCP, `GROQ_API_KEY` is required. Unity Agent V2 may use the other configured providers for its separate compatibility path:

```powershell
setx GEMINI_API_KEY "your-key"
setx GROQ_API_KEY "your-key"
```

Optional model overrides:

```powershell
setx GEMINI_MODEL "gemini-3.7-flash"
setx GROQ_MODEL "openai/gpt-oss-120b"
setx GEMINI_REASONING_EFFORT "high"
setx GROQ_BLENDER_MAX_TOKENS "2200"
setx GROQ_BLENDER_FALLBACK_MAX_TOKENS "2600"
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

For a Blender → Unity delivery, natural language is enough; no special command is required. The app verifies that the new FBX was created or changed before asking Unity to import and instantiate it. Generated assets are written to the configured Unity project, not committed to this repository.

The official `execute_blender_code` tool is available for complex Blender operations. It executes Python in the connected Blender process, so it must be treated as a powerful/destructive capability and used only with trusted requests. Keep Blender's normal save/recovery workflow enabled.

Destructive/high-impact work is held behind the explicit `APPROVE` / `CANCEL` risk gate.

## UI behavior

The conversation shows user prompts and final assistant results. Internal model/tool activity does **not** create chat bubbles; the current operation is shown in the status area and Live Inspector telemetry instead.

## Validation

GitHub Actions restores and builds the Windows .NET 8 project on every push to `main` and `beta/ship-v1`.

See `BETA_RELEASE.md` for the full SHIP test matrix and known boundaries.
