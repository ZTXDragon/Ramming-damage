using System;
using System.Threading;
using Cosmoteer.Ships.Parts;
using Halfling.Physics2D.Dynamics;
using Halfling.Physics2D.Dynamics.Contacts;

using SVec = System.Numerics.Vector2;

namespace ZTX.RammingDamage
{
    // Diagnostic helpers for the ramming pipeline. All cold-path (gated on
    // Config.DebugLog by callers).
    internal static partial class RammingHandler
    {
        private static void LogBelowThreshold(Contact contact, Body bA, Body bB,
            SVec normal, float vNormal,
            bool wasSuppressed, int refsA, int refsB, int rammingTotal)
        {
            if (!wasSuppressed) return;

            int seq = Interlocked.Increment(ref s_logSeq);
            int contactId = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(contact);

            SVec posA = default, posB = default, velA = default, velB = default;
            float massA = 0f, massB = 0f;
            try { posA = bA.Position;        } catch { }
            try { posB = bB.Position;        } catch { }
            try { velA = bA.LinearVelocity;  } catch { }
            try { velB = bB.LinearVelocity;  } catch { }
            try { massA = bA.Mass;           } catch { }
            try { massB = bB.Mass;           } catch { }

            Log.Info($"LATERAL_SUPPRESSED seq={seq} c=0x{contactId:X8} "
                     + $"v={vNormal:F2} normal=({normal.X:F3},{normal.Y:F3})");
            Log.Info($"   bA pos=({posA.X:F2},{posA.Y:F2}) vel=({velA.X:F2},{velA.Y:F2}) "
                     + $"mass={massA:F0} ramRefs={refsA}");
            Log.Info($"   bB pos=({posB.X:F2},{posB.Y:F2}) vel=({velB.X:F2},{velB.Y:F2}) "
                     + $"mass={massB:F0} ramRefs={refsB} rammingTotal={rammingTotal}");
        }

        private static int SafeHealth(Part part)
        {
            try { return part?.Health ?? 0; }
            catch { return -1; }
        }

        internal static void UpdateTickPairStats(Body bA, Body bB,
            Part partA, Part partB, int appliedA, int appliedB)
        {
            try
            {
                int hashA = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(bA);
                int hashB = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(bB);
                Body first, second;
                if (hashA < hashB) { first = bA; second = bB; }
                else               { first = bB; second = bA; }
                bool aIsFirst = (first == bA);

                var key = (first, second);
                lock (s_tickStatsLock)
                {
                    if (!s_tickPairStats.TryGetValue(key, out var stats))
                    {
                        stats = new TickPairStats { BodyA = first, BodyB = second };
                        try
                        {
                            stats.SpeedA = first.LinearVelocity.Length();
                            stats.SpeedB = second.LinearVelocity.Length();
                            stats.PosA = first.Position;
                            stats.PosB = second.Position;
                        }
                        catch { }
                        s_tickPairStats[key] = stats;
                    }
                    stats.HitCount++;

                    int firstApplied  = aIsFirst ? appliedA : appliedB;
                    int secondApplied = aIsFirst ? appliedB : appliedA;
                    Part firstPart    = aIsFirst ? partA    : partB;
                    Part secondPart   = aIsFirst ? partB    : partA;

                    if (firstApplied > 0 && firstPart != null)
                    {
                        int phash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(firstPart);
                        stats.DamagedPartsA.Add(phash);
                        stats.DmgBToA += firstApplied;
                        if (SafeHealth(firstPart) <= 0) stats.KilledPartsA.Add(phash);
                    }
                    if (secondApplied > 0 && secondPart != null)
                    {
                        int phash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(secondPart);
                        stats.DamagedPartsB.Add(phash);
                        stats.DmgAToB += secondApplied;
                        if (SafeHealth(secondPart) <= 0) stats.KilledPartsB.Add(phash);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("UpdateTickPairStats", ex);
            }
        }

        internal static void EmitTickPairSummariesAndClear()
        {
            lock (s_tickStatsLock)
            {
                if (s_tickPairStats.Count == 0) return;
                foreach (var kv in s_tickPairStats)
                {
                    var s = kv.Value;
                    try
                    {
                        float advanceTiles = 0f;
                        try
                        {
                            float relSpeed = (s.BodyA.LinearVelocity - s.BodyB.LinearVelocity).Length();
                            advanceTiles = relSpeed * s_lastStepDt;
                        }
                        catch { }

                        int dmgCellsA = s.DamagedPartsA.Count;
                        int dmgCellsB = s.DamagedPartsB.Count;
                        int killCellsA = s.KilledPartsA.Count;
                        int killCellsB = s.KilledPartsB.Count;
                        int totalKilled = killCellsA + killCellsB;
                        float ratio = advanceTiles > 0.001f ? totalKilled / advanceTiles : 0f;

                        int seq = NextLogSeq();
                        int hashA = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(s.BodyA);
                        int hashB = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(s.BodyB);
                        Log.Info($"TICK_PAIR tick={s_currentTick} seq={seq} "
                                 + $"A=0x{hashA:X8}(m={s.BodyA.Mass:F0}) "
                                 + $"B=0x{hashB:X8}(m={s.BodyB.Mass:F0})");
                        Log.Info($"   hits={s.HitCount} "
                                 + $"dmgAToB={s.DmgAToB} dmgBToA={s.DmgBToA} "
                                 + $"cellsDmg(A={dmgCellsA},B={dmgCellsB}) "
                                 + $"cellsKilled(A={killCellsA},B={killCellsB})");
                        bool stallA = s_stallStates.TryGetValue(s.BodyA, out var stA) && stA.IsStalling;
                        bool stallB = s_stallStates.TryGetValue(s.BodyB, out var stB) && stB.IsStalling;
                        Log.Info($"   speedA={s.SpeedA:F2} speedB={s.SpeedB:F2} "
                                 + $"dt={s_lastStepDt:F4} advance={advanceTiles:F2}t "
                                 + $"killRate={ratio:F2} cells/tile "
                                 + $"posA=({s.PosA.X:F1},{s.PosA.Y:F1}) "
                                 + $"posB=({s.PosB.X:F1},{s.PosB.Y:F1}) "
                                 + $"stallA={(stallA ? "Y" : "N")} stallB={(stallB ? "Y" : "N")}");
                    }
                    catch (Exception ex)
                    {
                        Log.Exception("EmitTickPairSummary (per-pair)", ex);
                    }
                }
                s_tickPairStats.Clear();
            }
        }

        private static (int total, int otherShip, int suppressed) SummarizeContacts(Body body)
        {
            int total = 0, otherShip = 0, suppressed = 0;
            if (body == null) return (0, 0, 0);
            try
            {
                var edge = body.ContactList;
                int guard = 0;
                while (edge != null && guard < 256)
                {
                    total++;
                    var c = edge.Contact;
                    if (c != null && !c.Enabled) suppressed++;
                    var other = edge.Other;
                    if (other != null && ShipFilter.IsShipBody(other)) otherShip++;
                    edge = edge.Next;
                    guard++;
                }
            }
            catch { /* destruction can null fields mid-walk */ }
            return (total, otherShip, suppressed);
        }
    }
}
