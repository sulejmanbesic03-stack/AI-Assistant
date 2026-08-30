using AI_Assistant.AI;


namespace AI_Assistant.Tools
{
    public static class AgentVersion
    {
        public const string Version =
            "0.6.0-cowork-v2";


        // Compile-time handshake retained for the legacy compatibility path.
        public const string RequiredContextRouterBuild =
            AIIntegration.ContextRouterBuild;

        public const string CoworkKernelBuild =
            "unity-cowork-v2-openrouter";
    }
}
