using AI_Assistant.TempCapabilities;
using System.Text.Json;
using System.Threading.Tasks;

public sealed class SetCapsuleMass : ITempCapability
{
    public string Name => "SetCapsuleMass";

    public Task<string> ExecuteAsync(
        TempCapabilityContext context,
        JsonElement arguments
    )
    {
        // Assuming the capsule GameObject is named "Capsule" in the scene hierarchy.
        string result =
            context
                .NewUnityBatch()
                .SetFloat(
                    "Capsule",
                    "UnityEngine.Rigidbody",
                    "mass",
                    10f
                )
                .SaveScene()
                .Execute();
        return Task.FromResult(result);
    }
}