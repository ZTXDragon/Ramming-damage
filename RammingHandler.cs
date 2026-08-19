using System;
using System.Threading;
using Cosmoteer;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Colliders;
using Halfling.Physics2D.Collision;
using Halfling.Physics2D.Common;
using Halfling.Physics2D.Dynamics;
using Halfling.Physics2D.Dynamics.Contacts;

using HVec = Halfling.Geometry.Vector2;
using SVec = System.Numerics.Vector2;

namespace ZTX.RammingDamage
{
    // Ship-vs-ship ramming pipeline. PreSolve / EndContact subscribed to
    // ContactManager by SimRootPatch. Hot path: minimal allocations,
    // exceptions caught (throws here desync MP). State + helpers split
    // across State / Suppress / Diagnostics partials.
    internal static partial class RammingHandler
    {
        public static void PreSolve(Contact contact, ref Manifold oldManifold)
        {
            try
            {
                var fA = contact.FixtureA;
                var fB = contact.FixtureB;
                if (fA == null || fB == null) return;
                var bA = fA.Body;
                var bB = fB.Body;
                if (bA == null || bB == null) return;

                if (!ShipFilter.TryGetShipPair(bA, bB, out var shipA, out var shipB))
                    return;
                if (!ShipFilter.ShouldApplyRamming(shipA, shipB)) return;
                if (contact.Manifold.PointCount == 0) return;
                System.Threading.Interlocked.Increment(ref s_contactsSeenTotal);

                contact.GetWorldManifold(out SVec normal, out FixedArray2<SVec> worldPoints);
                SVec worldPoint = worldPoints[0];

                SVec vAtA = GetEffectiveVelocityAtWorldPoint(bA, worldPoint);
                SVec vAtB = GetEffectiveVelocityAtWorldPoint(bB, worldPoint);
                SVec relV = vAtA - vAtB;
                float vNormal = relV.X * normal.X + relV.Y * normal.Y;
                float vRelMag = relV.Length();

                float mA = bA.Mass;
                float mB = bB.Mass;
                if (mA <= 0f || mB <= 0f) return;

                bool gateActiveRamming;
                lock (s_rammingLock)
                {
                    gateActiveRamming = IsRammingOrRecentlyRammed(bA)
                                     || IsRammingOrRecentlyRammed(bB);
                }
                float requiredSpeed = gateActiveRamming
                    ? Config.GetSustainSpeedForPair(mA, mB)
                    : Config.GetClosingSpeedForPair(mA, mB);
                if (vRelMag < requiredSpeed)
                {
                    bool inActiveRammingPair;
                    int rammingTotal, refsA, refsB;
                    lock (s_rammingLock)
                    {
                        inActiveRammingPair = IsActiveRammingPair(bA, bB);
                        rammingTotal = s_rammingContacts.Count;
                        s_rammingRefCount.TryGetValue(bA, out refsA);
                        s_rammingRefCount.TryGetValue(bB, out refsB);
                    }
                    if (inActiveRammingPair)
                    {
                        contact.Enabled = false;
                        contact.Manifold.PointCount = 0;
                    }
                    if (Config.DebugLog)
                        LogBelowThreshold(contact, bA, bB, normal, vNormal,
                            inActiveRammingPair, refsA, refsB, rammingTotal);
                    return;
                }

                if (!float.IsFinite(vNormal)) return;

                Ship sA = (Ship)shipA;
                Ship sB = (Ship)shipB;
                HVec localA = (HVec)bA.GetLocalPoint(worldPoint);
                HVec localB = (HVec)bB.GetLocalPoint(worldPoint);

                Part partA = sA.Physics.GetLocalContactPart(fA, localA, out BaseCollider hitColliderA);
                if (partA == null)
                {
                    contact.Enabled = false;
                    contact.Manifold.PointCount = 0;
                    return;
                }
                Part partB = sB.Physics.GetLocalContactPart(fB, localB, out BaseCollider hitColliderB);
                if (partB == null)
                {
                    contact.Enabled = false;
                    contact.Manifold.PointCount = 0;
                    return;
                }

                if (Config.DeadPartGate)
                {
                    int hpAGate = SafeHealth(partA);
                    int hpBGate = SafeHealth(partB);
                    if (hpAGate <= 0 || hpBGate <= 0)
                    {
                        System.Threading.Interlocked.Increment(ref s_deadPartGateTotal);
                        contact.Enabled = false;
                        contact.Manifold.PointCount = 0;
                        if (Config.DebugLog)
                        {
                            int seq = Interlocked.Increment(ref s_logSeq);
                            int contactId = System.Runtime.CompilerServices
                                .RuntimeHelpers.GetHashCode(contact);
                            Halfling.Geometry.IntVector2 caGate = default, cbGate = default;
                            try { caGate = partA.Rect.Location; }
                            catch { caGate = new Halfling.Geometry.IntVector2(-1, -1); }
                            try { cbGate = partB.Rect.Location; }
                            catch { cbGate = new Halfling.Geometry.IntVector2(-1, -1); }
                            string deadSide = (hpAGate <= 0 && hpBGate <= 0) ? "BOTH"
                                            : (hpAGate <= 0 ? "A" : "B");
                            Log.Info($"DEAD_PART_HIT tick={s_currentTick} seq={seq} "
                                     + $"c=0x{contactId:X8} dead={deadSide} "
                                     + $"partA={partA.Rules?.ID}@({caGate.X},{caGate.Y}) hpA={hpAGate} "
                                     + $"partB={partB.Rules?.ID}@({cbGate.X},{cbGate.Y}) hpB={hpBGate}");
                        }
                        return;
                    }
                }

                if (Config.PerPartSuppression)
                {
                    bool already;
                    lock (s_resolvedLock)
                    {
                        already = s_resolvedThisTick.Contains(partA)
                               || s_resolvedThisTick.Contains(partB);
                        if (!already)
                        {
                            s_resolvedThisTick.Add(partA);
                            s_resolvedThisTick.Add(partB);
                        }
                    }
                    if (already)
                    {
                        System.Threading.Interlocked.Increment(ref s_partSkipTotal);
                        contact.Enabled = false;
                        contact.Manifold.PointCount = 0;
                        if (Config.DebugLog)
                        {
                            int seq = Interlocked.Increment(ref s_logSeq);
                            int contactId = System.Runtime.CompilerServices
                                .RuntimeHelpers.GetHashCode(contact);
                            Log.Info($"PART_SKIP tick={s_currentTick} seq={seq} "
                                     + $"c=0x{contactId:X8} "
                                     + $"partA={partA.Rules?.ID} partB={partB.Rules?.ID}");
                        }
                        return;
                    }
                }

                float rawPenA = partA.GetInitialPenetrationResistance(hitColliderA);
                float rawPenB = partB.GetInitialPenetrationResistance(hitColliderB);

                // PenPriority: shadow INTERIOR parts whose pen is below their
                // body's max for this tick. Exterior parts (≥1 cell-face on the
                // hull boundary) are never shadowed — they're legitimately at
                // the ship's surface, not clipped through armor. Per-side: if
                // only one side is shadowed, that side skips damage but the
                // other still takes its hit. Both shadowed → suppress contact.
                bool aShadowed = false, bShadowed = false;
                if (Config.PenPriority)
                {
                    bool aInterior = !IsPartExterior(sA, partA);
                    bool bInterior = !IsPartExterior(sB, partB);
                    float prevMaxA = 0f, prevMaxB = 0f;
                    lock (s_maxPenLock)
                    {
                        if (aInterior
                            && s_maxPenPerBody.TryGetValue(bA, out prevMaxA)
                            && rawPenA < prevMaxA)
                            aShadowed = true;
                        if (bInterior
                            && s_maxPenPerBody.TryGetValue(bB, out prevMaxB)
                            && rawPenB < prevMaxB)
                            bShadowed = true;
                        s_maxPenPerBody[bA] = MathF.Max(prevMaxA, rawPenA);
                        s_maxPenPerBody[bB] = MathF.Max(prevMaxB, rawPenB);
                    }
                    if (aShadowed && bShadowed)
                    {
                        System.Threading.Interlocked.Increment(ref s_penShadowTotal);
                        contact.Enabled = false;
                        contact.Manifold.PointCount = 0;
                        if (Config.DebugLog)
                        {
                            int seq = Interlocked.Increment(ref s_logSeq);
                            int contactId = System.Runtime.CompilerServices
                                .RuntimeHelpers.GetHashCode(contact);
                            Log.Info($"PEN_SHADOWED tick={s_currentTick} seq={seq} "
                                     + $"c=0x{contactId:X8} both "
                                     + $"penA={rawPenA:F1}<{prevMaxA:F1} "
                                     + $"penB={rawPenB:F1}<{prevMaxB:F1} "
                                     + $"partA={partA.Rules?.ID} partB={partB.Rules?.ID}");
                        }
                        return;
                    }
                }

                int maxHpA = partA.Rules?.MaxHealth ?? 0;
                int maxHpB = partB.Rules?.MaxHealth ?? 0;
                var share = DamageMath.ComputeDamageShare(
                    maxHpA, maxHpB, rawPenA, rawPenB, Config.PenFloor);

                int dmgA = ClampToInt(share.DamageToA);
                int dmgB = ClampToInt(share.DamageToB);

                // The pen-ratio formula is denominated in MAX health, so on a
                // part that's already been chewed on it can exceed what's left.
                // Clamp to current health.
                dmgA = ClampToHealth(partA, dmgA);
                dmgB = ClampToHealth(partB, dmgB);

                SVec vAcom = bA.LinearVelocity;
                SVec vBcom = bB.LinearVelocity;

                string partAId = null, partBId = null;
                Halfling.Geometry.IntVector2 partACell = default, partBCell = default;
                int hpABefore = 0, hpBBefore = 0;
                SVec posA = default, posB = default;
                float angVelA = 0f, angVelB = 0f;
                int refsABefore = 0, refsBBefore = 0;
                int rammingTotalBefore = 0;
                if (Config.DebugLog)
                {
                    partAId = partA.Rules?.ID.ToString() ?? "<null>";
                    partBId = partB.Rules?.ID.ToString() ?? "<null>";
                    try { partACell = partA.Rect.Location; }
                    catch { partACell = new Halfling.Geometry.IntVector2(-1, -1); }
                    try { partBCell = partB.Rect.Location; }
                    catch { partBCell = new Halfling.Geometry.IntVector2(-1, -1); }
                    hpABefore = partA.Health;
                    hpBBefore = partB.Health;
                    posA = bA.Position;
                    posB = bB.Position;
                    angVelA = bA.AngularVelocity;
                    angVelB = bB.AngularVelocity;
                    lock (s_rammingLock)
                    {
                        s_rammingRefCount.TryGetValue(bA, out refsABefore);
                        s_rammingRefCount.TryGetValue(bB, out refsBBefore);
                        rammingTotalBefore = s_rammingContacts.Count;
                    }
                }

                // HIT-path suppression. Do NOT touch IsTouching — that would
                // skip EndContact and leak ref counts.
                contact.Enabled = false;
                contact.Manifold.PointCount = 0;

                MarkRammingActive(contact, bA, bB);

                bool aInvuln = sA.IsInvulnerable;
                bool bInvuln = sB.IsInvulnerable;

                var attackerMetaForA = sB.Metadata;  // sB attacks A → push B's player onto A's sim
                var attackerMetaForB = sA.Metadata;
                
                int appliedA = 0;
                if (!aInvuln && !aShadowed)
                {
                    QueueHit(partA, dmgA, hitColliderA, sA.Sim, attackerMetaForA);
                    appliedA = dmgA;
                }

                int appliedB = 0;
                if (!bInvuln && !bShadowed)
                {
                    QueueHit(partB, dmgB, hitColliderB, sB.Sim, attackerMetaForB);
                    appliedB = dmgB;
                }

                if (Config.DebugLog)
                    UpdateTickPairStats(bA, bB, partA, partB, appliedA, appliedB);

                float vRelFactor = ComputeVRelFactor(bA, bB);
                ApplyPushback(bA, bB, mA, mB,
                              appliedA, appliedB, Config.HpToEnergy, vRelFactor,
                              normal, worldPoint,
                              out float speedABefore,  out float speedAAfter,
                              out float speedBBefore,  out float speedBAfter,
                              out float angVelABefore, out float angVelAAfter,
                              out float angVelBBefore, out float angVelBAfter);

                if (Config.DebugLog)
                {
                    int seq = Interlocked.Increment(ref s_logSeq);
                    int hpAAfter = SafeHealth(partA);
                    int hpBAfter = SafeHealth(partB);
                    int rammingTotalAfter;
                    int refsAAfter, refsBAfter;
                    lock (s_rammingLock)
                    {
                        s_rammingRefCount.TryGetValue(bA, out refsAAfter);
                        s_rammingRefCount.TryGetValue(bB, out refsBAfter);
                        rammingTotalAfter = s_rammingContacts.Count;
                    }
                    int contactId = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(contact);
                    var (totalA, otherShipA, suppressedA) = SummarizeContacts(bA);
                    var (totalB, otherShipB, suppressedB) = SummarizeContacts(bB);
                    SVec forceA = bA._force;
                    SVec forceB = bB._force;
                    float mReduced = (mA * mB) / (mA + mB);
                    float keCol    = mReduced * vNormal * vNormal * 0.5f;
                    string modeTag = Config.PushbackFormula == (int)DamageMath.PushbackMode.NewtonSum ? "N3" : "Asym";
                    Log.Info($"HIT tick={s_currentTick} seq={seq} c=0x{contactId:X8}");
                    Log.Info($"   v={vNormal:F2} J={mReduced * vNormal:F0} normal=({normal.X:F3},{normal.Y:F3}) "
                             + $"mReduced={mReduced:F1} ke={keCol:F0} mode={modeTag} "
                             + $"worldPt=({worldPoint.X:F2},{worldPoint.Y:F2})");
                    Log.Info($"   bA pos=({posA.X:F2},{posA.Y:F2}) "
                             + $"vel=({vAcom.X:F2},{vAcom.Y:F2}) angVel={angVelA:F3} "
                             + $"mass={mA:F0} ramRefs={refsABefore}->{refsAAfter} "
                             + $"force=({forceA.X:F0},{forceA.Y:F0}) torque={bA._torque:F0} "
                             + $"contacts total={totalA} otherShip={otherShipA} suppressed={suppressedA}");
                    Log.Info($"   bB pos=({posB.X:F2},{posB.Y:F2}) "
                             + $"vel=({vBcom.X:F2},{vBcom.Y:F2}) angVel={angVelB:F3} "
                             + $"mass={mB:F0} ramRefs={refsBBefore}->{refsBAfter} "
                             + $"force=({forceB.X:F0},{forceB.Y:F0}) torque={bB._torque:F0} "
                             + $"contacts total={totalB} otherShip={otherShipB} suppressed={suppressedB}");
                    Log.Info($"   partA={partAId}@({partACell.X},{partACell.Y}) "
                             + $"pen={rawPenA:F1} hp={hpABefore}/{maxHpA} (still {hpAAfter}, "
                             + $"damage queued) dmgComp={dmgA} dmgQueued={appliedA}");
                    Log.Info($"   partB={partBId}@({partBCell.X},{partBCell.Y}) "
                             + $"pen={rawPenB:F1} hp={hpBBefore}/{maxHpB} (still {hpBAfter}, "
                             + $"damage queued) dmgComp={dmgB} dmgQueued={appliedB}");
                    Log.Info($"   speedA: {speedABefore:F2}->{speedAAfter:F2} "
                             + $"angVelA: {angVelABefore:F3}->{angVelAAfter:F3} "
                             + $"speedB: {speedBBefore:F2}->{speedBAfter:F2} "
                             + $"angVelB: {angVelBBefore:F3}->{angVelBAfter:F3} "
                             + $"vRelFactor={vRelFactor:F3} "
                             + $"rammingTotal={rammingTotalBefore}->{rammingTotalAfter}");
                }
            }
            catch (Exception ex)
            {
                Log.Exception("PreSolve", ex);
            }
        }

