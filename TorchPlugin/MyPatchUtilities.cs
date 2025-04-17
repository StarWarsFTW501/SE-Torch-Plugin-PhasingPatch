using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.Models;
using VRageMath;

namespace TorchPlugin
{
    internal class MyPatchUtilities
    {
        readonly static Dictionary<MyMissile, Task<(Vector3D, Vector3)?>> _collisionCorrectionTasks = new Dictionary<MyMissile, Task<(Vector3D, Vector3)?>>();

        readonly static FieldInfo _missileCollisionPointInfo = typeof(MyMissile).GetField("m_collisionPoint", BindingFlags.Instance | BindingFlags.NonPublic);
        readonly static FieldInfo _missileCollisionNormalInfo = typeof(MyMissile).GetField("m_collisionNormal", BindingFlags.Instance | BindingFlags.NonPublic);
        readonly static FieldInfo _missileCollidedEntityInfo = typeof(MyMissile).GetField("m_collidedEntity", BindingFlags.Instance | BindingFlags.NonPublic);
        readonly static FieldInfo _missileHealthPoolInfo = typeof(MyMissile).GetField("m_healthPool", BindingFlags.Instance | BindingFlags.NonPublic);
        readonly static FieldInfo _missileCollisionShapeKey = typeof(MyMissile).GetField("m_collisionShapeKey", BindingFlags.Instance | BindingFlags.NonPublic);

        readonly static MethodInfo _missileExplodeMethodInfo = typeof(MyMissile).GetMethod("Explode", BindingFlags.Instance | BindingFlags.NonPublic);

