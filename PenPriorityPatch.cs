using System;
using HarmonyLib;
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
    // Deterministic pen-priority pre-scan: seeds s_maxPenPerBody before any
    // PreSolve fires, so per-side shadowing isn't order-dependent under
    // CollideMultiCore.
    [HarmonyPatch(typeof(ContactManager), "Collide")]
    public static class ContactManagerCollidePrefixPatch
    {
        static void Prefix(ContactManager __instance)
        {
            if (!Config.PenPriority) return;
            if (__instance == null) return;

            bool anyRamming = RammingHandler.HasAnyRammingBody();

            try
            {
                lock (RammingHandler.s_maxPenLock)
                {
                    RammingHandler.s_maxPenPerBody.Clear();

                    if (!anyRamming) return;

                    Contact start = __instance.ContactList;
                    if (start == null) return;
                    Contact next = start.Next;
                    int guard = 0;
                    while (next != null && next != start && guard < 8192)
                    {
                        try { TryUpdateMaxPenFromContact(next); }
                        catch { /* contact disposed mid-walk — skip */ }

                        Contact n;
                        try { n = next.Next; }
                        catch { break; }   // list mutated under us — bail
                        next = n;
                        guard++;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("PenPriority pre-scan", ex);
            }
        }

        private static void TryUpdateMaxPenFromContact(Contact c)
        {
            var fA = c.FixtureA;
            var fB = c.FixtureB;
            if (fA == null || fB == null) return;
            var bA = fA.Body;
            var bB = fB.Body;
            if (bA == null || bB == null) return;

            if (!RammingHandler.IsRammingBody(bA)
                && !RammingHandler.IsRammingBody(bB))
                return;

            if (!ShipFilter.TryGetShipPair(bA, bB, out var shipA, out var shipB))
                return;

            if (c.Manifold.PointCount == 0) return;

            SVec normal;
            FixedArray2<SVec> worldPoints;
            try { c.GetWorldManifold(out normal, out worldPoints); }
            catch { return; }
            SVec worldPoint = worldPoints[0];

            Ship sA = (Ship)shipA;
            Ship sB = (Ship)shipB;

            HVec localA, localB;
            try
            {
                localA = (HVec)bA.GetLocalPoint(worldPoint);
                localB = (HVec)bB.GetLocalPoint(worldPoint);
            }
            catch { return; }

            Part partA, partB;
            BaseCollider hitColliderA, hitColliderB;
            try
            {
                partA = sA.Physics.GetLocalContactPart(fA, localA, out hitColliderA);
                partB = sB.Physics.GetLocalContactPart(fB, localB, out hitColliderB);
            }
            catch { return; }

            if (partA == null || partB == null) return;

            try { if (partA.Health <= 0 || partB.Health <= 0) return; }
            catch { return; }

            float penA, penB;
            try
            {
                penA = partA.GetInitialPenetrationResistance(hitColliderA);
                penB = partB.GetInitialPenetrationResistance(hitColliderB);
            }
            catch { return; }

            if (RammingHandler.s_maxPenPerBody.TryGetValue(bA, out float prevA))
                RammingHandler.s_maxPenPerBody[bA] = MathF.Max(prevA, penA);
            else
                RammingHandler.s_maxPenPerBody[bA] = penA;

            if (RammingHandler.s_maxPenPerBody.TryGetValue(bB, out float prevB))
                RammingHandler.s_maxPenPerBody[bB] = MathF.Max(prevB, penB);
            else
                RammingHandler.s_maxPenPerBody[bB] = penB;
        }
    }
}
