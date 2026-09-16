using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

namespace ServerGuard;

public class BlockVisibility
{
    private const int chunkSize = GlobalConstants.ChunkSize;
    private const int chunkMask = chunkSize - 1;
    private const int chunkBits = 5;
    private const int chunkVolume = chunkSize * chunkSize * chunkSize;
    private const int maxCachedChunks = 4096;
    private const int updateBatchSize = 512;
    private readonly ServerMain server;
    private readonly ServerGuardConfig config;
    private readonly BlockPalette palette;
    private readonly int salt = RandomNumberGenerator.GetInt32(int.MaxValue);
    private readonly object sync = new();
    private readonly int[] source = new int[chunkVolume];
    private readonly int[] compressionBuffer = new int[ChunkDataLayer.DATASLICES * ChunkDataLayer.SLICESIZE];
    private readonly ChunkDataPool pool;
    private readonly ChunkDataLayer output;
    private readonly IWorldChunk?[] neighbors = new IWorldChunk?[BlockFacing.NumberOfFaces];
    private readonly BlockPos pos = new(Dimensions.NormalWorld);
    private readonly BlockPos neighborPos = new(Dimensions.NormalWorld);
    private readonly Dictionary<(int X, int Y, int Z), LinkedListNode<CacheEntry>> cache = [];
    private readonly LinkedList<CacheEntry> cacheOrder = [];
    private readonly Action<int[], byte[], byte[], int> unpack;
    private long cacheBytes;

    public long CacheHits { get; private set; }
    public long ChunksMasked { get; private set; }
    public long BlocksMasked { get; private set; }
    public long CacheBytes => cacheBytes;

    private sealed class CacheEntry((int X, int Y, int Z) key, byte[] original, byte[] masked)
    {
        public readonly (int X, int Y, int Z) Key = key;
        public readonly byte[] Original = original;
        public readonly byte[] Masked = masked;
        public long Bytes => Original.LongLength + (ReferenceEquals(Original, Masked) ? 0 : Masked.LongLength);
    }

    public BlockVisibility(ServerMain server, ServerGuardConfig config, BlockPalette palette)
    {
        this.server = server;
        this.config = config;
        this.palette = palette;
        pool = new ChunkDataPool(chunkSize, server);
        output = new ChunkDataLayer(pool);
        unpack = AccessTools.Method(typeof(ChunkData), "UnpackBlocksTo", [typeof(int[]), typeof(byte[]), typeof(byte[]), typeof(int)])
            .CreateDelegate<Action<int[], byte[], byte[], int>>();
    }

    public void MaskChunk(Packet_ServerChunk packet)
    {
        if (!config.ConcealOre || packet.Empty != 0) return;
        if (packet.Compver != 2) throw new InvalidOperationException($"Unsupported chunk compression version {packet.Compver}.");

        lock (sync)
        {
            var key = (packet.X, packet.Y, packet.Z);
            if (cache.TryGetValue(key, out var cached))
            {
                if (ReferenceEquals(cached.Value.Original, packet.Blocks))
                {
                    cacheOrder.Remove(cached);
                    cacheOrder.AddLast(cached);
                    packet.SetBlocks(cached.Value.Masked);
                    CacheHits++;
                    return;
                }
                remove(cached);
            }

            unpack(source, packet.Blocks, packet.LightSat, packet.Compver);
            bool hasHost = false;
            foreach (int id in source)
            {
                if (palette.Host[id] == 0) continue;
                hasHost = true;
                break;
            }
            if (!hasHost)
            {
                addToCache(new CacheEntry(key, packet.Blocks, packet.Blocks));
                return;
            }

            pos.SetAndCorrectDimension(packet.X, packet.Y, packet.Z);
            neighborPos.Set(pos);
            foreach (BlockFacing face in BlockFacing.ALLFACES)
            {
                face.IterateThruFacingOffsets(neighborPos);
                neighbors[face.Index] = server.BlockAccessor.GetChunk(neighborPos.X, neighborPos.InternalY, neighborPos.Z);
            }

            output.PopulateWithAir();
            int changed = 0;
            try
            {
                for (int y = 0; y < chunkSize; y++)
                {
                    for (int z = 0; z < chunkSize; z++)
                    {
                        for (int x = 0; x < chunkSize; x++)
                        {
                            int index = (y * chunkSize + z) * chunkSize + x;
                            int id = source[index];
                            int host = palette.Host[id];
                            if (host != 0 && enclosed(x, y, z))
                            {
                                id = replacement(host, packet.X * chunkSize + x, packet.Y * chunkSize + y, packet.Z * chunkSize + z);
                                changed++;
                            }
                            output.SetUnsafe(index, id);
                        }
                    }
                }

                byte[] masked = ChunkDataLayer.Compress(output, compressionBuffer);
                addToCache(new CacheEntry(key, packet.Blocks, masked));
                packet.SetBlocks(masked);
                ChunksMasked++;
                BlocksMasked += changed;
            }
            finally
            {
                pool.FreeArrays(output);
                Array.Clear(neighbors);
            }
        }
        ServerMain.FrameProfiler.Mark("serverguard-chunks");
    }