        // Registers a phasing fix Task for missile
        internal static void InitiatePhasingFix(MyMissile missile)
        {
            MyEntity collidedEntity = (MyEntity)_missileCollidedEntityInfo.GetValue(missile);

            if (collidedEntity != null
                && collidedEntity is MyCubeGrid grid
                && !_collisionCorrectionTasks.ContainsKey(missile) // No task is already running for this missile
                && !grid.IsPreview
                && grid.Projector == null)
            {
                // Obtain previous missile position and the collision point subject to correction
                Vector3D position = missile.PositionComp.GetPosition();
                Vector3D detectedCollisionPoint = (Vector3D)_missileCollisionPointInfo.GetValue(missile);

                // Construct ray along which we want the corrected point found
                LineD ray = new LineD(position, detectedCollisionPoint);

                // Start task calculating the corrected point and normal
                var task = Task.Run(() =>
                {
                    // Obtain cells along correction ray
                    List<Vector3I> collidedGridCells = new List<Vector3I>();
                    grid.RayCastCells(position, detectedCollisionPoint, collidedGridCells, null, false, false);


                    (Vector3D, Vector3)? correctedCollisionData = null;

                    // Iterate over intersected cells - get first one hit
                    foreach (var gridCell in collidedGridCells)
                    {
                        if (grid.TryGetCube(gridCell, out MyCube cube)
                            && TryRayCastCube(cube, grid, ref ray, out correctedCollisionData))
                        {
                            break;
                        }
                    }

                    if (correctedCollisionData.HasValue)
                    {
                        return (correctedCollisionData.Value.Item1 - ray.Direction * Plugin.Instance.Config.BackMovement,
                            correctedCollisionData.Value.Item2);
                    }
                    return ((Vector3D, Vector3)?)null;
                });
                _collisionCorrectionTasks[missile] = task;
            }
        }
        // If any phasing fix was started for the given missile, synchronnously awaits its completion and applies the changes
        internal static void CompletePhasingFix(MyMissile missile)
        {
            if (_collisionCorrectionTasks.TryGetValue(missile, out var correctionTask))
            {
                (Vector3D, Vector3)? correctedCollisionData = correctionTask.Result;
                if (correctedCollisionData.HasValue)
                {
                    _missileCollisionPointInfo.SetValue(missile, correctedCollisionData.Value.Item1);
                    _missileCollisionNormalInfo.SetValue(missile, correctedCollisionData.Value.Item2);
                }
                _collisionCorrectionTasks.Remove(missile);
            }
        }
        internal static void HitSingleGridWithMissile(MyMissile missile, MyCubeGrid grid, Vector3D nextPosition)
        {
            Vector3D? collisionPointNullable = (Vector3D?)_missileCollisionPointInfo.GetValue(missile);
            if (grid != null
                && collisionPointNullable.HasValue)
            {
                LineD damageRay = new LineD(collisionPointNullable.Value, nextPosition);

                List<Vector3I> collidedGridCells = new List<Vector3I>();
                grid.RayCastCells(collisionPointNullable.Value, nextPosition, collidedGridCells, null, false, false);


                float missileHealth = (float)_missileHealthPoolInfo.GetValue(missile);
                uint collisionShapeKey = (uint)_missileCollisionShapeKey.GetValue(missile);
                Vector3 collisionNormal = (Vector3)_missileCollisionNormalInfo.GetValue(missile);

                HashSet<int> alreadyHitBlocks = new HashSet<int>();

                foreach (var gridCell in collidedGridCells)
                {
                    if (grid.TryGetCube(gridCell, out MyCube cube))
                    {
                        MySlimBlock slimBlock = cube.CubeBlock;
                        if (alreadyHitBlocks.Add(slimBlock.UniqueId) && (grid.BlocksDestructionEnabled || slimBlock.ForceBlockDestructible))
                        {
                            if (TryRayCastCube(cube, grid, ref damageRay, out _, false, IntersectionFlags.ALL_TRIANGLES))
                            {
                                float startingMissileHealth = missileHealth;

                                MyHitInfo hitInfo = new MyHitInfo()
                                {
                                    Normal = collisionNormal,
                                    Position = collisionPointNullable.Value,
                                    Velocity = missile.LinearVelocity,
                                    ShapeKey = collisionShapeKey
                                };

                                float damageToApply = Math.Min(slimBlock.GetRemainingDamage(), missileHealth);
                                slimBlock.DoDamage(damageToApply, MyDamageType.Bullet, true, hitInfo, missile.LauncherId);

                                if (float.IsPositiveInfinity(damageToApply))
                                    missileHealth = 0;
                                else
                                    missileHealth -= damageToApply;

                                if (missileHealth <= 0 || missileHealth == startingMissileHealth || slimBlock.Integrity > 0)
                                {
                                    missile.PositionComp.SetPosition(slimBlock.WorldPosition);
                                    _missileExplodeMethodInfo.Invoke(missile, null);
                                    break;
                                }
                            }
                        }
                    }
                }

                _missileHealthPoolInfo.SetValue(missile, missileHealth);
            }
        }
        internal static void HitMultipleGridsWithMissile(MyMissile missile, List<MyLineSegmentOverlapResult<MyEntity>> hits, Vector3D nextPosition)
        {
            Vector3D? collisionPointNullable = (Vector3D?)_missileCollisionPointInfo.GetValue(missile);
            if (collisionPointNullable.HasValue
                && hits.Count != 0)
            {
                List<MyCube> cubesToHit = new List<MyCube>();
                foreach (MyLineSegmentOverlapResult<MyEntity> hit in hits)
                {
                    if (hit.Element is MyCubeGrid grid)
                    {
                        List<Vector3I> collidedGridCells = new List<Vector3I>();
                        grid.RayCastCells(collisionPointNullable.Value, nextPosition, collidedGridCells, null, false, false);
                        foreach (MyCube cubeToAdd in collidedGridCells.Select(c => grid.TryGetCube(c, out MyCube cube) ? cube : null).Where(c => c != null))
                        {
                            int index = cubesToHit.BinarySearch(cubeToAdd, Comparer<MyCube>.Create((x, y) => Vector3D.DistanceSquared(collisionPointNullable.Value, x.CubeBlock.WorldPosition).CompareTo(Vector3D.DistanceSquared(collisionPointNullable.Value, y.CubeBlock.WorldPosition))));
                            cubesToHit.Insert(index < 0 ? ~index : index, cubeToAdd);
                        }
                    }
                }



                LineD damageRay = new LineD(collisionPointNullable.Value, nextPosition);

                float missileHealth = (float)_missileHealthPoolInfo.GetValue(missile);
                uint collisionShapeKey = (uint)_missileCollisionShapeKey.GetValue(missile);
                Vector3 collisionNormal = (Vector3)_missileCollisionNormalInfo.GetValue(missile);

                HashSet<int> alreadyHitBlocks = new HashSet<int>();

                foreach (MyCube cube in cubesToHit)
                {
                    MySlimBlock slimBlock = cube.CubeBlock;

                    MyCubeGrid grid = slimBlock.CubeGrid;

                    if (alreadyHitBlocks.Add(slimBlock.UniqueId) && (grid.BlocksDestructionEnabled || slimBlock.ForceBlockDestructible))
                    {
                        if (TryRayCastCube(cube, grid, ref damageRay, out _, false, IntersectionFlags.ALL_TRIANGLES))
                        {
                            float startingMissileHealth = missileHealth;

                            MyHitInfo hitInfo = new MyHitInfo()
                            {
                                Normal = collisionNormal,
                                Position = collisionPointNullable.Value,
                                Velocity = missile.LinearVelocity,
                                ShapeKey = collisionShapeKey
                            };

                            float damageToApply = Math.Min(slimBlock.GetRemainingDamage(), missileHealth);
                            slimBlock.DoDamage(damageToApply, MyDamageType.Bullet, true, hitInfo, missile.LauncherId);

                            if (float.IsPositiveInfinity(damageToApply))
                                missileHealth = 0;
                            else
                                missileHealth -= damageToApply;

                            if (missileHealth <= 0 || missileHealth == startingMissileHealth || slimBlock.Integrity > 0)
                            {
                                missile.PositionComp.SetPosition(slimBlock.WorldPosition);
                                _missileExplodeMethodInfo.Invoke(missile, null);
                                break;
                            }
                        }
                    }
                }

                _missileHealthPoolInfo.SetValue(missile, missileHealth);
            }

        }




