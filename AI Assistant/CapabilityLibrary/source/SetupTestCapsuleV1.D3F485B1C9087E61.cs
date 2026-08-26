using AI_Assistant.TempCapabilities;
using System.Text.Json;
using System.Threading.Tasks;

public sealed class SetupTestCapsuleV1 : ITempCapability
{
    public string Name => "SetupTestCapsuleV1";

    public Task<string> ExecuteAsync(
        TempCapabilityContext context,
        JsonElement arguments
    )
    {
        string result =
            context
                .NewUnityBatch()
                .CreatePrimitive(
                    "Capsule",
                    "TestCapsuleV1",
                    ""
                )
                .SetPosition(
                    "TestCapsuleV1",
                    0f,
                    2f,
                    0f
                )
                .AddComponent(
                    "TestCapsuleV1",
                    "UnityEngine.Rigidbody"
                )
                .SetFloat(
                    "TestCapsuleV1",
                    "UnityEngine.Rigidbody",
                    "mass",
                    1.5f
                )
                .SaveScene()
                .Execute();

        return Task.FromResult(result);
    }
}