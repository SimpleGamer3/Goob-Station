// SPDX-FileCopyrightText: 2024 BombasterDS <115770678+BombasterDS@users.noreply.github.com>
// SPDX-FileCopyrightText: 2024 Remuchi <72476615+Remuchi@users.noreply.github.com>
// SPDX-FileCopyrightText: 2024 VMSolidus <evilexecutive@gmail.com>
// SPDX-FileCopyrightText: 2024 whateverusername0 <whateveremail>
// SPDX-FileCopyrightText: 2025 Aiden <28298836+Aidenkrz@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Aviu00 <93730715+Aviu00@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Misandry <mary@thughunt.ing>
// SPDX-FileCopyrightText: 2025 Piras314 <p1r4s@proton.me>
// SPDX-FileCopyrightText: 2025 gluesniffler <159397573+gluesniffler@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 gus <august.eymann@gmail.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Goobstation.Common.CCVar;
using Content.Goobstation.Common.Standing;
using Content.Shared._Goobstation.Wizard.TimeStop;
using Content.Shared._Goobstation.Wizard.Traps;
using Content.Shared._Shitmed.Body.Organ;
using Content.Shared.Administration;
using Content.Shared.Body.Components;
using Content.Shared.DoAfter;
using Content.Shared.Input;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Systems;
using Content.Shared.Standing;
using Content.Shared.Stunnable;
using Robust.Shared.Configuration;
using Robust.Shared.Input.Binding;
using Robust.Shared.Player;
using Robust.Shared.Serialization;

namespace Content.Shared._White.Standing;

public abstract class SharedLayingDownSystem : EntitySystem
{
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly StandingStateSystem _standing = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly INetConfigurationManager _cfg = default!;

    public override void Initialize()
    {
        CommandBinds.Builder
            .Bind(ContentKeyFunctions.ToggleStanding, InputCmdHandler.FromDelegate(ToggleStanding))
            .Register<SharedLayingDownSystem>();

        SubscribeNetworkEvent<ChangeLayingDownEvent>(OnChangeState);

        SubscribeLocalEvent<StandingStateComponent, StandingUpDoAfterEvent>(OnStandingUpDoAfter);
        SubscribeLocalEvent<LayingDownComponent, RefreshMovementSpeedModifiersEvent>(OnRefreshMovementSpeed);
        SubscribeLocalEvent<LayingDownComponent, CheckAutoGetUpEvent>(OnCheckAutoGetUp);
    }

    public override void Shutdown()
    {
        base.Shutdown();

        CommandBinds.Unregister<SharedLayingDownSystem>();
    }

    private void ToggleStanding(ICommonSession? session)
    {
        if (session?.AttachedEntity == null ||
            !HasComp<LayingDownComponent>(session.AttachedEntity))
        {
            return;
        }

        RaiseNetworkEvent(new ChangeLayingDownEvent());
    }

    private void OnChangeState(ChangeLayingDownEvent ev, EntitySessionEventArgs args)
    {
        if (!args.SenderSession.AttachedEntity.HasValue)
            return;

        var uid = args.SenderSession.AttachedEntity.Value;

        if (HasComp<IceCubeComponent>(uid) || HasComp<FrozenComponent>(uid) ||
            HasComp<AdminFrozenComponent>(uid)) // Goob edit
            return;

        if (!TryComp(uid, out StandingStateComponent? standing) ||
            !TryComp(uid, out LayingDownComponent? layingDown))
        {
            return;
        }

        UpdateSpriteRotation(uid);
        RaiseLocalEvent(uid, new CheckAutoGetUpEvent());

        if (HasComp<KnockedDownComponent>(uid) || !_mobState.IsAlive(uid))
            return;

        if (_standing.IsDown(uid, standing))
            TryStandUp(uid, layingDown, standing);
        else
            TryLieDown(uid, layingDown, standing);
    }

    private void OnStandingUpDoAfter(EntityUid uid, StandingStateComponent component, StandingUpDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || HasComp<KnockedDownComponent>(uid) ||
            _mobState.IsIncapacitated(uid) || !_standing.Stand(uid))
        {
            component.CurrentState = StandingState.Lying;
            return;
        }

        component.CurrentState = StandingState.Standing;
    }

    private void OnRefreshMovementSpeed(EntityUid uid, LayingDownComponent component, RefreshMovementSpeedModifiersEvent args)
    {
        if (_standing.IsDown(uid))
            args.ModifySpeed(component.SpeedModify, component.SpeedModify);
        else
            args.ModifySpeed(1f, 1f);
    }

    public bool TryStandUp(EntityUid uid, LayingDownComponent? layingDown = null, StandingStateComponent? standingState = null)
    {
        if (!Resolve(uid, ref standingState, false) ||
            !Resolve(uid, ref layingDown, false) ||
            standingState.CurrentState is not StandingState.Lying ||
            !_mobState.IsAlive(uid) ||
            TerminatingOrDeleted(uid) ||
            // Shitmed Change
            !TryComp<BodyComponent>(uid, out var body) ||
            body.LegEntities.Count < body.RequiredLegs ||
            HasComp<DebrainedComponent>(uid))
            return false;

        // Goob edit start
        var ev = new GetStandingUpTimeMultiplierEvent();
        RaiseLocalEvent(uid, ev);

        var args = new DoAfterArgs(EntityManager,
            uid,
            layingDown.StandingUpTime * ev.Multiplier,
            new StandingUpDoAfterEvent(),
            uid) // Goob edit end
        {
            BreakOnHandChange = false,
            RequireCanInteract = false,
            MultiplyDelay = false, // Goobstatiom
        };

        if (!_doAfter.TryStartDoAfter(args))
            return false;

        standingState.CurrentState = StandingState.GettingUp;
        return true;
    }

    public bool TryLieDown(EntityUid uid, LayingDownComponent? layingDown = null, StandingStateComponent? standingState = null, DropHeldItemsBehavior behavior = DropHeldItemsBehavior.NoDrop)
    {
        if (!Resolve(uid, ref standingState, false) ||
            !Resolve(uid, ref layingDown, false) ||
            standingState.CurrentState is not StandingState.Standing)
        {
            if (behavior == DropHeldItemsBehavior.AlwaysDrop)
                RaiseLocalEvent(uid, new DropHandItemsEvent());

            return false;
        }

        _standing.Down(uid, true, behavior != DropHeldItemsBehavior.NoDrop, false, standingState);
        return true;
    }

    private void OnCheckAutoGetUp(Entity<LayingDownComponent> ent, ref CheckAutoGetUpEvent args)
    {
        if (HasComp<IceCubeComponent>(ent) || HasComp<FrozenComponent>(ent) || HasComp<AdminFrozenComponent>(ent))
        {
            ent.Comp.AutoGetUp = false;
            Dirty(ent);
           return;
        }

        if (!TryComp(ent, out ActorComponent? actor))
            return;

        ent.Comp.AutoGetUp = _cfg.GetClientCVar(actor.PlayerSession.Channel, GoobCVars.AutoGetUp);
        Dirty(ent);
    }

    public virtual void UpdateSpriteRotation(EntityUid uid) { }
}

[Serializable, NetSerializable]
public sealed partial class StandingUpDoAfterEvent : SimpleDoAfterEvent;