        // Raycast a cube's mesh. Optionally transform triangle data to get concrete point. True if intersected, false if not.
        private static bool TryRayCastCube(MyCube cube, MyCubeGrid grid, ref LineD ray, out (Vector3D, Vector3)? result, bool generateIntersectionData = true, IntersectionFlags flags = IntersectionFlags.DIRECT_TRIANGLES)
        {
            MySlimBlock slimBlock = cube.CubeBlock;

            result = null;

            if (slimBlock.FatBlock != null)
            {
                // Tries to directly raycast a fat block
                MatrixD invertedMatrix = MatrixD.Invert(slimBlock.FatBlock.WorldMatrix);
                MyIntersectionResultLineTriangleEx? detectedTriangle = slimBlock.FatBlock.ModelCollision.GetTrianglePruningStructure().GetIntersectionWithLine(grid, ref ray, ref invertedMatrix, flags);

                // If it did not get a result, tries to raycast the fat block's subparts
                if (!detectedTriangle.HasValue && slimBlock.FatBlock.Subparts != null)
                {
                    foreach (MyEntitySubpart subpart in slimBlock.FatBlock.Subparts.Values)
                    {
                        invertedMatrix = MatrixD.Invert(subpart.WorldMatrix);
                        detectedTriangle = subpart.ModelCollision.GetTrianglePruningStructure().GetIntersectionWithLine(grid, ref ray, ref invertedMatrix, flags);
                        if (detectedTriangle.HasValue)
                            break;
                    }
                }

                // If enabled, transforms the extracted triangle data to get results for the phasing fix
                if (detectedTriangle.HasValue)
                {
                    if (generateIntersectionData)
                        result = (Vector3D.Transform(detectedTriangle.Value.IntersectionPointInObjectSpace, slimBlock.FatBlock.WorldMatrix),
                            Vector3.TransformNormal(detectedTriangle.Value.NormalInObjectSpace, slimBlock.FatBlock.WorldMatrix));
                    return true;
                }
                return false;
            }
            else
            {
                double previousTriangleDistanceSquared = double.MaxValue;

                bool intersectedCube = false;

                // Tries to raycast a slim block's cube parts
                foreach (MyCubePart part in cube.Parts)
                {
                    MatrixD matrix = part.InstanceData.LocalMatrix * grid.WorldMatrix;
                    MatrixD invertedMatrix = MatrixD.Invert(matrix);

                    MyIntersectionResultLineTriangleEx? candidateTriangle = part.Model.GetTrianglePruningStructure().GetIntersectionWithLine(grid, ref ray, ref invertedMatrix, flags);

                    if (candidateTriangle.HasValue)
                    {
                        var triangle = candidateTriangle.Value;

                        var intersectionWorld = Vector3D.Transform(triangle.IntersectionPointInObjectSpace, matrix);

                        double distanceSquared = Vector3D.DistanceSquared(intersectionWorld, ray.From);

                        // Gets the intersection nearest to the raycast origin
                        if (distanceSquared < previousTriangleDistanceSquared)
                        {
                            previousTriangleDistanceSquared = distanceSquared;
                            intersectedCube = true;
                            if (generateIntersectionData)
                                result =
                                    (Vector3D.Transform(triangle.IntersectionPointInObjectSpace, matrix) - ray.Direction * Plugin.Instance.Config.BackMovement,
                                    Vector3.TransformNormal(triangle.NormalInObjectSpace, matrix));
                        }
                    }
                }

                return intersectedCube;
            }
        }
    }
}