        public static void EndContact(Contact contact)
        {
            try
            {
                var bA = contact?.FixtureA?.Body;
                var bB = contact?.FixtureB?.Body;
                bool wasRamming;
                int refsAAfter = 0, refsBAfter = 0;
                int rammingTotalAfter;
                lock (s_rammingLock)
                {
                    wasRamming = s_rammingContacts.Remove(contact);
                    if (wasRamming)
                    {
                        DecRefAndMaybeRestoreCCD(bA);
                        DecRefAndMaybeRestoreCCD(bB);
                        DecPartnerRef(bA, bB);
                        DecPartnerRef(bB, bA);
                        s_rammingRefCount.TryGetValue(bA, out refsAAfter);
                        s_rammingRefCount.TryGetValue(bB, out refsBAfter);
                    }
                    rammingTotalAfter = s_rammingContacts.Count;
                }
                if (wasRamming && Config.DebugLog)
                {
                    int seq = Interlocked.Increment(ref s_logSeq);
                    int contactId = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(contact);
                    Log.Info($"END seq={seq} c=0x{contactId:X8} wasRamming=true "
                             + $"bA.refs={refsAAfter} bB.refs={refsBAfter} "
                             + $"rammingTotal={rammingTotalAfter}");
                }
            }
            catch (Exception ex)
            {
                Log.Exception("EndContact", ex);
            }
        }

