using Robust.Shared.GameStates;

namespace Content.Shared.CM14.Xenos.Evolution;

/// <summary>
/// Marks an NPC xeno to automatically evolve when their evolution action is ready.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class XenoAIAutoEvolveComponent : Component
{
    /// <summary>
    /// How often to check if evolution is ready (in seconds).
    /// </summary>
    [DataField, AutoNetworkedField]
    public float CheckInterval = 30f;

    /// <summary>
    /// Time when the next check should occur.
    /// </summary>
    [DataField, AutoNetworkedField]
    public TimeSpan NextCheckTime;
}
