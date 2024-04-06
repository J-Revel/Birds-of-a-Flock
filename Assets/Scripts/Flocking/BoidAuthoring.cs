using JetBrains.Annotations;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEditor.Rendering;
using UnityEngine;

public class BoidAuthoring : MonoBehaviour
{
    public BoidBehaviourConfigAsset config;
    public float start_direction_angle;
    public float display_scale = 1;

    public class Baker : Baker<BoidAuthoring>
    {
        public override void Bake(BoidAuthoring authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.Dynamic);
            DependsOn(authoring.config);
            AddComponent<BoidState>(entity,
                new BoidState
                {
                    velocity = new float2(math.cos(authoring.start_direction_angle), math.sin(authoring.start_direction_angle))
                });
            AddComponent<BoidConfig>(entity, new BoidConfig
            {
                config = authoring.config.Bake(),
            });
            AddComponent<BoidNeighbourData>(entity);
            AddComponent<BoidDisplay>(entity, new BoidDisplay { display_scale = authoring.display_scale });
        }
    }
}

public struct BoidDisplay : IComponentData
{
    public float display_scale;
}

public struct BoidConfig: IComponentData
{
    public BoidBehaviourConfig config;
}

public struct BoidState : IComponentData
{
    public float2 velocity;
    public float2 acceleration;
}

public struct BoidPartitionCell : IComponentData
{
    public int2 partition;
}

public struct BoidNeighbourData : IComponentData
{
    public float2 average_velocity;
    public int neighbour_count;
}

[UpdateInGroup(typeof(FixedStepSimulationSystemGroup)), UpdateAfter(typeof(BehaviourZoneSystem))]
public partial class BoidMovementSystem : SystemBase
{
    public class Singleton : IComponentData
    {
        public NativeParallelMultiHashMap<int2, Entity> partition_grid;

    }

    protected override void OnCreate()
    {
        base.OnCreate();
        EntityManager.CreateSingleton<Singleton>(new Singleton { 
            partition_grid = new NativeParallelMultiHashMap<int2, Entity>(4096 * 4, Allocator.Persistent),
        });
    }

