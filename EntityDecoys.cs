using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.Server;

namespace ServerGuard;

public class EntityDecoys : IDisposable
{
    private const int intervalMs = 250;
    private const int maxTotal = 64;
    private const int searchColumns = 4;
    private const int searchDepth = 32;
    private const int maxRange = 80;
    private const int chunkSize = GlobalConstants.ChunkSize;
    private const int chunkMask = chunkSize - 1;
    private const int chunkBits = 5;
    private readonly ICoreServerAPI api;
    private readonly ServerMain server;
    private readonly ServerGuardConfig config;
    private readonly BlockPalette palette;
    private readonly EntityVisibility visibility;
    private readonly List<Template> templates = [];
    private readonly Dictionary<ConnectedClient, List<Decoy>> clients = [];
    private readonly List<ConnectedClient> connected = [];
    private readonly List<ConnectedClient> disconnected = [];
    private readonly List<EntityDespawn> despawns = [];
    private readonly BlockPos scratch = new(Dimensions.NormalWorld);
    private readonly BlockPos spacePos = new(Dimensions.NormalWorld);
    private readonly long listenerId;
    private readonly int minRange;
    private int cursor;

    public int ActiveCount { get; private set; }
    public long Spawned { get; private set; }

    public class Settings
    {
        public string[] Types = null!;
    }

    private sealed class Template(EntityProperties properties, TreeAttribute health, float width, float height)
    {
        public readonly EntityProperties Properties = properties;
        public readonly TreeAttribute Health = health;
        public readonly Cuboidf Bounds = new(-width / 2, 0, -width / 2, width / 2, height, width / 2);
        public readonly int MinCell = (int)Math.Floor(0.5 - width / 2);
        public readonly int MaxCell = (int)Math.Ceiling(0.5 + width / 2) - 1;
        public readonly int Height = (int)Math.Ceiling(height);
    }

    private sealed class Decoy(EntityAgent entity, Template template, long expires)
    {
        public readonly EntityAgent Entity = entity;
        public readonly Template Template = template;
        public readonly long Expires = expires;
        public long NextPosition;
        public int Tick;
    }

    public EntityDecoys(ICoreServerAPI api, ServerMain server, ServerGuardConfig config, BlockPalette palette, EntityVisibility visibility)
    {
        this.api = api;
        this.server = server;
        this.config = config;
        this.palette = palette;
        this.visibility = visibility;
        minRange = Math.Max(32, config.AlwaysVisibleRange + 8);
        Settings settings = api.Assets.Get("serverguard:config/entities.json").ToObject<Settings>();
        if (settings.Types?.Length is not > 0) throw new ArgumentException("ServerGuard requires decoy entity patterns.");
        foreach (EntityProperties properties in api.World.EntityTypes)
        {
            if (properties.Class is not ("EntityAgent" or "EntityDrifter")) continue;
            bool matches = false;
            string code = properties.Code.ToString();
            foreach (string pattern in settings.Types)
            {
                if (WildcardUtil.Match(pattern, code)) matches = true;
            }
            if (!matches) continue;
            var size = properties.SelectionBoxSize ?? properties.CollisionBoxSize;
            float width = Math.Max(size.X, properties.CollisionBoxSize.X);
            float height = Math.Max(size.Y, properties.CollisionBoxSize.Y);
            if (properties.Class == "EntityDrifter")
            {
                float[]? alternate = properties.Attributes["alternativeCollisionBox"].AsArray<float>();
                if (alternate?.Length != 2) throw new ArgumentException($"Missing alternate drifter bounds for {code}.");
                width = Math.Max(width, alternate[0]);
                height = Math.Max(height, alternate[1]);
            }
            if (width is <= 0 or > 3 || height is <= 0 or > 3) throw new ArgumentException($"Unsupported decoy bounds for {code}.");
            foreach (JsonObject behavior in properties.Server.BehaviorsAsJsonObj)
            {
                if (behavior["code"].AsString() != "health") continue;
                float max = behavior["maxhealth"].AsFloat();
                if (max <= 0) throw new ArgumentException($"Missing decoy health for {properties.Code}.");
                float current = behavior["currenthealth"].AsFloat(max);
                TreeAttribute health = new();
                health.SetFloat("basemaxhealth", max);
                health.SetFloat("maxhealth", max);
                health.SetFloat("currenthealth", current);
                health.SetFloat("previousHealthValue", current);
                templates.Add(new Template(properties, health, width, height));
                break;
            }
        }
        if (templates.Count == 0) throw new ArgumentException("No supported ServerGuard decoy entity types were registered.");
        listenerId = api.Event.RegisterGameTickListener(onTick, intervalMs);
        api.Logger.Notification("ServerGuard decoys: {0} creatures per player, {1} creature variants, {2} total cap.", config.EntityDecoysPerPlayer, templates.Count, maxTotal);
    }

