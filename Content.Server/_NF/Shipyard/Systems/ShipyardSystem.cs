using Content.Server.Shuttles.Systems;
using Content.Server.Shuttles.Components;
using Content.Shared.Shuttles.Components;
using Content.Server.Station.Components;
using Content.Server._NF.StationEvents.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Station.Systems;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared.GameTicking;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Content.Shared._NF.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.Asynchronous;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Shared._NF.Shipyard.Events;
using Content.Shared.Mobs.Components;
using Robust.Shared.Containers;
using Content.Server._NF.Station.Components;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.EntitySerialization;
using Robust.Shared.Utility;
using Content.Server.Shuttles.Save;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map.Events;
using Robust.Shared.Network;
using Robust.Shared.Player;
using System;
using Robust.Shared.Log;
using Robust.Shared.ContentPack;
using Content.Shared.Shuttles.Save; // For RequestLoadShipMessage, ShipConvertedToSecureFormatMessage
using Robust.Shared.Serialization.Markdown; // For MappingDataNode
using Robust.Shared.Serialization.Markdown.Mapping; // For MappingDataNode
using Robust.Shared.Serialization.Manager; // For DataNodeParser
using System.Collections.Generic; // For HashSet
using Robust.Shared.Maths; // For Angle and Matrix3Helpers
using System.Numerics; // For Matrix3x2
using Content.Shared.Access.Components; // For IdCardComponent
using Robust.Shared.Map.Components; // For MapGridComponent
using Content.Server._NF.StationEvents.Components; // For LinkedLifecycleGridParentComponent
using Content.Server.Maps; // For GameMapPrototype
using Content.Shared.Chat; // For InGameICChatType
using Content.Shared.Radio; // For RadioChannelPrototype
using Robust.Shared.Prototypes; // For Loc
using Content.Server.Radio.EntitySystems; // For RadioSystem
using Content.Server._NF.Shuttles.Systems; // For ShuttleRecordsSystem
using Content.Shared.Shuttles.Components; // For IFFComponent
using Content.Shared.Popups; // For PopupSystem
using Robust.Shared.Audio.Systems; // For SharedAudioSystem
using Content.Server.Administration.Logs; // For IAdminLogManager
using Content.Shared.Database; // For LogType

namespace Content.Server._NF.Shipyard.Systems;

/// <summary>
/// Temporary component to mark entities that should be anchored after grid loading is complete
/// </summary>
[RegisterComponent]
public sealed partial class PendingAnchorComponent : Component
{
}

public sealed partial class ShipyardSystem : SharedShipyardSystem
{
    [Dependency] private readonly IConfigurationManager _configManager = default!;
    [Dependency] private readonly DockingSystem _docking = default!;
    [Dependency] private readonly PricingSystem _pricing = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly MapLoaderSystem _mapLoader = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;
    [Dependency] private readonly MapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IEntitySystemManager _entitySystemManager = default!;
    [Dependency] private readonly IServerNetManager _netManager = default!; // Ensure this is present
    [Dependency] private readonly ITaskManager _taskManager = default!;
    [Dependency] private readonly IResourceManager _resources = default!;
    [Dependency] private readonly IDependencyCollection _dependency = default!; // For EntityDeserializer

    public MapId? ShipyardMap { get; private set; }
    private float _shuttleIndex;
    private const float ShuttleSpawnBuffer = 1f;
    private ISawmill _sawmill = default!;
    private bool _enabled;
    private float _baseSaleRate;

    // The type of error from the attempted sale of a ship.
    public enum ShipyardSaleError
    {
        Success, // Ship can be sold.
        Undocked, // Ship is not docked with the station.
        OrganicsAboard, // Sapient intelligence is aboard, cannot sell, would delete the organics
        InvalidShip, // Ship is invalid
        MessageOverwritten, // Overwritten message.
    }

    // TODO: swap to strictly being a formatted message.
    public struct ShipyardSaleResult
    {
        public ShipyardSaleError Error; // Whether or not the ship can be sold.
        public string? OrganicName; // In case an organic is aboard, this will be set to the first that's aboard.
        public string? OverwrittenMessage; // The message to write if Error is MessageOverwritten.
    }

