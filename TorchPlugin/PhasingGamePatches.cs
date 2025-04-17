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
                MyPatchUtilities.InitiatePhasingFix(__instance);
            }
        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MyMissile), "HitEntity")]
        public static void Prefix_MyMissile_HitEntity(MyMissile __instance)
        {
            if (Plugin.Instance.Config.Phasing)
            {
                MyPatchUtilities.CompletePhasingFix(__instance);
            }
        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MyMissile), "HitGrid")]
        public static bool Prefix_MyMissile_HitGrid(MyMissile __instance, MyCubeGrid grid, Vector3D nextPosition)
        {
            if (Plugin.Instance.Config.Damage)
            {
                // Replace single-grid damage application with our own

                MyPatchUtilities.HitSingleGridWithMissile(__instance, grid, nextPosition);
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
                // Replace multi-grid damage application with our own

                MyPatchUtilities.HitMultipleGridsWithMissile(__instance, hits, nextPosition);
                return false;
            }
            return true;
        }
    }
}
