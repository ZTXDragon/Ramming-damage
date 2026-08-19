using System;
using System.Threading;
using HarmonyLib;
using Halfling.Physics2D.Dynamics;

namespace ZTX.RammingDamage
{
    // Ship-vs-ship gate. Filters: both bodies are Ships, not same Ship, not
    // BOTH invulnerable, then the per-category allegiance gates in MayRam.
    // Single-side invulnerability is handled deeper in PreSolve so an
    // invulnerable rammer can still damage vulnerable enemies.
    internal static class ShipFilter
    {
        private static Type s_shipType;
        private static bool s_resolved;
        private static readonly object s_initLock = new object();

        private static int s_logCallCount;

        private static bool EnsureResolved()
        {
            if (s_resolved) return s_shipType != null;
            lock (s_initLock)
            {
                if (s_resolved) return s_shipType != null;
                s_shipType = AccessTools.TypeByName("Cosmoteer.Ships.Ship");
                if (s_shipType == null)
                    Log.Error("ShipFilter: cannot resolve Cosmoteer.Ships.Ship type.");
                s_resolved = true;
                return s_shipType != null;
            }
        }

        public static bool IsShipBody(Body body)
        {
            if (!EnsureResolved()) return false;
            object ud;
            try { ud = body?.UserData; }
            catch { return false; }
            return ud != null && s_shipType.IsInstanceOfType(ud);
        }

        public static bool TryGetShipPair(Body a, Body b, out object shipA, out object shipB)
        {
            shipA = shipB = null;
            if (!EnsureResolved()) return false;
            if (a == null || b == null) return false;
            try { shipA = a.UserData; }
            catch { return false; }
            try { shipB = b.UserData; }
            catch { return false; }
            if (shipA == null || shipB == null) return false;
            if (!s_shipType.IsInstanceOfType(shipA)) return false;
            if (!s_shipType.IsInstanceOfType(shipB)) return false;
            if (ReferenceEquals(shipA, shipB)) return false;
            return true;
        }

        public static bool ShouldApplyRamming(object shipA, object shipB)
        {
            try
            {
                var sA = shipA as Cosmoteer.Ships.Ship;
                var sB = shipB as Cosmoteer.Ships.Ship;
                if (sA == null || sB == null) return false;

                if (sA.IsInvulnerable && sB.IsInvulnerable)
                {
                    if (Config.DebugLog)
                    {
                        int n = Interlocked.Increment(ref s_logCallCount);
                        if ((n & 0xF) == 1)
                            Log.Info($"INVULN_SKIP_BOTH seq={n}");
                    }
                    return false;
                }

                return MayRam(sA, sB);
            }
            catch (Exception ex)
            {
                Log.Exception("ShipFilter.ShouldApplyRamming", ex);
                return false;
            }
        }

        internal enum RamPair
        {
            Self,       // both ships have the same owner -- your own two ships
            Asteroid,   // either side is an asteroid or megaroid
            Junk,       // either side is wreckage / derelict / abandoned (-3)
            Neutral,    // either side is an FTL gate or faction beacon (-1)
            Enemy,      // the two are hostile, including barbarians (-2)
            Ally,       // everything else: allies, truces, protection pacts
        }

        internal static RamPair Classify(Cosmoteer.Ships.Ship a, Cosmoteer.Ships.Ship b)
        {
            var metaA = a?.Metadata;
            var metaB = b?.Metadata;

            if (metaA != null && metaB != null && metaA.PlayerIndex == metaB.PlayerIndex)
                return RamPair.Self;

            if (IsAsteroid(a) || IsAsteroid(b)) return RamPair.Asteroid;

            if (metaA != null && metaB != null)
            {
                if (metaA.PlayerIndex == -3 || metaB.PlayerIndex == -3) return RamPair.Junk;
                if (metaA.PlayerIndex == -1 || metaB.PlayerIndex == -1) return RamPair.Neutral;
            }

            try { if (a.IsEnemiesWith(b)) return RamPair.Enemy; }
            catch { /* mid-destruction */ }

            return RamPair.Ally;
        }

        private static bool IsAsteroid(Cosmoteer.Ships.Ship s)
        {
            try { return s?.Rules != null && s.Rules.IsAsteroid; }
            catch { return false; }   // Rules can be null mid-destruction
        }

        internal static bool MayRam(Cosmoteer.Ships.Ship a, Cosmoteer.Ships.Ship b)
        {
            if (a == null || b == null) return false;
            if (!Config.UseRamDamageGates) return true;

            switch (Classify(a, b))
            {
                case RamPair.Self:     return Config.RamDamageSelf;
                case RamPair.Asteroid: return Config.RamDamageAsteroids;
                case RamPair.Junk:     return Config.RamDamageJunk;
                case RamPair.Neutral:  return Config.RamDamageNeutral;
                case RamPair.Enemy:    return Config.RamDamageEnemies;
                default:               return Config.RamDamageAllies;
            }
        }
    }
}
