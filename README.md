# AI-Assistant

An autonomous AI development assistant integrating Unity engineering workflows with provider-routed reasoning models.

## Unity Cowork Agent V2

Unity engineering requests use a Cowork-style execution kernel:

1. Inspect the live Unity project.
2. Design the smallest safe implementation.
3. Prefer persistent scripts and deterministic Unity actions.
4. Reuse known capabilities when available.
5. Use a generated dynamic Unity capability only as a RunCommand-style escape hatch.
6. Execute locally through the Unity bridge.
7. Observe the post-attempt project state and console.
8. Correct from fresh state instead of blindly retrying the same plan.
9. Reject duplicate failed plans and request a fresh delta instead of dead-ending.
10. Resume failed tasks with a new live inspect cycle so partial Unity state is never assumed away.

The Unity batch bridge is transactional/idempotent: failed deterministic batches roll back through Unity Undo, while create operations reuse an already-existing exact hierarchy path instead of creating duplicate objects.

The previous AIIntegration path is retained for compatibility with non-Unity workflows.

## Free-first model routing

Default Unity Agent V2 model/provider routing:

1. **Gemini 3.7 Flash** — `gemini-3.7-flash`, direct Gemini API, `high` reasoning by default.
2. **GLM 5.2 Free** — `z-ai/glm-5.2:free` through OpenRouter.
3. **MiniMax M3 Free** — `minimax/minimax-m3:free` through OpenRouter model fallback.
4. **Nemotron 3 Ultra Free** — `nvidia/nemotron-3-ultra-550b-a55b:free` through OpenRouter model fallback.
5. **Groq GPT-OSS-120B** — `openai/gpt-oss-120b` as the final direct provider fallback.

The OpenRouter adapter accepts only `:free` model overrides (or `openrouter/free`). A stale paid `OPENROUTER_MODEL`, such as `z-ai/glm-5.3`, is ignored and falls back to `z-ai/glm-5.2:free`.

The provider adapter also detects gateway responses that incorrectly arrive as HTTP 200 with a top-level JSON `error` object. Those are converted into real provider failures so the next free provider is tried automatically.

### Environment keys

```powershell
[Environment]::SetEnvironmentVariable("GEMINI_API_KEY","YOUR_KEY","User")
[Environment]::SetEnvironmentVariable("OPENROUTER_API_KEY","YOUR_KEY","User")
[Environment]::SetEnvironmentVariable("GROQ_API_KEY","YOUR_KEY","User")
```

Optional reasoning/model settings:

```powershell
[Environment]::SetEnvironmentVariable("GEMINI_REASONING_EFFORT","high","User")
[Environment]::SetEnvironmentVariable("OPENROUTER_MODEL","z-ai/glm-5.2:free","User")
[Environment]::SetEnvironmentVariable("OPENROUTER_REASONING_EFFORT","high","User")
[Environment]::SetEnvironmentVariable("OPENROUTER_FALLBACK_MODELS","minimax/minimax-m3:free,nvidia/nemotron-3-ultra-550b-a55b:free","User")
```

Restart AI Assistant after changing user environment variables.

Gemini, OpenRouter and Groq account/billing state is controlled by those providers. The project deliberately selects free-tier/free-endpoint models, but keep the associated provider accounts/projects on their free plans if zero spend is required.

## Validation

GitHub Actions builds the Windows .NET 8 project on every push to `main`.