    private void onTick(float dt)
    {
        if (server.ShuttingDown) return;
        long now = Environment.TickCount64;
        connected.Clear();
        foreach (ConnectedClient client in server.Clients.Values)
        {
            if (client.Entityplayer == null || !client.State.ConnectedOrPlaying()) continue;
            connected.Add(client);
            if (!clients.ContainsKey(client)) clients.Add(client, []);
        }
        disconnected.Clear();
        foreach (var entry in clients)
        {
            if (!server.Clients.TryGetValue(entry.Key.Id, out ConnectedClient? current) || !ReferenceEquals(current, entry.Key) || current.Entityplayer == null || !current.State.ConnectedOrPlaying())
            {
                despawnAll(entry.Key, entry.Value);
                disconnected.Add(entry.Key);
            }
        }
        foreach (ConnectedClient client in disconnected) clients.Remove(client);
        if (connected.Count > 0)
        {
            if (cursor >= connected.Count) cursor = 0;
            for (int i = 0; i < connected.Count; i++)
            {
                ConnectedClient recipient = connected[(cursor + i) % connected.Count];
                refresh(recipient, clients[recipient], now);
            }
            ConnectedClient client = connected[cursor++];
            List<Decoy> decoys = clients[client];
            if (ActiveCount < maxTotal && decoys.Count < config.EntityDecoysPerPlayer) trySpawn(client, decoys, now);
        }
        ServerMain.FrameProfiler.Mark("serverguard-decoys");
    }

    private void refresh(ConnectedClient client, List<Decoy> decoys, long now)
    {
        despawns.Clear();
        for (int i = decoys.Count - 1; i >= 0; i--)
        {
            Decoy decoy = decoys[i];
            EntityAgent entity = decoy.Entity;
            scratch.SetAndCorrectDimension((int)Math.Floor(entity.Pos.X), (int)Math.Floor(entity.Pos.InternalY), (int)Math.Floor(entity.Pos.Z));
            if (now >= decoy.Expires || !inRange(client, entity.Pos) || !hasSpace(client, scratch, decoy.Template) || visibility.CanSeeDecoy(client, entity))
            {
                despawns.Add(new EntityDespawn { EntityId = entity.EntityId, DespawnData = new EntityDespawnData { Reason = EnumDespawnReason.OutOfRange } });
                decoys.RemoveAt(i);
                ActiveCount--;
                continue;
            }
            if (now < decoy.NextPosition) continue;
            decoy.NextPosition = now + 1000;
            decoy.Tick += 30;
            entity.Pos.HeadYaw = entity.Pos.Yaw + (float)(api.World.Rand.NextDouble() - 0.5);
            server.SendPacket(client.Id, new Packet_Server {
                Id = Packet_ServerIdEnum.EntitySpawnPosition,
                EntityPosition = ServerPackets.getEntityPositionPacket(entity.Pos, entity, decoy.Tick)
            });
        }
        sendDespawns(client);
    }

    private bool inRange(ConnectedClient client, EntityPos position)
    {
        var viewer = client.Entityplayer.Pos;
        if (viewer.Dimension != position.Dimension) return false;
        double distance = viewer.SquareDistanceTo(position);
        return distance > minRange * minRange && distance < maxRange * maxRange;
    }

    private bool hasSpace(ConnectedClient client, BlockPos position, Template template)
    {
        if (position.Y < 1) return false;
        for (int dy = -1; dy < template.Height; dy++)
        {
            for (int dz = template.MinCell; dz <= template.MaxCell; dz++)
            {
                for (int dx = template.MinCell; dx <= template.MaxCell; dx++)
                {
                    spacePos.Set(position).Add(dx, dy, dz);
                    int x = spacePos.X >> chunkBits;
                    int y = spacePos.InternalY >> chunkBits;
                    int z = spacePos.Z >> chunkBits;
                    if (!client.DidSendChunk(server.WorldMap.ChunkIndex3D(x, y, z))) return false;
                    IWorldChunk? chunk = server.BlockAccessor.GetChunk(x, y, z);
                    if (chunk == null || chunk.Disposed) return false;
                    int index = ((spacePos.Y & chunkMask) * chunkSize + (spacePos.Z & chunkMask)) * chunkSize + (spacePos.X & chunkMask);
                    int solid = chunk.UnpackAndReadBlock(index, BlockLayersAccess.Solid);
                    if (dy < 0)
                    {
                        if (!palette.Opaque[solid]) return false;
                    }
                    else if (solid != 0 || chunk.UnpackAndReadBlock(index, BlockLayersAccess.Fluid) != 0) return false;
                }
            }
        }
        return true;
    }

