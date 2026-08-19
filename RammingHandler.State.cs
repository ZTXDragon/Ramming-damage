using System;
using System.Collections.Generic;
using System.Threading;
using Halfling.Physics2D.Dynamics;
using Halfling.Physics2D.Dynamics.Contacts;
using SVec = System.Numerics.Vector2;

namespace ZTX.RammingDamage
{
    // Static state for RammingHandler. Locks: s_rammingLock (ramming-set
    // bookkeeping), s_slowdownLock (velocity write), s_resolvedLock
    // (per-part suppression), s_maxPenLock (PenPriority).
    internal static partial class RammingHandler
    {
        internal sealed class StallState
        {
            public int   CountdownTicks          = -1;       // -1 = idle
            public SVec  CapturedVelocity        = SVec.Zero;
            public float CapturedAngularVelocity = 0f;
            public bool  IsStalling              = false;
            public int   LastComputedN           = 0;        // logging only
        }

        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<Body, StallState>
            s_stallStates = new System.Collections.Concurrent.ConcurrentDictionary<Body, StallState>();

        private static readonly HashSet<Contact> s_rammingContacts = new HashSet<Contact>();
        private static readonly Dictionary<Body, int> s_rammingRefCount = new Dictionary<Body, int>();
        private static readonly Dictionary<Body, Dictionary<Body, int>> s_partnerRefs
            = new Dictionary<Body, Dictionary<Body, int>>();
        private static readonly Dictionary<Body, long> s_lastRammingTick
            = new Dictionary<Body, long>();
        private static readonly object s_rammingLock = new object();

        internal static readonly object s_slowdownLock = new object();

        internal static readonly HashSet<Cosmoteer.Ships.Parts.Part> s_resolvedThisTick
            = new HashSet<Cosmoteer.Ships.Parts.Part>();
        internal static readonly object s_resolvedLock = new object();

        internal struct PendingHit
        {
            public Cosmoteer.Ships.Parts.Part                   Part;
            public int                                          Damage;
            public Cosmoteer.Ships.Parts.Colliders.BaseCollider HitCollider;
            public Cosmoteer.Simulation.SimRoot                 Sim;
            public int                                          AttackerPlayerIndex;
            public bool                                         HasAttacker;
        }

        internal static readonly List<PendingHit> s_pendingHits
            = new List<PendingHit>(64);

        internal static readonly Dictionary<Cosmoteer.Ships.Parts.Part, int> s_pendingDamage
            = new Dictionary<Cosmoteer.Ships.Parts.Part, int>();

        internal static readonly object s_pendingLock = new object();

        internal static long s_partSkipTotal;      // PerPartSuppression skips
        internal static long s_penShadowTotal;     // PenPriority full shadows
        internal static long s_deadPartGateTotal;  // DeadPartGate rejections
        internal static long s_contactsSeenTotal;  // contacts reaching the gates

        internal static long s_deferredHitsTotal;
        internal static long s_deferredKillsTotal;
        internal static int  s_drainSeq;
        internal static bool s_firstDrainLogged;

        internal static readonly Dictionary<Body, float>
            s_maxPenPerBody = new Dictionary<Body, float>();
        internal static readonly object s_maxPenLock = new object();

        internal sealed class TickPairStats
        {
            public Body BodyA, BodyB;
            public int HitCount;
            public int DmgAToB;
            public int DmgBToA;
            public float SpeedA, SpeedB;
            public System.Numerics.Vector2 PosA, PosB;
            public HashSet<int> DamagedPartsA = new HashSet<int>();
            public HashSet<int> DamagedPartsB = new HashSet<int>();
            public HashSet<int> KilledPartsA  = new HashSet<int>();
            public HashSet<int> KilledPartsB  = new HashSet<int>();
        }

        internal static readonly Dictionary<(Body, Body), TickPairStats>
            s_tickPairStats = new Dictionary<(Body, Body), TickPairStats>();
        internal static readonly object s_tickStatsLock = new object();

        internal static float s_lastStepDt = 1f / 30f;

        internal static long s_currentTick;   // bumped by WorldStepPatch.Prefix
        private static int s_logSeq;

        internal static bool IsRammingBody(Body body)
            => IsRammingBodyWithGrace(body, Config.SustainGraceTicks);

        internal static bool IsRammingBodyWithGrace(Body body, long graceTicks)
        {
            if (body == null) return false;
            lock (s_rammingLock)
            {
                if (s_rammingRefCount.ContainsKey(body)) return true;
                if (graceTicks <= 0) return false;
                if (!s_lastRammingTick.TryGetValue(body, out long last)) return false;
                long age = s_currentTick - last;
                return age >= 0 && age < graceTicks;
            }
        }

