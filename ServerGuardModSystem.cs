using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace ServerGuard;

public class ServerGuardModSystem : ModSystem
{
    private const string harmonyId = "serverguard.network";
    private ServerGuardConfig config = null!;
    private BlockVisibility blocks = null!;
    private EntityVisibility entities = null!;
    private EntityDecoys? decoys;
    private AccessTools.FieldRef<ChunkColumnLoadRequest, int> getDimension = null!;
    private Harmony harmony = null!;
    private static ServerGuardModSystem? active;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        string? version = typeof(GameVersion).GetField(nameof(GameVersion.ShortGameVersion))?.GetRawConstantValue() as string;
        if (version != "1.22.7") throw new NotSupportedException($"ServerGuard requires Vintage Story 1.22.7. Found {version}.");
        if (api.World is not ServerMain server) throw new NotSupportedException("ServerGuard requires the standard Vintage Story server.");
        if (active != null) throw new InvalidOperationException("ServerGuard is already running in this process.");

        config = api.LoadModConfig<ServerGuardConfig>("serverguard.json") ?? new ServerGuardConfig();
        config.Validate();
        api.StoreModConfig(config, "serverguard.json");
        BlockPalette palette = new(api.World, api.Assets.Get("serverguard:config/blocks.json").ToObject<BlockPalette.Settings>());
        blocks = new BlockVisibility(server, config, palette);
        getDimension = AccessTools.FieldRefAccess<ChunkColumnLoadRequest, int>("dimension");
        entities = new EntityVisibility(server, config, palette);
        harmony = new Harmony(harmonyId);
        try
        {
            patch(typeof(ServerChunk), "ToPacket", [typeof(int), typeof(int), typeof(int), typeof(bool)], nameof(afterChunk), false);
            patch(typeof(ServerMain), "SendSetBlocksPacket", [typeof(List<BlockPos>), typeof(int)], nameof(beforeBlocks), true);
            patch(typeof(ServerMain), "SendSetBlock", [typeof(IServerPlayer), typeof(int), typeof(int), typeof(int), typeof(int), typeof(bool)], nameof(beforeBlock), true);
            patch(typeof(PhysicsManager), "UpdateTrackedEntityState", [typeof(Entity), typeof(List<ConnectedClient>), typeof(int)], nameof(afterTracking), false);
            patch(typeof(PhysicsManager), "PrepareEntitySpawns", [typeof(Entity[]), typeof(List<ConnectedClient>)], nameof(afterSpawns), false);
            patch(typeof(PhysicsManager), "SendPrioritySpawn", [typeof(Entity), typeof(ICollection<ConnectedClient>)], nameof(beforePrioritySpawn), true);
            patch(typeof(ServerMain).Assembly.GetType("Vintagestory.Server.ServerSystemSupplyChunks", true)!, "mainThreadLoadChunkColumn", [typeof(ChunkColumnLoadRequest)], nameof(afterColumnLoaded), false);
            api.ChatCommands.Create("serverguard")
                .WithDescription(Lang.Get("serverguard:command-description"))
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(onStatus);
            active = this;
            if (config.ConcealEntities && config.EntityDecoysPerPlayer > 0) decoys = new EntityDecoys(api, server, config, palette, entities);
        }
        catch
        {
            active = null;
            disposeProtection();
            throw;
        }

        api.Logger.Notification("ServerGuard loaded with {0} ore variants. Ore concealment: {1}, entity concealment: {2}, player concealment: {3}.", palette.OreCount, config.ConcealOre, config.ConcealEntities, config.ConcealEntities && config.ConcealPlayers);
    }

    private void patch(Type owner, string name, Type[] parameters, string handler, bool prefix)
    {
        MethodInfo method = AccessTools.DeclaredMethod(owner, name, parameters)
            ?? throw new MissingMethodException(owner.FullName, name);
        HarmonyMethod hook = new(typeof(ServerGuardModSystem), handler);
        harmony.Patch(method, prefix: prefix ? hook : null, postfix: prefix ? null : hook);
    }

    private TextCommandResult onStatus(TextCommandCallingArgs args)
    {
        if (!ReferenceEquals(active, this)) return TextCommandResult.Error(Lang.Get("serverguard:inactive"));
        return TextCommandResult.Success(Lang.Get("serverguard:status",
            config.ConcealOre, config.ConcealEntities, blocks.ChunksMasked, blocks.CacheHits, blocks.CacheBytes, blocks.BlocksMasked, entities.Rays, entities.Concealed, entities.BudgetExhaustions, decoys?.ActiveCount ?? 0, decoys?.Spawned ?? 0, config.ConcealEntities && config.ConcealPlayers));
    }

    private static void afterChunk(Packet_ServerChunk __result) => active?.blocks.MaskChunk(__result);

    private static void afterColumnLoaded(ChunkColumnLoadRequest chunkRequest)
    {
        ServerGuardModSystem? guard = active;
        if (guard != null) guard.blocks.OnColumnLoaded(chunkRequest, guard.getDimension(chunkRequest));
    }

    private static bool beforeBlocks(List<BlockPos> positions, int packetId)
    {
        ServerGuardModSystem? guard = active;
        if (guard?.config.ConcealOre != true) return true;
        guard.blocks.SendUpdates(positions, packetId);
        return false;
    }

    private static void beforeBlock(IServerPlayer player, ref int blockId, int posX, int posY, int posZ)
    {
        ServerGuardModSystem? guard = active;
        if (guard != null) blockId = guard.blocks.MaskSingle(player, blockId, posX, posY, posZ);
    }

    private static void afterTracking(Entity entity, List<ConnectedClient> clients, int zeroBasedThreadNum)
    {
        ServerGuardModSystem? guard = active;
        if (guard?.config.ConcealEntities == true) guard.entities.FilterTracking(entity, clients, zeroBasedThreadNum);
    }

    private static void afterSpawns(List<ConnectedClient> clientList)
    {
        ServerGuardModSystem? guard = active;
        if (guard?.config.ConcealEntities == true) guard.entities.FilterSpawns(clientList);
    }

    private static void beforePrioritySpawn(Entity entity, ref ICollection<ConnectedClient> clientList)
    {
        ServerGuardModSystem? guard = active;
        if (guard?.config.ConcealEntities != true) return;
        List<ConnectedClient> visible = [];
        foreach (ConnectedClient client in clientList)
        {
            if (guard.entities.CanSee(client, entity)) visible.Add(client);
        }
        clientList = visible;
    }

    public override void Dispose()
    {
        if (!ReferenceEquals(active, this)) return;
        active = null;
        disposeProtection();
    }

    private void disposeProtection()
    {
        try
        {
            decoys?.Dispose();
        }
        finally
        {
            try
            {
                harmony.UnpatchAll(harmonyId);
            }
            finally
            {
                entities.Dispose();
            }
        }
    }
}