        internal static SVec GetEffectiveVelocity(Body b)
        {
            if (b == null) return SVec.Zero;
            if (s_stallStates.TryGetValue(b, out var s) && s.IsStalling)
                return s.CapturedVelocity;
            try { return b.LinearVelocity; }
            catch { return SVec.Zero; }
        }

        internal static SVec GetEffectiveVelocityAtWorldPoint(Body b, SVec worldPoint)
        {
            if (b == null) return SVec.Zero;
            if (s_stallStates.TryGetValue(b, out var s) && s.IsStalling)
                return s.CapturedVelocity;
            try { return b.GetLinearVelocityFromWorldPoint(worldPoint); }
            catch { return SVec.Zero; }
        }

        internal static void RamLogStallTrig(Body body, StallState state, float speedPre, float phasePerTick)
        {
            if (!Config.DebugLog) return;
            int seq = Interlocked.Increment(ref s_logSeq);
            int hash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(body);
            Log.Info($"STALL_TRIG tick={s_currentTick} seq={seq} body=0x{hash:X8} "
                   + $"speed={speedPre:F2} cap=({state.CapturedVelocity.X:F2},{state.CapturedVelocity.Y:F2}) "
                   + $"capAng={state.CapturedAngularVelocity:F3} "
                   + $"N_next={state.LastComputedN} phasePerTick={phasePerTick:F2}");
        }

