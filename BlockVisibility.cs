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
    private const int clusterRegionShift = 4;
    private static readonly (int X, int Y, int Z)[] clusterShapes = [(1, 1, 1), (2, 1, 1), (1, 1, 2), (1, 2, 1), (0, 1, 0), (2, 0, 2)];
    private static readonly (int X, int Y, int Z)[] secondRing = buildSecondRing();
    private readonly ServerMain server;
    private readonly ServerGuardConfig config;
    private readonly BlockPalette palette;
    private readonly int salt = RandomNumberGenerator.GetInt32(int.MaxValue);
    private readonly object sync = new();
    private readonly int[] source = new int[chunkVolume];
    private readonly bool[] opaqueCells = new bool[chunkVolume];
    private readonly bool[] enclosedCells = new bool[chunkVolume];
    private readonly int[] compressionBuffer = new int[ChunkDataLayer.DATASLICES * ChunkDataLayer.SLICESIZE];
    private readonly ChunkDataPool pool;
    private readonly ChunkDataLayer output;
    private readonly IWorldChunk?[] neighbors = new IWorldChunk?[BlockFacing.NumberOfFaces];
    private readonly BlockPos pos = new(Dimensions.NormalWorld);
    private readonly BlockPos neighborPos = new(Dimensions.NormalWorld);
    private readonly BlockPos ringPos = new(Dimensions.NormalWorld);
    private readonly BlockPos changedPos = new(Dimensions.NormalWorld);
    private readonly BlockPos adjacentPos = new(Dimensions.NormalWorld);
    private readonly BlockPos seamPos = new(Dimensions.NormalWorld);
    // Only valid for one resend. Block changes that bypass these hooks would leave it stale.
    private readonly Dictionary<(int X, int Y, int Z), bool> knownEnclosure = [];
    // The loading column's face layer, with one extra row above and below, and the layer behind it.
    private readonly bool[] seamOpening = new bool[(chunkSize + 2) * chunkSize];
    private readonly bool[] seamOpeningDeep = new bool[chunkSize * chunkSize];
    private readonly List<BlockPos> seamCells = [];
    private readonly List<int> seamViews = [];
    // Set while computing what a client was sent before this column loaded, when its reads came back missing.
    private (int X, int Z, int MinY, int MaxY)? hiddenColumn;
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

    public void MaskIdentification(Packet_ServerIdentification identification, bool controlServerPrivilege)
    {
        // The world seed lets any client recreate deposit positions offline
        if (config.ConcealOre && !controlServerPrivilege) identification.Seed = 0;
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
            for (int i = 0; i < chunkVolume; i++)
            {
                int id = source[i];
                opaqueCells[i] = palette.Opaque[id];
                if (palette.Host[id] != 0) hasHost = true;
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
                            enclosedCells[index] = opaqueCells[index] && enclosed(x, y, z);
                        }
                    }
                }
                for (int y = 0; y < chunkSize; y++)
                {
                    for (int z = 0; z < chunkSize; z++)
                    {
                        for (int x = 0; x < chunkSize; x++)
                        {
                            int index = (y * chunkSize + z) * chunkSize + x;
                            int id = source[index];
                            int host = palette.Host[id];
                            if (host != 0 && enclosedCells[index])
                            {
                                id = buried(x, y, z) ? replacement(host, packet.X * chunkSize + x, packet.Y * chunkSize + y, packet.Z * chunkSize + z) : host;
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
        return opaqueAt(x - 1, y, z)
            && opaqueAt(x + 1, y, z)
            && opaqueAt(x, y - 1, z)
            && opaqueAt(x, y + 1, z)
            && opaqueAt(x, y, z - 1)
            && opaqueAt(x, y, z + 1);
    }

    private bool opaqueAt(int x, int y, int z)
    {
        if ((uint)x < chunkSize && (uint)y < chunkSize && (uint)z < chunkSize) return opaqueCells[(y * chunkSize + z) * chunkSize + x];
        return palette.IsOpaque(readNeighbor(x, y, z));
    }

    // Decoys stay at least two layers from any exposed face.
    // The client removes a mined block itself before the server can resend the layer behind it.
    private bool buried(int x, int y, int z)
    {
        if (onChunkEdge(x, y, z)) return false;
        return enclosedAt(x - 1, y, z)
            && enclosedAt(x + 1, y, z)
            && enclosedAt(x, y - 1, z)
            && enclosedAt(x, y + 1, z)
            && enclosedAt(x, y, z - 1)
            && enclosedAt(x, y, z + 1);
    }

    private bool enclosedAt(int x, int y, int z)
    {
        if ((uint)x < chunkSize && (uint)y < chunkSize && (uint)z < chunkSize) return enclosedCells[(y * chunkSize + z) * chunkSize + x];
        return enclosed(x, y, z);
    }

    // Cells along a chunk edge would need a diagonal chunk for their second ring, so they never hold a decoy.
    private static bool onChunkEdge(int x, int y, int z)
    {
        bool edgeX = (x & chunkMask) is 0 or chunkMask;
        bool edgeY = (y & chunkMask) is 0 or chunkMask;
        bool edgeZ = (z & chunkMask) is 0 or chunkMask;
        return edgeX ? edgeY || edgeZ : edgeY && edgeZ;
    }

    // Reads a cell outside the chunk from one of the six face-adjacent chunks, which onChunkEdge guarantees is enough.
    private int readNeighbor(int x, int y, int z)
    {
        BlockFacing face;
        if ((uint)x >= chunkSize) face = x < 0 ? BlockFacing.WEST : BlockFacing.EAST;
        else if ((uint)y >= chunkSize) face = y < 0 ? BlockFacing.DOWN : BlockFacing.UP;
        else face = z < 0 ? BlockFacing.NORTH : BlockFacing.SOUTH;
        IWorldChunk? chunk = neighbors[face.Index];
        if (chunk == null || chunk.Disposed) return -1;
        return chunk.UnpackAndReadBlock(((y & chunkMask) * chunkSize + (z & chunkMask)) * chunkSize + (x & chunkMask), BlockLayersAccess.Solid);
    }

    private int replacement(int host, int x, int y, int z)
    {
        int[] variants = palette.Decoys[host];
        if (config.DecoyPercent == 0 || variants.Length == 0) return host;
        // Coarse noise picks a cluster shape per region so decoy pockets vary between blobs, streaks, and thin seams.
        // Small clusters retain compression and use the same rule for stone and real ore.
        uint region = (uint)GameMath.MurmurHash3((x >> clusterRegionShift) ^ salt, y >> clusterRegionShift, z >> clusterRegionShift);
        var (shiftX, shiftY, shiftZ) = clusterShapes[region % (uint)clusterShapes.Length];
        uint noise = (uint)GameMath.MurmurHash3((x >> shiftX) ^ salt, y >> shiftY, z >> shiftZ);
        return noise % 100 < config.DecoyPercent ? variants[noise / 100 % (uint)variants.Length] : host;
    }

    private int getView(int id, BlockPos position)
    {
        int host = palette.Host[id];
        if (host == 0 || !enclosed(position)) return id;
        return buried(position) ? replacement(host, position.X, position.InternalY, position.Z) : host;
    }

    private bool enclosed(BlockPos position)
    {
        var key = (position.X, position.InternalY, position.Z);
        if (knownEnclosure.TryGetValue(key, out bool known)) return known;
        bool result = true;
        neighborPos.Set(position);
        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            face.IterateThruFacingOffsets(neighborPos);
            if (palette.IsOpaque(getBlock(neighborPos))) continue;
            result = false;
            break;
        }
        knownEnclosure[key] = result;
        return result;
    }

    private bool buried(BlockPos position)
    {
        if (onChunkEdge(position.X, position.InternalY, position.Z)) return false;
        ringPos.Set(position);
        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            face.IterateThruFacingOffsets(ringPos);
            if (!enclosed(ringPos)) return false;
        }
        return true;
    }

    // A second-ring cell only needs resending when its hash could place a decoy there.
    private bool mayShowDecoy(int id, BlockPos position)
    {
        int host = palette.Host[id];
        return host != 0 && replacement(host, position.X, position.InternalY, position.Z) != host;
    }

    private int getBlock(BlockPos position)
    {
        int chunkX = position.X >> chunkBits;
        int chunkY = position.InternalY >> chunkBits;
        int chunkZ = position.Z >> chunkBits;
        if (hiddenColumn is { } hidden && chunkX == hidden.X && chunkZ == hidden.Z && chunkY >= hidden.MinY && chunkY < hidden.MaxY) return -1;
        IWorldChunk? chunk = server.BlockAccessor.GetChunk(chunkX, chunkY, chunkZ);
        if (chunk == null || chunk.Disposed) return -1;
        return chunk.UnpackAndReadBlock(((position.Y & chunkMask) * chunkSize + (position.Z & chunkMask)) * chunkSize + (position.X & chunkMask), BlockLayersAccess.Solid);
    }

    public int MaskSingle(IServerPlayer player, int id, int x, int y, int z)
    {
        // Negative packet IDs encode the fluid layer as -blockId - 1, including fluid removal.
        if (id < 0 || !config.ConcealOre) return id;
        lock (sync)
        {
            knownEnclosure.Clear();
            ConnectedClient client = server.Clients[player.ClientId];
            BlockPos changed = changedPos.SetAndCorrectDimension(x, y, z);
            invalidateAround(changed);
            BlockPos adjacent = adjacentPos.Set(changed);
            foreach (BlockFacing face in BlockFacing.ALLFACES)
            {
                face.IterateThruFacingOffsets(adjacent);
                int actual = getBlock(adjacent);
                if (actual >= 0 && palette.Host[actual] != 0) sendView(client, adjacent, actual);
            }
            foreach (var (dx, dy, dz) in secondRing)
            {
                adjacent.Set(changed).Add(dx, dy, dz);
                int actual = getBlock(adjacent);
                if (actual >= 0 && mayShowDecoy(actual, adjacent)) sendView(client, adjacent, actual);
            }
            return getView(id, changed);
        }
    }

    private void sendView(ConnectedClient client, BlockPos position, int actual)
    {
        if (!didSend(client, position)) return;
        server.SendPacket(client.Id, new Packet_Server {
            Id = Packet_ServerIdEnum.ExchangeBlock,
            ExchangeBlock = new Packet_ServerExchangeBlock {
                X = position.X,
                Y = position.InternalY,
                Z = position.Z,
                BlockType = getView(actual, position)
            }
        });
    }

    public void SendUpdates(List<BlockPos> positions, int packetId)
    {
        lock (sync)
        {
            for (int start = 0; start < positions.Count; start += updateBatchSize)
            {
                knownEnclosure.Clear();
                Dictionary<(int X, int Y, int Z), HashSet<BlockPos>> groups = [];
                int end = Math.Min(start + updateBatchSize, positions.Count);
                for (int i = start; i < end; i++)
                {
                    BlockPos changed = positions[i];
                    invalidateAround(changed);
                    addUpdate(groups, changed);
                    BlockPos adjacent = adjacentPos.Set(changed);
                    foreach (BlockFacing face in BlockFacing.ALLFACES)
                    {
                        face.IterateThruFacingOffsets(adjacent);
                        int id = getBlock(adjacent);
                        if (id >= 0 && palette.Host[id] != 0) addUpdate(groups, adjacent.Copy());
                    }
                    foreach (var (dx, dy, dz) in secondRing)
                    {
                        adjacent.Set(changed).Add(dx, dy, dz);
                        int id = getBlock(adjacent);
                        if (id >= 0 && mayShowDecoy(id, adjacent)) addUpdate(groups, adjacent.Copy());
                    }
                }
                sendGroups(groups, packetId);
            }
        }
        ServerMain.FrameProfiler.Mark("serverguard-blockupdates");
    }

    private void sendGroups(Dictionary<(int X, int Y, int Z), HashSet<BlockPos>> groups, int packetId)
    {
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
        foreach (var (dx, dy, dz) in secondRing)
        {
            pos.Set(position).Add(dx, dy, dz);
            invalidate(pos.X >> chunkBits, pos.InternalY >> chunkBits, pos.Z >> chunkBits);
        }
    }

    private static (int X, int Y, int Z)[] buildSecondRing()
    {
        const int distance = 2;
        List<(int X, int Y, int Z)> ring = [];
        for (int dy = -distance; dy <= distance; dy++)
        {
            for (int dz = -distance; dz <= distance; dz++)
            {
                for (int dx = -distance; dx <= distance; dx++)
                {
                    if (Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) == distance) ring.Add((dx, dy, dz));
                }
            }
        }
        return [.. ring];
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
            int minY = dimension * GlobalConstants.DimensionSizeInChunks;
            int maxY = minY + request.Chunks.Length;
            Dictionary<(int X, int Y, int Z), HashSet<BlockPos>> groups = [];
            for (int chunkY = minY; chunkY < maxY; chunkY++)
            {
                invalidate(request.ChunkX, chunkY, request.ChunkZ);
                foreach (BlockFacing face in BlockFacing.HORIZONTALS)
                {
                    pos.SetAndCorrectDimension(request.ChunkX, chunkY, request.ChunkZ).Add(face);
                    invalidate(pos.X, pos.InternalY, pos.Z);
                    if (anySent(server.WorldMap.ChunkIndex3D(pos.X, pos.InternalY, pos.Z))) addSeam(groups, request.ChunkX, chunkY, request.ChunkZ, face, minY, maxY);
                }
            }
            sendGroups(groups, Packet_ServerIdEnum.SetBlocksNoRelight);
        }
    }

    private bool anySent(long chunkIndex)
    {
        foreach (ConnectedClient client in server.Clients.Values)
        {
            if (client.DidSendChunk(chunkIndex)) return true;
        }
        return false;
    }

    private void addSeam(Dictionary<(int X, int Y, int Z), HashSet<BlockPos>> groups, int columnX, int chunkY, int columnZ, BlockFacing face, int minY, int maxY)
    {
        bool alongX = face.Axis == EnumAxis.X;
        int step = alongX ? face.Normali.X : face.Normali.Z;
        int columnEdge = (alongX ? columnX : columnZ) * chunkSize + (step > 0 ? chunkMask : 0);
        int baseU = (alongX ? columnZ : columnX) * chunkSize;
        int baseY = chunkY * chunkSize;
        bool anyOpening = false;
        for (int v = -1; v <= chunkSize; v++)
        {
            for (int u = 0; u < chunkSize; u++)
            {
                bool open = !palette.IsOpaque(getBlock(seamCell(alongX, columnEdge, baseU + u, baseY + v)));
                seamOpening[(v + 1) * chunkSize + u] = open;
                anyOpening |= open;
            }
        }
        for (int v = 0; v < chunkSize; v++)
        {
            for (int u = 0; u < chunkSize; u++)
            {
                bool open = !palette.IsOpaque(getBlock(seamCell(alongX, columnEdge - step, baseU + u, baseY + v)));
                seamOpeningDeep[v * chunkSize + u] = open;
                anyOpening |= open;
            }
        }
        if (!anyOpening) return;

        seamCells.Clear();
        for (int depth = 0; depth < 2; depth++)
        {
            for (int v = 0; v < chunkSize; v++)
            {
                for (int u = 0; u < chunkSize; u++)
                {
                    if (!nearOpening(u, v, depth)) continue;
                    BlockPos cell = seamCell(alongX, columnEdge + step * (depth + 1), baseU + u, baseY + v);
                    int id = getBlock(cell);
                    if (id >= 0 && palette.Host[id] != 0) seamCells.Add(cell.Copy());
                }
            }
        }
        if (seamCells.Count == 0) return;

        seamViews.Clear();
        knownEnclosure.Clear();
        hiddenColumn = (columnX, columnZ, minY, maxY);
        try
        {
            foreach (BlockPos cell in seamCells) seamViews.Add(getView(getBlock(cell), cell));
        }
        finally
        {
            hiddenColumn = null;
            knownEnclosure.Clear();
        }
        for (int i = 0; i < seamCells.Count; i++)
        {
            BlockPos cell = seamCells[i];
            if (getView(getBlock(cell), cell) != seamViews[i]) addUpdate(groups, cell);
        }
    }

    // Cells beside a seam cell along the seam belong to other columns, which this load does not change.
    private bool nearOpening(int u, int v, int depth)
    {
        int row = (v + 1) * chunkSize + u;
        if (seamOpening[row]) return true;
        if (depth == 1) return false;
        return seamOpeningDeep[v * chunkSize + u]
            || seamOpening[row - chunkSize]
            || seamOpening[row + chunkSize]
            || (u > 0 && seamOpening[row - 1])
            || (u < chunkMask && seamOpening[row + 1]);
    }

    private BlockPos seamCell(bool alongX, int along, int u, int y) => alongX ? seamPos.SetAndCorrectDimension(along, y, u) : seamPos.SetAndCorrectDimension(u, y, along);
}
