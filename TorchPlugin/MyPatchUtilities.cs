﻿using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.Models;
using VRageMath;

namespace TorchPlugin
{
    internal class MyPatchUtilities
    {
        readonly static Dictionary<MyMissile, Task<(Vector3D, Vector3)?>> _collisionCorrectionTasks = new Dictionary<MyMissile, Task<(Vector3D, Vector3)?>>();
        readonly static List<Vector3I> _missileDamageGridCells = new List<Vector3I>();

        readonly static FieldInfo _missileCollisionPointInfo = typeof(MyMissile).GetField("m_collisionPoint", BindingFlags.Instance | BindingFlags.NonPublic);
        readonly static FieldInfo _missileCollisionNormalInfo = typeof(MyMissile).GetField("m_collisionNormal", BindingFlags.Instance | BindingFlags.NonPublic);
        readonly static FieldInfo _missileCollidedEntityInfo = typeof(MyMissile).GetField("m_collidedEntity", BindingFlags.Instance | BindingFlags.NonPublic);
        readonly static FieldInfo _missileCollisionShapeKey = typeof(MyMissile).GetField("m_collisionShapeKey", BindingFlags.Instance | BindingFlags.NonPublic);

        readonly static MethodInfo _missileExplodeMethodInfo = typeof(MyMissile).GetMethod("Explode", BindingFlags.Instance | BindingFlags.NonPublic);

