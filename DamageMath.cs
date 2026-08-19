using System;
using SVec = System.Numerics.Vector2;

namespace ZTX.RammingDamage
{
    // Pure-math helpers (no game types) so the formulas can be unit-tested
    // from RunSelfTest() at startup.
    internal static class DamageMath
    {

        public readonly struct Share
        {
            public readonly double DamageToA;
            public readonly double DamageToB;
            public Share(double a, double b) { DamageToA = a; DamageToB = b; }
        }

        public static Share ComputeDamageShare(
            int maxHpA, int maxHpB,
            double rawPenA, double rawPenB,
            double penFloor)
        {
            double penA = rawPenA > penFloor ? rawPenA : penFloor;
            double penB = rawPenB > penFloor ? rawPenB : penFloor;
            double maxPen = penA > penB ? penA : penB;
            double dmgA = maxHpA * (penB / maxPen);
            double dmgB = maxHpB * (penA / maxPen);
            return new Share(dmgA, dmgB);
        }

        public static int ComputeN(float speed, float dt, float maxLayers,
                                   float destroyRatePerTick, float disableSpeed)
        {
            if (speed < disableSpeed) return -1;

            double advance = (double)speed * (double)dt;
            double phasePerTick = advance - (double)destroyRatePerTick;
            if (phasePerTick <= 0.0) return -1;

            double nfRaw = (double)maxLayers / phasePerTick;

            const double snapEpsilon = 1e-5;
            double nearestInt = Math.Round(nfRaw);
            double nf = (Math.Abs(nfRaw - nearestInt) < snapEpsilon)
                      ? nearestInt
                      : Math.Floor(nfRaw);

            if (nf > 1000.0) return -1;
            int n = (int)nf;
            return n < 1 ? 1 : n;
        }

        public readonly struct PushbackPair
        {
            public readonly SVec ImpulseA;
            public readonly SVec ImpulseB;
            public PushbackPair(SVec ia, SVec ib) { ImpulseA = ia; ImpulseB = ib; }
        }

        public enum PushbackMode
        {
            Asymmetric = 0,
            NewtonSum  = 1,
        }

        public static PushbackPair ComputePushback(
            int appliedA, int appliedB,
            float hpToEnergy, float vRelFactor,
            SVec normal,
            PushbackMode mode = PushbackMode.NewtonSum)
        {
            float scale = hpToEnergy * vRelFactor;
            float magA, magB;
            switch (mode)
            {
                case PushbackMode.NewtonSum:
                    float half = scale * (appliedA + appliedB) * 0.5f;
                    magA = half;
                    magB = half;
                    break;
                case PushbackMode.Asymmetric:
                default:
                    magA = scale * appliedB;
                    magB = scale * appliedA;
                    break;
            }
            return new PushbackPair(
                new SVec(-normal.X * magA, -normal.Y * magA),
                new SVec(+normal.X * magB, +normal.Y * magB));
        }

