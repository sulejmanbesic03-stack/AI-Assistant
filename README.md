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

Default Unity Agent V2 model/provider routing is deliberately simple:

1. **MiniMax M3 Free** — `minimax/minimax-m3:free` through OpenRouter. This is the main model.
2. **Gemini 3.7 Flash** — `gemini-3.7-flash` through the direct Gemini API, using `high` reasoning by default.
3. **Groq GPT-OSS-120B** — `openai/gpt-oss-120b` through the direct Groq API as the final fallback.

GLM and Nemotron are not part of the default route.

The provider adapter detects gateway responses that arrive as HTTP 200 with a top-level JSON `error` object and converts them into real provider failures so fallback can continue automatically.

### Environment keys

```powershell
[Environment]::SetEnvironmentVariable("OPENROUTER_API_KEY","YOUR_KEY","User")
[Environment]::SetEnvironmentVariable("GEMINI_API_KEY","YOUR_KEY","User")
[Environment]::SetEnvironmentVariable("GROQ_API_KEY","YOUR_KEY","User")
```

Optional Gemini reasoning setting:

```powershell
[Environment]::SetEnvironmentVariable("GEMINI_REASONING_EFFORT","high","User")
```

Restart AI Assistant after changing user environment variables.

MiniMax is pinned to its OpenRouter `:free` model. Gemini and Groq are used through their direct provider APIs; keep those provider projects/accounts on free tiers if zero spend is required.

## Validation

GitHub Actions builds the Windows .NET 8 project on every push to `main`.
