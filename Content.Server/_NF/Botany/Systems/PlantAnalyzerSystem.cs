using Content.Server.Botany.Components;
using Content.Server.PowerCell;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared._NF.PlantAnalyzer;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using System.Linq;
using System.Text;
using Robust.Shared.Timing;
using Content.Shared.Atmos;

namespace Content.Server.Botany.Systems;

public sealed class PlantAnalyzerSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly PowerCellSystem _cell = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfterSystem = default!;
    [Dependency] private readonly UserInterfaceSystem _uiSystem = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlantAnalyzerComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<PlantAnalyzerComponent, PlantAnalyzerDoAfterEvent>(OnDoAfter);
        SubscribeLocalEvent<PlantAnalyzerComponent, PlantAnalyzerSetMode>(OnModeSelected);
        SubscribeLocalEvent<PlantAnalyzerComponent, PlantAnalyzerRequestRefresh>(OnRefreshRequested);
    }

    private void OnAfterInteract(Entity<PlantAnalyzerComponent> ent, ref AfterInteractEvent args)
    {
        if (args.Target == null || !args.CanReach || !_cell.HasActivatableCharge(ent, user: args.User))
            return;

        if (ent.Comp.DoAfter != null)
        {
            // If the referenced DoAfter already finished or was cancelled, clear the stale reference.
            if (!_doAfterSystem.IsRunning(ent.Comp.DoAfter.Value))
                ent.Comp.DoAfter = null;
            else
                return;
        }

        if (HasComp<SeedComponent>(args.Target) || TryComp<PlantHolderComponent>(args.Target, out var plantHolder) && plantHolder.Seed != null || TryComp<BotanySwabComponent>(args.Target, out var swabComp) && swabComp.SeedData != null)
        {

            if (ent.Comp.Settings.AdvancedScan)
            {
                var doAfterArgs = new DoAfterArgs(EntityManager, args.User, ent.Comp.Settings.AdvScanDelay, new PlantAnalyzerDoAfterEvent(), ent, target: args.Target, used: ent)
                {
                    NeedHand = true,
                    BreakOnDamage = true,
                    BreakOnMove = true,
                    MovementThreshold = 0.01f
                };
                _doAfterSystem.TryStartDoAfter(doAfterArgs, out ent.Comp.DoAfter);
            }
            else
            {
                var doAfterArgs = new DoAfterArgs(EntityManager, args.User, ent.Comp.Settings.ScanDelay, new PlantAnalyzerDoAfterEvent(), ent, target: args.Target, used: ent)
                {
                    NeedHand = true,
                    BreakOnDamage = true,
                    BreakOnMove = true,
                    MovementThreshold = 0.01f
                };
                _doAfterSystem.TryStartDoAfter(doAfterArgs, out ent.Comp.DoAfter);
            }
        }
    }

    private void OnDoAfter(Entity<PlantAnalyzerComponent> ent, ref PlantAnalyzerDoAfterEvent args)
    {
        ent.Comp.DoAfter = null;
        // Double charge use for advanced scan.
        if (ent.Comp.Settings.AdvancedScan)
        {
            if (!_cell.TryUseActivatableCharge(ent, user: args.User))
                return;
        }
        if (args.Handled || args.Cancelled || args.Args.Target == null || !_cell.TryUseActivatableCharge(ent.Owner, user: args.User))
            return;

        _audio.PlayPvs(ent.Comp.ScanningEndSound, ent);

        // Try to open the UI for the user; only send state if it's open.
        if (!_uiSystem.TryOpenUi(ent.Owner, PlantAnalyzerUiKey.Key, args.User))
            return;

        UpdateScannedUser(ent, args.Args.Target.Value, args.User);

        args.Handled = true;
    }

    private void OpenUserInterface(EntityUid user, EntityUid analyzer)
    {
        if (!TryComp<ActorComponent>(user, out var actor) || !_uiSystem.HasUi(analyzer, PlantAnalyzerUiKey.Key))
            return;

        _uiSystem.OpenUi(analyzer, PlantAnalyzerUiKey.Key, actor.PlayerSession);
    }

    public void UpdateScannedUser(Entity<PlantAnalyzerComponent> ent, EntityUid target, EntityUid actor)
    {
        // Track the last target we scanned so the client can ask for refreshes.
        ent.Comp.LastScannedTarget = target;
        if (!_uiSystem.HasUi(ent.Owner, PlantAnalyzerUiKey.Key))
            return;

        if (TryComp<SeedComponent>(target, out var seedComp))
        {
                if (seedComp.Seed != null)
                {
                        var state = ObtainingGeneDataSeed(seedComp.Seed, target, false, ent.Comp.Settings.AdvancedScan);
                        var uiState = new PlantAnalyzerUserInterfaceState()
                        {
                            TargetEntity = state.TargetEntity,
                            IsTray = state.IsTray,
                            IsSwab = state.IsSwab,
                            SeedName = state.SeedName,
                            SeedChem = state.SeedChem,
                            HarvestType = state.HarvestType,
                            ExudeGases = state.ExudeGases,
                            ConsumeGases = state.ConsumeGases,
                            Endurance = state.Endurance,
                            SeedYield = state.SeedYield,
                            Lifespan = state.Lifespan,
                            Maturation = state.Maturation,
                            Production = state.Production,
                            GrowthStages = state.GrowthStages,
                            SeedPotency = state.SeedPotency,
                            Speciation = state.Speciation,
                            AdvancedInfo = state.AdvancedInfo
                        };
                        _uiSystem.SetUiState(ent.Owner, PlantAnalyzerUiKey.Key, uiState);
                        Timer.Spawn(TimeSpan.Zero, () => _uiSystem.ServerSendUiMessage(ent.Owner, PlantAnalyzerUiKey.Key, state, actor));
                }
            else if (seedComp.SeedId != null && _prototypeManager.TryIndex(seedComp.SeedId, out SeedPrototype? protoSeed))
                {
                    var state = ObtainingGeneDataSeed(protoSeed, target, false, ent.Comp.Settings.AdvancedScan);
                    var uiState2 = new PlantAnalyzerUserInterfaceState()
                    {
                        TargetEntity = state.TargetEntity,
                        IsTray = state.IsTray,
                        IsSwab = state.IsSwab,
                        SeedName = state.SeedName,
                        SeedChem = state.SeedChem,
                        HarvestType = state.HarvestType,
                        ExudeGases = state.ExudeGases,
                        ConsumeGases = state.ConsumeGases,
                        Endurance = state.Endurance,
                        SeedYield = state.SeedYield,
                        Lifespan = state.Lifespan,
                        Maturation = state.Maturation,
                        Production = state.Production,
                        GrowthStages = state.GrowthStages,
                        SeedPotency = state.SeedPotency,
                        Speciation = state.Speciation,
                        AdvancedInfo = state.AdvancedInfo
                    };
                    _uiSystem.SetUiState(ent.Owner, PlantAnalyzerUiKey.Key, uiState2);
                    Timer.Spawn(TimeSpan.Zero, () => _uiSystem.ServerSendUiMessage(ent.Owner, PlantAnalyzerUiKey.Key, state, actor));
                }
        }
        else if (TryComp<PlantHolderComponent>(target, out var plantComp))
        {
            if (plantComp.Seed != null)
            {
                var state = ObtainingGeneDataSeed(plantComp.Seed, target, true, ent.Comp.Settings.AdvancedScan);
                var uiState3 = new PlantAnalyzerUserInterfaceState()
                {
                    TargetEntity = state.TargetEntity,
                    IsTray = state.IsTray,
                    IsSwab = state.IsSwab,
                    SeedName = state.SeedName,
                    SeedChem = state.SeedChem,
                    HarvestType = state.HarvestType,
                    ExudeGases = state.ExudeGases,
                    ConsumeGases = state.ConsumeGases,
                    Endurance = state.Endurance,
                    SeedYield = state.SeedYield,
                    Lifespan = state.Lifespan,
                    Maturation = state.Maturation,
                    Production = state.Production,
                    GrowthStages = state.GrowthStages,
                    SeedPotency = state.SeedPotency,
                    Speciation = state.Speciation,
                    AdvancedInfo = state.AdvancedInfo
                };
                _uiSystem.SetUiState(ent.Owner, PlantAnalyzerUiKey.Key, uiState3);
                Timer.Spawn(TimeSpan.Zero, () => _uiSystem.ServerSendUiMessage(ent.Owner, PlantAnalyzerUiKey.Key, state, actor));
            }
        }
        else if (TryComp<BotanySwabComponent>(target, out var swab))
        {
            if (swab.SeedData != null)
            {
                var state = ObtainingGeneDataSeed(swab.SeedData, target, false, ent.Comp.Settings.AdvancedScan);
                state.IsSwab = true;
                var uiState4 = new PlantAnalyzerUserInterfaceState()
                {
                    TargetEntity = state.TargetEntity,
                    IsTray = state.IsTray,
                    IsSwab = state.IsSwab,
                    SeedName = state.SeedName,
                    SeedChem = state.SeedChem,
                    HarvestType = state.HarvestType,
                    ExudeGases = state.ExudeGases,
                    ConsumeGases = state.ConsumeGases,
                    Endurance = state.Endurance,
                    SeedYield = state.SeedYield,
                    Lifespan = state.Lifespan,
                    Maturation = state.Maturation,
                    Production = state.Production,
                    GrowthStages = state.GrowthStages,
                    SeedPotency = state.SeedPotency,
                    Speciation = state.Speciation,
                    AdvancedInfo = state.AdvancedInfo
                };
                _uiSystem.SetUiState(ent.Owner, PlantAnalyzerUiKey.Key, uiState4);
                Timer.Spawn(TimeSpan.Zero, () => _uiSystem.ServerSendUiMessage(ent.Owner, PlantAnalyzerUiKey.Key, state, actor));
            }
        }
    }

    private void OnRefreshRequested(Entity<PlantAnalyzerComponent> ent, ref PlantAnalyzerRequestRefresh args)
    {
        if (ent.Comp.LastScannedTarget == null)
            return;

        var target = ent.Comp.LastScannedTarget.Value;

        if (!_uiSystem.HasUi(ent.Owner, PlantAnalyzerUiKey.Key))
            return;

        // Resend UI state for the last-scanned target. We use SetUiState so open UIs receive UpdateState.
        if (TryComp<SeedComponent>(target, out var seedComp))
        {
            if (seedComp.Seed != null)
            {
                var state = ObtainingGeneDataSeed(seedComp.Seed, target, false, ent.Comp.Settings.AdvancedScan);
                var uiState = new PlantAnalyzerUserInterfaceState()
                {
                    TargetEntity = state.TargetEntity,
                    IsTray = state.IsTray,
                    IsSwab = state.IsSwab,
                    SeedName = state.SeedName,
                    SeedChem = state.SeedChem,
                    HarvestType = state.HarvestType,
                    ExudeGases = state.ExudeGases,
                    ConsumeGases = state.ConsumeGases,
                    Endurance = state.Endurance,
                    SeedYield = state.SeedYield,
                    Lifespan = state.Lifespan,
                    Maturation = state.Maturation,
                    Production = state.Production,
                    GrowthStages = state.GrowthStages,
                    SeedPotency = state.SeedPotency,
                    Speciation = state.Speciation,
                    AdvancedInfo = state.AdvancedInfo
                };
                _uiSystem.SetUiState(ent.Owner, PlantAnalyzerUiKey.Key, uiState);
            }
            else if (seedComp.SeedId != null && _prototypeManager.TryIndex(seedComp.SeedId, out SeedPrototype? protoSeed))
            {
                var state = ObtainingGeneDataSeed(protoSeed, target, false, ent.Comp.Settings.AdvancedScan);
                var uiState2 = new PlantAnalyzerUserInterfaceState()
                {
                    TargetEntity = state.TargetEntity,
                    IsTray = state.IsTray,
                    IsSwab = state.IsSwab,
                    SeedName = state.SeedName,
                    SeedChem = state.SeedChem,
                    HarvestType = state.HarvestType,
                    ExudeGases = state.ExudeGases,
                    ConsumeGases = state.ConsumeGases,
                    Endurance = state.Endurance,
                    SeedYield = state.SeedYield,
                    Lifespan = state.Lifespan,
                    Maturation = state.Maturation,
                    Production = state.Production,
                    GrowthStages = state.GrowthStages,
                    SeedPotency = state.SeedPotency,
                    Speciation = state.Speciation,
                    AdvancedInfo = state.AdvancedInfo
                };
                _uiSystem.SetUiState(ent.Owner, PlantAnalyzerUiKey.Key, uiState2);
            }
        }
        else if (TryComp<PlantHolderComponent>(target, out var plantComp))
        {
            if (plantComp.Seed != null)
            {
                var state = ObtainingGeneDataSeed(plantComp.Seed, target, true, ent.Comp.Settings.AdvancedScan);
                var uiState3 = new PlantAnalyzerUserInterfaceState()
                {
                    TargetEntity = state.TargetEntity,
                    IsTray = state.IsTray,
                    IsSwab = state.IsSwab,
                    SeedName = state.SeedName,
                    SeedChem = state.SeedChem,
                    HarvestType = state.HarvestType,
                    ExudeGases = state.ExudeGases,
                    ConsumeGases = state.ConsumeGases,
                    Endurance = state.Endurance,
                    SeedYield = state.SeedYield,
                    Lifespan = state.Lifespan,
                    Maturation = state.Maturation,
                    Production = state.Production,
                    GrowthStages = state.GrowthStages,
                    SeedPotency = state.SeedPotency,
                    Speciation = state.Speciation,
                    AdvancedInfo = state.AdvancedInfo
                };
                _uiSystem.SetUiState(ent.Owner, PlantAnalyzerUiKey.Key, uiState3);
            }
        }
        else if (TryComp<BotanySwabComponent>(target, out var swab))
        {
            if (swab.SeedData != null)
            {
                var state = ObtainingGeneDataSeed(swab.SeedData, target, false, ent.Comp.Settings.AdvancedScan);
                state.IsSwab = true;
                var uiState4 = new PlantAnalyzerUserInterfaceState()
                {
                    TargetEntity = state.TargetEntity,
                    IsTray = state.IsTray,
                    IsSwab = state.IsSwab,
                    SeedName = state.SeedName,
                    SeedChem = state.SeedChem,
                    HarvestType = state.HarvestType,
                    ExudeGases = state.ExudeGases,
                    ConsumeGases = state.ConsumeGases,
                    Endurance = state.Endurance,
                    SeedYield = state.SeedYield,
                    Lifespan = state.Lifespan,
                    Maturation = state.Maturation,
                    Production = state.Production,
                    GrowthStages = state.GrowthStages,
                    SeedPotency = state.SeedPotency,
                    Speciation = state.Speciation,
                    AdvancedInfo = state.AdvancedInfo
                };
                _uiSystem.SetUiState(ent.Owner, PlantAnalyzerUiKey.Key, uiState4);
            }
        }
    }

    /// <summary>
    ///     Analysis of seed from prototype.
    /// </summary>
    public PlantAnalyzerScannedSeedPlantInformation ObtainingGeneDataSeed(SeedData seedData, EntityUid target, bool isTray, bool scanIsAdvanced)
    {
        // Get trickier fields first.
        AnalyzerHarvestType harvestType = AnalyzerHarvestType.Unknown;
        switch (seedData.HarvestRepeat)
        {
            case HarvestType.Repeat:
                harvestType = AnalyzerHarvestType.Repeat;
                break;
            case HarvestType.NoRepeat:
                harvestType = AnalyzerHarvestType.NoRepeat;
                break;
            case HarvestType.SelfHarvest:
                harvestType = AnalyzerHarvestType.SelfHarvest;
                break;
            default:
                break;
        }

        var mutationProtos = seedData.MutationPrototypes;
        List<string> mutationStrings = new();
        foreach (var mutationProto in mutationProtos)
        {
            if (_prototypeManager.TryIndex<SeedPrototype>(mutationProto, out var seed))
            {
                mutationStrings.Add(seed.DisplayName);
            }
        }

        PlantAnalyzerScannedSeedPlantInformation ret = new()
        {
            TargetEntity = GetNetEntity(target),
            IsTray = isTray,
            SeedName = seedData.DisplayName,
            SeedChem = seedData.Chemicals.Keys.ToArray(),
            HarvestType = harvestType,
            ExudeGases = GetGasFlags(seedData.ExudeGasses.Keys),
            ConsumeGases = GetGasFlags(seedData.ConsumeGasses.Keys),
            Endurance = seedData.Endurance,
            SeedYield = seedData.Yield,
            Lifespan = seedData.Lifespan,
            Maturation = seedData.Maturation,
            Production = seedData.Production,
            GrowthStages = seedData.GrowthStages,
            SeedPotency = seedData.Potency,
            Speciation = mutationStrings.ToArray()
        };

        if (scanIsAdvanced)
        {
            AdvancedScanInfo advancedInfo = new()
            {
                NutrientConsumption = seedData.NutrientConsumption,
                WaterConsumption = seedData.WaterConsumption,
                IdealHeat = seedData.IdealHeat,
                HeatTolerance = seedData.HeatTolerance,
                IdealLight = seedData.IdealLight,
                LightTolerance = seedData.LightTolerance,
                ToxinsTolerance = seedData.ToxinsTolerance,
                LowPressureTolerance = seedData.LowPressureTolerance,
                HighPressureTolerance = seedData.HighPressureTolerance,
                PestTolerance = seedData.PestTolerance,
                WeedTolerance = seedData.WeedTolerance,
                Mutations = GetMutationFlags(seedData)
            };
            for(int i=0;i<ret.SeedChem.Length;i++) 
            {            
                ret.SeedChem[i] += seedData.Chemicals[ret.SeedChem[i]].Max;
            }
            ret.AdvancedInfo = advancedInfo;
        }
        return ret;
    }

    public MutationFlags GetMutationFlags(SeedData plant)
    {
        MutationFlags ret = MutationFlags.None;
        if (plant.TurnIntoKudzu) ret |= MutationFlags.TurnIntoKudzu;
        if (plant.Seedless || plant.PermanentlySeedless) ret |= MutationFlags.Seedless;
        if (plant.Ligneous) ret |= MutationFlags.Ligneous;
        if (plant.CanScream) ret |= MutationFlags.CanScream;

        return ret;
    }

    public GasFlags GetGasFlags(IEnumerable<Gas> gases)
    {
        var gasFlags = GasFlags.None;
        foreach (var gas in gases)
        {
            switch (gas)
            {
                case Gas.Nitrogen:
                    gasFlags |= GasFlags.Nitrogen;
                    break;
                case Gas.Oxygen:
                    gasFlags |= GasFlags.Oxygen;
                    break;
                case Gas.CarbonDioxide:
                    gasFlags |= GasFlags.CarbonDioxide;
                    break;
                case Gas.Plasma:
                    gasFlags |= GasFlags.Plasma;
                    break;
                case Gas.Tritium:
                    gasFlags |= GasFlags.Tritium;
                    break;
                case Gas.WaterVapor:
                    gasFlags |= GasFlags.WaterVapor;
                    break;
                case Gas.Ammonia:
                    gasFlags |= GasFlags.Ammonia;
                    break;
                case Gas.NitrousOxide:
                    gasFlags |= GasFlags.NitrousOxide;
                    break;
                case Gas.Frezon:
                    gasFlags |= GasFlags.Frezon;
                    break;
            }
        }
        return gasFlags;
    }

    private void OnModeSelected(Entity<PlantAnalyzerComponent> ent, ref PlantAnalyzerSetMode args)
    {
        SetMode(ent, args.AdvancedScan);
    }

    public void SetMode(Entity<PlantAnalyzerComponent> ent, bool isAdvMode)
    {
        if (ent.Comp.DoAfter != null)
            return;
        ent.Comp.Settings.AdvancedScan = isAdvMode;
    }
}
