using System.Collections.Generic;
using UnityEngine;

namespace MinecraftRepo
{
    /// Keeps PhysGrabObjects that live in the inventory or inside item entities out of the world, the way
    /// R.E.P.O. hides equipped items: OverrideDeactivate parks them at physDisabledPosition, kinematic, no collisions.
    /// Only the host's call parks them, so in multiplayer the host keeps every stored object parked, including the
    /// ones in other players' inventories (it never releases an object it didn't place itself or get asked to).
    internal static class Storage
    {
        static readonly HashSet<PhysGrabObject> stored = new HashSet<PhysGrabObject>();

        public static bool IsStored(PhysGrabObject o) => stored.Contains(o);

        public static void Store(PhysGrabObject o)
        {
            if (!o) return;
            foreach (var g in o.playerGrabbing.ToArray())
                if (g) g.ReleaseObject(o.photonView ? o.photonView.ViewID : -1, 0.5f);
            stored.Add(o);
            o.OverrideIndestructible(1f);
            o.OverrideDeactivate(0.5f);
        }

        /// On a client this only lets go of it here; the host (which parks it) teleports it and gives it back to physics.
        public static void Release(PhysGrabObject o, Vector3 pos, Quaternion rot, Vector3 velocity)
        {
            if (!o) return;
            stored.Remove(o);
            RepoWeapons.Restore(o);
            if (!SemiFunc.IsMasterClientOrSingleplayer())
            {
                McNet.SendReleaseObject(o, pos, rot, velocity);
                return;
            }
            o.OverrideDeactivateReset();
            o.OverrideIndestructible(2f);
            if (o.impactDetector) o.impactDetector.ImpactDisable(1f);
            o.Teleport(pos, rot);
            if (o.rb)
            {
                o.rb.velocity = velocity;
                o.rb.angularVelocity = Vector3.zero;
            }
        }

        public static void Tick()
        {
            stored.RemoveWhere(o => o == null);
            foreach (var o in stored)
            {
                o.OverrideDeactivate(0.5f);
                o.OverrideIndestructible(0.5f);
            }
        }

        public static void Clear()
        {
            stored.Clear();
            RepoWeapons.Clear();
        }
    }
}
