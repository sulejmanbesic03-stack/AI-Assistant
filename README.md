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

## OpenRouter

Set the key as a user environment variable on Windows:

```powershell
[Environment]::SetEnvironmentVariable("OPENROUTER_API_KEY","YOUR_KEY","User")
```

Default Unity Agent V2 model routing:

- Main: `z-ai/glm-5.3`
- Model fallback 1: `z-ai/glm-5.2`
- Model fallback 2: `z-ai/glm-5.3-flash`
- Direct provider fallback: Gemini, then Groq when their keys are configured
- Default OpenRouter reasoning effort: `high`

Optional overrides:

```powershell
[Environment]::SetEnvironmentVariable("OPENROUTER_MODEL","z-ai/glm-5.3","User")
[Environment]::SetEnvironmentVariable("OPENROUTER_REASONING_EFFORT","high","User")
[Environment]::SetEnvironmentVariable("OPENROUTER_FALLBACK_MODELS","z-ai/glm-5.2,z-ai/glm-5.3-flash","User")
```

Restart the AI Assistant after changing user environment variables.

## Validation

GitHub Actions builds the Windows .NET 8 project on every push to `main`.
