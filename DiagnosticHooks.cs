using System;
using HarmonyLib;
using Halfling.Physics2D.Dynamics;

namespace ZTX.RammingDamage
{
    // Per-tick housekeeping around World.Step: bump tick counter, cache dt,
    // sweep speed gate, clear PerPartSuppression set, emit tick-pair summaries.
    // PenPriority's dict is cleared+repopulated by PenPriorityPatch itself.
    [HarmonyPatch(typeof(World), nameof(World.Step),
        new Type[] { typeof(float), typeof(SolverIterations) },
        new ArgumentType[] { ArgumentType.Normal, ArgumentType.Ref })]
    public static class WorldStepPatch
    {
        static void Prefix(float dt, World __instance)
        {
            try
            {
                RammingHandler.DrainPendingHits(__instance?.IsLocked ?? false, "prefix");

                RammingHandler.s_currentTick++;
                RammingHandler.s_lastStepDt = dt;
                RammingHandler.SweepSpeedGate();

                if (Config.PerPartSuppression)
                {
                    lock (RammingHandler.s_resolvedLock)
                        RammingHandler.s_resolvedThisTick.Clear();
                }
            }
            catch (Exception ex)
            {
                Log.Exception("WorldStepPatch.Prefix", ex);
            }
        }

        static void Postfix(float dt, World __instance)
        {
            try
            {
                RammingHandler.DrainPendingHits(__instance?.IsLocked ?? false, "postfix");

                if (Config.DebugLog)
                    RammingHandler.EmitTickPairSummariesAndClear();
            }
            catch (Exception ex)
            {
                Log.Exception("WorldStepPatch.Postfix", ex);
            }
        }
    }
}