        internal static int NextLogSeq() => Interlocked.Increment(ref s_logSeq);

        private static int ClampToInt(double damage)
        {
            double rounded = Math.Round(damage);
            if (rounded < 1.0) return 1;
            if (rounded > int.MaxValue) return int.MaxValue;
            return (int)rounded;
        }

        private static void IncRefAndDisableCCD(Body body)
        {
            if (body == null) return;
            s_rammingRefCount.TryGetValue(body, out int count);
            s_rammingRefCount[body] = count + 1;
            s_lastRammingTick[body] = s_currentTick;
            try { body.IgnoreCCD = true; } catch { /* destroyed */ }

            if (count == 0)
            {
                var newState = new StallState();
                if (Config.StallEnabled)
                {
                    try
                    {
                        float bodyMass = body.Mass;
                        bool tooHeavy = Config.StallDisableAboveMass > 0f
                                        && bodyMass >= Config.StallDisableAboveMass;
                        if (!tooHeavy)
                        {
                            float speed = body.LinearVelocity.Length();
                            int n = DamageMath.ComputeN(
                                speed, s_lastStepDt,
                                Config.MaxPhaseLayers,
                                Config.DestroyRatePerTick,
                                Config.StallDisableSpeed);
                            if (n > 0)
                            {
                                newState.CountdownTicks = n;
                                newState.LastComputedN  = n;
                            }
                        }
                    }
                    catch { /* body destroyed */ }
                }
                s_stallStates.TryAdd(body, newState);
            }
        }

        private static void DecRefAndMaybeRestoreCCD(Body body)
        {
            if (body == null) return;
            if (!s_rammingRefCount.TryGetValue(body, out int count)) return;
            if (count <= 1)
                s_rammingRefCount.Remove(body);
            else
                s_rammingRefCount[body] = count - 1;
        }

        private static void IncPartnerRef(Body owner, Body partner)
        {
            if (owner == null || partner == null) return;
            if (!s_partnerRefs.TryGetValue(owner, out var partners))
            {
                partners = new Dictionary<Body, int>();
                s_partnerRefs[owner] = partners;
            }
            partners.TryGetValue(partner, out int count);
            partners[partner] = count + 1;
        }

        private static void DecPartnerRef(Body owner, Body partner)
        {
            if (owner == null || partner == null) return;
            if (!s_partnerRefs.TryGetValue(owner, out var partners)) return;
            if (!partners.TryGetValue(partner, out int count)) return;
            if (count <= 1)
            {
                partners.Remove(partner);
                if (partners.Count == 0) s_partnerRefs.Remove(owner);
            }
            else
            {
                partners[partner] = count - 1;
            }
        }

        private static bool IsActiveRammingPair(Body a, Body b)
        {
            if (a == null || b == null) return false;
            return s_partnerRefs.TryGetValue(a, out var partners)
                && partners.ContainsKey(b);
        }

        private static bool IsRammingOrRecentlyRammed(Body body)
        {
            if (body == null) return false;

            float bodyMass;
            try { bodyMass = body.Mass; }
            catch { ForceDisableRamming(body); return false; }
            float minSp = Config.GetSustainSpeed(bodyMass);
            if (minSp > 0f)
            {
                SVec vEff = GetEffectiveVelocity(body);
                float spSq = vEff.LengthSquared();
                if (!float.IsFinite(spSq) || spSq < minSp * minSp)
                {
                    ForceDisableRamming(body);
                    return false;
                }
            }

            if (s_rammingRefCount.ContainsKey(body)) return true;
            if (Config.SustainGraceTicks <= 0) return false;
            if (!s_lastRammingTick.TryGetValue(body, out long last)) return false;
            long age = s_currentTick - last;
            if (age >= 0 && age < Config.SustainGraceTicks) return true;

            ForceDisableRamming(body);
            return false;
        }

        internal static void SweepSpeedGate()
        {
            lock (s_rammingLock)
            {
                if (s_lastRammingTick.Count == 0) return;

                var bodies = new Body[s_lastRammingTick.Count];
                s_lastRammingTick.Keys.CopyTo(bodies, 0);

                for (int i = 0; i < bodies.Length; i++)
                {
                    var body = bodies[i];
                    if (body == null) continue;

                    if (!s_rammingRefCount.ContainsKey(body)
                        && Config.SustainGraceTicks > 0
                        && s_lastRammingTick.TryGetValue(body, out long last))
                    {
                        long age = s_currentTick - last;
                        if (age >= Config.SustainGraceTicks)
                        {
                            ForceDisableRamming(body);
                            continue;
                        }
                    }

                    float bodyMass;
                    try { bodyMass = body.Mass; }
                    catch { ForceDisableRamming(body); continue; }
                    float minSp = Config.GetSustainSpeed(bodyMass);
                    if (minSp <= 0f) continue;
                    SVec vEff = GetEffectiveVelocity(body);
                    float spSq = vEff.LengthSquared();
                    if (!float.IsFinite(spSq) || spSq < minSp * minSp)
                        ForceDisableRamming(body);
                }
            }
        }

