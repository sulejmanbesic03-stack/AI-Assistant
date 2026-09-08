# AI Assistant 0.11.1 Cowork SHIP V1

This branch is the integrated beta/SHIP candidate that treats Unity and Blender as separate execution domains behind one desktop runtime.

## Architecture

```text
User goal
  -> Groq intent router (natural language)
      -> Unity Cowork Agent V2
          -> compact live Unity snapshot
          -> adaptive free-first model request
          -> deterministic/local execution
          -> semantic bridge result validation
          -> compile + live verification
          -> correction delta when required
      -> official Blender Lab MCP client
          -> uvx starts pinned official blender-mcp over stdio
          -> MCP initialize + tools/list discovery
          -> Groq local tool-calling loop
          -> official Blender add-on on localhost:9876
          -> host verifies a new usable FBX
          -> optional deterministic handoff to Unity
      -> legacy compatibility path for non-V2 workflows
```

## What changed

- Added an official Blender MCP client/orchestrator; the app does not invent a second Blender bridge or scene JSON format.
- Added direct Blender -> Unity model handoff after a verified export.
- Added a high-risk approval gate. Destructive/high-impact requests are held until the user types `APPROVE`.
- Blender MCP uses direct Groq Qwen 3.6 27B first and direct Groq GPT-OSS 120B fallback. OpenRouter is isolated to the separate Unity provider path.
- Natural-language intent routing sends a 3D-create-and-deliver request to Blender → Unity without requiring Blender-specific keywords.
- MCP tool schemas are compacted and limited to the official core tool set to protect Groq free-tier input tokens.
- Malformed tool calls, MCP protocol errors, cancellation, pagination and uvx startup failures are surfaced and recoverable.
- Unity bridge responses are validated semantically, so harmless fields such as `error:null` or `errors:[]` no longer create false task failures.
- Runtime activity no longer fills the conversation. The chat contains user/assistant results while the current tool/model operation is shown in Live Inspector/status telemetry.
- Blender 5.1+ with the matching official MCP add-on is the supported path.
- The runtime verifies that the expected FBX is new, readable and has an FBX header before Unity is called.
- Added runtime diagnostics and a GitHub Actions Release build gate.

## Required setup

### API keys

For Blender MCP, configure this Windows environment variable:

```powershell
setx GROQ_API_KEY "your-key"
```

Unity Agent V2 can additionally use its separately configured providers.

Optional model overrides:

```powershell
setx GEMINI_MODEL "gemini-3.7-flash"
setx GROQ_MODEL "openai/gpt-oss-120b"
setx GEMINI_REASONING_EFFORT "high"
setx GROQ_BLENDER_MODEL "qwen/qwen3.6-27b"
setx GROQ_BLENDER_FALLBACK_MODEL "openai/gpt-oss-120b"
setx GROQ_BLENDER_MAX_TOKENS "2200"
setx GROQ_BLENDER_FALLBACK_MAX_TOKENS "2600"
```

Restart AI Assistant after changing environment variables.

### Blender

Install Blender 5.1 or newer and enable the official MCP add-on. In the add-on preferences, start the MCP Server. In the app open **Settings** and configure the executable if auto-detection does not find it.

Examples:

```text
C:\Program Files\Blender Foundation\Blender 5.1\blender.exe
C:\Program Files\Blender Foundation\Blender 5.2\blender.exe
```

Recommended workspace:

```text
C:\BlenderProjects
```

The desktop app starts the pinned official server with `uvx`; `uvx` must be on PATH. If required, set `BLENDER_MCP_COMMAND` to the full path of `uvx.exe`. The server then communicates with the running Blender add-on on `localhost:9876`.

Pinned server source:

```text
git+https://projects.blender.org/lab/blender_mcp.git@4309a39646e644261624bfcd2bca669b343b7621#subdirectory=mcp
```

### Unity

In **Settings**, set Unity project root to the local clone of `AIIntegrationProject` on its `beta/ship-v1` branch.

Blender exports are written at runtime to the configured Unity project:

```text
Assets/AI_Generated/Models
```

The Unity SHIP branch includes `AIGeneratedAssetPostprocessor.cs`, which applies conservative import defaults and adds:

```text
AI Assistant > Generated Assets > Reveal Folder
AI Assistant > Generated Assets > Reimport All
```

## Commands

Unity:

```text
/agent Create or repair a simple Enemy AI using the current project setup...
/plan Inspect the current Player setup and propose the safest repair...
```

Blender:

```text
/blender Create a low-poly wooden barrel suitable for a survival game, around 1400 triangles, with separate metal hoops.
/blender Create a stylized modular wooden crate and export it for Unity.
```

Risk gate:

```text
/agent delete all old enemy controller scripts and replace the entire system
```

The runtime should hold the task and require:

```text
APPROVE
```

or:

```text
CANCEL
```

## SHIP test matrix

1. **Startup / diagnostics**
   - app opens
   - runtime panel shows configured providers
   - Blender path validation is accurate
   - Unity project root validation is accurate
   - runtime activity appears in Live Inspector instead of chat bubbles

2. **Unity simple mutation**
   - create primitive or material
   - save scene
   - verify no false failure from harmless bridge error fields
   - verify no duplicate execution after continuation

3. **Unity code repair**
   - break a small gameplay script deliberately
   - ask Agent V2 to repair it
   - confirm compile watchdog + correction delta

4. **Unity complex task**
   - Player controller repair
   - Enemy patrol/chase/attack state machine

5. **Blender MCP simple asset**
   - `/blender create a low-poly barrel`
   - confirm the app discovers official tools over stdio
   - confirm the Blender add-on executes the tool call
   - confirm the requested export path exists and is readable

6. **Natural-language Blender → Unity**
   - `Napravi mi AA charactera za survival, neka bude muško, i pošalji mi ga u Unity.`
   - confirm the router selects Blender → Unity
   - confirm no generic placeholder instruction replaces the user request
   - confirm Unity imports and instantiates only after a new FBX is verified

7. **Blender recovery**
   - stop the official MCP server or disable the add-on
   - confirm the error identifies uvx/MCP/add-on connectivity
   - retry after restoring the add-on without duplicating the root

8. **Blender safety**
   - verify destructive requests are held by the risk gate
   - remember that official `execute_blender_code` is powerful and runs in Blender

9. **Provider fallback**
   - test Qwen primary and direct Groq GPT-OSS 120B fallback
   - confirm OpenRouter is not used by the Blender MCP path
   - hit/imitate a rate limit and confirm no blind repeat loop

10. **Risk gate**
   - request deletion/replacement
   - confirm no execution before `APPROVE`

## Known boundaries

This remains a beta/SHIP candidate rather than a claim of perfect autonomy. Important boundaries are intentional:

- Blender verification proves that a new readable FBX was exported, not artistic quality or production topology.
- Groq free-tier limits can still prevent a request; the app must report that failure and never claim an export succeeded.
- Unity Play Mode verification still occurs only when explicitly requested by the current Agent V2 flow.
- Blender-to-Unity handoff imports and instantiates the generated model but does not automatically build production prefabs, LOD groups, colliders or materials unless requested.
- The generated FBX is written to the configured Unity project at runtime and is not committed to this repository.
- The risk gate is a conservative lexical host check plus the existing Unity execution safeguards; it is not an operating-system sandbox.

The design goal is to keep model intelligence replaceable while moving reliability, safety, verification and state into deterministic host code.
