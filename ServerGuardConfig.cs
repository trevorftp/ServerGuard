using System;

namespace ServerGuard;

public class ServerGuardConfig
{
    public bool ConcealOre = true;
    public int DecoyPercent = 12;
    public int ChunkCacheMiB = 64;
    public bool ConcealEntities = true;
    public bool ConcealPlayers = false;
    public int AlwaysVisibleRange = 16;
    public int EntityRayBudgetPerThread = 512;
    public int EntityDecoysPerPlayer = 32;
    public int EntityDecoysGlobalCap = 512;
    public int DecoyRayBudgetPerThread = 4096;
    public bool ConcealPlayerInventory = true;
    public bool ConcealContainers = true;

    public void Validate()
    {
        if (DecoyPercent is < 0 or > 50) throw new ArgumentOutOfRangeException(nameof(DecoyPercent), DecoyPercent, "Expected 0 to 50 in ModConfig/serverguard.json.");
        if (ChunkCacheMiB is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(ChunkCacheMiB), ChunkCacheMiB, "Expected 1 to 1024 in ModConfig/serverguard.json.");
        if (AlwaysVisibleRange is < 8 or > 64) throw new ArgumentOutOfRangeException(nameof(AlwaysVisibleRange), AlwaysVisibleRange, "Expected 8 to 64 blocks in ModConfig/serverguard.json.");
        if (EntityRayBudgetPerThread is < 16 or > 16384) throw new ArgumentOutOfRangeException(nameof(EntityRayBudgetPerThread), EntityRayBudgetPerThread, "Expected 16 to 16384 in ModConfig/serverguard.json.");
        if (EntityDecoysPerPlayer is < 0 or > 128) throw new ArgumentOutOfRangeException(nameof(EntityDecoysPerPlayer), EntityDecoysPerPlayer, "Expected 0 to 128 in ModConfig/serverguard.json.");
        if (EntityDecoysGlobalCap is < 0 or > 4096) throw new ArgumentOutOfRangeException(nameof(EntityDecoysGlobalCap), EntityDecoysGlobalCap, "Expected 0 to 4096 in ModConfig/serverguard.json.");
        if (DecoyRayBudgetPerThread is < 16 or > 16384) throw new ArgumentOutOfRangeException(nameof(DecoyRayBudgetPerThread), DecoyRayBudgetPerThread, "Expected 16 to 16384 in ModConfig/serverguard.json.");
    }
}