        public static void RunSelfTest()
        {
            int passed = 0, failed = 0;

            {
                var s = ComputeDamageShare(8000, 1000, 7, 1, 0.5);
                AssertNear("armor->corridor dmgA", s.DamageToA, 1142.857, 0.01, ref passed, ref failed);
                AssertNear("armor->corridor dmgB", s.DamageToB, 1000.0, 0.01, ref passed, ref failed);
            }
            {
                var s = ComputeDamageShare(1000, 8000, 1, 7, 0.5);
                AssertNear("corridor->armor dmgA", s.DamageToA, 1000.0, 0.01, ref passed, ref failed);
                AssertNear("corridor->armor dmgB", s.DamageToB, 1142.857, 0.01, ref passed, ref failed);
            }
            {
                var s = ComputeDamageShare(8000, 8000, 7, 7, 0.5);
                AssertNear("armor=armor dmgA", s.DamageToA, 8000.0, 0.01, ref passed, ref failed);
                AssertNear("armor=armor dmgB", s.DamageToB, 8000.0, 0.01, ref passed, ref failed);
            }
            {
                var s = ComputeDamageShare(200, 200, 0, 0, 0.5);
                AssertNear("struct=struct dmgA", s.DamageToA, 200.0, 0.01, ref passed, ref failed);
                AssertNear("struct=struct dmgB", s.DamageToB, 200.0, 0.01, ref passed, ref failed);
            }
            {
                var s = ComputeDamageShare(10, 1000, 10, 1, 0.5);
                AssertNear("spike dmg/HIT", s.DamageToA, 1.0, 0.001, ref passed, ref failed);
                AssertNear("spike->corridor dmg", s.DamageToB, 1000.0, 0.01, ref passed, ref failed);
            }
            {
                var s = ComputeDamageShare(1000, 1000, 4, 3, 0.5);
                AssertNear("pen-4 vs pen-3 high", s.DamageToA, 750.0, 0.01, ref passed, ref failed);
                AssertNear("pen-4 vs pen-3 low", s.DamageToB, 1000.0, 0.01, ref passed, ref failed);
            }

            {
                var p = ComputePushback(1000, 1000, 0.25f, 1f, new SVec(1f, 0f), PushbackMode.Asymmetric);
                AssertVecNear("Asym symmetric A", p.ImpulseA, new SVec(-250f, 0f), 0.01f, ref passed, ref failed);
                AssertVecNear("Asym symmetric B", p.ImpulseB, new SVec(+250f, 0f), 0.01f, ref passed, ref failed);
            }
            {
                var p = ComputePushback(1143, 1000, 0.25f, 1f, new SVec(1f, 0f), PushbackMode.Asymmetric);
                AssertVecNear("Asym asymmetric A", p.ImpulseA, new SVec(-250f, 0f),    0.01f, ref passed, ref failed);
                AssertVecNear("Asym asymmetric B", p.ImpulseB, new SVec(+285.75f, 0f), 0.01f, ref passed, ref failed);
            }
            {
                var p = ComputePushback(1000, 1000, 0.25f, 0f, new SVec(1f, 0f), PushbackMode.Asymmetric);
                AssertVecNear("Asym vRelFactor=0 A", p.ImpulseA, SVec.Zero, 0.001f, ref passed, ref failed);
                AssertVecNear("Asym vRelFactor=0 B", p.ImpulseB, SVec.Zero, 0.001f, ref passed, ref failed);
            }
            {
                var p = ComputePushback(0, 0, 0.25f, 1f, new SVec(1f, 0f), PushbackMode.Asymmetric);
                AssertVecNear("Asym applied=0 A", p.ImpulseA, SVec.Zero, 0.001f, ref passed, ref failed);
                AssertVecNear("Asym applied=0 B", p.ImpulseB, SVec.Zero, 0.001f, ref passed, ref failed);
            }
            {
                var p = ComputePushback(0, 1000, 0.25f, 1f, new SVec(1f, 0f), PushbackMode.Asymmetric);
                AssertVecNear("Asym one-sided A", p.ImpulseA, new SVec(-250f, 0f), 0.01f, ref passed, ref failed);
                AssertVecNear("Asym one-sided B", p.ImpulseB, SVec.Zero,           0.001f, ref passed, ref failed);
            }

            {
                var p = ComputePushback(1000, 1000, 0.25f, 1f, new SVec(1f, 0f), PushbackMode.NewtonSum);
                AssertVecNear("N3 symmetric A", p.ImpulseA, new SVec(-250f, 0f), 0.01f, ref passed, ref failed);
                AssertVecNear("N3 symmetric B", p.ImpulseB, new SVec(+250f, 0f), 0.01f, ref passed, ref failed);
            }
            {
                var p = ComputePushback(1143, 1000, 0.25f, 1f, new SVec(1f, 0f), PushbackMode.NewtonSum);
                AssertVecNear("N3 asymmetric A", p.ImpulseA, new SVec(-267.875f, 0f), 0.01f, ref passed, ref failed);
                AssertVecNear("N3 asymmetric B", p.ImpulseB, new SVec(+267.875f, 0f), 0.01f, ref passed, ref failed);
            }
            {
                var p = ComputePushback(1000, 1000, 0.25f, 0f, new SVec(1f, 0f), PushbackMode.NewtonSum);
                AssertVecNear("N3 vRelFactor=0 A", p.ImpulseA, SVec.Zero, 0.001f, ref passed, ref failed);
                AssertVecNear("N3 vRelFactor=0 B", p.ImpulseB, SVec.Zero, 0.001f, ref passed, ref failed);
            }
            {
                var p = ComputePushback(0, 0, 0.25f, 1f, new SVec(1f, 0f), PushbackMode.NewtonSum);
                AssertVecNear("N3 applied=0 A", p.ImpulseA, SVec.Zero, 0.001f, ref passed, ref failed);
                AssertVecNear("N3 applied=0 B", p.ImpulseB, SVec.Zero, 0.001f, ref passed, ref failed);
            }
            {
                var p = ComputePushback(0, 1000, 0.25f, 1f, new SVec(1f, 0f), PushbackMode.NewtonSum);
                AssertVecNear("N3 one-sided A", p.ImpulseA, new SVec(-125f, 0f), 0.01f, ref passed, ref failed);
                AssertVecNear("N3 one-sided B", p.ImpulseB, new SVec(+125f, 0f), 0.01f, ref passed, ref failed);
            }
            {
                var p = ComputePushback(500, 1000, 0.25f, 1f, new SVec(0.6f, 0.8f), PushbackMode.NewtonSum);
                AssertVecNear("N3 diagonal A", p.ImpulseA, new SVec(-112.5f, -150f), 0.01f, ref passed, ref failed);
                AssertVecNear("N3 diagonal B", p.ImpulseB, new SVec(+112.5f, +150f), 0.01f, ref passed, ref failed);
            }

            AssertEq("ComputeN 80m/s @ 30Hz",   ComputeN(80f, 1f/30f, 4f, 1f, 30f),   2, ref passed, ref failed);
            AssertEq("ComputeN 60m/s @ 30Hz",   ComputeN(60f, 1f/30f, 4f, 1f, 30f),   4, ref passed, ref failed);
            AssertEq("ComputeN 30m/s no stall", ComputeN(30f, 1f/30f, 4f, 1f, 30f),  -1, ref passed, ref failed);
            AssertEq("ComputeN below disable",  ComputeN(20f, 1f/30f, 4f, 1f, 30f),  -1, ref passed, ref failed);
            AssertEq("ComputeN 200m/s clamped", ComputeN(200f, 1f/30f, 4f, 1f, 30f),  1, ref passed, ref failed);
            AssertEq("ComputeN zero speed",     ComputeN(0f, 1f/30f, 4f, 1f, 30f),   -1, ref passed, ref failed);
            AssertEq("ComputeN 80m/s @ 60Hz",   ComputeN(80f, 1f/60f, 4f, 1f, 30f),  12, ref passed, ref failed);
            AssertEq("ComputeN destroyRate=0",  ComputeN(60f, 1f/30f, 4f, 0f, 30f),   2, ref passed, ref failed);
            AssertEq("ComputeN N=1 formula",    ComputeN(60f, 1f/30f, 1f, 1f, 30f),   1, ref passed, ref failed);
            AssertEq("ComputeN tiny capped",    ComputeN(30.001f, 1f/30f, 4f, 1f, 30f), -1, ref passed, ref failed);

            Log.Info($"DamageMath self-test: {passed} passed, {failed} failed.");
        }

        private static void AssertNear(string name, double actual, double expected,
                                       double tolerance, ref int passed, ref int failed)
        {
            if (Math.Abs(actual - expected) <= tolerance) passed++;
            else
            {
                failed++;
                Log.Error($"FAIL [{name}]: expected={expected:F4}, actual={actual:F4}");
            }
        }

        private static void AssertEq(string name, int actual, int expected,
                                     ref int passed, ref int failed)
        {
            if (actual == expected) passed++;
            else
            {
                failed++;
                Log.Error($"FAIL [{name}]: expected={expected}, actual={actual}");
            }
        }

        private static void AssertVecNear(string name, SVec actual, SVec expected,
                                          float tolerance, ref int passed, ref int failed)
        {
            float dx = actual.X - expected.X;
            float dy = actual.Y - expected.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist <= tolerance) passed++;
            else
            {
                failed++;
                Log.Error($"FAIL [{name}]: expected=({expected.X:F4},{expected.Y:F4}), "
                          + $"actual=({actual.X:F4},{actual.Y:F4}), dist={dist:F4}");
            }
        }
    }
}
