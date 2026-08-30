# AI-Assistant

An autonomous AI development assistant integrating Unity engineering workflows with provider-routed reasoning models.

## Unity Cowork Agent V2

Unity engineering requests now use a Cowork-style execution kernel:

1. Inspect the live Unity project.
2. Design the smallest safe implementation.
3. Prefer persistent scripts and deterministic Unity actions.
4. Reuse known capabilities when available.
5. Use a generated dynamic Unity capability only as a RunCommand-style escape hatch.
6. Execute locally through the Unity bridge.
7. Observe the post-attempt project state and console.
8. Correct from fresh state instead of blindly retrying the same plan.
9. Stop duplicate execution plans and keep task state for continuation.

The previous AIIntegration path is retained for compatibility with non-Unity workflows.

## Free model routing

Set the OpenRouter key as a user environment variable on Windows:

```powershell
[Environment]::SetEnvironmentVariable("OPENROUTER_API_KEY","YOUR_KEY","User")
```

Default Unity Agent V2 routing is intentionally free-only:

- Main OpenRouter model: `z-ai/glm-5.2:free`
- OpenRouter model fallback: `nvidia/nemotron-3-ultra-550b-a55b:free`
- Direct provider fallback: `gemini-3.6-flash` when `GEMINI_API_KEY` is configured
- Final direct provider fallback: `openai/gpt-oss-120b` on Groq when `GROQ_API_KEY` is configured
- Default OpenRouter reasoning effort: `high`

The OpenRouter adapter accepts only `:free` model overrides (or `openrouter/free`). If `OPENROUTER_MODEL` still contains an older paid model such as `z-ai/glm-5.3`, Agent V2 ignores it and falls back to `z-ai/glm-5.2:free`.

Optional free-only overrides:

```powershell
[Environment]::SetEnvironmentVariable("OPENROUTER_MODEL","z-ai/glm-5.2:free","User")
[Environment]::SetEnvironmentVariable("OPENROUTER_REASONING_EFFORT","high","User")
[Environment]::SetEnvironmentVariable("OPENROUTER_FALLBACK_MODELS","nvidia/nemotron-3-ultra-550b-a55b:free","User")
```

Restart the AI Assistant after changing user environment variables.

Important: Gemini and Groq have free account tiers, but billing/account state is controlled by those providers. The project selects their free-tier-supported models; keep those provider accounts on their free plans if zero spend is required.

## Validation

GitHub Actions builds the Windows .NET 8 project on every push to `main`.
