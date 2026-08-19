using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace ZTX.RammingDamage
{
    // Tunables loaded from sibling config.rules at startup. Locale-invariant
    // parsing; missing file or bad lines fall through to defaults.
    internal static class Config
    {
        public static float MinClosingSpeed = 50f;
        public static float MinSustainSpeed = 5f;

        public static bool MassClosingSpeedTableEnabled = false;
        public static (float MassCap, float Speed)[] MassClosingSpeedTiers
            = Array.Empty<(float, float)>();
        public static bool MassSustainSpeedTableEnabled = false;
        public static (float MassCap, float Speed)[] MassSustainSpeedTiers
            = Array.Empty<(float, float)>();
        public static int MassTierPairRule = 0;

        public static bool ImpulseReductionTableEnabled = true;

        public static (float MassCap, float Multiplier)[] ImpulseReductionTiers
            = new (float, float)[] { (5f, 0.05f), (500f, 1.0f) };

        public static float PenFloor             = 0.5f;
        public static float HpToEnergy           = 0.5f;
        public static float LinearMultiplier     = 1f;
        public static float RotationMultiplier   = 1f;
        public static int   PushbackFormula      = 1;
        public static float SlowdownHeadOnFactor = 0.71f;
        public static long  SustainGraceTicks    = 30;

       
        public static bool  PerPartSuppression   = true;
        public static bool  PenPriority          = true;

        public static bool  StallEnabled         = false;
        public static float MaxPhaseLayers       = 3f;
        public static float StallDisableSpeed    = 30f;
        public static float DestroyRatePerTick   = 1f;
        public static long  StallGraceTicks      = 2;
        public static float StallDisableAboveMass = 100000f;

        public static bool UseRamDamageGates = true;

        public static bool RamDamageSelf      = false;  // your own other ships
        public static bool RamDamageNeutral   = true;   // FTL gates, faction beacons
        public static bool RamDamageAllies    = false;  // allies, truces, protection pacts
        public static bool RamDamageAsteroids = true;   // asteroids and megaroids
        public static bool RamDamageJunk      = true;   // wreckage, derelicts, abandoned
        public static bool RamDamageEnemies   = true;   // hostiles, incl. barbarians

        public static bool DebugLog     = false;
        public static bool DeadPartGate = true;

        public static void Load()
        {
            string path = "<unknown>";
            try
            {
                string asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(asmDir))
                {
                    Log.Info("Could not resolve assembly directory. Using defaults.");
                    return;
                }
                path = Path.Combine(asmDir, "config.rules");

                if (!File.Exists(path))
                {
                    Log.Info("config.rules not found at " + path + ". Using defaults.");
                    return;
                }

                int parsed = 0;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw;
                    int commentIdx = line.IndexOf("//", StringComparison.Ordinal);
                    if (commentIdx >= 0) line = line.Substring(0, commentIdx);

                    line = line.Trim();
                    if (line.Length == 0) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    if (key.Length == 0) continue;

                    if (TryAssign(key, val)) parsed++;
                }

                Log.Info($"Config loaded: {parsed} key(s) from {path}");
                Log.Info( "  [thresholds]  "
                         + $"MinClosingSpeed={MinClosingSpeed} "
                         + $"MinSustainSpeed={MinSustainSpeed} "
                         + $"SustainGraceTicks={SustainGraceTicks}");
                Log.Info( "  [mass tiers]  "
                         + $"MassClosingSpeedTableEnabled={MassClosingSpeedTableEnabled} "
                         + $"MassClosingSpeedTiers={DescribeTiers(MassClosingSpeedTiers)} "
                         + $"MassSustainSpeedTableEnabled={MassSustainSpeedTableEnabled} "
                         + $"MassSustainSpeedTiers={DescribeTiers(MassSustainSpeedTiers)} "
                         + $"MassTierPairRule={MassTierPairRule}");
                Log.Info( "  [damage]      "
                         + $"PenFloor={PenFloor}");
                Log.Info( "  [pushback]    "
                         + $"HpToEnergy={HpToEnergy} "
                         + $"LinearMultiplier={LinearMultiplier} "
                         + $"RotationMultiplier={RotationMultiplier} "
                         + $"PushbackFormula={PushbackFormula} "
                         + $"SlowdownHeadOnFactor={SlowdownHeadOnFactor} "
                         + $"ImpulseReductionTableEnabled={ImpulseReductionTableEnabled} "
                         + $"ImpulseReductionTiers={DescribeTiers(ImpulseReductionTiers)}");
                Log.Info( "  [per-tick]    "
                         + $"PerPartSuppression={PerPartSuppression} "
                         + $"PenPriority={PenPriority} "
                         + $"DeadPartGate={DeadPartGate}");
                Log.Info( "  [ram gates]   "
                         + $"UseRamDamageGates={UseRamDamageGates} "
                         + $"Self={RamDamageSelf} "
                         + $"Neutral={RamDamageNeutral} "
                         + $"Allies={RamDamageAllies} "
                         + $"Asteroids={RamDamageAsteroids} "
                         + $"Junk={RamDamageJunk} "
                         + $"Enemies={RamDamageEnemies}");
                Log.Info( "  [stall]       "
                         + $"StallEnabled={StallEnabled} "
                         + $"MaxPhaseLayers={MaxPhaseLayers} "
                         + $"StallDisableSpeed={StallDisableSpeed} "
                         + $"DestroyRatePerTick={DestroyRatePerTick} "
                         + $"StallGraceTicks={StallGraceTicks} "
                         + $"StallDisableAboveMass={StallDisableAboveMass}");
                Log.Info( "  [diagnostics] "
                         + $"DebugLog={DebugLog}");
            }
            catch (Exception ex)
            {
                Log.Exception("Config.Load (using defaults)", ex);
            }
        }

        public static float GetClosingSpeed(float mass)
        {
            if (!MassClosingSpeedTableEnabled) return MinClosingSpeed;
            for (int i = 0; i < MassClosingSpeedTiers.Length; i++)
            {
                if (mass <= MassClosingSpeedTiers[i].MassCap)
                    return MassClosingSpeedTiers[i].Speed;
            }
            return MinClosingSpeed;
        }

        public static float GetSustainSpeed(float mass)
        {
            if (!MassSustainSpeedTableEnabled) return MinSustainSpeed;
            for (int i = 0; i < MassSustainSpeedTiers.Length; i++)
            {
                if (mass <= MassSustainSpeedTiers[i].MassCap)
                    return MassSustainSpeedTiers[i].Speed;
            }
            return MinSustainSpeed;
        }

        public static float GetClosingSpeedForPair(float mA, float mB)
        {
            float a = GetClosingSpeed(mA);
            float b = GetClosingSpeed(mB);
            return MassTierPairRule == 1 ? MathF.Max(a, b) : MathF.Min(a, b);
        }

        public static float GetSustainSpeedForPair(float mA, float mB)
        {
            float a = GetSustainSpeed(mA);
            float b = GetSustainSpeed(mB);
            return MassTierPairRule == 1 ? MathF.Max(a, b) : MathF.Min(a, b);
        }

        public static float GetImpulseMultiplier(float mass)
        {
            if (!ImpulseReductionTableEnabled) return 1f;
            var tiers = ImpulseReductionTiers;
            if (tiers.Length == 0) return 1f;
            if (tiers.Length == 1 || mass <= tiers[0].MassCap)
                return tiers[0].Multiplier;
            int last = tiers.Length - 1;
            if (mass >= tiers[last].MassCap)
                return tiers[last].Multiplier;

            for (int i = 1; i <= last; i++)
            {
                if (mass <= tiers[i].MassCap)
                {
                    float lowMass = tiers[i - 1].MassCap;
                    float highMass = tiers[i].MassCap;
                    float lowMul = tiers[i - 1].Multiplier;
                    float highMul = tiers[i].Multiplier;
                    float span = highMass - lowMass;
                    if (span <= 0f) return highMul;
                    float t = (mass - lowMass) / span;
                    return lowMul + (highMul - lowMul) * t;
                }
            }
            return 1f;
        }

        private static bool TryAssign(string key, string val)
        {
            switch (key)
            {
                case "MinClosingSpeed":
                    if (TryParseFloat(val, out float mcs)) { MinClosingSpeed = mcs; return true; }
                    break;
                case "MinSustainSpeed":
                    if (TryParseFloat(val, out float mss)) { MinSustainSpeed = mss; return true; }
                    break;
                case "PenFloor":
                    if (TryParseFloat(val, out float pf)) { PenFloor = pf; return true; }
                    break;
                case "HpToEnergy":
                    if (TryParseFloat(val, out float h2e)) { HpToEnergy = h2e; return true; }
                    break;
                case "LinearMultiplier":
                    if (TryParseFloat(val, out float linm))
                    {
                        LinearMultiplier = linm < 0f ? 0f : linm;
                        return true;
                    }
                    break;
                case "RotationMultiplier":
                    if (TryParseFloat(val, out float rotm))
                    {
                        RotationMultiplier = rotm < 0f ? 0f : rotm;
                        return true;
                    }
                    break;
                case "PushbackFormula":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pbf))
                    {
                        if (pbf < 0) pbf = 0;
                        if (pbf > 1) pbf = 1;
                        PushbackFormula = pbf;
                        return true;
                    }
                    break;
                case "DebugLog":
                    if (TryParseBool(val, out bool dl)) { DebugLog = dl; return true; }
                    break;
                case "DeadPartGate":
                    if (TryParseBool(val, out bool dpg)) { DeadPartGate = dpg; return true; }
                    break;
                case "RamDamageSelf":
                    if (TryParseBool(val, out bool rdSelf)) { RamDamageSelf = rdSelf; return true; }
                    break;
                case "RamDamageNeutral":
                    if (TryParseBool(val, out bool rdNeu)) { RamDamageNeutral = rdNeu; return true; }
                    break;
                case "RamDamageAllies":
                    if (TryParseBool(val, out bool rdAlly)) { RamDamageAllies = rdAlly; return true; }
                    break;
                case "RamDamageAsteroids":
                    if (TryParseBool(val, out bool rdAst)) { RamDamageAsteroids = rdAst; return true; }
                    break;
                case "RamDamageJunk":
                    if (TryParseBool(val, out bool rdJunk)) { RamDamageJunk = rdJunk; return true; }
                    break;
                case "RamDamageEnemies":
                    if (TryParseBool(val, out bool rdEnemy)) { RamDamageEnemies = rdEnemy; return true; }
                    break;
                case "UseRamDamageGates":
                case "RespectFriendlyFire":
                    if (TryParseBool(val, out bool urg)) { UseRamDamageGates = urg; return true; }
                    break;
                case "SustainGraceTicks":
                    if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long sgt))
                    {
                        SustainGraceTicks = sgt < 0 ? 0 : sgt;
                        return true;
                    }
                    break;
                case "StallGraceTicks":
                    if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long sgtStall))
                    {
                        StallGraceTicks = sgtStall < 0 ? 0 : sgtStall;
                        return true;
                    }
                    break;
                case "PerPartSuppression":
                    if (TryParseBool(val, out bool pps)) { PerPartSuppression = pps; return true; }
                    break;
                case "PenPriority":
                    if (TryParseBool(val, out bool penP)) { PenPriority = penP; return true; }
                    break;
                case "SlowdownHeadOnFactor":
                    if (TryParseFloat(val, out float shof))
                    {
                        SlowdownHeadOnFactor = shof < 0f ? 0f : (shof > 1f ? 1f : shof);
                        return true;
                    }
                    break;
                case "MassClosingSpeedTableEnabled":
                    if (TryParseBool(val, out bool mctEn)) { MassClosingSpeedTableEnabled = mctEn; return true; }
                    break;
                case "MassClosingSpeedTiers":
                    if (TryParseTierTable(val, out var mct))
                    {
                        MassClosingSpeedTiers = mct;
                        return true;
                    }
                    break;
                case "MassSustainSpeedTableEnabled":
                    if (TryParseBool(val, out bool mstEn)) { MassSustainSpeedTableEnabled = mstEn; return true; }
                    break;
                case "MassSustainSpeedTiers":
                    if (TryParseTierTable(val, out var mst))
                    {
                        MassSustainSpeedTiers = mst;
                        return true;
                    }
                    break;
                case "MassTierPairRule":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mtpr))
                    {
                        if (mtpr < 0) mtpr = 0;
                        if (mtpr > 1) mtpr = 1;
                        MassTierPairRule = mtpr;
                        return true;
                    }
                    break;
                case "ImpulseReductionTableEnabled":
                    if (TryParseBool(val, out bool irtEn)) { ImpulseReductionTableEnabled = irtEn; return true; }
                    break;
                case "ImpulseReductionTiers":
                    if (TryParseTierTable(val, out var irt))
                    {
                        ImpulseReductionTiers = irt;
                        return true;
                    }
                    break;
                case "StallEnabled":
                    if (TryParseBool(val, out bool se)) { StallEnabled = se; return true; }
                    break;
                case "MaxPhaseLayers":
                    if (TryParseFloat(val, out float mpl))
                    {
                        MaxPhaseLayers = mpl < 1f ? 1f : mpl;
                        return true;
                    }
                    break;
                case "StallDisableSpeed":
                    if (TryParseFloat(val, out float sds))
                    {
                        StallDisableSpeed = sds < 0f ? 0f : sds;
                        return true;
                    }
                    break;
                case "DestroyRatePerTick":
                    if (TryParseFloat(val, out float drpt))
                    {
                        DestroyRatePerTick = drpt < 0f ? 0f : drpt;
                        return true;
                    }
                    break;
                case "StallDisableAboveMass":
                    if (TryParseFloat(val, out float sdam))
                    {
                        StallDisableAboveMass = sdam < 0f ? 0f : sdam;
                        return true;
                    }
                    break;
                default:
                    Log.Info("Config: unknown key '" + key + "' ignored.");
                    return false;
            }
            Log.Info("Config: failed to parse value '" + val + "' for key '" + key + "', ignored.");
            return false;
        }

        private static bool TryParseTierTable(string val, out (float MassCap, float Speed)[] result)
        {
            if (string.IsNullOrWhiteSpace(val))
            {
                result = Array.Empty<(float, float)>();
                return true;
            }
            string[] parts = val.Split(',');
            var tiers = new List<(float MassCap, float Speed)>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                string trimmed = parts[i].Trim();
                if (trimmed.Length == 0) continue;
                int colon = trimmed.IndexOf(':');
                if (colon <= 0) { result = null; return false; }
                string capStr = trimmed.Substring(0, colon).Trim();
                string spStr = trimmed.Substring(colon + 1).Trim();
                if (!TryParseFloat(capStr, out float cap)) { result = null; return false; }
                if (!TryParseFloat(spStr, out float sp))   { result = null; return false; }
                tiers.Add((cap, sp));
            }
            tiers.Sort((a, b) => a.MassCap.CompareTo(b.MassCap));
            result = tiers.ToArray();
            return true;
        }

        private static string DescribeTiers((float MassCap, float Speed)[] tiers)
        {
            if (tiers == null || tiers.Length == 0) return "[]";
            var sb = new System.Text.StringBuilder();
            sb.Append('[');
            for (int i = 0; i < tiers.Length; i++)
            {
                if (i > 0) sb.Append(',');
                // "0.####" not "0.#": the impulse table uses values like 0.05,
                // which "0.#" rounds to 0.1 in the dump and misleads bug reports.
                sb.Append(tiers[i].MassCap.ToString("0.####", CultureInfo.InvariantCulture));
                sb.Append(':');
                sb.Append(tiers[i].Speed.ToString("0.####", CultureInfo.InvariantCulture));
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static bool TryParseFloat(string s, out float result)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

        private static bool TryParseBool(string s, out bool result)
        {
            switch (s.Trim().ToLowerInvariant())
            {
                case "true": case "1": case "yes": case "on":  result = true;  return true;
                case "false": case "0": case "no": case "off": result = false; return true;
                default: result = false; return false;
            }
        }
    }
}
