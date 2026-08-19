using System;
using System.Reflection;
using HarmonyLib;

namespace ZTX.RammingDamage
{
    // Routes log messages to Cosmoteer's own log file via reflection.
    //
    // VERSION is stamped into the prefix so every log line a user pastes into
    // a bug report tells us which build of the mod they're running. Bump it
    internal static class Log
    {
        public const string VERSION = "v2.2";
        private const string Prefix = "[ZTX.Ramming " + VERSION + "] ";
        private static MethodInfo s_log;
        private static bool s_resolved;

        private static MethodInfo GetLogMethod()
        {
            if (s_resolved) return s_log;
            s_resolved = true;
            var loggerType = AccessTools.TypeByName("Halfling.Logging.Logger");
            if (loggerType != null)
            {
                s_log = AccessTools.Method(loggerType, "Log", new[] { typeof(string) });
            }
            return s_log;
        }

        public static void Info(string msg)
        {
            try
            {
                var m = GetLogMethod();
                if (m != null) m.Invoke(null, new object[] { Prefix + msg });
            }
            catch { }
        }

        public static void Error(string msg) => Info("ERROR: " + msg);

        public static void Exception(string context, Exception ex)
        {
            if (ex == null)
            {
                Error(context + " threw: <null exception>");
                return;
            }
            var inner = ex.InnerException ?? ex;
            Error(context + " threw: " + inner.GetType().Name + ": " + inner.Message);
        }
    }
}