        // Registers a phasing fix Task for missile
        internal static void InitiatePhasingFix(MyMissile missile)
        {
            // This should never happen, but the phasing fix will not be used or have memory fall out of scope if the missile never executes MarkForExplosion, which it only does on servers
            if (!Sync.IsServer) return;

            MyEntity collidedEntity = (MyEntity)_missileCollidedEntityInfo.GetValue(missile);

            Vector3D position;
            Vector3D? detectedCollisionPoint;

            lock (missile)
            {
                // Obtain previous missile position and the collision point subject to correction
                position = missile.PositionComp.GetPosition();
                detectedCollisionPoint = (Vector3D?)_missileCollisionPointInfo.GetValue(missile);
            }

            if (collidedEntity != null
                && detectedCollisionPoint.HasValue
                && collidedEntity is MyCubeGrid grid
                && !_collisionCorrectionTasks.ContainsKey(missile) // No task is already running for this missile
                && !grid.IsPreview
                && grid.Projector == null)
            {
                Vector3D globalSpaceDirection = position - detectedCollisionPoint.Value;


                // For whatever reason, phasing can happen in the opposite direction as well
                // -> invert the phase check not to pull the collision out of the grid but rather to push it into it
                bool invertPhaseCheck = globalSpaceDirection.Dot(missile.LinearVelocity) > 0;

                if (invertPhaseCheck)
                    globalSpaceDirection *= -1;

                // Correct position to furthest extents of the grid
                globalSpaceDirection = globalSpaceDirection.Normalized();

                MatrixD gridMatrix = grid.WorldMatrix;
                MatrixD gridMatrixTransposed = MatrixD.Transpose(gridMatrix);

                Vector3D gridSpaceDirection = Vector3D.TransformNormal(globalSpaceDirection, gridMatrixTransposed);

                Vector3I gridMin = grid.Min, gridMax = grid.Max;
                

                // Take the corner which the direction points closest to, and make a relative vector from detected collision to the corner
                // (buffered by .3 blocks)
                var gridSpaceRelativeRelevantCorner = new Vector3D(
                    gridSpaceDirection.X < 0 ? gridMin.X - .8 : gridMax.X + .8,
                    gridSpaceDirection.Y < 0 ? gridMin.Y - .8 : gridMax.Y + .8,
                    gridSpaceDirection.Z < 0 ? gridMin.Z - .8 : gridMax.Z + .8
                    ) * grid.GridSize - Vector3D.TransformNormal(detectedCollisionPoint.Value - gridMatrix.Translation, gridMatrixTransposed); ;

                // Take the multiplier that gets our vector to the grid extents, capped to 250 meters
                double vectorMultiplier = MathHelper.Min((gridSpaceRelativeRelevantCorner / gridSpaceDirection).Min(), 250);

#if DEBUG
                lock (Plugin.Instance.Log)
                {
                    Plugin.Instance.Log.Info($"Raycast distance set to {vectorMultiplier:0.000} m");
                }
                if ((bool)typeof(Commands).GetField("GPSSpam", BindingFlags.Static | BindingFlags.Public).GetValue(null))
                {
                    MyGpsCollection gpsCollection = (MyGpsCollection)MyAPIGateway.Session?.GPS;
                    lock (gpsCollection)
                    {
                        MyGps gpsCollision = new MyGps
                        {
                            Coords = detectedCollisionPoint.Value,
                            Name = "m_collisionPoint",
                            DisplayName = "m_collisionPoint",
                            GPSColor = Color.DarkRed,
                            ShowOnHud = true
                        };
                        MyGps gpsPrevTick = new MyGps
                        {
                            Coords = position,
                            Name = "lastTick",
                            DisplayName = "lastTick",
                            GPSColor = Color.Cyan,
                            ShowOnHud = true
                        };
                        MyGps gpsCastFrom = new MyGps
                        {
                            Coords = detectedCollisionPoint.Value + globalSpaceDirection * vectorMultiplier,
                            Name = "castFrom",
                            DisplayName = "castFrom",
                            GPSColor = Color.Gold,
                            ShowOnHud = true
                        };
                        if (gpsCollection != null)
                        {
                            foreach (var player in MySession.Static.Players.GetOnlinePlayers())
                            {
                                gpsCollection.SendAddGpsRequest(player.Identity.IdentityId, ref gpsCollision);
                                gpsCollection.SendAddGpsRequest(player.Identity.IdentityId, ref gpsPrevTick);
                                gpsCollection.SendAddGpsRequest(player.Identity.IdentityId, ref gpsCastFrom);
                            }
                        }
                    }
                }
#endif

                position = detectedCollisionPoint.Value + globalSpaceDirection * vectorMultiplier;
                if (!globalSpaceDirection.IsZero())
                {
                    // Start task calculating the corrected point and normal
                    var task = Task.Run(() =>
                    {
                        

                        // Construct ray along which we want the corrected point found
                        LineD ray = new LineD(position, detectedCollisionPoint.Value);

                        // Obtain cells along correction ray
                        List<Vector3I> collidedGridCells = new List<Vector3I>();
                        grid.RayCastCells(position, detectedCollisionPoint.Value, collidedGridCells, null, false, false);
                        (Vector3D, Vector3)? correctedCollisionData = null;

                        // Iterate over intersected cells - get first one hit
                        foreach (var gridCell in collidedGridCells)
                        {
                            // If we both found a cube in a grid cell and got a hit on it with the raycast, we have acquired a better collision point
                            if (grid.TryGetCube(gridCell, out MyCube cube)
                                && TryRayCastCube(cube, grid, ref ray, out correctedCollisionData))
                            {
                                break;
                            }
                        }

                        if (correctedCollisionData.HasValue)
                        {
                            // Finish the task with the newly created collision point and normal
                            // The collision point gets moved back along the damage ray by the configured amount to avoid clipping through the surface, especially with the damage fix enabled
                            return (correctedCollisionData.Value.Item1 - ray.Direction * Plugin.Instance.Config.BackMovement,
                                correctedCollisionData.Value.Item2);
                        }

                        // No better collision point was found - returning no data
                        return ((Vector3D, Vector3)?)null;
                    });

                    // Stores the task reference for this missile
                    _collisionCorrectionTasks[missile] = task;
                }
            }
        }
        // If any phasing fix was started for the given missile, synchronnously awaits its completion and applies the changes
        internal static void CompletePhasingFix(MyMissile missile)
        {
            // Retrieve the task calculating this missile's correction point
            if (_collisionCorrectionTasks.TryGetValue(missile, out var correctionTask))
            {
                // Synchronnously wait for the task to finish execution
                (Vector3D, Vector3)? correctedCollisionData = correctionTask.Result;

                // If a correction point was found, set the relevant private fields through reflection
                if (correctedCollisionData.HasValue)
                {
                    _missileCollisionPointInfo.SetValue(missile, correctedCollisionData.Value.Item1);
                    _missileCollisionNormalInfo.SetValue(missile, correctedCollisionData.Value.Item2);
                }

                // Clean up task reference from dictionary
                ClearPhasingFix(missile);
            }
        }
        // Clean up reference to the task, available even if we aren't using it in the end
        internal static void ClearPhasingFix(MyMissile missile)
        {
            _collisionCorrectionTasks.Remove(missile);
        }
        internal static void HitSingleGridWithMissile(MyMissile missile, MyCubeGrid grid, Vector3D nextPosition)
        {
            Vector3D? collisionPointNullable = (Vector3D?)_missileCollisionPointInfo.GetValue(missile);

            // No application if we don't have a collision point or the grid is null... shouldn't be called then, but you never know
            if (grid != null
                && collisionPointNullable.HasValue)
            {
                // Construct the damage ray
                LineD damageRay = new LineD(collisionPointNullable.Value, nextPosition);

                // Take all grid cells intersected by damaging ray
                grid.RayCastCells(collisionPointNullable.Value, nextPosition, _missileDamageGridCells, null, false, false);

                // Pull relevant data through reflection.. I'm not sure if the shape key is even used, but just to be safe, we use the one given by Havok before the phasing patch was run
                uint collisionShapeKey = (uint)_missileCollisionShapeKey.GetValue(missile);
                Vector3 collisionNormal = (Vector3)_missileCollisionNormalInfo.GetValue(missile);

                // Maintaining a list of already considered block IDs to not needlessly visit the same block twice, firing more raycasts
                HashSet<int> alreadyHitBlocks = new HashSet<int>();

#if DEBUG
                int hitBlocks = 0;
                int raycasts = 0;
#endif

                foreach (var gridCell in _missileDamageGridCells)
                {
                    // Attempt a cube raycast
                    // The triangle is not transformed in this call to save the tiny bit of performance it costs
                    if (grid.TryGetCube(gridCell, out MyCube cube))
                    {
                        MySlimBlock slimBlock = cube.CubeBlock;
                        if (alreadyHitBlocks.Add(slimBlock.UniqueId) && (grid.BlocksDestructionEnabled || slimBlock.ForceBlockDestructible))
                        {
#if DEBUG
                            raycasts++;
#endif
                            // Attempt a cube raycast
                            // The triangle is not transformed in this call to save the tiny bit of performance it costs
                            if (TryRayCastCube(cube, grid, ref damageRay, out _, false))
                            {
                                float startingMissileHealth = missile.HealthPool;

                                MyHitInfo hitInfo = new MyHitInfo()
                                {
                                    Normal = collisionNormal,
                                    Position = collisionPointNullable.Value,
                                    Velocity = missile.LinearVelocity,
                                    ShapeKey = collisionShapeKey
                                };

                                // Damage code pretty much copied from keen implementation, just extracted a bit to avoid forcing calls to inaccessible methods through reflection
                                float damageToApply = Math.Min(slimBlock.GetRemainingDamage(), missile.HealthPool);
                                slimBlock.DoDamage(damageToApply, MyDamageType.Bullet, true, hitInfo, missile.LauncherId);

#if DEBUG
                                hitBlocks++;
#endif

                                // I'm pretty sure the positive infinity thing won't ever trigger but whatever
                                if (float.IsPositiveInfinity(damageToApply))
                                    missile.HealthPool = 0;
                                else
                                    missile.HealthPool -= damageToApply;

                                if (missile.HealthPool <= 0 || missile.HealthPool == startingMissileHealth || slimBlock.Integrity > 0)
                                {
                                    // Kill the missile - it's run out of health - and stop hitting further blocks (no more raycasts)
                                    missile.PositionComp.SetPosition(slimBlock.WorldPosition);
                                    _missileExplodeMethodInfo.Invoke(missile, null);
                                    break;
                                }
                            }
                        }
                    }
                }

#if DEBUG
                Plugin.Instance.Log.Info($"Single-grid hit complete. Cells: {_missileDamageGridCells} <> Raycasts: {raycasts} <> Blocks: {hitBlocks} <> Remaining health: {missile.HealthPool}");
#endif
                _missileDamageGridCells.Clear();
            }
        }
        internal static void HitMultipleGridsWithMissile(MyMissile missile, List<MyLineSegmentOverlapResult<MyEntity>> hits, Vector3D nextPosition)
        {
            Vector3D? collisionPoint = (Vector3D?)_missileCollisionPointInfo.GetValue(missile);

#if DEBUG
            int hitCells = 0;
            int hitBlocks = 0;
            int raycasts = 0;
#endif

            // No application if we don't have a collision point or there are no hits given... shouldn't be called then, but you never know
            if (collisionPoint.HasValue
                && hits.Count != 0)
            {
                // Assemble ordered list of blocks to apply damage to, pulling from each grid
                List<MyCube> cubesToHit = new List<MyCube>();
                foreach (MyLineSegmentOverlapResult<MyEntity> hit in hits)
                {
                    if (hit.Element is MyCubeGrid grid)
                    {
                        // Take all grid cells intersected by damaging ray
                        grid.RayCastCells(collisionPoint.Value, nextPosition, _missileDamageGridCells, null, false, false);

                        // Go through grid cells and pick those occupied
                        foreach (Vector3I gridCell in _missileDamageGridCells)
                        {
                            if (grid.TryGetCube(gridCell, out MyCube cube))
                            {
                                // Maintain order in the list using binarysearch, sorting by distance from collision point
                                int index = cubesToHit.BinarySearch(cube, Comparer<MyCube>.Create((x, y) => Vector3D.DistanceSquared(collisionPoint.Value, x.CubeBlock.WorldPosition).CompareTo(Vector3D.DistanceSquared(collisionPoint.Value, y.CubeBlock.WorldPosition))));
                                cubesToHit.Insert(index < 0 ? ~index : index, cube);

                                // We cannot assume that all blocks in a grid fall in a contiguous block of entries in the damage list
                                // because there can be another grid between blocks of the first grid and vice versa
                            }
                        }
                        // Keen code would now sort the list in place - we don't have to as we maintained order
#if DEBUG
                        hitCells += _missileDamageGridCells.Count;
#endif
                        _missileDamageGridCells.Clear();
                    }
                }


                // Construct the damage ray itself
                LineD damageRay = new LineD(collisionPoint.Value, nextPosition);

                // Pull required data from the missile
                uint collisionShapeKey = (uint)_missileCollisionShapeKey.GetValue(missile);
                Vector3 collisionNormal = (Vector3)_missileCollisionNormalInfo.GetValue(missile);

                // Maintaining a list of already considered block IDs to not needlessly visit the same block twice, firing more raycasts
                HashSet<int> alreadyHitBlocks = new HashSet<int>();

                foreach (MyCube cube in cubesToHit)
                {
                    MySlimBlock slimBlock = cube.CubeBlock;

                    MyCubeGrid grid = slimBlock.CubeGrid;

                    if (alreadyHitBlocks.Add(slimBlock.UniqueId) && (grid.BlocksDestructionEnabled || slimBlock.ForceBlockDestructible))
                    {
#if DEBUG
                        raycasts++;
#endif
                        // Attempt a cube raycast
                        // The triangle is not transformed in this call to save the tiny bit of performance it costs
                        if (TryRayCastCube(cube, grid, ref damageRay, out _, false))
                        {
                            float startingMissileHealth = missile.HealthPool;

                            MyHitInfo hitInfo = new MyHitInfo()
                            {
                                Normal = collisionNormal,
                                Position = collisionPoint.Value,
                                Velocity = missile.LinearVelocity,
                                ShapeKey = collisionShapeKey
                            };

                            // Damage code pretty much copied from keen implementation, just extracted a bit to avoid forcing calls to inaccessible methods through reflection
                            float damageToApply = Math.Min(slimBlock.GetRemainingDamage(), missile.HealthPool);
                            slimBlock.DoDamage(damageToApply, MyDamageType.Bullet, true, hitInfo, missile.LauncherId);

#if DEBUG
                            hitBlocks++;
#endif

                            // I'm pretty sure the positive infinity thing won't ever trigger but whatever
                            if (float.IsPositiveInfinity(damageToApply))
                                missile.HealthPool = 0;
                            else
                                missile.HealthPool -= damageToApply;

                            if (missile.HealthPool <= 0 || missile.HealthPool == startingMissileHealth || slimBlock.Integrity > 0)
                            {
                                // Kill the missile - it's run out of health - and stop hitting further blocks (no more raycasts)
                                missile.PositionComp.SetPosition(slimBlock.WorldPosition);
                                _missileExplodeMethodInfo.Invoke(missile, null);
                                break;
                            }
                        }
                    }
                }
#if DEBUG
                Plugin.Instance.Log.Info($"Multi-grid hit complete. Cells: {hitCells} <> Raycasts: {raycasts} <> Blocks: {hitBlocks} <> Remaining health: {missile.HealthPool}");
#endif
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
                    {
                        result = (Vector3D.Transform(detectedTriangle.Value.IntersectionPointInObjectSpace, slimBlock.FatBlock.WorldMatrix),
                            Vector3.TransformNormal(detectedTriangle.Value.NormalInObjectSpace, slimBlock.FatBlock.WorldMatrix));
                    }
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
                        Vector3D intersectionWorld = Vector3D.Transform(candidateTriangle.Value.IntersectionPointInObjectSpace, matrix);

                        double distanceSquared = Vector3D.DistanceSquared(intersectionWorld, ray.From);

                        // Gets the intersection nearest to the raycast origin
                        if (distanceSquared < previousTriangleDistanceSquared)
                        {
                            previousTriangleDistanceSquared = distanceSquared;
                            intersectedCube = true;
                            if (generateIntersectionData)
                            {
                                // The result needs to be transformed here because each cube part has a different matrix
                                result = (Vector3D.Transform(candidateTriangle.Value.IntersectionPointInObjectSpace, matrix),
                                    Vector3.TransformNormal(candidateTriangle.Value.NormalInObjectSpace, matrix));
                            }
                        }
                    }
                }

                return intersectedCube;
            }
        }
    }
}