        internal static bool HasAnyRammingBody()
        {
            lock (s_rammingLock)
            {
                return s_rammingRefCount.Count > 0 || s_lastRammingTick.Count > 0;
            }
        }

        internal static void ResetAllState()
        {
            try
            {
                lock (s_rammingLock)
                {
                    s_rammingContacts.Clear();
                    s_rammingRefCount.Clear();
                    s_partnerRefs.Clear();
                    s_lastRammingTick.Clear();
                }
                s_stallStates.Clear();
                lock (s_resolvedLock)  { s_resolvedThisTick.Clear(); }
                lock (s_pendingLock)
                {
                    s_pendingHits.Clear();
                    s_pendingDamage.Clear();
                }
                if (s_deferredHitsTotal > 0)
                {
                    Log.Info($"Sim totals: {s_contactsSeenTotal} ramming contacts, "
                             + $"{s_deferredHitsTotal} deferred hits, "
                             + $"{s_deferredKillsTotal} parts destroyed. "
                             + $"Gate activity: PerPartSuppression skipped "
                             + $"{s_partSkipTotal}, PenPriority shadowed "
                             + $"{s_penShadowTotal}, DeadPartGate rejected "
                             + $"{s_deadPartGateTotal}. "
                             + $"(PerPartSuppression={Config.PerPartSuppression} "
                             + $"PenPriority={Config.PenPriority} "
                             + $"DeadPartGate={Config.DeadPartGate})");
                }

                s_partSkipTotal      = 0;
                s_penShadowTotal     = 0;
                s_deadPartGateTotal  = 0;
                s_contactsSeenTotal  = 0;
                s_deferredHitsTotal  = 0;
                s_deferredKillsTotal = 0;
                s_drainSeq           = 0;
                s_firstDrainLogged   = false;
                lock (s_maxPenLock)    { s_maxPenPerBody.Clear(); }
                lock (s_tickStatsLock) { s_tickPairStats.Clear(); }
                s_currentTick = 0;
                s_lastStepDt = 1f / 30f;
                Log.Info("ResetAllState: cleared all per-sim state.");
            }
            catch (Exception ex)
            {
                Log.Exception("ResetAllState", ex);
            }
        }

        private static void ForceDisableRamming(Body body)
        {
            if (body == null) return;

            bool hadRefs = s_rammingRefCount.ContainsKey(body);
            bool hadGrace = s_lastRammingTick.ContainsKey(body);
            bool hadPartners = s_partnerRefs.ContainsKey(body);
            if (!hadRefs && !hadGrace && !hadPartners) return;

            s_lastRammingTick.Remove(body);
            if (s_rammingRefCount.Remove(body))
            {
                try { body.IgnoreCCD = false; } catch { /* destroyed */ }
            }

            if (s_stallStates.TryRemove(body, out var stallSt) && stallSt.IsStalling)
            {
                try
                {
                    body.LinearVelocity  = stallSt.CapturedVelocity;
                    body.AngularVelocity = stallSt.CapturedAngularVelocity;
                }
                catch { /* destroyed */ }
            }

            if (s_partnerRefs.TryGetValue(body, out var outgoing))
            {
                var partners = new Body[outgoing.Count];
                outgoing.Keys.CopyTo(partners, 0);
                foreach (var partner in partners)
                {
                    if (s_partnerRefs.TryGetValue(partner, out var partnerOut))
                    {
                        partnerOut.Remove(body);
                        if (partnerOut.Count == 0)
                            s_partnerRefs.Remove(partner);
                    }
                }
                s_partnerRefs.Remove(body);
            }

            if (Config.DebugLog)
            {
                int hash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(body);
                float speed = GetEffectiveVelocity(body).Length();
                Log.Info($"FORCE_DISABLE tick={s_currentTick} body=0x{hash:X8} "
                         + $"speed={speed:F2} "
                         + $"refs={(hadRefs ? "cleared" : "-")} "
                         + $"grace={(hadGrace ? "cleared" : "-")} "
                         + $"partners={(hadPartners ? "cleared" : "-")}");
            }
        }
    }
}
