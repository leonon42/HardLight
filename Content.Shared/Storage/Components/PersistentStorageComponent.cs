using Robust.Shared.GameObjects;

namespace Content.Shared.Storage.Components;

/// <summary>
/// Marker component for containers that should preserve their contents during ship save/load operations.
/// Used by the ship persistence system to identify storage containers that should not be emptied
/// when preparing ships for serialization.
/// </summary>
[RegisterComponent]
public sealed partial class PersistentStorageComponent : Component
{
    // Marker component - no additional data needed
    // The presence of this component indicates the container should preserve contents during ship save/load
}