    private bool enclosed(int x, int y, int z)
    {
        return palette.IsOpaque(read(x - 1, y, z, BlockFacing.WEST))
            && palette.IsOpaque(read(x + 1, y, z, BlockFacing.EAST))
            && palette.IsOpaque(read(x, y - 1, z, BlockFacing.DOWN))
            && palette.IsOpaque(read(x, y + 1, z, BlockFacing.UP))
            && palette.IsOpaque(read(x, y, z - 1, BlockFacing.NORTH))
            && palette.IsOpaque(read(x, y, z + 1, BlockFacing.SOUTH));
    }

    private int read(int x, int y, int z, BlockFacing face)
    {
        if ((uint)x < chunkSize && (uint)y < chunkSize && (uint)z < chunkSize) return source[(y * chunkSize + z) * chunkSize + x];
        IWorldChunk? chunk = neighbors[face.Index];
        if (chunk == null || chunk.Disposed) return -1;
        return chunk.UnpackAndReadBlock(((y & chunkMask) * chunkSize + (z & chunkMask)) * chunkSize + (x & chunkMask), BlockLayersAccess.Solid);
    }

    private int replacement(int host, int x, int y, int z)
    {
        int[] variants = palette.Decoys[host];
        if (config.DecoyPercent == 0 || variants.Length == 0) return host;
        // Small clusters retain compression and use the same rule for stone and real ore.
        uint noise = (uint)GameMath.MurmurHash3((x >> 1) ^ salt, y >> 1, z >> 1);
        return noise % 100 < config.DecoyPercent ? variants[noise / 100 % (uint)variants.Length] : host;
    }

