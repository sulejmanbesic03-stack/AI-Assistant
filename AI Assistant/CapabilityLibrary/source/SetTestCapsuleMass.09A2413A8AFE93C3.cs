using AI_Assistant.TempCapabilities;
using System.Text.Json;
using System.Threading.Tasks;

public sealed class SetTestCapsuleMass : ITempCapability
{
    public string Name => "SetTestCapsuleMass";

    public Task<string> ExecuteAsync(
        TempCapabilityContext context,
        JsonElement arguments
    )
    {
        string result =
            context
                .NewUnityBatch()
                .SetFloat(
                    "TestCapsuleV1",
                    "UnityEngine.Rigidbody",
                    "mass",
                    10f
                )
                .SaveScene()
                .Execute();
        return Task.FromResult(result);
    }
}