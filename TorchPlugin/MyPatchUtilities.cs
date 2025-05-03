using Sandbox;
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
        static Dictionary<MyMissile, Task<(Vector3D, Vector3)?>> _collisionCorrectionTasks;

        readonly static List<Vector3I> _missileDamageGridCells;

        readonly static FieldInfo _missileCollisionPointInfo;
        readonly static FieldInfo _missileCollisionNormalInfo;
        readonly static FieldInfo _missileCollidedEntityInfo;
        readonly static FieldInfo _missileCollisionShapeKey;

        readonly static MethodInfo _missileExplodeMethodInfo;

        static MyPatchUtilities()
        {
            _missileCollisionPointInfo = typeof(MyMissile).GetField("m_collisionPoint", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw GenerateMissingMemberException(typeof(MyMissile), "m_collisionPoint", MissingMemberVariant.Field);
            _missileCollisionNormalInfo = typeof(MyMissile).GetField("m_collisionNormal", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw GenerateMissingMemberException(typeof(MyMissile), "m_collisionNormal", MissingMemberVariant.Field);
            _missileCollidedEntityInfo = typeof(MyMissile).GetField("m_collidedEntity", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw GenerateMissingMemberException(typeof(MyMissile), "m_collidedEntity", MissingMemberVariant.Field);
            _missileCollisionShapeKey = typeof(MyMissile).GetField("m_collisionShapeKey", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw GenerateMissingMemberException(typeof(MyMissile), "m_collisionShapeKey", MissingMemberVariant.Field);

            _missileExplodeMethodInfo = typeof(MyMissile).GetMethod("Explode", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw GenerateMissingMemberException(typeof(MyMissile), "Explode", MissingMemberVariant.Method);

            _collisionCorrectionTasks = new Dictionary<MyMissile, Task<(Vector3D, Vector3)?>>();
            _missileDamageGridCells = new List<Vector3I>();
        }


        /// <summary>
        /// Registers and starts a phasing patch <see cref="Task" /> for the given missile unless one is already registered.
        /// </summary>
        /// <param name="missile">Missile instance, the collision of which the <see cref="Task" /> will be correcting.</param>
        internal static void InitiatePhasingFix(MyMissile missile)
        {
            // This should never happen, but the phasing fix will not be used or have memory fall out of scope if the missile never executes MarkForExplosion, which it only does on servers
            if (!Sync.IsServer) return;
            
            MyEntity collidedEntity = (MyEntity)_missileCollidedEntityInfo.GetValue(missile);

            // Obtain previous missile position and the collision point subject to correction
            Vector3D position = missile.PositionComp.GetPosition();
            Vector3 normal = (Vector3)_missileCollisionNormalInfo.GetValue(missile);
            Vector3D? pulledCollisionPoint = (Vector3D?)_missileCollisionPointInfo.GetValue(missile);

            if (collidedEntity != null
                && pulledCollisionPoint.HasValue
                && collidedEntity is MyCubeGrid grid
                && !_collisionCorrectionTasks.ContainsKey(missile) // No task is already running for this missile
                && !grid.IsPreview
                && grid.Projector == null)
            {
                // Start task calculating the corrected point and normal
                var task = Task.Run(() =>
                {
                    // Correct the collision point to lay on the missile's trajectory
                    Vector3D globalSpaceDirection = position - pulledCollisionPoint.Value;
                    Vector3D velocityNorm = missile.LinearVelocity.Normalized();
                    globalSpaceDirection = (globalSpaceDirection.IsZero() ? 1 : velocityNorm.Dot(globalSpaceDirection)) * velocityNorm;

                    // globalSpaceDirection is from the collision to last tick pos, = we need to subtract it from last tick pos to get the global collision point
                    Vector3D orthCollisionPoint = position - globalSpaceDirection;




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
                    // (padded by .3 blocks)
                    var gridSpaceRelativeRelevantCorner = new Vector3D(
                        gridSpaceDirection.X < 0 ? gridMin.X - .8 : gridMax.X + .8,
                        gridSpaceDirection.Y < 0 ? gridMin.Y - .8 : gridMax.Y + .8,
                        gridSpaceDirection.Z < 0 ? gridMin.Z - .8 : gridMax.Z + .8
                        ) * grid.GridSize - Vector3D.TransformNormal(orthCollisionPoint - gridMatrix.Translation, gridMatrixTransposed);

                    // Take the multiplier that gets our vector to the grid extents, capped to 250 meters
                    double vectorMultiplier = MathHelper.Min((gridSpaceRelativeRelevantCorner / gridSpaceDirection).Min(), 250);

                    // Adjust the "position" (now the actual raycast start position) to the edge of the grid
                    position = orthCollisionPoint + globalSpaceDirection * vectorMultiplier;




                    // Construct ray along which we want the corrected point found
                    LineD ray = new LineD(position, orthCollisionPoint);

                    // Obtain cells along correction ray
                    List<Vector3I> collidedGridCells = new List<Vector3I>();
                    grid.RayCastCells(position, orthCollisionPoint, collidedGridCells, null, false, false);
                    (Vector3D, Vector3)? correctedCollisionData = null;

                    // Iterate over intersected cells - get first one hit
                    foreach (var gridCell in collidedGridCells)
                    {
                        // If we both found a cube in a grid cell and got a hit on it with the raycast, we have acquired a better collision point
                        if (grid.TryGetCube(gridCell, out MyCube cube) && 
                            TryRayCastCube(cube, grid, ref ray, out correctedCollisionData))
                                break;
                    }

                    if (correctedCollisionData.HasValue)
                    {
                        // Finish the task with the newly created collision point and normal
                        // The collision point gets moved back along the damage ray by the configured amount to avoid clipping through the surface, especially with the damage fix enabled
                        return (correctedCollisionData.Value.Item1 - ray.Direction * Plugin.Instance.Config.BackMovement,
                            correctedCollisionData.Value.Item2);
                    }

                    // No better collision point was found - returning collision point forced onto trajectory
                    return ((Vector3D, Vector3)?)(orthCollisionPoint, normal);
                });

                // Stores the task reference for this missile
                _collisionCorrectionTasks[missile] = task;
            }
        }
        /// <summary>
        /// If any phasing patch <see cref="Task" /> was started for the given missile, synchronnously blocks until its completion and applies the patch.
        /// </summary>
        /// <param name="missile">Missile instance which the <see cref="Task" /> was related to.</param>
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
        /// <summary>
        /// Unregisters the phasing patch <see cref="Task" /> regardless of whether its output was used or not.
        /// </summary>
        /// <param name="missile">Missile instance which the <see cref="Task" /> was related to.</param>
        internal static void ClearPhasingFix(MyMissile missile)
        {
            _collisionCorrectionTasks.Remove(missile);
        }
        /// <summary>
        /// Unregisters all phasing patch <see cref="Task" />s. Intended for releasing memory when the plugin is disabled.
        /// </summary>
        internal static void ClearAllPhasingFixes()
        {
            _collisionCorrectionTasks = new Dictionary<MyMissile, Task<(Vector3D, Vector3)?>>();
        }
        /// <summary>
        /// Applies missile damage to a single collided grid upon landing. Part of the damage patch.
        /// </summary>
        /// <param name="missile">Missile which is dealing damage.</param>
        /// <param name="grid">Grid involved in the collision.</param>
        /// <param name="nextPosition">The upcoming world space position of the missile in the next frame.</param>
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
        /// <summary>
        /// Applies missile damage to multiple collided grids upon landing. Part of the damage patch.
        /// </summary>
        /// <param name="missile">Missile which is dealing damage.</param>
        /// <param name="hits">Entities (grids) involved in the collision.</param>
        /// <param name="nextPosition">The upcoming world space position of the missile in the next frame.</param>
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
                                cubesToHit.Add(cube);
                            }
                        }
#if DEBUG
                        hitCells += _missileDamageGridCells.Count;
#endif
                        _missileDamageGridCells.Clear();
                    }
                }

                // Sort cubes by distance from the collision point rather than grid 
                cubesToHit.SortNoAlloc((x, y) => Vector3D.DistanceSquared(collisionPoint.Value, x.CubeBlock.WorldPosition).CompareTo(Vector3D.DistanceSquared(collisionPoint.Value, y.CubeBlock.WorldPosition)));


                // Construct the damage ray itself
                LineD damageRay = new LineD(collisionPoint.Value, nextPosition);

                // Pull required data from the missile
                uint collisionShapeKey = (uint)_missileCollisionShapeKey.GetValue(missile);
                Vector3 collisionNormal = (Vector3)_missileCollisionNormalInfo.GetValue(missile);

                // Maintaining a list of already considered block IDs to not visit the same block twice, which could happen for multi-block structures since we don't check for duplicates in the list when creating it
                // It is not actually a concern for damage because the whole multi-block would either have died from the shot, absorbed it (and thus stopped enumeration) or been missed by the raycast, but it would mean an extra raycast
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




        /// <summary>
        /// Raycasts a cube's mesh. Optionally transforms triangle data to get concrete intersection point.
        /// </summary>
        /// <param name="cube">Cube whose mesh to raycast.</param>
        /// <param name="grid">Cube grid in which the raycast is being executed.</param>
        /// <param name="ray">The line for which intersections are being queried.</param>
        /// <param name="result">Concrete intersection point output. Point and mesh normal in world space. <see langword="null" /> if <c>generateIntersectionData</c> is <see langword="false"/>.</param>
        /// <param name="generateIntersectionData">Whether or not to generate the intersection point output. The output will be <see langword="null"/> if set to <see langword="false"/>.</param>
        /// <param name="flags">Intersection flags for the mesh query related to backface culling.</param>
        /// <returns><see langword="true" /> if an intersection was found, <see langword="false" /> otherwise</returns>
        private static bool TryRayCastCube(MyCube cube, MyCubeGrid grid, ref LineD ray, out (Vector3D Point, Vector3 Normal)? result, bool generateIntersectionData = true, IntersectionFlags flags = IntersectionFlags.DIRECT_TRIANGLES)
        {
            MySlimBlock slimBlock = cube.CubeBlock;

            result = null;

            if (slimBlock.FatBlock != null)
            {
                lock (slimBlock.FatBlock)
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
            }
            else
            {
                double previousTriangleDistanceSquared = double.MaxValue;
                bool intersectedCube = false;

                // Tries to raycast a slim block's cube parts
                foreach (MyCubePart part in cube.Parts)
                {
                    lock (part)
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
                }

                return intersectedCube;
            }
        }
        /// <summary>
        /// Generates the appropriate exception outlining which member in which class was not found by reflection.
        /// </summary>
        /// <param name="targetType">The type expected to have this member.</param>
        /// <param name="memberName">The name of the missing member.</param>
        /// <param name="exceptionVariant">The variant of member that was expected.</param>
        /// <returns>An appropriately typed exception with an appropriate explanation.</returns>
        internal static MissingMemberException GenerateMissingMemberException(Type targetType, string memberName, MissingMemberVariant exceptionVariant)
        {
            string message = $"{Plugin.PluginName} reflection failure - {exceptionVariant} '{memberName}' in '{targetType.FullName}' not found!";
            switch (exceptionVariant)
            {
                case MissingMemberVariant.Field:
                    return new MissingFieldException(message);
                case MissingMemberVariant.Method:
                case MissingMemberVariant.Constructor:
                    return new MissingMethodException(message);
                default:
                    return new MissingMemberException(message);
            }
        }
    }
    /// <summary>
    /// Enum for declaring which variant of member is missing for exception generation.
    /// </summary>
    internal enum MissingMemberVariant
    {
        Field,
        Method,
        Property,
        Constructor,
        Operator
    }
}
