# Agent V2

Agent V2 is the long-term Unity orchestration path for AI Assistant.

## Why it exists

The old loop lets the model repeatedly decide which Unity tool to call. Complex jobs can therefore consume many model requests, repeatedly resend tool schemas/context, hit provider rate limits, lose progress, and create provider-specific tool-history problems.

Agent V2 moves orchestration into C#.

## Flow

```text
User goal
  -> compact deterministic Unity snapshot
  -> one model implementation request
  -> C# executes persistent scripts/actions locally
  -> Unity compilation
  -> one final verification
  -> optional single model repair if compilation failed
```

Normal successful work should use one AI request. A compile repair uses a second request. Official Unity documentation can cause one additional refinement request only when the implementation explicitly marks a version-sensitive uncertainty.

## Provider behavior

The model receives no Unity tool schemas and no Unity tool-call history in V2. Gemini and Groq are implementation engines only.

Gemini is preferred when `GEMINI_API_KEY` exists. If Gemini returns a transient failure such as 429/503 and `GROQ_API_KEY` exists, the task switches to Groq and stays there for the rest of that task.

Optional environment variables:

- `GEMINI_MODEL` - overrides the Gemini model name.
- `GROQ_MODEL` - overrides the Groq model name.
- `AI_AGENT_V2=0` - temporarily disables V2 and lets the old Unity tool loop handle requests.

## Modes

Normal Unity action prompts use Agent mode automatically.

- `/agent <goal>` forces Agent V2.
- `/plan <goal>` captures project context and returns a plan without mutating Unity.

## State

Task progress is kept in `AgentTaskStateV2`, not provider chat/tool history. If a provider is unavailable or execution fails, a plain `nastavi` / `continue` can resume the stored goal and snapshot.

## Safety rules

The implementation prompt contains a compact permanent set of Unity engineering rules:

- repair existing controllers/AI instead of stacking duplicates;
- do not add duplicate physical body colliders;
- keep one authority for movement and one for mouse look;
- isolate Rigidbody collision rotation from camera pitch;
- use explicit Enemy AI state transitions;
- every stopped NavMeshAgent must have an explicit resume path;
- persistent gameplay code stays in normal MonoBehaviour scripts;
- temporary capabilities are only for one-shot Editor setup that the deterministic command layer cannot express.

## First tests

### Player repair

```text
/agent Inspect and repair the current Player controller. When the Player collides with an object the camera starts rotating uncontrollably. Inspect the existing Player hierarchy, colliders, Rigidbody/CharacterController and movement/camera scripts, repair the existing system rather than creating another controller, compile it, save the scene and verify the final setup.
```

### Enemy AI

```text
/agent Create or repair a simple Enemy AI using the current project setup. It must patrol, detect and chase the Player, stop in attack range without dead-ending, resume chase when the Player leaves attack range, and return to patrol when the Player escapes. Use persistent MonoBehaviour code and the existing navigation setup where possible. Compile, configure the Enemy and save the scene.
```

To request Play Mode verification, say it explicitly, for example `testiraj u Play Mode`.