    public override void Initialize()
    {
        base.Initialize();

        // FIXME: Load-bearing jank - game doesn't want to create a shipyard map at this point.
        _enabled = _configManager.GetCVar(NFCCVars.Shipyard);
        _configManager.OnValueChanged(NFCCVars.Shipyard, SetShipyardEnabled); // NOTE: run immediately set to false, see comment above

        _configManager.OnValueChanged(NFCCVars.ShipyardSellRate, SetShipyardSellRate, true);
        _sawmill = Logger.GetSawmill("shipyard");

        SubscribeLocalEvent<ShipyardConsoleComponent, ComponentStartup>(OnShipyardStartup);
        SubscribeLocalEvent<ShipyardConsoleComponent, BoundUIOpenedEvent>(OnConsoleUIOpened);
        SubscribeLocalEvent<ShipyardConsoleComponent, ShipyardConsoleSellMessage>(OnSellMessage);
        SubscribeLocalEvent<ShipyardConsoleComponent, ShipyardConsolePurchaseMessage>(OnPurchaseMessage);
        SubscribeLocalEvent<ShipyardConsoleComponent, ShipyardConsoleLoadMessage>(OnLoadMessage);
        SubscribeLocalEvent<ShipyardConsoleComponent, EntInsertedIntoContainerMessage>(OnItemSlotChanged);
        SubscribeLocalEvent<ShipyardConsoleComponent, EntRemovedFromContainerMessage>(OnItemSlotChanged);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<StationDeedSpawnerComponent, MapInitEvent>(OnInitDeedSpawner);
    }

