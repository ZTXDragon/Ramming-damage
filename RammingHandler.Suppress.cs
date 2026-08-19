using System;
using Cosmoteer;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Colliders;
using Halfling.Physics2D.Dynamics;
using Halfling.Physics2D.Dynamics.Contacts;

using SVec = System.Numerics.Vector2;

namespace ZTX.RammingDamage
{
    // Engine-side suppression (Enabled=false in HIT path + retro-suppress
    // sibling contacts) and pushback impulse write.
    internal static partial class RammingHandler
    {

        // Caps damage at the part's remaining health, less anything already
        // queued against it this tick.
        private static int ClampToHealth(Part part, int damage)
        {
            if (part == null || damage <= 0) return 0;
            int hp = SafeHealth(part);
            if (hp <= 0) return 0;

            lock (s_pendingLock)
            {
                s_pendingDamage.TryGetValue(part, out int queued);
                int remaining = hp - queued;
                if (remaining <= 0) return 0;
                if (damage > remaining) damage = remaining;
            }
            return damage;
        }

        // Records a hit for DrainPendingHits() to apply once World.Step has
        // returned and cleared IsLocked. Calling Part.OnHit here instead would
        // remove a collision fixture mid-step and throw "The World is locked."
        internal static void QueueHit(Part part, int damage, BaseCollider hitCollider,
                                      Cosmoteer.Simulation.SimRoot sim,
                                      ShipMetadata attackerMeta)
        {
            if (part == null || damage <= 0) return;
            try
            {
                lock (s_pendingLock)
                {
                    s_pendingDamage.TryGetValue(part, out int queued);
                    s_pendingDamage[part] = queued + damage;
                    s_pendingHits.Add(new PendingHit
                    {
                        Part                = part,
                        Damage              = damage,
                        HitCollider         = hitCollider,
                        Sim                 = sim,
                        AttackerPlayerIndex = attackerMeta?.PlayerIndex ?? 0,
                        HasAttacker         = attackerMeta != null,
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Exception("QueueHit", ex);
            }
        }

        internal static void DrainPendingHits(bool worldLocked, string phase)
        {
            PendingHit[] batch;
            lock (s_pendingLock)
            {
                if (s_pendingHits.Count == 0)
                {
                    s_pendingDamage.Clear();
                    return;
                }
                batch = s_pendingHits.ToArray();
                s_pendingHits.Clear();
                s_pendingDamage.Clear();
            }

            if (worldLocked)
            {
                Log.Error($"DRAIN RAN WHILE WORLD LOCKED (phase={phase}, queued={batch.Length}) "
                          + "-- deferred damage is at the WRONG point in the tick. "
                          + "Part.OnHit is about to throw 'The World is locked.'");
            }

            long prevTotal = s_deferredHitsTotal;
            int applied = 0, killed = 0, skipped = 0;

            for (int i = 0; i < batch.Length; i++)
            {
                var hit = batch[i];
                try
                {
                    var part = hit.Part;
                    if (part == null) { skipped++; continue; }

                    int hpBefore = SafeHealth(part);
                    if (hpBefore <= 0) { skipped++; continue; }

                    using var popper = hit.HasAttacker
                        ? hit.Sim?.PushCurrentActionSourcePlayer(hit.AttackerPlayerIndex)
                        : null;
                    part.OnHit(hit.Damage, DamageType.Default, hit.HitCollider, false, null);

                    applied++;
                    if (SafeHealth(part) <= 0) killed++;
                }
                catch (Exception ex)
                {
                    Log.Exception("DrainPendingHits.OnHit", ex);
                }
            }

            s_deferredHitsTotal  += applied;
            s_deferredKillsTotal += killed;
            int seq = ++s_drainSeq;

            if (!s_firstDrainLogged && applied > 0)
            {
                s_firstDrainLogged = true;
                Log.Info($"DEFERRED DAMAGE ACTIVE — first drain applied {applied} hit(s), "
                         + $"{killed} part(s) destroyed, phase={phase}, worldLocked={worldLocked}. "
                         + "Part.OnHit now runs AFTER World.Step, not inside it.");
            }
            else if (Config.DebugLog)
            {
                Log.Info($"DRAIN tick={s_currentTick} seq={seq} phase={phase} "
                         + $"queued={batch.Length} applied={applied} killed={killed} "
                         + $"skipped={skipped} totalHits={s_deferredHitsTotal} "
                         + $"totalKills={s_deferredKillsTotal}");
            }
            else if (prevTotal / 1000 != s_deferredHitsTotal / 1000)
            {
                Log.Info($"Deferred damage: {s_deferredHitsTotal} hits applied after "
                         + $"World.Step, {s_deferredKillsTotal} part(s) destroyed.");
            }
        }

        private static void ApplyPushback(
            Body bA, Body bB,
            float mA, float mB,
            int appliedA, int appliedB, float hpToEnergy,
            float vRelFactor,
            SVec normal, SVec worldPoint,
            out float speedABefore,  out float speedAAfter,
            out float speedBBefore,  out float speedBAfter,
            out float angVelABefore, out float angVelAAfter,
            out float angVelBBefore, out float angVelBAfter)
        {
            lock (s_slowdownLock)
            {
                s_stallStates.TryGetValue(bA, out var sA);
                s_stallStates.TryGetValue(bB, out var sB);
                SVec vAEff = (sA != null && sA.IsStalling) ? sA.CapturedVelocity : SafeLinearVelocity(bA);
                SVec vBEff = (sB != null && sB.IsStalling) ? sB.CapturedVelocity : SafeLinearVelocity(bB);
                speedABefore  = vAEff.Length();
                speedBBefore  = vBEff.Length();
                angVelABefore = (sA != null && sA.IsStalling) ? sA.CapturedAngularVelocity : SafeAngularVelocity(bA);
                angVelBBefore = (sB != null && sB.IsStalling) ? sB.CapturedAngularVelocity : SafeAngularVelocity(bB);

                var mode = (DamageMath.PushbackMode)Config.PushbackFormula;
                var pair = DamageMath.ComputePushback(
                    appliedA, appliedB, hpToEnergy, vRelFactor, normal, mode);

                ApplyImpulseOrStash(bA, mA, pair.ImpulseA, worldPoint, sA);
                ApplyImpulseOrStash(bB, mB, pair.ImpulseB, worldPoint, sB);

                speedAAfter  = (sA != null && sA.IsStalling) ? sA.CapturedVelocity.Length() : SafeLinearVelocity(bA).Length();
                speedBAfter  = (sB != null && sB.IsStalling) ? sB.CapturedVelocity.Length() : SafeLinearVelocity(bB).Length();
                angVelAAfter = (sA != null && sA.IsStalling) ? sA.CapturedAngularVelocity : SafeAngularVelocity(bA);
                angVelBAfter = (sB != null && sB.IsStalling) ? sB.CapturedAngularVelocity : SafeAngularVelocity(bB);
            }
        }

        private static SVec SafeLinearVelocity(Body b)
        {
            if (b == null) return SVec.Zero;
            try { return b.LinearVelocity; } catch { return SVec.Zero; }
        }

        private static float SafeAngularVelocity(Body b)
        {
            if (b == null) return 0f;
            try { return b.AngularVelocity; } catch { return 0f; }
        }

        private static void ApplyImpulseOrStash(
            Body b, float mass, SVec impulse, SVec worldPoint, StallState state)
        {
            if (b == null || mass <= 0f) return;

            float linMul = Config.LinearMultiplier;
            float rotMul = Config.RotationMultiplier;

            float bodyMul = Config.GetImpulseMultiplier(mass);
            if (bodyMul != 1f) impulse = impulse * bodyMul;

            if (state != null && state.IsStalling)
            {
                SVec r;
                try
                {
                    var wc = b.WorldCenter;
                    r = new SVec(worldPoint.X - wc.X, worldPoint.Y - wc.Y);
                }
                catch { return; /* body destroyed mid-impulse */ }

                float invMass = 1f / mass;
                float invI;
                try
                {
                    float inertia = b.Inertia;
                    if (inertia <= 0f) return;
                    invI = 1f / inertia;
                }
                catch { return; /* body destroyed mid-impulse */ }

                state.CapturedVelocity        += impulse * (invMass * linMul);
                state.CapturedAngularVelocity += (r.X * impulse.Y - r.Y * impulse.X) * (invI * rotMul);
                return;
            }

            if (linMul == 1f && rotMul == 1f)
            {
                try { b.ApplyLinearImpulse(impulse, worldPoint); }
                catch { /* body destroyed */ }
            }
            else
            {
                try { b.ApplyLinearImpulse(impulse * linMul); }
                catch { /* body destroyed */ }

                SVec r;
                try
                {
                    var wc = b.WorldCenter;
                    r = new SVec(worldPoint.X - wc.X, worldPoint.Y - wc.Y);
                }
                catch { return; /* body destroyed mid-impulse */ }

                float angImp = (r.X * impulse.Y - r.Y * impulse.X) * rotMul;
                try { b.ApplyAngularImpulse(angImp); }
                catch { /* body destroyed */ }
            }
        }

        private static void MarkRammingActive(Contact contact, Body bA, Body bB)
        {
            lock (s_rammingLock)
            {
                if (s_rammingContacts.Add(contact))
                {
                    IncRefAndDisableCCD(bA);
                    IncRefAndDisableCCD(bB);
                    IncPartnerRef(bA, bB);
                    IncPartnerRef(bB, bA);
                }
            }
            RetroSuppressShipContacts(bA, bB);
            RetroSuppressShipContacts(bB, bA);
        }

        // Enabled=false on every contact between body and partner. Do NOT
        // touch PointCount (sibling PreSolves bail) or IsTouching (EndContact
        // skips → leaked refs). 256-iter cap for safety.
        private static void RetroSuppressShipContacts(Body body, Body partner)
        {
            if (body == null || partner == null) return;
            try
            {
                var edge = body.ContactList;
                int guard = 0;
                while (edge != null && guard < 256)
                {
                    var c = edge.Contact;
                    if (c != null && ReferenceEquals(edge.Other, partner))
                        c.Enabled = false;
                    edge = edge.Next;
                    guard++;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("RetroSuppressShipContacts", ex);
            }
        }
    }
}
