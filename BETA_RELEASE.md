# AI Assistant 0.7.1 Cowork SHIP V1

This branch is the integrated beta/SHIP candidate that treats Unity and Blender as separate execution domains behind one desktop runtime.

## Architecture

```text
User goal
  -> runtime router
      -> Unity Cowork Agent V2
          -> compact live Unity snapshot
          -> adaptive free-first model request
          -> deterministic/local execution
          -> semantic bridge result validation
          -> compile + live verification
          -> correction delta when required
      -> Controlled Blender Agent V2
          -> probe installed Blender version
          -> model generates version-compatible scene-construction Python only
          -> host safety scan
          -> Blender headless factory-startup execution
          -> host-controlled .blend save
          -> host-controlled FBX/GLB export
          -> Python traceback + file verification
          -> one bounded automatic repair pass from the failed log
          -> optional handoff to Unity Assets/AI_Generated/Models
      -> legacy compatibility path for non-V2 workflows
```

## What changed

- Added a controlled Blender execution domain.
- Added Unity-to-Blender project settings in the WPF UI.
- Added direct Blender -> Unity model handoff.
- Added a Unity Editor postprocessor for generated models.
- Added a high-risk approval gate. Destructive/high-impact requests are held until the user types `APPROVE`.
- Replaced the fixed MiniMax-first provider with an adaptive zero-cost-first router:
  - initial implementation: OpenRouter free router -> Gemini -> Groq
  - correction passes: Groq GPT-OSS 120B -> Gemini -> OpenRouter free router
  - 429 cooldowns are remembered and blind retries are blocked
  - the successful provider remains sticky during a normal task
- Provider model IDs are overrideable with environment variables.
- Unity bridge responses are validated semantically, so harmless fields such as `error:null` or `errors:[]` no longer create false task failures.
- Runtime activity no longer fills the conversation. The chat contains user/assistant results while the current tool/model operation is shown in Live Inspector/status telemetry.
- Blender 3.6 LTS and Blender 4.x are supported by the controlled pipeline. The runtime probes the installed version and tells the model which API level to target.
- Blender execution catches Python tracebacks, verifies both `.blend` and export files, and performs one automatic correction pass from the failed script/log before returning failure.
- Added runtime diagnostics and a GitHub Actions Release build gate.

## Required setup

### API keys

At least one must be configured as a Windows environment variable:

```powershell
setx OPENROUTER_API_KEY "your-key"
setx GEMINI_API_KEY "your-key"
setx GROQ_API_KEY "your-key"
```

Recommended zero-cost setup is to configure all three, because the router can survive temporary provider limits.

Optional model overrides:

```powershell
setx OPENROUTER_MODEL "openrouter/free"
setx GEMINI_MODEL "gemini-3.7-flash"
setx GROQ_MODEL "openai/gpt-oss-120b"
setx GEMINI_REASONING_EFFORT "high"
```

The default OpenRouter model is `openrouter/free`, so SHIP V1 is not tied to one promotional free model.

### Blender

Install Blender 3.6 LTS or Blender 4.x. In the app open **Settings** and configure the executable if auto-detection does not find it.

Examples:

```text
C:\Program Files\Blender Foundation\Blender 3.6\blender.exe
C:\Program Files\Blender Foundation\Blender 4.5\blender.exe
```

Recommended workspace:

```text
C:\BlenderProjects
```

Blender runs with:

```text
--background --factory-startup --python <generated script>
```

The generated AI script is not allowed to save files, export, spawn subprocesses, access network modules or perform arbitrary file IO. The host owns save/export and verification.

### Unity

In **Settings**, set Unity project root to the local clone of `AIIntegrationProject` on its `beta/ship-v1` branch.

Blender exports are copied to:

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

5. **Blender simple asset**
   - `/blender create a low-poly barrel`
   - verify `.blend` and `.fbx` or `.glb`
   - verify asset appears in `Assets/AI_Generated/Models`
   - repeat on Blender 3.6 LTS

6. **Blender recovery**
   - force or encounter a generated Python API error
   - confirm traceback is captured
   - confirm one correction pass runs from the log
   - confirm final files are verified before success

7. **Blender safety**
   - request something that tempts arbitrary filesystem/network access
   - verify generated script is blocked if it contains forbidden operations

8. **Provider fallback**
   - test with only one key
   - test with OpenRouter + Groq
   - hit/imitate a rate limit and confirm no blind repeat loop

9. **Risk gate**
   - request deletion/replacement
   - confirm no execution before `APPROVE`

## Known boundaries

This remains a beta/SHIP candidate rather than a claim of perfect autonomy. Important boundaries are intentional:

- Blender verification proves the headless run and expected files, not artistic quality. Visual/mesh-quality scoring is a later layer.
- Free model quality varies, especially through a rotating free router.
- Unity Play Mode verification still occurs only when explicitly requested by the current Agent V2 flow.
- Blender-to-Unity handoff imports the generated model but does not automatically build production prefabs, LOD groups, colliders or materials unless the subsequent Unity task requests them.
- The risk gate is a conservative lexical host check plus the existing Unity execution safeguards; it is not an operating-system sandbox.

The design goal is to keep model intelligence replaceable while moving reliability, safety, verification and state into deterministic host code.
