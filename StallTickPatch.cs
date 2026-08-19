using System;
using HarmonyLib;
using Halfling.Physics2D.Dynamics;

using SVec = System.Numerics.Vector2;

namespace ZTX.RammingDamage
{
    // Per-tick stall state machine. Harmony Prefix on ContactManager.Collide,
    // sibling to ContactManagerCollidePrefixPatch (pen pre-scan).
    //
    // Per ramming-active body each tick:
    //   1. Drop state if body left ramming (restoring captured vel if mid-stall).
    //   2. If stalling: restore captured vel + angVel, mark idle.
    //   3. Idle (CountdownTicks==-1): try to schedule via DamageMath.ComputeN.
    //   4. Counting down: decrement; if hits 0, capture vel + angVel, zero
    //      body, mark stalling.
    [HarmonyPatch(typeof(ContactManager), "Collide")]
    public static class StallTickPatch
    {
        static void Prefix()
        {
            try { PrefixImpl(); }
            catch (Exception ex)
            {
                Log.Exception("StallTickPatch.Prefix", ex);
            }
        }

        private static void PrefixImpl()
        {
            if (!Config.StallEnabled) return;

            if (RammingHandler.s_stallStates.IsEmpty) return;

            float dt = RammingHandler.s_lastStepDt;

            Body[] keys;
            try { keys = System.Linq.Enumerable.ToArray(RammingHandler.s_stallStates.Keys); }
            catch { return; }

            foreach (Body body in keys)
            {
                if (!RammingHandler.s_stallStates.TryGetValue(body, out var state))
                    continue;

                // Mass cap: bodies at or above StallDisableAboveMass never
                // stall. Bodies BELOW the cap but currently in contact with a
                // body at or above the cap are also exempted.
                if (Config.StallDisableAboveMass > 0f)
                {
                    float curMass;
                    try { curMass = body.Mass; }
                    catch { curMass = 0f; }

                    bool exempt = (curMass >= Config.StallDisableAboveMass)
                                || HasHeavyContactPartner(body, Config.StallDisableAboveMass);

                    if (exempt)
                    {
                        if (state.IsStalling)
                        {
                            try
                            {
                                body.LinearVelocity  = state.CapturedVelocity;
                                body.AngularVelocity = state.CapturedAngularVelocity;
                            }
                            catch { /* destroyed */ }
                            state.IsStalling = false;
                        }
                        state.CountdownTicks = -1;
                        continue;
                    }
                }

                if (!RammingHandler.IsRammingBodyWithGrace(body, Config.StallGraceTicks))
                {
                    if (state.IsStalling && body != null)
                    {
                        try
                        {
                            body.LinearVelocity  = state.CapturedVelocity;
                            body.AngularVelocity = state.CapturedAngularVelocity;
                        }
                        catch { /* destroyed */ }
                    }
                    RammingHandler.s_stallStates.TryRemove(body, out _);
                    continue;
                }

                if (state.IsStalling)
                {
                    try
                    {
                        body.LinearVelocity  = state.CapturedVelocity;
                        body.AngularVelocity = state.CapturedAngularVelocity;
                    }
                    catch
                    {
                        RammingHandler.s_stallStates.TryRemove(body, out _);
                        continue;
                    }
                    RammingHandler.RamLogStallRestore(body, state);
                    state.IsStalling = false;
                    state.CountdownTicks = -1;
                }

                if (state.CountdownTicks == -1)
                {
                    float speed;
                    try { speed = body.LinearVelocity.Length(); }
                    catch { continue; }

                    int n = DamageMath.ComputeN(
                        speed, dt,
                        Config.MaxPhaseLayers,
                        Config.DestroyRatePerTick,
                        Config.StallDisableSpeed);

                    if (n > 0)
                    {
                        state.CountdownTicks = n;
                        state.LastComputedN  = n;
                    }
                }
                else
                {
                    float curSpeed;
                    try { curSpeed = body.LinearVelocity.Length(); }
                    catch { continue; }
                    if (curSpeed < Config.StallDisableSpeed)
                    {
                        state.CountdownTicks = -1;
                        continue;
                    }

                    state.CountdownTicks -= 1;
                    if (state.CountdownTicks == 0)
                    {
                        SVec liveVel;
                        float liveAngVel;
                        try
                        {
                            liveVel    = body.LinearVelocity;
                            liveAngVel = body.AngularVelocity;
                        }
                        catch { continue; }
                        state.CapturedVelocity        = liveVel;
                        state.CapturedAngularVelocity = liveAngVel;
                        try
                        {
                            body.LinearVelocity  = SVec.Zero;
                            body.AngularVelocity = 0f;
                        }
                        catch
                        {
                            RammingHandler.s_stallStates.TryRemove(body, out _);
                            continue;
                        }
                        state.IsStalling = true;
                        float phasePerTick = liveVel.Length() * dt - Config.DestroyRatePerTick;
                        RammingHandler.RamLogStallTrig(body, state, liveVel.Length(), phasePerTick);
                    }
                }
            }
        }

        private static bool HasHeavyContactPartner(Body body, float massThreshold)
        {
            if (body == null || massThreshold <= 0f) return false;
            try
            {
                var edge = body.ContactList;
                int guard = 0;
                while (edge != null && guard < 256)
                {
                    Body other = null;
                    try { other = edge.Other; }
                    catch { /* edge torn down mid-walk */ }

                    if (other != null)
                    {
                        float otherMass = 0f;
                        try { otherMass = other.Mass; }
                        catch { /* other body torn down */ }
                        if (otherMass >= massThreshold) return true;
                    }

                    try { edge = edge.Next; }
                    catch { break; }
                    guard++;
                }
            }
            catch { /* contact list torn down */ }
            return false;
        }
    }
}