        internal static void RamLogStallRestore(Body body, StallState state)
        {
            if (!Config.DebugLog) return;
            int seq = Interlocked.Increment(ref s_logSeq);
            int hash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(body);
            float restoredSpeed = state.CapturedVelocity.Length();
            Log.Info($"STALL_REST tick={s_currentTick} seq={seq} body=0x{hash:X8} "
                   + $"restored=({state.CapturedVelocity.X:F2},{state.CapturedVelocity.Y:F2}) "
                   + $"restoredAng={state.CapturedAngularVelocity:F3} "
                   + $"speed={restoredSpeed:F2}");
        }

        private static bool IsPartExterior(Ship ship, Part part)
        {
            if (ship == null || part == null) return true;
            try
            {
                var partsMgr = ship.Parts;
                foreach (var cell in part.GetAdjacentCells(
                    PartRectType.Physical, AdjacencyFlags.Sides))
                {
                    if (partsMgr[cell, PartRectType.Physical] == null)
                        return true;
                }
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static float ComputeVRelFactor(Body bA, Body bB)
        {
            SVec velA = GetEffectiveVelocity(bA);
            SVec velB = GetEffectiveVelocity(bB);
            float spA = velA.Length();
            float spB = velB.Length();
            if (spA <= 0.01f || spB <= 0.01f) return 1.0f;

            float cosVel = (velA.X * velB.X + velA.Y * velB.Y) / (spA * spB);
            if (cosVel >= 0f) return 1.0f;

            float h = -cosVel;
            float factor = 1.0f - (1.0f - Config.SlowdownHeadOnFactor) * h * h;
            return factor < 0f ? 0f : factor;
        }
    }
}