    private void trySpawn(ConnectedClient client, List<Decoy> decoys, long now)
    {
        Random random = api.World.Rand;
        Template template = templates[random.Next(templates.Count)];
        var properties = template.Properties;
        // A serialization-only instance avoids registering server AI, physics, or behavior callbacks.
        if (api.ClassRegistry.CreateEntity(properties) is not EntityAgent entity || entity is EntityPlayer) throw new InvalidOperationException($"Unsupported decoy class for {properties.Code}.");
        entity.Api = api;
        entity.World = server;
        entity.Code = properties.Code;
        entity.Tags = properties.Tags;
        entity.SelectionBox = template.Bounds;
        var viewer = client.Entityplayer.Pos;
        entity.Pos.Dimension = viewer.Dimension;
        for (int column = 0; column < searchColumns; column++)
        {
            double angle = random.NextDouble() * Math.PI * 2;
            double radius = minRange + 4 + random.NextDouble() * (maxRange - minRange - 4);
            int x = (int)Math.Floor(viewer.X + Math.Cos(angle) * radius);
            int z = (int)Math.Floor(viewer.Z + Math.Sin(angle) * radius);
            for (int depth = 0; depth < searchDepth; depth++)
            {
                scratch.Set(x, (int)Math.Floor(viewer.Y) + 8 - depth, z);
                scratch.dimension = viewer.Dimension;
                entity.Pos.SetPos(x + 0.5, scratch.Y, z + 0.5);
                if (!inRange(client, entity.Pos) || !hasSpace(client, scratch, template) || visibility.CanSeeDecoy(client, entity)) continue;
                bool occupied = false;
                foreach (Decoy existing in decoys)
                {
                    if (existing.Entity.Pos.SquareDistanceTo(entity.Pos) < 9) occupied = true;
                }
                if (occupied) break;
                // Reserve IDs from the native main-thread allocator without spawning into the world.
                entity.EntityId = ++((SaveGame)api.WorldManager.SaveGame).LastEntityId;
                entity.Pos.Yaw = (float)(random.NextDouble() * Math.PI * 2);
                entity.PositionBeforeFalling.Set(entity.Pos.X, entity.Pos.Y, entity.Pos.Z);
                entity.WatchedAttributes.SetAttribute("health", template.Health.Clone());
                entity.WatchedAttributes.SetInt("textureIndex", random.Next(properties.Client.TexturesAlternatesCount + 1));
                Packet_Entity packet = ServerPackets.GetEntityPacket(entity);
                decoys.Add(new Decoy(entity, template, now + random.Next(30000, 60001)));
                ActiveCount++;
                Spawned++;
                server.SendPacket(client.Id, new Packet_Server {
                    Id = Packet_ServerIdEnum.EntitySpawn,
                    EntitySpawn = new Packet_EntitySpawn { Entity = [packet], EntityCount = 1, EntityLength = 1 }
                });
                return;
            }
        }
    }

    private void despawnAll(ConnectedClient client, List<Decoy> decoys)
    {
        despawns.Clear();
        foreach (Decoy decoy in decoys)
        {
            despawns.Add(new EntityDespawn { EntityId = decoy.Entity.EntityId, DespawnData = new EntityDespawnData { Reason = EnumDespawnReason.OutOfRange } });
        }
        if (client.State.ConnectedOrPlaying()) sendDespawns(client);
        ActiveCount -= decoys.Count;
        decoys.Clear();
    }

    private void sendDespawns(ConnectedClient client)
    {
        if (despawns.Count == 0) return;
        server.SendPacket(client.Id, ServerPackets.GetEntityDespawnPacket(despawns));
    }

    public void Dispose()
    {
        api.Event.UnregisterGameTickListener(listenerId);
        foreach (var entry in clients) despawnAll(entry.Key, entry.Value);
        clients.Clear();
    }
}
