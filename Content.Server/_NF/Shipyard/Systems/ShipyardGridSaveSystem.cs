using Content.Server._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Events;
using Content.Shared.Shuttles.Save; // For SendShipSaveDataClientMessage
using Content.Server.Maps;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Piping.Components;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared.Access.Components;
using Content.Shared.Atmos.Components;
using Content.Shared.Movement.Components;
using Content.Shared.Power.Components;
using Content.Shared.VendingMachines;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Access.Components;
using Content.Server.StationRecords;
using Content.Shared.Storage.Components;
using Content.Shared.Chemistry.Components;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using System.Numerics;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Utility;
using Robust.Shared.ContentPack;
using Robust.Server.Player;
using Robust.Shared.EntitySerialization;
using Robust.Shared.Physics.Components;
using Robust.Shared.Containers;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Content.Server._NF.Shipyard.Systems;

/// <summary>
/// System for saving ships using the MapLoaderSystem infrastructure.
/// Saves ships as complete YAML files similar to savegrid command,
/// after cleaning them of problematic components and moving to exports folder.
/// </summary>
public sealed class ShipyardGridSaveSystem : EntitySystem
{
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IEntitySystemManager _entitySystemManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly SharedTransformSystem _transformSystem = default!;
    [Dependency] private readonly IResourceManager _resourceManager = default!;
    [Dependency] private readonly SharedContainerSystem _containerSystem = default!;

    private ISawmill _sawmill = default!;
    private MapLoaderSystem _mapLoader = default!;
    private SharedMapSystem _mapSystem = default!;

    public override void Initialize()
    {
        base.Initialize();

        // Initialize sawmill for logging
        _sawmill = Logger.GetSawmill("shipyard.gridsave");

        // Get the MapLoaderSystem reference
        _mapLoader = _entitySystemManager.GetEntitySystem<MapLoaderSystem>();
        _mapSystem = _entitySystemManager.GetEntitySystem<SharedMapSystem>();

        // Subscribe to shipyard console events
        SubscribeLocalEvent<ShipyardConsoleComponent, ShipyardConsoleSaveMessage>(OnSaveShipMessage);
    }

    private void OnSaveShipMessage(EntityUid consoleUid, ShipyardConsoleComponent component, ShipyardConsoleSaveMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId)
        {
            _sawmill.Warning("No ID card in shipyard console slot");
            return;
        }

        if (!_entityManager.TryGetComponent<ShuttleDeedComponent>(targetId, out var deed))
        {
            _sawmill.Warning("ID card does not have a shuttle deed");
            return;
        }

        if (deed.ShuttleUid == null || !_entityManager.TryGetEntity(deed.ShuttleUid.Value, out var shuttleUid))
        {
            _sawmill.Warning("Shuttle deed does not reference a valid shuttle");
            return;
        }

        if (!_entityManager.TryGetComponent<MapGridComponent>(shuttleUid.Value, out var gridComponent))
        {
            _sawmill.Warning("Shuttle entity is not a valid grid");
            return;
        }

        // Get player session
        if (!_playerManager.TryGetSessionByEntity(player, out var playerSession))
        {
            _sawmill.Warning("Could not get player session");
            return;
        }

        _sawmill.Info($"Starting ship save for {deed.ShuttleName ?? "Unknown_Ship"} owned by {playerSession.Name}");

