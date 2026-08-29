using AI_Assistant.AI;


namespace AI_Assistant.Tools
{
    public static class AgentVersion
    {
        public const string Version =
            "0.4.7-gemini-timeout-preview";


        // Compile-time handshake. A new version label cannot compile
        // against an older AIIntegration without ContextRouterBuild.
        public const string RequiredContextRouterBuild =
            AIIntegration.ContextRouterBuild;
    }
}