using Sandbox.Engine.Physics;
using Sandbox.Game.Weapons;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using VRage.Game.Entity;
using System.Reflection;
using Sandbox.Game.Entities;
using VRageMath;
using VRage.Game.Models;
using TorchPlugin;
using Sandbox;

namespace ClientPlugin
{
    [HarmonyPatch]
    public class PhasingGamePatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MyMissile), "OnContactStart")]
        public static void Postfix_MyMissile_OnContactStart(MyMissile __instance, ref MyPhysics.MyContactPointEvent value)
        {
            if (Plugin.Instance.Config.Phasing)
            {
#if DEBUG
                Plugin.Instance.Log.Debug("OnContactStart patch triggered (prefix)");
#endif

                // Start the phasing fix for this task while the main thread completes this tick and gets back to this missile next tick
                MyPatchUtilities.InitiatePhasingFix(__instance);
            }
        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MyMissile), "HitEntity")]
        public static void Prefix_MyMissile_HitEntity(MyMissile __instance)
        {
            if (Plugin.Instance.Config.Phasing)
            {
#if DEBUG
                Plugin.Instance.Log.Debug("HitEntity patch triggered (prefix)");
#endif

                // Main thread has reached the point where it executes this missile's hit logic. Complete the fix before continuing.
                MyPatchUtilities.CompletePhasingFix(__instance);
            }
        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MyMissile), "MarkForExplosion")]
        public static void Postfix_MyMissile_MarkForExplosion(MyMissile __instance, bool force)
        {
            if (Plugin.Instance.Config.Phasing)
            {
#if DEBUG
                Plugin.Instance.Log.Debug("MarkForExplosion patch triggered (postfix)");
#endif

                // The MarkForExplosion method has finished. Regardless of if it tried to hit an entity, clean up all references to the phasing fix.
                MyPatchUtilities.ClearPhasingFix(__instance);
            }
        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MyMissile), "HitGrid")]
        public static bool Prefix_MyMissile_HitGrid(MyMissile __instance, MyCubeGrid grid, Vector3D nextPosition)
        {
            if (Plugin.Instance.Config.Damage)
            {
#if DEBUG
                Plugin.Instance.Log.Debug("HitGrid patch triggered (prefix)");
#endif

                // Run replacement single-grid damage application
                MyPatchUtilities.HitSingleGridWithMissile(__instance, grid, nextPosition);

                // Stop vanilla damage application from executing
                return false;
            }
            return true;
        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MyMissile), "HitMultipleGrids")]
        public static bool Prefix_MyMissile_HitMultipleGrids(MyMissile __instance, List<MyLineSegmentOverlapResult<MyEntity>> hits, Vector3D nextPosition)
        {
            if (Plugin.Instance.Config.Damage)
            {
#if DEBUG
                Plugin.Instance.Log.Debug("HitMultipleGrids patch triggered (prefix)");
#endif

                // Run replacement multi-grid damage application
                MyPatchUtilities.HitMultipleGridsWithMissile(__instance, hits, nextPosition);

                // Stop vanilla damage application from executing
                return false;
            }
            return true;
        }
    }
}