        // Save the ship using our new grid-based system
        _ = Task.Run(async () =>
        {
            var success = await TrySaveGridAsShip(shuttleUid.Value, deed.ShuttleName ?? "Unknown_Ship", playerSession.UserId.ToString(), playerSession);

            if (success)
            {
                // Clean up the deed after successful save
                _entityManager.RemoveComponent<ShuttleDeedComponent>(targetId);

                // Also remove any other shuttle deeds that reference this shuttle
                RemoveAllShuttleDeeds(shuttleUid.Value);

                _sawmill.Info($"Successfully saved and removed ship {deed.ShuttleName}");
            }
            else
            {
                _sawmill.Error($"Failed to save ship {deed.ShuttleName}");
            }
        });
    }

    /// <summary>
    /// Removes all ShuttleDeedComponents that reference the specified shuttle EntityUid
    /// </summary>
    private void RemoveAllShuttleDeeds(EntityUid shuttleUid)
    {
        var query = _entityManager.EntityQueryEnumerator<ShuttleDeedComponent>();
        var deedsToRemove = new List<EntityUid>();

        while (query.MoveNext(out var entityUid, out var deed))
        {
            if (deed.ShuttleUid != null && _entityManager.TryGetEntity(deed.ShuttleUid.Value, out var deedShuttleEntity) && deedShuttleEntity == shuttleUid)
            {
                deedsToRemove.Add(entityUid);
            }
        }

        foreach (var deedEntity in deedsToRemove)
        {
            _entityManager.RemoveComponent<ShuttleDeedComponent>(deedEntity);
            _sawmill.Info($"Removed shuttle deed from entity {deedEntity}");
        }
    }

    /// <summary>
    /// Saves a grid to a YAML file using MapLoaderSystem, after cleaning it of problematic components.
    /// </summary>
    public async Task<bool> TrySaveGridAsShip(EntityUid gridUid, string shipName, string playerUserId, ICommonSession playerSession)
    {
        if (!_entityManager.HasComponent<MapGridComponent>(gridUid))
        {
            _sawmill.Error($"Entity {gridUid} is not a valid grid");
            return false;
        }

        MapId? tempMapId = null;
        EntityUid? tempGridUid = null;

        try
        {
            _sawmill.Info($"Starting 5-step ship save process for grid {gridUid} as '{shipName}'");

            // STEP 1: Create a blank map and teleport the ship to it for saving
            tempMapId = await Step1_CreateBlankMapAndTeleportShip(gridUid);
            if (!tempMapId.HasValue)
            {
                _sawmill.Error("Step 1 failed: Could not create temporary map or teleport ship");
                return false;
            }
            tempGridUid = gridUid; // Grid was moved, same EntityUid
            _sawmill.Info($"Step 1 complete: Ship teleported to temporary map {tempMapId.Value}");

            // STEP 2: Empty containers and clean grid of problematic components, delete freefloating entities
            var step2Success = await Step2_EmptyContainersAndCleanGrid(tempGridUid.Value);
            if (!step2Success)
            {
                _sawmill.Error("Step 2 failed: Could not clean grid properly");
                return false;
            }
            _sawmill.Info("Step 2 complete: Containers emptied and grid cleaned");

            // STEP 3: Delete vending machines and remaining problematic structures
            var step3Success = await Step3_DeleteProblematicStructures(tempGridUid.Value);
            if (!step3Success)
            {
                _sawmill.Error("Step 3 failed: Could not remove problematic structures");
                return false;
            }
            _sawmill.Info("Step 3 complete: Problematic structures removed");

            // STEP 4: Save the grid
            var saveSuccess = await Step4_SaveGrid(tempGridUid.Value, shipName, playerSession);
            if (!saveSuccess)
            {
                _sawmill.Error("Step 4 failed: Could not save grid");
                return false;
            }
            _sawmill.Info("Step 4 complete: Grid saved successfully");

            // STEP 5: Throw event, remove shuttle deed, update console
            var step5Success = await Step5_PostSaveCleanupAndEvents(gridUid, shipName, playerUserId, playerSession);
            if (!step5Success)
            {
                _sawmill.Error("Step 5 failed: Could not complete post-save cleanup");
                // Don't return false here as the ship was saved successfully
            }
            _sawmill.Info("Step 5 complete: Post-save cleanup and events fired");

            _sawmill.Info($"Ship save process completed successfully for '{shipName}'");
            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Exception during ship save: {ex}");
            return false;
        }
        finally
        {
            // Clean up temporary resources
            if (tempMapId.HasValue)
            {
                await Task.Delay(500); // Give systems time to finish processing
                try
                {
                    _mapManager.DeleteMap(tempMapId.Value);
                    _sawmill.Info($"Cleaned up temporary map {tempMapId.Value}");
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to clean up temporary map {tempMapId.Value}: {ex}");
                }
            }
        }
    }

    #region 5-Step Ship Save Process

    /// <summary>
    /// STEP 1: Create a blank map and teleport the ship to it for saving
    /// </summary>
    private async Task<MapId?> Step1_CreateBlankMapAndTeleportShip(EntityUid gridUid)
    {
        try
        {
            _sawmill.Info("Step 1: Creating blank map and teleporting ship");

            // Create a temporary blank map for saving
            var tempMapId = _mapManager.CreateMap();
            _sawmill.Info($"Created temporary map {tempMapId}");

            // Move the grid to the temporary map and normalize its position/rotation
            var gridTransform = _entityManager.GetComponent<TransformComponent>(gridUid);
            _transformSystem.SetCoordinates(gridUid, new EntityCoordinates(_mapManager.GetMapEntityId(tempMapId), Vector2.Zero));
            _transformSystem.SetLocalRotation(gridUid, Angle.Zero);

            _sawmill.Info($"Teleported grid {gridUid} to temporary map {tempMapId} at origin with normalized rotation");

            return tempMapId;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Step 1 failed: {ex}");
            return null;
        }
    }

    /// <summary>
    /// STEP 2: Empty the contents of any container on the grid, then clean the grid of problematic components
    /// and delete freefloating entities that are not anchored or connected to the grid
    /// </summary>
    private async Task<bool> Step2_EmptyContainersAndCleanGrid(EntityUid gridUid)
    {
        try
        {
            _sawmill.Info("Step 2: Emptying containers and cleaning grid");

            var allEntities = new HashSet<EntityUid>();

            // Get all entities on the grid
            if (_entityManager.TryGetComponent<MapGridComponent>(gridUid, out var grid))
            {
                var gridBounds = grid.LocalAABB;
                var lookupSystem = _entitySystemManager.GetEntitySystem<EntityLookupSystem>();
                foreach (var entity in lookupSystem.GetEntitiesIntersecting(gridUid, gridBounds))
                {
                    if (entity != gridUid) // Don't include the grid itself
                        allEntities.Add(entity);
                }
            }

            _sawmill.Info($"Found {allEntities.Count} entities to process");

            var entitiesRemoved = 0;
            var containersEmptied = 0;
            var componentsRemoved = 0;

            // First pass: Selectively empty containers - preserve bluespace stash contents
            foreach (var entity in allEntities.ToList())
            {
                if (!_entityManager.EntityExists(entity))
                    continue;

                // Remove problematic components that cause serialization issues
                if (_entityManager.RemoveComponent<StationRecordsComponent>(entity))
                {
                    componentsRemoved++;
                    _sawmill.Info($"Removed StationRecordsComponent from entity {entity}");
                }

                // Handle containers - preserve stash contents, empty others
                if (_entityManager.TryGetComponent<ContainerManagerComponent>(entity, out var containerManager))
                {
                    // Check if this is a bluespace stash structure that should preserve contents
                    bool isBlueSpaceStash = IsBlueSpaceStashStructure(entity);
                    
                    if (isBlueSpaceStash)
                    {
                        _sawmill.Info($"Preserving contents of bluespace stash structure {entity}");
                        continue; // Skip emptying this container
                    }

                    // For all other containers: empty them (remove loose items)
                    foreach (var container in containerManager.Containers.Values)
                    {
                        var containedEntities = container.ContainedEntities.ToList();
                        foreach (var containedEntity in containedEntities)
                        {
                            try
                            {
                                _entityManager.DeleteEntity(containedEntity);
                                entitiesRemoved++;
                            }
                            catch (Exception ex)
                            {
                                _sawmill.Warning($"Failed to delete contained entity {containedEntity}: {ex}");
                            }
                        }
                        if (containedEntities.Count > 0)
                        {
                            containersEmptied++;
                            _sawmill.Info($"Emptied container with {containedEntities.Count} items");
                        }
                    }
                }
            }

            // Second pass: Delete freefloating entities (not anchored or connected to grid)
            var freefloatingEntities = new List<EntityUid>();
            foreach (var entity in allEntities)
            {
                if (!_entityManager.EntityExists(entity))
                    continue;

                // Skip structural grid components
                if (_entityManager.HasComponent<MapGridComponent>(entity))
                    continue;

                // Check if entity is anchored or in a container
                if (_entityManager.TryGetComponent<TransformComponent>(entity, out var transform))
                {
                    // If not anchored and not in a container, mark for deletion
                    if (!transform.Anchored && !_containerSystem.IsEntityInContainer(entity))
                    {
                        freefloatingEntities.Add(entity);
                    }
                }
            }

            // Delete freefloating entities with enhanced error handling
            foreach (var entity in freefloatingEntities)
            {
                try
                {
                    if (_entityManager.EntityExists(entity))
                    {
                        _entityManager.DeleteEntity(entity);
                        entitiesRemoved++;
                    }
                }
                catch (NullReferenceException ex)
                {
                    // Handle gas canister null reference errors specifically
                    _sawmill.Warning($"Failed to delete freefloating entity {entity} due to null reference (likely gas canister IoC issue): {ex.Message}");
                }
                catch (Exception ex)
                {
                    _sawmill.Warning($"Failed to delete freefloating entity {entity}: {ex}");
                }
            }

            _sawmill.Info($"Step 2 complete: Emptied {containersEmptied} containers, removed {entitiesRemoved} entities, removed {componentsRemoved} components");
            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Step 2 failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// STEP 3: Delete any vending machines or remaining structures that would pose a problem to ship saving
    /// </summary>
    private async Task<bool> Step3_DeleteProblematicStructures(EntityUid gridUid)
    {
        try
        {
            _sawmill.Info("Step 3: Deleting problematic structures");

            var allEntities = new HashSet<EntityUid>();

            // Get all remaining entities on the grid
            if (_entityManager.TryGetComponent<MapGridComponent>(gridUid, out var grid))
            {
                var gridBounds = grid.LocalAABB;
                var lookupSystem = _entitySystemManager.GetEntitySystem<EntityLookupSystem>();
                foreach (var entity in lookupSystem.GetEntitiesIntersecting(gridUid, gridBounds))
                {
                    if (entity != gridUid) // Don't include the grid itself
                        allEntities.Add(entity);
                }
            }

            var structuresRemoved = 0;
            var componentsRemoved = 0;

            foreach (var entity in allEntities.ToList())
            {
                if (!_entityManager.EntityExists(entity))
                    continue;

                // Delete vending machines completely
                if (_entityManager.HasComponent<VendingMachineComponent>(entity))
                {
                    _sawmill.Info($"Removing vending machine entity {entity}");
                    _entityManager.DeleteEntity(entity);
                    structuresRemoved++;
                    continue;
                }

                // Remove problematic components from remaining entities
                
                // Remove session-specific components
                if (_entityManager.RemoveComponent<ActorComponent>(entity))
                    componentsRemoved++;
                if (_entityManager.RemoveComponent<EyeComponent>(entity))
                    componentsRemoved++;

                // Smart physics preservation - reset runtime state but keep collision
                if (_entityManager.TryGetComponent<PhysicsComponent>(entity, out var physics))
                {
                    // Reset problematic runtime physics state that causes serialization issues
                    // Keep the component for collision detection but clear runtime data
                    physics.LinearVelocity = Vector2.Zero;
                    physics.AngularVelocity = 0f;
                    // Don't remove the component - just reset problematic fields
                }

                // Preserve reagent containers in machines - DON'T empty them for machine functionality
                bool hasReagentContainer = _entityManager.HasComponent<ReagentContainerComponent>(entity);
                if (hasReagentContainer)
                {
                    _sawmill.Info($"Preserving reagent container in machine entity {entity}");
                    // Don't modify reagent containers - machines need them to function after load
                }

                // Remove atmospheric components that hold problematic runtime state
                if (_entityManager.RemoveComponent<AtmosDeviceComponent>(entity))
                    componentsRemoved++;

                // Reset power components to clean state through proper systems
                if (_entityManager.TryGetComponent<BatteryComponent>(entity, out var battery))
                {
                    if (_entitySystemManager.TryGetEntitySystem<BatterySystem>(out var batterySystem))
                    {
                        batterySystem.SetCharge(entity, battery.MaxCharge);
                    }
                }
            }

            _sawmill.Info($"Step 3 complete: Removed {structuresRemoved} problematic structures, cleaned {componentsRemoved} components");
            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Step 3 failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// STEP 4: Save the grid
    /// </summary>
    private async Task<bool> Step4_SaveGrid(EntityUid gridUid, string shipName, ICommonSession playerSession)
    {
        try
        {
            _sawmill.Info($"Step 4: Saving grid as '{shipName}'");

            // Save the grid using MapLoaderSystem to a temporary file
            var fileName = $"{shipName}.yml";
            var tempFilePath = new ResPath("/") / "UserData" / fileName;

            bool success = _mapLoader.TrySaveGrid(gridUid, tempFilePath);

            if (success)
            {
                _sawmill.Info($"Successfully saved grid to {fileName}");

                // Read the YAML file and send to client
                try
                {
                    using var fileStream = _resourceManager.UserData.OpenRead(tempFilePath);
                    using var reader = new StreamReader(fileStream);
                    var yamlContent = await reader.ReadToEndAsync();

                    // Send the YAML data to the client for local saving
                    var saveMessage = new SendShipSaveDataClientMessage(shipName, yamlContent);
                    RaiseNetworkEvent(saveMessage, playerSession);

                    _sawmill.Info($"Sent ship data '{shipName}' to client {playerSession.Name} for local saving");

                    // Clean up the temporary server file
                    await TryDeleteFileWithRetry(tempFilePath);
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to read/send YAML file: {ex}");
                    success = false;
                }
            }
            else
            {
                _sawmill.Error($"Failed to save grid to {fileName}");
            }

            return success;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Step 4 failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// STEP 5: Throw event to say grid was saved, remove shuttle deed from player's ID, and update console
    /// </summary>
    private async Task<bool> Step5_PostSaveCleanupAndEvents(EntityUid originalGridUid, string shipName, string playerUserId, ICommonSession playerSession)
    {
        try
        {
            _sawmill.Info("Step 5: Post-save cleanup and events");

            // Fire grid saved event
            var gridSavedEvent = new ShipSavedEvent
            {
                GridUid = originalGridUid,
                ShipName = shipName,
                PlayerUserId = playerUserId,
                PlayerSession = playerSession
            };
            RaiseLocalEvent(gridSavedEvent);
            _sawmill.Info($"Fired ShipSavedEvent for '{shipName}'");

            // Remove shuttle deed from player's ID if they have one
            if (_playerManager.TryGetSessionById(new NetUserId(Guid.Parse(playerUserId)), out var session) &&
                session.AttachedEntity != null)
            {
                var playerEntity = session.AttachedEntity.Value;

                // Look for ID cards in the player's inventory or hands
                // This is a simplified approach - in practice you'd want to check hands, inventory slots, etc.
                var handsQuery = _entityManager.GetComponent<HandsComponent>(playerEntity);
                foreach (var hand in handsQuery.Hands.Values)
                {
                    if (hand.HeldEntity != null &&
                        _entityManager.TryGetComponent<IdCardComponent>(hand.HeldEntity.Value, out var idCard) &&
                        _entityManager.TryGetComponent<ShuttleDeedComponent>(hand.HeldEntity.Value, out var shuttleDeed))
                    {
                        // Remove the shuttle deed component
                        _entityManager.RemoveComponent<ShuttleDeedComponent>(hand.HeldEntity.Value);
                        _sawmill.Info($"Removed shuttle deed from player {playerUserId}'s ID card");
                        break;
                    }
                }
            }

            // Delete the original grid entity now that save is complete
            if (_entityManager.EntityExists(originalGridUid))
            {
                await Task.Delay(100); // Brief delay to ensure all events are processed
                _entityManager.DeleteEntity(originalGridUid);
                _sawmill.Info($"Deleted original grid entity {originalGridUid}");
            }

            _sawmill.Info("Step 5 complete: Events fired, deed removed, grid deleted");
            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Step 5 failed: {ex}");
            return false;
        }
    }

    #endregion

    /// <summary>
    /// Legacy method - replaced by 5-step process above</summary>
    private async Task<EntityUid?> MoveAndCleanGrid(EntityUid originalGridUid, MapId targetMapId)
    {
        try
        {
            // Move the grid to the temporary map and normalize its rotation
            var gridTransform = _entityManager.GetComponent<TransformComponent>(originalGridUid);
            _transformSystem.SetCoordinates(originalGridUid, new EntityCoordinates(_mapManager.GetMapEntityId(targetMapId), Vector2.Zero));

            // Normalize grid rotation to 0 degrees
            _transformSystem.SetLocalRotation(originalGridUid, Angle.Zero);

            _sawmill.Info($"Moved grid {originalGridUid} to temporary map {targetMapId} and normalized rotation");

            // Clean the grid of problematic components
            CleanGridForSaving(originalGridUid);

            return originalGridUid;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Failed to move and clean grid: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Removes problematic components from a grid before saving.
    /// This includes session-specific data, vending machines, runtime state, etc.
    /// Uses a two-phase approach: first delete problematic entities, then clean remaining entities.
    /// </summary>
    public void CleanGridForSaving(EntityUid gridUid)
    {
        _sawmill.Info($"Starting grid cleanup for {gridUid}");

        var allEntities = new HashSet<EntityUid>();

        // Get all entities on the grid
        if (_entityManager.TryGetComponent<MapGridComponent>(gridUid, out var grid))
        {
            var gridBounds = grid.LocalAABB;
            var lookupSystem = _entitySystemManager.GetEntitySystem<EntityLookupSystem>();
            foreach (var entity in lookupSystem.GetEntitiesIntersecting(gridUid, gridBounds))
            {
                if (entity != gridUid) // Don't include the grid itself
                    allEntities.Add(entity);
            }
        }

        _sawmill.Info($"Found {allEntities.Count} entities to clean on grid");

        var entitiesRemoved = 0;
        var componentsRemoved = 0;

        // PHASE 1: Delete problematic entities that should not be saved
        // This eliminates them from processing entirely, avoiding race conditions
        _sawmill.Info("Phase 1: Deleting problematic entities");

        var entitiesToDelete = new List<EntityUid>();

        foreach (var entity in allEntities)
        {
            try
            {
                // Check if entity still exists before processing
                if (!_entityManager.EntityExists(entity))
                    continue;

                // Mark vending machines for deletion to avoid lag and complexity
                if (_entityManager.HasComponent<VendingMachineComponent>(entity))
                {
                    entitiesToDelete.Add(entity);
                    continue;
                }

                // Mark loose items for deletion (not anchored and not in containers)
                if (_entityManager.TryGetComponent<TransformComponent>(entity, out var transform))
                {
                    // Check if item is loose (not anchored and not in a container)
                    if (!transform.Anchored && !_containerSystem.IsEntityInContainer(entity))
                    {
                        // Skip important structural entities like the grid itself
                        if (!_entityManager.HasComponent<MapGridComponent>(entity))
                        {
                            entitiesToDelete.Add(entity);
                            continue;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _sawmill.Warning($"Error evaluating entity {entity} for deletion: {ex}");
            }
        }

        // Actually delete the marked entities
        foreach (var entity in entitiesToDelete)
        {
            try
            {
                if (_entityManager.EntityExists(entity))
                {
                    _sawmill.Info($"Deleting problematic entity {entity} during ship cleanup");
                    _entityManager.DeleteEntity(entity);
                    entitiesRemoved++;
                }
            }
            catch (Exception ex)
            {
                _sawmill.Warning($"Error deleting entity {entity}: {ex}");
            }
        }

        _sawmill.Info($"Phase 1 complete: deleted {entitiesRemoved} entities");

        // PHASE 2: Clean components from remaining entities
        // Re-gather remaining entities to avoid processing deleted ones
        _sawmill.Info("Phase 2: Cleaning components from remaining entities");

        var remainingEntities = new HashSet<EntityUid>();

        if (_entityManager.TryGetComponent<MapGridComponent>(gridUid, out grid))
        {
            var gridBounds = grid.LocalAABB;
            var lookupSystem = _entitySystemManager.GetEntitySystem<EntityLookupSystem>();
            foreach (var entity in lookupSystem.GetEntitiesIntersecting(gridUid, gridBounds))
            {
                if (entity != gridUid) // Don't include the grid itself
                    remainingEntities.Add(entity);
            }
        }

        _sawmill.Info($"Found {remainingEntities.Count} remaining entities to clean components from");

        foreach (var entity in remainingEntities)
        {
            try
            {
                // Check if entity still exists before processing
                if (!_entityManager.EntityExists(entity))
                    continue;

                // Remove session-specific components that shouldn't be saved
                if (_entityManager.RemoveComponent<ActorComponent>(entity))
                    componentsRemoved++;
                if (_entityManager.RemoveComponent<EyeComponent>(entity))
                    componentsRemoved++;

                // Note: Removed PhysicsComponent deletion that was causing collision issues in loaded ships
                // PhysicsComponent and FixturesComponent are needed for proper collision detection

                // Reset power components to clean state through the proper system
                if (_entityManager.TryGetComponent<BatteryComponent>(entity, out var battery))
                {
                    // Use the battery system instead of direct access
                    if (_entitySystemManager.TryGetEntitySystem<BatterySystem>(out var batterySystem))
                    {
                        batterySystem.SetCharge(entity, battery.MaxCharge);
                    }
                }

                // Remove problematic atmospheric state
                if (_entityManager.RemoveComponent<AtmosDeviceComponent>(entity))
                    componentsRemoved++;

                // Remove any other problematic components
                // Note: We're being conservative here - removing things that commonly cause issues
            }
            catch (Exception ex)
            {
                _sawmill.Warning($"Error cleaning entity {entity}: {ex}");
            }
        }

        _sawmill.Info($"Grid cleanup complete: deleted {entitiesRemoved} entities, removed {componentsRemoved} components from {remainingEntities.Count} remaining entities");
    }

    /// <summary>
    /// Writes YAML data to a temporary file in UserData for loading
    /// </summary>
    public async Task<bool> WriteYamlToUserData(string fileName, string yamlData)
    {
        try
        {
            var userDataPath = _resourceManager.UserData;
            var resPath = new ResPath(fileName);

            await using var stream = userDataPath.OpenWrite(resPath);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(yamlData);

            _sawmill.Info($"Temporary YAML file written: {resPath}");
            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Failed to write temporary YAML file {fileName}: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Attempts to delete a file with retry logic to handle file access conflicts
    /// </summary>
    private async Task TryDeleteFileWithRetry(ResPath filePath, int maxRetries = 3, int delayMs = 100)
    {
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                _resourceManager.UserData.Delete(filePath);
                _sawmill.Info($"Successfully deleted temporary server file {filePath}");
                return;
            }
            catch (IOException ex) when (attempt < maxRetries - 1)
            {
                _sawmill.Warning($"File deletion attempt {attempt + 1} failed for {filePath}: {ex.Message}. Retrying in {delayMs}ms...");
                await Task.Delay(delayMs);
                delayMs *= 2; // Exponential backoff
            }
            catch (Exception ex)
            {
                _sawmill.Error($"Failed to delete temporary file {filePath} on attempt {attempt + 1}: {ex}");
                if (attempt == maxRetries - 1)
                {
                    _sawmill.Error($"Giving up on deleting {filePath} after {maxRetries} attempts");
                }
                break;
            }
        }
    }

    /// <summary>
    /// Determines if an entity is a bluespace stash structure that should preserve its contents
    /// </summary>
    private bool IsBlueSpaceStashStructure(EntityUid entity)
    {
        // Check if entity has Storage component and is a bluespace stash
        if (!_entityManager.TryGetComponent<StorageComponent>(entity, out var storage))
            return false;

        // Check if the entity prototype ID contains "bluespacestash"
        if (_entityManager.TryGetComponent<MetaDataComponent>(entity, out var metaData))
        {
            var prototypeId = metaData.EntityPrototype?.ID;
            if (prototypeId != null && prototypeId.Contains("bluespacestash"))
            {
                return true;
            }
        }

        return false;
    }

}
