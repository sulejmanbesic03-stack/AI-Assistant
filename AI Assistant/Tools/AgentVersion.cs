using AI_Assistant.AI;


namespace AI_Assistant.Tools
{
    public static class AgentVersion
    {
        public const string Version =
            "0.6.4-cowork-minimax";


        // Compile-time handshake retained for the legacy compatibility path.
        public const string RequiredContextRouterBuild =
            AIIntegration.ContextRouterBuild;

        public const string CoworkKernelBuild =
            "unity-cowork-v2-transactional-minimax-free";
    }
}
