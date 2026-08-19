using System;
using System.Reflection;
using HarmonyLib;
using Halfling.Physics2D.Dynamics;

namespace ZTX.RammingDamage
{
    // Postfix on SimRoot.StartInit (creates PhysicsWorld). Subscribes PreSolve
    // and EndContact to the fresh ContactManager. Re-subscribes naturally on
    // sim teardown + re-init.
    [HarmonyPatch]
    public static class SimRootInitPhysicsWorldPatch
    {
        private static MethodBase s_target;
        private const string TargetMethodName = "StartInit";

        static MethodBase TargetMethod()
        {
            if (s_target != null) return s_target;
            var type = AccessTools.TypeByName("Cosmoteer.Simulation.SimRoot");
            if (type == null)
                throw new Exception("[ZTX.Ramming] SimRoot type not found");

            MethodInfo found = null;
            foreach (var m in type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.Name != TargetMethodName) continue;
                if (found != null)
                    throw new Exception("[ZTX.Ramming] SimRoot." + TargetMethodName
                                        + " is overloaded -- patch needs param-type disambiguation.");
                found = m;
            }
            if (found == null)
                throw new Exception("[ZTX.Ramming] SimRoot." + TargetMethodName + "() not found");
            s_target = found;
            return s_target;
        }

        static void Postfix(object __instance)
        {
            try
            {
                var prop = __instance.GetType().GetProperty(
                    "PhysicsWorld",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop == null)
                {
                    Log.Error("SimRootPatch: PhysicsWorld property not found.");
                    return;
                }
                var world = prop.GetValue(__instance) as World;
                if (world == null)
                {
                    Log.Error("SimRootPatch: PhysicsWorld is null after init.");
                    return;
                }
                world.ContactManager.PreSolve -= RammingHandler.PreSolve;
                world.ContactManager.PreSolve += RammingHandler.PreSolve;
                world.ContactManager.EndContact -= RammingHandler.EndContact;
                world.ContactManager.EndContact += RammingHandler.EndContact;
                Log.Info("PreSolve + EndContact subscribed to PhysicsWorld.ContactManager.");

                RammingHandler.ResetAllState();
            }
            catch (Exception ex)
            {
                Log.Exception("SimRootInitPhysicsWorldPatch.Postfix", ex);
            }
        }
    }

    [HarmonyPatch]
    public static class SimRootDisposePatch
    {
        private static MethodBase s_target;

        static MethodBase TargetMethod()
        {
            if (s_target != null) return s_target;
            var type = AccessTools.TypeByName("Cosmoteer.Simulation.SimRoot");
            if (type == null)
                throw new Exception("[ZTX.Ramming] SimRoot type not found");

            var method = type.GetMethod(
                "Dispose",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            if (method == null)
                throw new Exception("[ZTX.Ramming] SimRoot.Dispose() not found");
            s_target = method;
            return s_target;
        }

        static void Postfix()
        {
            try { RammingHandler.ResetAllState(); }
            catch (Exception ex)
            {
                Log.Exception("SimRootDisposePatch.Postfix", ex);
            }
        }
    }
}
