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

namespace ClientPlugin
{
    [HarmonyPatch]
    public class PhasingGamePatches
    {
        readonly static FieldInfo _missileCollisionPointInfo = typeof(MyMissile).GetField("m_collisionPoint", BindingFlags.Instance | BindingFlags.NonPublic);
        readonly static FieldInfo _missileCollisionNormalInfo = typeof(MyMissile).GetField("m_collisionNormal", BindingFlags.Instance | BindingFlags.NonPublic);
        readonly static FieldInfo _missileCollidedEntityInfo = typeof(MyMissile).GetField("m_collidedEntity", BindingFlags.Instance | BindingFlags.NonPublic);


        [HarmonyPostfix]
        [HarmonyPatch(typeof(MyMissile), "OnContactStart")]
        public static void Postfix_MyMissile_OnContactStart(MyMissile __instance, ref MyPhysics.MyContactPointEvent value)
        {
            if (Plugin.Instance.Config.Enabled)
            {
                MyEntity collidedEntity = (MyEntity)_missileCollidedEntityInfo.GetValue(__instance);

                // Only perform patch for hits on grids
                if (collidedEntity != null && collidedEntity is MyCubeGrid grid)
                {
                    // Obtain missile position in the current tick and the collision point given by
                    Vector3D missilePosition = __instance.PositionComp.GetPosition();
                    Vector3D detectedCollisionPoint = (Vector3D)_missileCollisionPointInfo.GetValue(__instance);

                    LineD collisionRay = new LineD(missilePosition, detectedCollisionPoint);


                    MyIntersectionResultLineTriangleEx? intersectedTriangle;
                    if (grid.GetIntersectionWithLine(ref collisionRay, out intersectedTriangle))
                    {
                        _missileCollisionPointInfo.SetValue(__instance, intersectedTriangle.Value.IntersectionPointInWorldSpace);
                        _missileCollisionNormalInfo.SetValue(__instance, intersectedTriangle.Value.NormalInWorldSpace);
                    }
                }
            }
        }
    }
}