    private int getView(int id, BlockPos position)
    {
        int host = palette.Host[id];
        if (host == 0) return id;
        neighborPos.Set(position);
        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            face.IterateThruFacingOffsets(neighborPos);
            if (!palette.IsOpaque(getBlock(neighborPos))) return id;
        }
        return replacement(host, position.X, position.InternalY, position.Z);
    }

    private int getBlock(BlockPos position)
    {
        IWorldChunk? chunk = server.BlockAccessor.GetChunk(position.X >> chunkBits, position.InternalY >> chunkBits, position.Z >> chunkBits);
        if (chunk == null || chunk.Disposed) return -1;
        return chunk.UnpackAndReadBlock(((position.Y & chunkMask) * chunkSize + (position.Z & chunkMask)) * chunkSize + (position.X & chunkMask), BlockLayersAccess.Solid);
    }

    public int MaskSingle(IServerPlayer player, int id, int x, int y, int z)
    {
        // Negative packet IDs encode the fluid layer as -blockId - 1, including fluid removal.
        if (id < 0 || !config.ConcealOre) return id;
        lock (sync)
        {
            ConnectedClient client = server.Clients[player.ClientId];
            BlockPos changed = new BlockPos(Dimensions.NormalWorld).SetAndCorrectDimension(x, y, z);
            invalidateAround(changed);
            BlockPos adjacent = changed.Copy();
            foreach (BlockFacing face in BlockFacing.ALLFACES)
            {
                face.IterateThruFacingOffsets(adjacent);
                int actual = getBlock(adjacent);
                if (actual < 0 || palette.Host[actual] == 0 || !didSend(client, adjacent)) continue;
                server.SendPacket(player.ClientId, new Packet_Server {
                    Id = Packet_ServerIdEnum.ExchangeBlock,
                    ExchangeBlock = new Packet_ServerExchangeBlock {
                        X = adjacent.X,
                        Y = adjacent.InternalY,
                        Z = adjacent.Z,
                        BlockType = getView(actual, adjacent)
                    }
                });
            }
            return getView(id, changed);
        }
    }

    public void SendUpdates(List<BlockPos> positions, int packetId)
    {
        lock (sync)
        {
            for (int start = 0; start < positions.Count; start += updateBatchSize)
            {
                Dictionary<(int X, int Y, int Z), HashSet<BlockPos>> groups = [];
                int end = Math.Min(start + updateBatchSize, positions.Count);
                for (int i = start; i < end; i++)
                {
                    BlockPos changed = positions[i];
                    invalidateAround(changed);
                    addUpdate(groups, changed);
                    BlockPos adjacent = changed.Copy();
                    foreach (BlockFacing face in BlockFacing.ALLFACES)
                    {
                        face.IterateThruFacingOffsets(adjacent);
                        int id = getBlock(adjacent);
                        if (id >= 0 && palette.Host[id] != 0) addUpdate(groups, adjacent.Copy());
                    }
                }

                foreach (var group in groups)
                {
                    BlockPos[] updates = [.. group.Value];
                    Packet_ServerSetBlocks blocks = new();
                    blocks.SetSetBlocks(packUpdates(updates));
                    Packet_Server packet = new() { Id = packetId, SetBlocks = blocks };
                    foreach (ConnectedClient client in server.Clients.Values)
                    {
                        if (client.Player == null || !client.State.ConnectedOrPlaying() || !didSend(client, updates[0])) continue;
                        server.SendPacket(client.Id, packet);
                    }
                }
            }
        }
        ServerMain.FrameProfiler.Mark("serverguard-blockupdates");
    }

    private static void addUpdate(Dictionary<(int X, int Y, int Z), HashSet<BlockPos>> groups, BlockPos position)
    {
        var key = (position.X >> chunkBits, position.InternalY >> chunkBits, position.Z >> chunkBits);
        if (!groups.TryGetValue(key, out var group)) groups[key] = group = [];
        group.Add(position);
    }

    private byte[] packUpdates(BlockPos[] updates)
    {
        using MemoryStream stream = new(4 + updates.Length * 5 * sizeof(int));
        using BinaryWriter writer = new(stream);
        writer.Write(updates.Length);
        foreach (BlockPos position in updates) writer.Write(position.X);
        foreach (BlockPos position in updates) writer.Write(position.InternalY);
        foreach (BlockPos position in updates) writer.Write(position.Z);
        foreach (BlockPos position in updates)
        {
            int id = server.BlockAccessor.GetBlock(position, BlockLayersAccess.Solid).Id;
            writer.Write(getView(id, position));
        }
        foreach (BlockPos position in updates) writer.Write(server.BlockAccessor.GetBlock(position, BlockLayersAccess.Fluid).Id);
        return Compression.Compress(stream.ToArray());
    }

    private bool didSend(ConnectedClient client, BlockPos position)
    {
        return client.Entityplayer?.Pos.Dimension == position.dimension
            && client.DidSendChunk(server.WorldMap.ChunkIndex3D(position.X >> chunkBits, position.InternalY >> chunkBits, position.Z >> chunkBits));
    }

    private void invalidateAround(BlockPos position)
    {
        invalidate(position.X >> chunkBits, position.InternalY >> chunkBits, position.Z >> chunkBits);
        pos.Set(position);
        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            face.IterateThruFacingOffsets(pos);
            invalidate(pos.X >> chunkBits, pos.InternalY >> chunkBits, pos.Z >> chunkBits);
        }
    }

    private void invalidate(int x, int y, int z)
    {
        if (cache.TryGetValue((x, y, z), out var entry)) remove(entry);
    }

    private void remove(LinkedListNode<CacheEntry> entry)
    {
        cache.Remove(entry.Value.Key);
        cacheOrder.Remove(entry);
        cacheBytes -= entry.Value.Bytes;
    }

    private void addToCache(CacheEntry entry)
    {
        cache[entry.Key] = cacheOrder.AddLast(entry);
        cacheBytes += entry.Bytes;
        while (cacheBytes > config.ChunkCacheMiB * 1024L * 1024 || cache.Count > maxCachedChunks) remove(cacheOrder.First!);
    }

    public void OnColumnLoaded(IChunkColumnGenerateRequest request, int dimension)
    {
        if (!config.ConcealOre || server.ShuttingDown) return;
        lock (sync)
        {
            for (int y = 0; y < request.Chunks.Length; y++)
            {
                int chunkY = y + dimension * GlobalConstants.DimensionSizeInChunks;
                invalidate(request.ChunkX, chunkY, request.ChunkZ);
                foreach (BlockFacing face in BlockFacing.HORIZONTALS)
                {
                    pos.SetAndCorrectDimension(request.ChunkX, chunkY, request.ChunkZ).Add(face);
                    invalidate(pos.X, pos.InternalY, pos.Z);
                    long index = server.WorldMap.ChunkIndex3D(pos.X, pos.InternalY, pos.Z);
                    foreach (ConnectedClient client in server.Clients.Values)
                    {
                        if (client.DidSendChunk(index)) client.forceSendChunks.Add(index);
                    }
                }
            }
        }
    }
}
