using AI_Assistant.TempCapabilities;
using System.Text.Json;
using System.Threading.Tasks;

public sealed class CreateTestCapsuleV1 : ITempCapability
{
    public string Name => "CreateTestCapsuleV1";

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
                .SetPosition(
                    "TestCapsuleV1",
                    0f,
                    2f,
                    0f
                )
                .SaveScene()
                .Execute();

        return Task.FromResult(result);
    }
}