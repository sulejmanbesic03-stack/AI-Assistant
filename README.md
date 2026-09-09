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

### Internal visual preview

For Blender asset requests the desktop app can create an internal concept preview before the MCP run. The preview is generated directly through the Gemini image API, saved outside the repository at `%LOCALAPPDATA%\AI Assistant\Previews\latest.png`, and shown only in the right-side Asset preview panel. It is visual feedback, not a claim that the image itself is the final mesh, and it is never added to the conversation as a chat message.

The preview is optional and best-effort: if `GEMINI_API_KEY` is missing, the image model is unavailable, or the request fails, the Blender and Unity stages continue normally. Set `AI_PREVIEW_ENABLED=0` to disable it. Override the image model with `GEMINI_IMAGE_MODEL`; the default is `gemini-3.1-flash-image`.

For asset-generation requests that enter the Blender → Unity pipeline, the app also requires a visual QA pass before export. After a real Blender geometry mutation it calls the MCP `get_viewport_screenshot` tool, sends the actual viewport together with the internal reference to a vision-capable provider, and feeds the structured review back into Blender for repair. A missing screenshot tool, invalid image response, or failed review blocks export instead of accepting the model’s textual success claim. The reviewer defaults to `qwen/qwen3.6-27b`, can be overridden with `GROQ_BLENDER_VISION_MODEL`, and falls back through the configured OpenRouter vision model and `MiniMax-M3`. The separate GPT-OSS 120B fallback remains text-only and is not used to make visual pass/fail decisions.

The model roles are intentionally separated:

- Groq Qwen 3.6 27B: Blender MCP tool loop and multimodal-capable asset interpretation.
- Direct Groq GPT-OSS 120B: Blender MCP fallback and intent classification.
- Gemini image model: internal visual concept/reference preview only.
- Official Blender MCP: Blender execution transport; it does not pretend to be a text-to-3D generator.
- Unity Agent V2: Unity project changes, import, scene handoff and live verification.

Requirements:

- Blender 5.1 or newer with the official MCP add-on enabled.
- Start MCP Server in the add-on preferences.
- `uvx` available on PATH.

The desktop app launches the official server from:

```text
git+https://projects.blender.org/lab/blender_mcp.git@4309a39646e644261624bfcd2bca669b343b7621#subdirectory=mcp
```

The server revision is pinned so a future upstream change cannot silently break a production build. Upgrade it deliberately after testing the matching Blender add-on. AI Assistant also constrains the temporary `uvx` environment to `mcp>=1.2,<2`, matching the official server source revision's `mcp.server.fastmcp` import.

The Blender MCP provider uses a controlled fallback chain:

1. `qwen/qwen3.6-27b` primary
2. `openai/gpt-oss-120b` fallback
3. OpenRouter when `OPENROUTER_API_KEY` is configured
4. MiniMax direct API when `MINIMAX_API_KEY` is configured
5. InclusionAI through a configured OpenAI-compatible endpoint (`INCLUSIONAI_API_KEY` + `INCLUSIONAI_BASE_URL`)

Each provider gets at most two attempts for transient timeout/5xx failures; a 429 is skipped immediately and the next configured provider is tried. The fallback chain is per model request, so a Groq timeout does not kill the complete Blender task. Set `BLENDER_OPENROUTER_MODEL` for Blender tool calls and `BLENDER_OPENROUTER_VISION_MODEL` (or `OPENROUTER_VISION_MODEL`) for viewport images; otherwise the configured `OPENROUTER_MODEL` is used.

To override the models and provider timeouts:

```powershell
[Environment]::SetEnvironmentVariable("GROQ_BLENDER_MODEL","qwen/qwen3.6-27b","User")
[Environment]::SetEnvironmentVariable("GROQ_BLENDER_FALLBACK_MODEL","openai/gpt-oss-120b","User")
[Environment]::SetEnvironmentVariable("GROQ_ROUTER_MODEL","openai/gpt-oss-120b","User")
[Environment]::SetEnvironmentVariable("OPENROUTER_API_KEY","your-key","User")
[Environment]::SetEnvironmentVariable("BLENDER_OPENROUTER_MODEL","nex-agi/nex-n2.5-pro:free","User")
[Environment]::SetEnvironmentVariable("BLENDER_OPENROUTER_VISION_MODEL","your-vision-model-id","User")
[Environment]::SetEnvironmentVariable("MINIMAX_API_KEY","your-key","User")
[Environment]::SetEnvironmentVariable("MINIMAX_MODEL","MiniMax-M2.7","User")
[Environment]::SetEnvironmentVariable("MINIMAX_VISION_MODEL","MiniMax-M3","User")
[Environment]::SetEnvironmentVariable("INCLUSIONAI_API_KEY","your-key","User")
[Environment]::SetEnvironmentVariable("INCLUSIONAI_BASE_URL","https://your-inclusionai-endpoint/v1","User")
[Environment]::SetEnvironmentVariable("INCLUSIONAI_MODEL","inclusionai/ling-3.0-flash","User")
[Environment]::SetEnvironmentVariable("INCLUSIONAI_VISION_MODEL","your-vision-model-id","User")
[Environment]::SetEnvironmentVariable("GROQ_BLENDER_REQUEST_TIMEOUT_SECONDS","180","User")
[Environment]::SetEnvironmentVariable("BLENDER_MCP_REQUEST_TIMEOUT_SECONDS","240","User")
[Environment]::SetEnvironmentVariable("GROQ_BLENDER_VISION_TIMEOUT_SECONDS","90","User")
[Environment]::SetEnvironmentVariable("BLENDER_VISION_FALLBACK_TIMEOUT_SECONDS","90","User")
[Environment]::SetEnvironmentVariable("GROQ_ROUTER_TIMEOUT_SECONDS","45","User")
```

If `uvx` is not on PATH, set its full executable path:

```powershell
[Environment]::SetEnvironmentVariable("BLENDER_MCP_COMMAND","C:\\Users\\YOUR_NAME\\.local\\bin\\uvx.exe","User")
```


## General provider routing

Unity Agent V2 keeps its own provider routing and compatibility behavior. The active Blender MCP path uses Groq → OpenRouter → MiniMax → InclusionAI. The natural-language intent router uses the same order, so a simple prompt can still reach Blender when Groq is unavailable. The viewport reviewer uses Groq → OpenRouter → MiniMax-M3; M2.x text models are not used for images. Model IDs can be overridden with environment variables.

## Required environment keys

For Blender MCP, configure at least one provider. `GROQ_API_KEY` is the primary path; `OPENROUTER_API_KEY` can take over when Groq is rate-limited or times out; MiniMax can run without Groq, and InclusionAI requires its key plus an OpenAI-compatible base URL. Unity Agent V2 keeps its separate compatibility path:

```powershell
setx GEMINI_API_KEY "your-key"
setx GROQ_API_KEY "your-key"
setx OPENROUTER_API_KEY "your-key"
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
