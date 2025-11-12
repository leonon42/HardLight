using Content.Shared.Actions;
using Content.Shared.CM14.Xenos;
using Content.Shared.CM14.Xenos.Evolution;
using Robust.Server.GameObjects;
using Robust.Shared.Timing;

namespace Content.Server.CM14.Xenos.Evolution;

/// <summary>
/// Handles automatic evolution for AI-controlled (non-player) xenos when their evolution action is ready.
/// </summary>
public sealed class XenoAIAutoEvolveSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<XenoAIAutoEvolveComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<XenoAIAutoEvolveComponent> ent, ref MapInitEvent args)
    {
        ent.Comp.NextCheckTime = _timing.CurTime + TimeSpan.FromSeconds(ent.Comp.CheckInterval);
        Dirty(ent);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var curTime = _timing.CurTime;
        var query = EntityQueryEnumerator<XenoAIAutoEvolveComponent, XenoComponent>();

        while (query.MoveNext(out var uid, out var autoEvolve, out var xeno))
        {
            // Skip player-controlled xenos - only AI xenos should auto-evolve
            if (HasComp<ActorComponent>(uid))
                continue;

            // Only check at intervals to avoid excessive processing
            if (curTime < autoEvolve.NextCheckTime)
                continue;

            autoEvolve.NextCheckTime = curTime + TimeSpan.FromSeconds(autoEvolve.CheckInterval);
            Dirty(uid, autoEvolve);

            // Skip if this xeno can't evolve
            if (xeno.EvolvesTo.Count == 0)
                continue;

            // Check if the evolve action exists and is ready
            if (xeno.EvolveAction == null)
                continue;

            // Check if the action is off cooldown
            if (!_actions.ValidAction(xeno.EvolveAction.Value))
                continue;

            // Trigger the evolution action
            var ev = new XenoOpenEvolutionsEvent();
            RaiseLocalEvent(uid, ev);
        }
    }
}
