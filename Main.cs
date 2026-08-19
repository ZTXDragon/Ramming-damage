using System;
using System.Reflection;
using HarmonyLib;

namespace ZTX.RammingDamage
{
    // YAML entry points. AssemblyLoadInitializer fires right after assembly
    // load; InitializePatches is a fallback for older YAML versions.
    public static class Main
    {
        private static bool s_initialized;

        public static void AssemblyLoadInitializer() => RunInitOnce();
        public static void InitializePatches() => RunInitOnce();

        private static void RunInitOnce()
        {
            if (s_initialized) return;
            s_initialized = true;
            try
            {
                Config.Load();
                var harmony = new Harmony("ztx.ramming_damage");
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                DamageMath.RunSelfTest();
                Log.Info("init OK. " + Log.VERSION
                         + " | .NET " + Environment.Version
                         + " | OS " + Environment.OSVersion.VersionString);
            }
            catch (Exception ex)
            {
                Log.Exception("Main.RunInitOnce", ex);
            }
        }
    }
}
