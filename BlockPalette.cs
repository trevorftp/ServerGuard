using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

namespace ServerGuard;

public class BlockPalette
{
    public class Settings
    {
        public string HostPattern = null!;
        public string OrePattern = null!;
        public string HostCode = null!;
    }

    public readonly bool[] Opaque;
    public readonly int[] Host;
    public readonly int[][] Decoys;
    public readonly int OreCount;

    public BlockPalette(IWorldAccessor world, Settings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.HostPattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.OrePattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.HostCode);
        Opaque = new bool[world.Blocks.Count];
        Host = new int[world.Blocks.Count];
        Decoys = new int[world.Blocks.Count][];
        Array.Fill(Decoys, Array.Empty<int>());
        Dictionary<int, List<int>> ores = [];

        foreach (Block block in world.Blocks)
        {
            if (block.Code == null) continue;
            bool opaque = block.SideOpaque.All && block.SideSolid.All && block.DrawType == EnumDrawType.Cube;
            Opaque[block.Id] = opaque;
            if (!opaque || block.EntityClass != null || block.LightHsv[2] != 0) continue;
            bool ore = WildcardUtil.Match(settings.OrePattern, block.Code.ToString());
            if (!ore && !WildcardUtil.Match(settings.HostPattern, block.Code.ToString())) continue;
            string? rock = block.Variant["rock"];
            if (rock == null) continue;
            Block? host = world.GetBlock(AssetLocation.Create(settings.HostCode.Replace("{rock}", rock), block.Code.Domain));
            if (host == null || host.Id == 0) throw new InvalidOperationException($"Missing host rock for {block.Code}.");
            Host[block.Id] = host.Id;
            if (!ore) continue;
            if (!ores.TryGetValue(host.Id, out var variants)) ores[host.Id] = variants = [];
            variants.Add(block.Id);
            OreCount++;
        }

        foreach (var entry in ores) Decoys[entry.Key] = entry.Value.ToArray();
        if (OreCount == 0) throw new InvalidOperationException("ServerGuard did not resolve any supported ore blocks.");
    }

    public bool IsOpaque(int id) => id < 0 || Opaque[id];
}
