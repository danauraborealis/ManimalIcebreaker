using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // generates NavMeshDoorLink objects from the scene's Door components.
    //
    // ground truth (factory, all 68 baked links joined to their doors):
    //  - Open1 == Close1 == the hinge == door.transform.position (median error 0.01m)
    //  - Open2 = leaf end at SHUT pose (the doorway plane); Close2_Normal = leaf end at
    //    OPEN pose — the two planes are separated by exactly door.OpenAngle (max 1 deg off)
    //  - Close2_Breach = open pose swung back ~16 deg (0 on some door types)
    //  - all five points at hinge height; BottomY/FarestClosePoint have zero consumers
    //  - ShallTryInteract false on every baked link (carver system dormant on factory)
    //  - carvers must still EXIST: BotDoorsController.method_0 dereferences them with no
    //    null guard — so we call TryCreateCrave, the shipped never-called-in-game factory
    //  - SetDoor/Init wiring happens automatically in BotDoorsController.RefreshData
    //    (matches our DoorId string), as long as links exist before BotsController.Init:170
    internal static class DoorScanner
    {
        private const float BreachBackswingDeg = 16f;

        public static List<NavMeshDoorLink> GenerateAndSwap()
        {
            // remove baked links BEFORE the harvest points — AIVoxelesData.RestoreData and
            // BotDoorsController.RefreshData both FindObjectsOfType later in this same load
            int removed = 0;
            foreach (var baked in Object.FindObjectsOfType<NavMeshDoorLink>())
            {
                Object.DestroyImmediate(baked.gameObject);
                removed++;
            }

            var links = new List<NavMeshDoorLink>();
            int id = 1;
            int skipped = 0;
            foreach (var door in Object.FindObjectsOfType<Door>())
            {
                // baked rule (verified: all 9 unlinked factory doors): no links on doors
                // that start Locked — bots cant open them, a link just invites rattling
                if (door == null || !door.Operatable || door.DoorState == EDoorState.Locked)
                {
                    skipped++;
                    continue;
                }
                var t = door.transform;
                var hinge = t.position;
                // the game's own conventions: swing axis resolves against the PARENT
                // transform (GetDoorRotation does exactly this), leaf direction against the
                // door's own transform, which rotates with the leaf
                var axis = WorldInteractiveObject.GetRotationAxis(door.DoorAxis, t.parent);
                var leafNow = WorldInteractiveObject.GetRotationAxis(door.DoorForward, t);
                if (axis.sqrMagnitude < 0.5f || leafNow.sqrMagnitude < 0.5f)
                {
                    skipped++;
                    continue;
                }
                float curAngle = door.GetAngle(door.DoorState);
                float width = LeafWidth(door, leafNow);
                float swingSign = Mathf.Sign(door.OpenAngle - door.CloseAngle);

                var shutDir = LeafDirAt(door.GetAngle(EDoorState.Shut), curAngle, axis, leafNow);
                var openDir = LeafDirAt(door.OpenAngle, curAngle, axis, leafNow);
                var breachDir = LeafDirAt(door.OpenAngle - swingSign * BreachBackswingDeg, curAngle, axis, leafNow);
                if (shutDir == Vector3.zero || openDir == Vector3.zero)
                {
                    skipped++;
                    continue;
                }

                var go = new GameObject($"GenDoorLink_{id}");
                go.transform.position = hinge;
                var link = go.AddComponent<NavMeshDoorLink>();
                link.Id = id;
                link.DoorId = door.Id;
                link.Open1 = hinge;
                link.Close1 = hinge;
                link.Open2 = hinge + shutDir * width;
                link.Close2_Normal = hinge + openDir * width;
                link.Close2_Breach = hinge + breachDir * width;
                link.ShallTryInteract = false;
                // Awake already ran at AddComponent with zeroed fields — recompute the mids
                link.MidOpen = (link.Open1 + link.Open2) / 2f;
                link.MidClose = (link.Close1 + link.Close2_Normal) / 2f;
                link.TryCreateCrave();
                links.Add(link);
                id++;
            }
            Plugin.Log.LogInfo($"door scan: {links.Count} links generated ({removed} baked removed, {skipped} doors skipped)");
            return links;
        }

        // leaf world direction with the door rotated to `angle`, starting from its current
        // pose at `curAngle` — horizontal, like every baked link
        private static Vector3 LeafDirAt(float angle, float curAngle, Vector3 axis, Vector3 leafNow)
        {
            var d = Quaternion.AngleAxis(angle - curAngle, axis) * leafNow;
            d.y = 0f;
            return d.sqrMagnitude > 0.0001f ? d.normalized : Vector3.zero;
        }

        // leaf length from child colliders projected onto the leaf direction (world AABB
        // projection — good enough; baked factory is a uniform 1.08m)
        private static float LeafWidth(Door door, Vector3 leafDir)
        {
            float best = 0f;
            foreach (var col in door.GetComponentsInChildren<Collider>())
            {
                if (col == null || col.isTrigger)
                    continue;
                var e = col.bounds.extents;
                float proj = Mathf.Abs(e.x * leafDir.x) + Mathf.Abs(e.y * leafDir.y) + Mathf.Abs(e.z * leafDir.z);
                best = Mathf.Max(best, proj * 2f);
            }
            return best > 0.3f ? Mathf.Clamp(best, 0.5f, 2.5f) : 1.08f;
        }
    }
}