    protected override void OnUpdate()
    {
        float dt = SystemAPI.Time.DeltaTime;

        NativeParallelMultiHashMap<int2, Entity> partition_grid = SystemAPI.ManagedAPI.GetSingleton<Singleton>().partition_grid;
        float boids_partition_size = SystemAPI.GetSingleton<GlobalConfig>().boid_partition_size;
        float collider_partition_size = SystemAPI.GetSingleton<GlobalConfig>().collider_partition_size;
        partition_grid.Clear();
        NativeParallelMultiHashMap<int2, Entity> partition = partition_grid;

        Entities.WithDeferredPlaybackSystem<EndFixedStepSimulationEntityCommandBufferSystem>()
            .WithNone<BoidPartitionCell>()
            .ForEach((EntityCommandBuffer command_buffer, Entity entity, in LocalTransform transform, in BoidState state, in BoidConfig config) =>
            {
                int2 cell = (int2)(transform.Position.xz / boids_partition_size);
                command_buffer.AddComponent<BoidPartitionCell>(entity, new BoidPartitionCell { partition = cell });
                partition.Add(cell, entity);
            }).Schedule();
        Entities
            .WithName("Position_Update")
            .ForEach((Entity entity, ref LocalTransform transform, ref BoidState state, ref BoidPartitionCell partition_cell, in BoidConfig config, in BoidDisplay display) =>
            {
                float3 velocity = new float3(state.velocity.x, 0, state.velocity.y);
                transform.Position += velocity * dt;
                transform.Scale = display.display_scale;
                transform.Rotation = quaternion.LookRotation(velocity, new float3(0, 1, 0));
                int2 new_partition_cell = (int2)(transform.Position.xz / boids_partition_size);
                if (math.lengthsq(partition_cell.partition - new_partition_cell) > 0)
                {
                    partition.Remove(partition_cell.partition, entity);
                    partition.Add(new_partition_cell, entity);
                }
            }).Schedule();
        var writer = partition.AsParallelWriter();
        Entities.WithName("Partition_Update").ForEach((Entity entity, in LocalTransform transform, in BoidState state) =>
        {
            int2 cell = (int2)(transform.Position.xz / boids_partition_size);
            writer.Add(cell, entity);
        }).ScheduleParallel();
        Entities
            .WithName("Neighbour_Data_Compute")
            .WithReadOnly(partition)
            .ForEach((Entity entity, ref BoidNeighbourData neighbour_data, in BoidState state, in LocalTransform transform, in BoidPartitionCell partition_cell, in BoidConfig config) =>
            {
                float2 position = transform.Position.xz;

                float max_range = config.config.neighbour_detection_range;
                int2 min_partition = (int2)((position - max_range) / boids_partition_size);
                int2 max_partition = (int2)((position + max_range) / boids_partition_size);

                float2 neighbour_velocity_sum = float2.zero;
                int neighbour_count = 0;
                for (int i = min_partition.x; i <= max_partition.x; i++)
                {
                    for (int j = min_partition.y; j <= max_partition.y; j++)
                    {
                        foreach (Entity neighbour_entity in partition.GetValuesForKey(new int2(i, j)))
                        {
                            if (neighbour_entity == entity)
                                continue;
                            neighbour_velocity_sum += SystemAPI.GetComponent<BoidState>(neighbour_entity).velocity;
                            neighbour_count++;
                        }
                    }
                }
                if (neighbour_count > 0)
                {
                    neighbour_data.average_velocity = neighbour_velocity_sum / neighbour_count;
                    neighbour_data.neighbour_count = neighbour_count;

            }
        }).ScheduleParallel();
        float3 mouse_pos_screen = (float3)Input.mousePosition;
        mouse_pos_screen.z = 10;
        float2 mouse_position = ((float3)Camera.main.ScreenToWorldPoint(mouse_pos_screen)).xz;

        Entities
            .WithName("Attraction_Repulsion")
            .WithReadOnly(partition)
            .ForEach((Entity entity, ref BoidState state, in LocalTransform transform, in BoidPartitionCell partition_cell, in BoidConfig config, in BoidNeighbourData neighbour_data) =>
            {
                state.acceleration = new float2();
                float2 position = transform.Position.xz;

                float max_range = math.max(config.config.attraction_range, config.config.repulsion_range);
                int2 min_partition = (int2)((position - max_range) / boids_partition_size);
                int2 max_partition = (int2)((position + max_range) / boids_partition_size);
                
                for (int i = min_partition.x; i <= max_partition.x; i++)
                {
                    for (int j = min_partition.y; j <= max_partition.y; j++)
                    {
                        foreach (Entity neighbour_entity in partition.GetValuesForKey(new int2(i, j)))
                        {
                            if (neighbour_entity == entity)
                                continue;
                            float2 neighbour_position = SystemAPI.GetComponent<LocalTransform>(neighbour_entity).Position.xz;
                            float2 direction = math.all(neighbour_position == position) ? float2.zero : math.normalize(neighbour_position - position);
                            float distancesq = math.distancesq(position, neighbour_position);
                            if (distancesq < config.config.attraction_range)
                            {
                                state.acceleration += direction * config.config.attraction_force;
                            }
                            if (distancesq < config.config.repulsion_range)
                            {
                                state.acceleration += -direction * config.config.repulsion_force;
                            }
                        }
                    }
                }
                if (math.all(neighbour_data.average_velocity != float2.zero))
                    state.acceleration += math.normalize(neighbour_data.average_velocity) * config.config.align_force;
                state.velocity += math.normalize(mouse_position - position) * config.config.mouse_attraction_force;
                state.velocity = state.velocity + state.acceleration * dt;
                state.velocity = math.normalize(state.velocity) * config.config.speed;
            }).ScheduleParallel();
        Entities
            .WithName("Segment_Collision")
            .WithReadOnly(partition)
            .ForEach((Entity entity, ref BoidState state, in LocalTransform transform, in BoidPartitionCell partition_cell, in BoidConfig config, in BoidNeighbourData neighbour_data) =>
            {
                state.acceleration = new float2();
                float2 position = transform.Position.xz;

                float max_range = config.config.wall_repulsion_range;
                int2 min_partition = (int2)((position - max_range) / collider_partition_size);
                int2 max_partition = (int2)((position + max_range) / collider_partition_size);
                
                for (int i = min_partition.x; i <= max_partition.x; i++)
                {
                    for (int j = min_partition.y; j <= max_partition.y; j++)
                    {
                        foreach (Entity neighbour_entity in partition.GetValuesForKey(new int2(i, j)))
                        {
                            if (neighbour_entity == entity)
                                continue;
                            ColliderSegment segment = SystemAPI.GetComponent<ColliderSegment>(neighbour_entity);
                            float distance = segment.DistanceFromPointSq(new float3(position.x, 0, position.y));
                            if (distance < config.config.wall_repulsion_range)
                            {
                                state.acceleration += segment.collision_normal.xz * config.config.wall_repulsion_force;
                            }
                        }
                    }
                }
                if (math.all(neighbour_data.average_velocity != float2.zero))
                    state.acceleration += math.normalize(neighbour_data.average_velocity) * config.config.align_force;
                state.velocity += math.normalize(mouse_position - position) * config.config.mouse_attraction_force;
                state.velocity = state.velocity + state.acceleration * dt;
                state.velocity = math.normalize(state.velocity) * config.config.speed;
            }).ScheduleParallel();
    }
}
