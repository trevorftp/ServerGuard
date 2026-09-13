using System;
using System.Collections.Generic;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Server;

namespace ServerGuard;

public class EntityVisibility : IDisposable
{
    private const int visibilityIntervalMs = 200;
    private const int boundsCorners = 1 << 3;
    private const int samplesPerEntity = 1 + boundsCorners;
    private const double boundsMargin = 0.25;
    private readonly ServerGuardConfig config;
    private readonly ThreadLocal<VisibilityWorker> workers;
    private readonly double maxRayRangeSq;
    private long rays;
    private long concealed;
    private long budgetExhaustions;

    public long Rays => Interlocked.Read(ref rays);
    public long Concealed => Interlocked.Read(ref concealed);
    public long BudgetExhaustions => Interlocked.Read(ref budgetExhaustions);

    private sealed class VisibilityWorker(ServerMain server, BlockPalette palette)
    {
        public readonly AABBIntersectionTest Raycaster = new(new OcclusionSupplier(server));
        public readonly Ray Ray = new();
        public readonly BlockFilter Filter = (pos, block) => palette.Opaque[block.Id];
        public readonly Dictionary<(int ClientId, long EntityId), bool> Visible = [];
        public long Epoch = -1;
        public int Remaining;
    }

    private sealed class OcclusionSupplier(IWorldIntersectionSupplier world) : IWorldIntersectionSupplier
    {
        public IBlockAccessor blockAccessor => world.blockAccessor;
        public Vec3i MapSize => world.MapSize;

        public Block GetBlock(BlockPos pos) => world.GetBlock(pos);

        // Full-cube bounds avoid decor queries for solid-block occlusion on physics threads.
        public Cuboidf[] GetBlockIntersectionBoxes(BlockPos pos) => Block.DefaultCollisionSelectionBoxes;

        public Entity[] GetEntitiesAround(Vec3d position, float horRange, float vertRange, ActionConsumable<Entity>? matches = null) => world.GetEntitiesAround(position, horRange, vertRange, matches);

        public bool IsValidPos(BlockPos pos) => world.IsValidPos(pos);
    }

    public EntityVisibility(ServerMain server, ServerGuardConfig config, BlockPalette palette)
    {
        this.config = config;
        double range = server.DefaultEntityTrackingRange * GlobalConstants.ChunkSize;
        maxRayRangeSq = range * range;
        workers = new ThreadLocal<VisibilityWorker>(() => new VisibilityWorker(server, palette));
    }

    public bool CanSee(ConnectedClient client, Entity entity, bool useCache = true)
    {
        if (!config.ConcealEntities || (!config.ConcealPlayers && entity is EntityPlayer)) return true;
        EntityPlayer? viewer = client.Entityplayer;
        if (viewer == null || ReferenceEquals(viewer, entity)) return true;
        if (viewer.Pos.Dimension != entity.Pos.Dimension) return false;
        if (entity.AllowOutsideLoadedRange || ReferenceEquals(viewer.MountedOn?.MountSupplier.OnEntity, entity)) return true;

        double distanceSq = viewer.Pos.SquareDistanceTo(entity.Pos);
        if (distanceSq <= config.AlwaysVisibleRange * config.AlwaysVisibleRange || distanceSq > maxRayRangeSq) return true;
        Cuboidf? box = entity.SelectionBox;
        if (box == null) return true;

        VisibilityWorker worker = workers.Value!;
        long epoch = Environment.TickCount64 / visibilityIntervalMs;
        if (epoch != worker.Epoch)
        {
            worker.Epoch = epoch;
            worker.Remaining = config.EntityRayBudgetPerThread;
            worker.Visible.Clear();
        }
        var key = (client.Id, entity.EntityId);
        bool visible;
        if (useCache && worker.Visible.TryGetValue(key, out visible)) return visible;
        if (worker.Remaining < samplesPerEntity)
        {
            // Keep ordinary visibility when the ray budget cannot establish occlusion.
            Interlocked.Increment(ref budgetExhaustions);
            return true;
        }

        var origin = worker.Ray.origin;
        origin.Set(viewer.Pos.X + viewer.LocalEyePos.X, viewer.Pos.InternalY + viewer.LocalEyePos.Y, viewer.Pos.Z + viewer.LocalEyePos.Z);
        visible = clearRay(worker, entity.Pos.X + box.MidX, entity.Pos.InternalY + box.MidY, entity.Pos.Z + box.MidZ);
        for (int corner = 0; corner < boundsCorners && !visible; corner++)
        {
            double x = (corner & 1) == 0 ? box.X1 - boundsMargin : box.X2 + boundsMargin;
            double y = (corner & 2) == 0 ? box.Y1 - boundsMargin : box.Y2 + boundsMargin;
            double z = (corner & 4) == 0 ? box.Z1 - boundsMargin : box.Z2 + boundsMargin;
            visible = clearRay(worker, entity.Pos.X + x, entity.Pos.InternalY + y, entity.Pos.Z + z);
        }
        if (useCache) worker.Visible[key] = visible;
        if (!visible) Interlocked.Increment(ref concealed);
        return visible;
    }

    private bool clearRay(VisibilityWorker worker, double x, double y, double z)
    {
        var ray = worker.Ray;
        ray.dir.Set(x - ray.origin.X, y - ray.origin.Y, z - ray.origin.Z);
        float distance = (float)ray.dir.Length();
        worker.Raycaster.LoadRayAndPos(ray);
        worker.Remaining--;
        Interlocked.Increment(ref rays);
        return worker.Raycaster.GetSelectedBlock(distance, worker.Filter) == null;
    }

    public void FilterTracking(Entity entity, List<ConnectedClient> clients, int threadIndex)
    {
        foreach (ConnectedClient client in clients)
        {
            List<Entity> tracked = client.threadedTrackedEntities[threadIndex];
            int last = tracked.Count - 1;
            if (last >= 0 && ReferenceEquals(tracked[last], entity) && !CanSee(client, entity)) tracked.RemoveAt(last);
        }
    }

    public void FilterSpawns(List<ConnectedClient> clients)
    {
        foreach (ConnectedClient client in clients)
        {
            List<Entity> spawns = client.EntitySpawnsToSend;
            for (int i = spawns.Count - 1; i >= 0; i--)
            {
                Entity entity = spawns[i];
                if (CanSee(client, entity)) continue;
                client.TrackedEntities.Remove(entity.EntityId);
                spawns.RemoveAt(i);
            }
        }
    }

    public void Dispose() => workers.Dispose();
}