    public override void Shutdown()
    {
        _configManager.UnsubValueChanged(NFCCVars.Shipyard, SetShipyardEnabled);
        _configManager.UnsubValueChanged(NFCCVars.ShipyardSellRate, SetShipyardSellRate);
    }
    private void OnShipyardStartup(EntityUid uid, ShipyardConsoleComponent component, ComponentStartup args)
    {
        if (!_enabled)
            return;
        InitializeConsole();
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
        {
            CleanupShipyard();
        }

    private void SetShipyardEnabled(bool value)
    {
        if (_enabled == value)
            return;

        _enabled = value;

        if (value)
            SetupShipyardIfNeeded();
        else
            CleanupShipyard();
    }

    private void SetShipyardSellRate(float value)
    {
        _baseSaleRate = Math.Clamp(value, 0.0f, 1.0f);
    }

    /// <summary>
    /// Adds a ship to the shipyard, calculates its price, and attempts to ftl-dock it to the given station
    /// </summary>
    /// <param name="stationUid">The ID of the station to dock the shuttle to</param>
    /// <param name="shuttlePath">The path to the shuttle file to load. Must be a grid file!</param>
    /// <param name="shuttleEntityUid">The EntityUid of the shuttle that was purchased</param>
    /// <summary>
    /// Purchases a shuttle and docks it to the grid the console is on, independent of station data.
    /// </summary>
    /// <param name="consoleUid">The entity of the shipyard console to dock to its grid</param>
    /// <param name="shuttlePath">The path to the shuttle file to load. Must be a grid file!</param>
    /// <param name="shuttleEntityUid">The EntityUid of the shuttle that was purchased</param>
    public bool TryPurchaseShuttle(EntityUid consoleUid, ResPath shuttlePath, [NotNullWhen(true)] out EntityUid? shuttleEntityUid)
    {
        // Get the grid the console is on
        if (!TryComp<TransformComponent>(consoleUid, out var consoleXform)
            || consoleXform.GridUid == null
            || !TryAddShuttle(shuttlePath, out var shuttleGrid)
            || !TryComp<ShuttleComponent>(shuttleGrid, out var shuttleComponent))
        {
            shuttleEntityUid = null;
            return false;
        }

        var price = _pricing.AppraiseGrid(shuttleGrid.Value, null);
        var targetGrid = consoleXform.GridUid.Value;

        _sawmill.Info($"Shuttle {shuttlePath} was purchased at {ToPrettyString(consoleUid)} for {price:f2}");
        _shuttle.TryFTLDock(shuttleGrid.Value, shuttleComponent, targetGrid);
        shuttleEntityUid = shuttleGrid;
        return true;
    }

    /// <summary>
    /// Loads a shuttle from a file and docks it to the grid the console is on, like ship purchases.
    /// This is used for loading saved ships.
    /// </summary>
    /// <param name="consoleUid">The entity of the shipyard console to dock to its grid</param>
    /// <param name="shuttlePath">The path to the shuttle file to load. Must be a grid file!</param>
    /// <param name="shuttleEntityUid">The EntityUid of the shuttle that was loaded</param>
    public bool TryPurchaseShuttleFromFile(EntityUid consoleUid, ResPath shuttlePath, [NotNullWhen(true)] out EntityUid? shuttleEntityUid)
    {
        // Get the grid the console is on
        if (!TryComp<TransformComponent>(consoleUid, out var consoleXform)
            || consoleXform.GridUid == null
            || !TryAddShuttle(shuttlePath, out var shuttleGrid)
            || !TryComp<ShuttleComponent>(shuttleGrid, out var shuttleComponent))
        {
            shuttleEntityUid = null;
            return false;
        }

        var targetGrid = consoleXform.GridUid.Value;

        _sawmill.Info($"Shuttle loaded from file {shuttlePath} at {ToPrettyString(consoleUid)}");
        _shuttle.TryFTLDock(shuttleGrid.Value, shuttleComponent, targetGrid);
        shuttleEntityUid = shuttleGrid;
        return true;
    }

    /// <summary>
    /// Loads a shuttle into the ShipyardMap from a file path
    /// </summary>
    /// <param name="shuttlePath">The path to the grid file to load. Must be a grid file!</param>
    /// <returns>Returns the EntityUid of the shuttle</returns>
    private bool TryAddShuttle(ResPath shuttlePath, [NotNullWhen(true)] out EntityUid? shuttleGrid)
    {
        shuttleGrid = null;
        SetupShipyardIfNeeded();
        if (ShipyardMap == null)
            return false;

        if (!_mapLoader.TryLoadGrid(ShipyardMap.Value, shuttlePath, out var grid, offset: new Vector2(500f + _shuttleIndex, 1f)))
        {
            _sawmill.Error($"Unable to spawn shuttle {shuttlePath}");
            return false;
        }

        _shuttleIndex += grid.Value.Comp.LocalAABB.Width + ShuttleSpawnBuffer;

        shuttleGrid = grid.Value.Owner;
        return true;
    }

    /// <summary>
    /// Loads a ship directly from YAML data, following the same pattern as ship purchases
    /// </summary>
    /// <param name="consoleUid">The entity of the shipyard console to dock to its grid</param>
    /// <param name="yamlData">The YAML data of the ship to load</param>
    /// <param name="shuttleEntityUid">The EntityUid of the shuttle that was loaded</param>
    public bool TryLoadShipFromYaml(EntityUid consoleUid, string yamlData, [NotNullWhen(true)] out EntityUid? shuttleEntityUid)
    {
        shuttleEntityUid = null;

        // Get the grid the console is on
        if (!TryComp<TransformComponent>(consoleUid, out var consoleXform) || consoleXform.GridUid == null)
        {
            return false;
        }

        var targetGrid = consoleXform.GridUid.Value;

        try
        {
            // Setup shipyard
            SetupShipyardIfNeeded();
            if (ShipyardMap == null)
                return false;

            // Load ship directly from YAML data (bypassing file system)
            if (!TryLoadGridFromYamlData(yamlData, ShipyardMap.Value, new Vector2(500f + _shuttleIndex, 1f), out var grid))
            {
                _sawmill.Error($"Unable to load ship from YAML data");
                return false;
            }

            var shuttleGrid = grid.Value.Owner;

            if (!TryComp<ShuttleComponent>(shuttleGrid, out var shuttleComponent))
            {
                _sawmill.Error("Loaded entity is not a shuttle");
                return false;
            }

            // Update shuttle index for spacing
            _shuttleIndex += grid.Value.Comp.LocalAABB.Width + ShuttleSpawnBuffer;

            _sawmill.Info($"Ship loaded from YAML data at {ToPrettyString(consoleUid)}");

            // FTL docking will be handled in step 5 of the comprehensive loading process

            shuttleEntityUid = shuttleGrid;
            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Exception while loading ship from YAML: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Loads a grid directly from YAML string data, similar to MapLoaderSystem.TryLoadGrid but without file system dependency
    /// </summary>
    private bool TryLoadGridFromYamlData(string yamlData, MapId map, Vector2 offset, [NotNullWhen(true)] out Entity<MapGridComponent>? grid)
    {
        grid = null;

        try
        {
            // Parse YAML data directly (same approach as MapLoaderSystem.TryReadFile)
            using var textReader = new StringReader(yamlData);
            var documents = DataNodeParser.ParseYamlStream(textReader).ToArray();

            switch (documents.Length)
            {
                case < 1:
                    _sawmill.Error("YAML data has no documents.");
                    return false;
                case > 1:
                    _sawmill.Error("YAML data has too many documents. Ship files should contain exactly one.");
                    return false;
            }

            var data = (MappingDataNode)documents[0].Root;

            // Create load options (same as MapLoaderSystem.TryLoadGrid)
            var opts = new MapLoadOptions
            {
                MergeMap = map,
                Offset = offset,
                Rotation = Angle.Zero,
                DeserializationOptions = DeserializationOptions.Default,
                ExpectedCategory = FileCategory.Grid
            };

            // Process data with EntityDeserializer (same as MapLoaderSystem.TryLoadGeneric)
            var ev = new BeforeEntityReadEvent();
            RaiseLocalEvent(ev);

            opts.DeserializationOptions.AssignMapids = opts.ForceMapId == null;

            if (opts.MergeMap is { } targetId && !_map.MapExists(targetId))
                throw new Exception($"Target map {targetId} does not exist");

            var deserializer = new EntityDeserializer(
                _dependency,
                data,
                opts.DeserializationOptions,
                ev.RenamedPrototypes,
                ev.DeletedPrototypes);

            if (!deserializer.TryProcessData())
            {
                _sawmill.Error("Failed to process YAML entity data");
                return false;
            }

            deserializer.CreateEntities();

            if (opts.ExpectedCategory is { } exp && exp != deserializer.Result.Category)
            {
                _sawmill.Error($"YAML data does not contain the expected data. Expected {exp} but got {deserializer.Result.Category}");
                _mapLoader.Delete(deserializer.Result);
                return false;
            }

            // Apply transformations and start entities (same as MapLoaderSystem)
            var merged = new HashSet<EntityUid>();
            MergeMaps(deserializer, opts, merged);

            if (!SetMapId(deserializer, opts))
            {
                _mapLoader.Delete(deserializer.Result);
                return false;
            }

            ApplyTransform(deserializer, opts);
            deserializer.StartEntities();

            if (opts.MergeMap is { } mergeMap)
                MapInitalizeMerged(merged, mergeMap);

            // Process deferred anchoring after all entities are started and physics is stable
            ProcessPendingAnchors(merged);

            // Check for exactly one grid (same as MapLoaderSystem.TryLoadGrid)
            if (deserializer.Result.Grids.Count == 1)
            {
                grid = deserializer.Result.Grids.Single();
                return true;
            }

            _mapLoader.Delete(deserializer.Result);
            return false;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Exception while loading grid from YAML data: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Helper methods for our custom YAML loading - based on MapLoaderSystem implementation
    /// </summary>
    private void MergeMaps(EntityDeserializer deserializer, MapLoadOptions opts, HashSet<EntityUid> merged)
    {
        if (opts.MergeMap is not {} targetId)
            return;

        if (!_map.TryGetMap(targetId, out var targetUid))
            throw new Exception($"Target map {targetId} does not exist");

        deserializer.Result.Category = FileCategory.Unknown;
        var rotation = opts.Rotation;
        var matrix = Matrix3Helpers.CreateTransform(opts.Offset, rotation);
        var target = new Entity<TransformComponent>(targetUid.Value, Transform(targetUid.Value));

        // Apply transforms to all loaded entities that should be merged
        foreach (var uid in deserializer.Result.Entities)
        {
            if (TryComp<TransformComponent>(uid, out var xform))
            {
                if (xform.MapUid == null || HasComp<MapComponent>(uid))
                {
                    Merge(merged, uid, target, matrix, rotation);
                }
            }
        }
    }

    private void Merge(
        HashSet<EntityUid> merged,
        EntityUid uid,
        Entity<TransformComponent> target,
        in Matrix3x2 matrix,
        Angle rotation)
    {
        merged.Add(uid);
        var xform = Transform(uid);

        // Store whether the entity was anchored before transformation
        // We'll use this information later to re-anchor entities after startup
        var wasAnchored = xform.Anchored;

        // Apply transform matrix (same as RobustToolbox MapLoaderSystem)
        var angle = xform.LocalRotation + rotation;
        var pos = System.Numerics.Vector2.Transform(xform.LocalPosition, matrix);
        var coords = new EntityCoordinates(target.Owner, pos);
        _transform.SetCoordinates((uid, xform, MetaData(uid)), coords, rotation: angle, newParent: target.Comp);

        // Store anchoring information for later processing
        if (wasAnchored)
        {
            EnsureComp<PendingAnchorComponent>(uid);
        }

        // Delete any map entities since we're merging
        if (HasComp<MapComponent>(uid))
        {
            QueueDel(uid);
        }
    }

    private bool SetMapId(EntityDeserializer deserializer, MapLoadOptions opts)
    {
        // Check for any entities with MapComponents that might need MapId assignment
        foreach (var uid in deserializer.Result.Entities)
        {
            if (TryComp<MapComponent>(uid, out var mapComp))
            {
                if (opts.ForceMapId != null)
                {
                    // Should not happen in our shipyard use case
                    _sawmill.Error("Unexpected ForceMapId when merging maps");
                    return false;
                }
            }
        }
        return true;
    }

    private void ApplyTransform(EntityDeserializer deserializer, MapLoadOptions opts)
    {
        if (opts.Rotation == Angle.Zero && opts.Offset == Vector2.Zero)
            return;

        // If merging onto a single map, the transformation was already applied by MergeMaps
        if (opts.MergeMap != null)
            return;

        var matrix = Matrix3Helpers.CreateTransform(opts.Offset, opts.Rotation);

        // Apply transforms to all children of loaded maps
        foreach (var uid in deserializer.Result.Entities)
        {
            if (TryComp<TransformComponent>(uid, out var xform))
            {
                // Check if this entity is attached to a map
                if (xform.MapUid != null && HasComp<MapComponent>(xform.MapUid.Value))
                {
                    var pos = System.Numerics.Vector2.Transform(xform.LocalPosition, matrix);
                    _transform.SetLocalPosition(uid, pos);
                    _transform.SetLocalRotation(uid, xform.LocalRotation + opts.Rotation);
                }
            }
        }
    }

    private void MapInitalizeMerged(HashSet<EntityUid> merged, MapId targetId)
    {
        // Initialize merged entities according to the target map's state
        if (!_map.TryGetMap(targetId, out var targetUid))
            throw new Exception($"Target map {targetId} does not exist");

        if (_map.IsInitialized(targetUid.Value))
        {
            foreach (var uid in merged)
            {
                if (TryComp<MetaDataComponent>(uid, out var metadata))
                {
                    EntityManager.RunMapInit(uid, metadata);
                }
            }
        }

        var paused = _map.IsPaused(targetUid.Value);
        foreach (var uid in merged)
        {
            if (TryComp<MetaDataComponent>(uid, out var metadata))
            {
                _metaData.SetEntityPaused(uid, paused, metadata);
            }
        }
    }

    private void ProcessPendingAnchors(HashSet<EntityUid> merged)
    {
        // Process entities that need to be anchored after grid loading
        foreach (var uid in merged)
        {
            if (!TryComp<PendingAnchorComponent>(uid, out _))
                continue;

            // Remove the temporary component
            RemComp<PendingAnchorComponent>(uid);

            // Try to anchor the entity if it's on a valid grid
            if (TryComp<TransformComponent>(uid, out var xform) && xform.GridUid != null)
            {
                try
                {
                    _transform.AnchorEntity(uid, xform);
                }
                catch (Exception ex)
                {
                    // Log but don't fail - some entities might not be anchorable
                    _sawmill.Warning($"Failed to anchor entity {uid}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Checks a shuttle to make sure that it is docked to the given station, and that there are no lifeforms aboard. Then it teleports tagged items on top of the console, appraises the grid, outputs to the server log, and deletes the grid
    /// </summary>
    /// <param name="stationUid">The ID of the station that the shuttle is docked to</param>
    /// <param name="shuttleUid">The grid ID of the shuttle to be appraised and sold</param>
    /// <param name="consoleUid">The ID of the console being used to sell the ship</param>
    /// <summary>
    /// Sells a shuttle, checking that it is docked to the grid the console is on, and not to a station.
    /// </summary>
    /// <param name="shuttleUid">The grid ID of the shuttle to be appraised and sold</param>
    /// <param name="consoleUid">The ID of the console being used to sell the ship</param>
    /// <param name="bill">The amount the shuttle is sold for</param>
    public ShipyardSaleResult TrySellShuttle(EntityUid shuttleUid, EntityUid consoleUid, out int bill)
    {
        ShipyardSaleResult result = new ShipyardSaleResult();
        bill = 0;

        if (!HasComp<ShuttleComponent>(shuttleUid)
            || !TryComp(shuttleUid, out TransformComponent? xform)
            || !TryComp<TransformComponent>(consoleUid, out var consoleXform)
            || consoleXform.GridUid == null
            || ShipyardMap == null)
        {
            result.Error = ShipyardSaleError.InvalidShip;
            return result;
        }

        var targetGrid = consoleXform.GridUid.Value;
        var gridDocks = _docking.GetDocks(targetGrid);
        var shuttleDocks = _docking.GetDocks(shuttleUid);
        var isDocked = false;

        foreach (var shuttleDock in shuttleDocks)
        {
            foreach (var gridDock in gridDocks)
            {
                if (shuttleDock.Comp.DockedWith == gridDock.Owner)
                {
                    isDocked = true;
                    break;
                }
            }
            if (isDocked)
                break;
        }

        if (!isDocked)
        {
            _sawmill.Warning($"shuttle is not docked to the console's grid");
            result.Error = ShipyardSaleError.Undocked;
            return result;
        }

        var mobQuery = GetEntityQuery<MobStateComponent>();
        var xformQuery = GetEntityQuery<TransformComponent>();

        var charName = FoundOrganics(shuttleUid, mobQuery, xformQuery);
        if (charName is not null)
        {
            _sawmill.Warning($"organics on board");
            result.Error = ShipyardSaleError.OrganicsAboard;
            result.OrganicName = charName;
            return result;
        }

        if (TryComp<ShipyardConsoleComponent>(consoleUid, out var comp))
        {
            CleanGrid(shuttleUid, consoleUid);
        }

        bill = (int)_pricing.AppraiseGrid(shuttleUid, LacksPreserveOnSaleComp);
        QueueDel(shuttleUid);
        _sawmill.Info($"Sold shuttle {shuttleUid} for {bill}");

        // Update all record UI (skip records, no new records)
        _shuttleRecordsSystem.RefreshStateForAll(true);

        result.Error = ShipyardSaleError.Success;
        return result;
    }

    private void CleanGrid(EntityUid grid, EntityUid destination)
    {
        var xform = Transform(grid);
        var enumerator = xform.ChildEnumerator;
        var entitiesToPreserve = new List<EntityUid>();

        while (enumerator.MoveNext(out var child))
        {
            FindEntitiesToPreserve(child, ref entitiesToPreserve);
        }
        foreach (var ent in entitiesToPreserve)
        {
            // Teleport this item and all its children to the floor (or space).
            _transform.SetCoordinates(ent, new EntityCoordinates(destination, 0, 0));
            _transform.AttachToGridOrMap(ent);
        }
    }

    // checks if something has the ShipyardPreserveOnSaleComponent and if it does, adds it to the list
    private void FindEntitiesToPreserve(EntityUid entity, ref List<EntityUid> output)
    {
        if (TryComp<ShipyardSellConditionComponent>(entity, out var comp) && comp.PreserveOnSale == true)
        {
            output.Add(entity);
            return;
        }
        else if (TryComp<ContainerManagerComponent>(entity, out var containers))
        {
            foreach (var container in containers.Containers.Values)
            {
                foreach (var ent in container.ContainedEntities)
                {
                    FindEntitiesToPreserve(ent, ref output);
                }
            }
        }
    }

    // returns false if it has ShipyardPreserveOnSaleComponent, true otherwise
    private bool LacksPreserveOnSaleComp(EntityUid uid)
    {
        return !TryComp<ShipyardSellConditionComponent>(uid, out var comp) || comp.PreserveOnSale == false;
    }
    private void CleanupShipyard()
    {
        if (ShipyardMap == null || !_map.MapExists(ShipyardMap.Value))
        {
            ShipyardMap = null;
            return;
        }

        _map.DeleteMap(ShipyardMap.Value);
    }

    public void SetupShipyardIfNeeded()
    {
        if (ShipyardMap != null && _map.MapExists(ShipyardMap.Value))
            return;

        _map.CreateMap(out var shipyardMap);
        ShipyardMap = shipyardMap;

        _map.SetPaused(ShipyardMap.Value, false);
    }

    // <summary>
    // Tries to rename a shuttle deed and update the respective components.
    // Returns true if successful.
    //
    // Null name parts are promptly ignored.
    // </summary>
    public bool TryRenameShuttle(EntityUid uid, ShuttleDeedComponent? shuttleDeed, string? newName, string? newSuffix)
    {
        if (!Resolve(uid, ref shuttleDeed))
            return false;

        var shuttle = shuttleDeed.ShuttleUid;
        if (shuttle != null
             && TryGetEntity(shuttle.Value, out var shuttleEntity)
             && _station.GetOwningStation(shuttleEntity.Value) is { Valid: true } shuttleStation)
        {
            shuttleDeed.ShuttleName = newName;
            shuttleDeed.ShuttleNameSuffix = newSuffix;
            Dirty(uid, shuttleDeed);

            var fullName = GetFullName(shuttleDeed);
            _station.RenameStation(shuttleStation, fullName, loud: false);
            _metaData.SetEntityName(shuttleEntity.Value, fullName);
            _metaData.SetEntityName(shuttleStation, fullName);
        }
        else
        {
            _sawmill.Error($"Could not rename shuttle {ToPrettyString(shuttle):entity} to {newName}");
            return false;
        }

        //TODO: move this to an event that others hook into.
        if (shuttleDeed.ShuttleUid != null &&
            _shuttleRecordsSystem.TryGetRecord(shuttleDeed.ShuttleUid.Value, out var record))
        {
            record.Name = newName ?? "";
            record.Suffix = newSuffix ?? "";
            _shuttleRecordsSystem.TryUpdateRecord(record);
        }

        return true;
    }

    /// <summary>
    /// Returns the full name of the shuttle component in the form of [prefix] [name] [suffix].
    /// </summary>
    public static string GetFullName(ShuttleDeedComponent comp)
    {
        string?[] parts = { comp.ShuttleName, comp.ShuttleNameSuffix };
        return string.Join(' ', parts.Where(it => it != null));
    }

    private void SendLoadMessage(EntityUid uid, EntityUid player, string name, string shipyardChannel, bool secret = false)
    {
        var channel = _prototypeManager.Index<RadioChannelPrototype>(shipyardChannel);

        if (secret)
        {
            _radio.SendRadioMessage(uid, Loc.GetString("shipyard-console-docking-secret"), channel, uid);
        }
        else
        {
            _radio.SendRadioMessage(uid, Loc.GetString("shipyard-console-docking", ("owner", player), ("vessel", name)), channel, uid);
        }
    }

    #region Comprehensive Ship Loading System

    /// <summary>
    /// Comprehensive ship loading following the same procedure as ship purchases:
    /// 1. Load ship from YAML data
    /// 2. FTL the ship to the station
    /// 3. Set up shuttle deed and database systems
    /// 4. Update player ID card with deed
    /// 5. Fire ShipLoadedEvent and update console
    /// </summary>
    public async Task<bool> TryLoadShipComprehensive(EntityUid consoleUid, EntityUid idCardUid, string yamlData, string shipName, string playerUserId, ICommonSession playerSession, string shipyardChannel, string? filePath = null)
    {
        EntityUid? loadedShipUid = null;

        try
        {
            _sawmill.Info($"Starting comprehensive ship load process for '{shipName}' by player {playerUserId}");

            // STEP 1: Load ship from YAML data
            loadedShipUid = await LoadStep1_LoadShipFromYaml(consoleUid, yamlData, shipName);
            if (!loadedShipUid.HasValue)
            {
                _sawmill.Error("Step 1 failed: Could not load ship from YAML data");
                return false;
            }
            _sawmill.Info($"Step 1 complete: Ship loaded with EntityUid {loadedShipUid.Value}");

            // STEP 2: FTL the ship to the station (already done in LoadShipFromYaml)
            // This step is inherently part of the loading process
            // _sawmill.Info("Step 2 complete: Ship FTL'd to station during load");

            // STEP 3: Set up shuttle deed and database systems
            var deedSuccess = await LoadStep3_SetupShuttleDeed(loadedShipUid.Value, shipName, playerUserId);
            if (!deedSuccess)
            {
                _sawmill.Error("Step 3 failed: Could not set up shuttle deed");
                return false;
            }
            _sawmill.Info("Step 3 complete: Shuttle deed and database systems set up");

            // STEP 4: Update player ID card with deed
            var idUpdateSuccess = await LoadStep4_UpdatePlayerIdCard(idCardUid, loadedShipUid.Value, shipName, playerUserId);
            if (!idUpdateSuccess)
            {
                _sawmill.Error("Step 4 failed: Could not update player ID card with deed");
                return false;
            }
            _sawmill.Info("Step 4 complete: Player ID card updated with deed");

            // STEP 5: Fire ShipLoadedEvent and update console
            var eventSuccess = await LoadStep5_FireEventAndUpdateConsole(consoleUid, idCardUid, loadedShipUid.Value, shipName, playerUserId, playerSession, yamlData, shipyardChannel, filePath);
            if (!eventSuccess)
            {
                _sawmill.Error("Step 5 failed: Could not fire events and update console");
                // Don't return false here as the ship was loaded successfully
            }
            _sawmill.Info("Step 5 complete: Events fired and console updated");

            _sawmill.Info($"Comprehensive ship load process completed successfully for '{shipName}'");
            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Exception during comprehensive ship load: {ex}");
            return false;
        }
    }

    /// <summary>
    /// STEP 1: Load ship from YAML data and FTL to station
    /// </summary>
    private async Task<EntityUid?> LoadStep1_LoadShipFromYaml(EntityUid consoleUid, string yamlData, string shipName)
    {
        try
        {
            _sawmill.Info($"Step 1: Loading ship '{shipName}' from YAML data");

            // Use the existing ship loading logic but return the EntityUid
            if (!TryLoadShipFromYaml(consoleUid, yamlData, out var shuttleEntityUid))
            {
                _sawmill.Error("Failed to load ship from YAML data using existing system");
                return null;
            }

            _sawmill.Info($"Successfully loaded ship from YAML data: {shuttleEntityUid}");
            return shuttleEntityUid;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Step 1 failed: {ex}");
            return null;
        }
    }

    /// <summary>
    /// STEP 3: Set up shuttle deed and database systems for loaded ship
    /// </summary>
    private async Task<bool> LoadStep3_SetupShuttleDeed(EntityUid shipUid, string shipName, string playerUserId)
    {
        try
        {
            _sawmill.Info("Step 3: Setting up shuttle deed using purchase flow patterns");

            // Get the player name in the exact same format as purchases
            string? shuttleOwner = null;
            if (_playerManager.TryGetPlayerData(new NetUserId(Guid.Parse(playerUserId)), out var playerData))
            {
                shuttleOwner = playerData.UserName?.Trim();
            }

            if (string.IsNullOrEmpty(shuttleOwner))
            {
                _sawmill.Error($"Could not get player name for userId {playerUserId}");
                return false;
            }

            // Add deed component to the ship using exact purchase flow pattern
            var shipDeedComponent = EnsureComp<ShuttleDeedComponent>(shipUid);
            AssignShuttleDeedProperties((shipUid, shipDeedComponent), shipUid, shipName, shuttleOwner, true);

            _sawmill.Info($"Added ShuttleDeedComponent to ship {shipUid} using purchase flow patterns");

            // Ships already have IFF from YAML templates - don't manually add
            // LinkedLifecycleGridParentComponent already added by purchase systems - don't duplicate

            _sawmill.Info($"Ship {shipUid} with name '{shipName}' configured for player '{shuttleOwner}' ownership");
            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Step 3 failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// STEP 4: Update player ID card with shuttle deed
    /// </summary>
    private async Task<bool> LoadStep4_UpdatePlayerIdCard(EntityUid idCardUid, EntityUid shipUid, string shipName, string playerUserId)
    {
        try
        {
            _sawmill.Info("Step 4: Updating player ID card with shuttle deed using purchase flow patterns");

            // Get the player name in the exact same format as purchases
            string? shuttleOwner = null;
            if (_playerManager.TryGetPlayerData(new NetUserId(Guid.Parse(playerUserId)), out var playerData))
            {
                shuttleOwner = playerData.UserName?.Trim();
            }

            if (string.IsNullOrEmpty(shuttleOwner))
            {
                _sawmill.Error($"Could not get player name for userId {playerUserId}");
                return false;
            }

            // Use exact same deed assignment pattern as purchases
            var idDeedComponent = EnsureComp<ShuttleDeedComponent>(idCardUid);
            AssignShuttleDeedProperties((idCardUid, idDeedComponent), shipUid, shipName, shuttleOwner, true);

            _sawmill.Info($"Updated ShuttleDeedComponent on ID card {idCardUid} for ship '{shipName}' using purchase flow patterns");

            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Step 4 failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// STEP 5: Fire ShipLoadedEvent and update console
    /// </summary>
    private async Task<bool> LoadStep5_FireEventAndUpdateConsole(EntityUid consoleUid, EntityUid idCardUid, EntityUid shipUid, string shipName, string playerUserId, ICommonSession playerSession, string yamlData, string shipyardChannel, string? filePath)
    {
        try
        {
            _sawmill.Info("Step 5: FTL docking ship to station, firing ShipLoadedEvent and updating console");

            // First, FTL dock the ship to the station
            if (!TryComp<TransformComponent>(consoleUid, out var consoleXform) || consoleXform.GridUid == null)
            {
                _sawmill.Error("Step 5 failed: Could not get console grid for FTL docking");
                return false;
            }

            if (!TryComp<ShuttleComponent>(shipUid, out var shuttleComponent))
            {
                _sawmill.Error("Step 5 failed: Ship does not have ShuttleComponent for FTL docking");
                return false;
            }

            var targetGrid = consoleXform.GridUid.Value;
            _sawmill.Info($"Attempting to FTL dock ship {shipUid} to station grid {targetGrid}");

            if (_shuttle.TryFTLDock(shipUid, shuttleComponent, targetGrid))
            {
                _sawmill.Info($"Successfully FTL docked ship {shipUid} to station grid {targetGrid}");
            }
            else
            {
                _sawmill.Warning($"Failed to FTL dock ship {shipUid} to station grid {targetGrid} - ship may need manual docking");
                // Don't fail the entire operation if docking fails, as the ship was loaded successfully
            }

            // Fire the ShipLoadedEvent
            var shipLoadedEvent = new ShipLoadedEvent
            {
                ConsoleUid = consoleUid,
                IdCardUid = idCardUid,
                ShipGridUid = shipUid,
                ShipName = shipName,
                PlayerUserId = playerUserId,
                PlayerSession = playerSession,
                YamlData = yamlData,
                FilePath = filePath,
                ShipyardChannel = shipyardChannel
            };
            RaiseLocalEvent(shipLoadedEvent);
            _sawmill.Info($"Fired ShipLoadedEvent for ship '{shipName}'");

            // Console updates are handled by the calling method (UI feedback)
            // Additional console-specific updates could go here if needed

            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Step 5 failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Attempts to extract ship name from YAML data
    /// </summary>
    private string? ExtractShipNameFromYaml(string yamlData)
    {
        try
        {
            // Simple YAML parsing to extract ship name
            var lines = yamlData.Split('\n');
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (trimmedLine.StartsWith("shipName:"))
                {
                    var parts = trimmedLine.Split(':', 2);
                    if (parts.Length > 1)
                    {
                        return parts[1].Trim().Trim('"', '\'');
                    }
                }
                // Also check for entity names that might indicate ship name
                if (trimmedLine.StartsWith("name:"))
                {
                    var parts = trimmedLine.Split(':', 2);
                    if (parts.Length > 1)
                    {
                        var name = parts[1].Trim().Trim('"', '\'');
                        // Only use if it looks like a ship name (not generic component names)
                        if (!name.Contains("Component") && !name.Contains("System") && name.Length > 3)
                        {
                            return name;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _sawmill.Warning($"Failed to extract ship name from YAML: {ex}");
        }
        return null;
    }

    #endregion
}
