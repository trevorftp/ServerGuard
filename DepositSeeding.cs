using System;
using System.Security.Cryptography;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace ServerGuard;

public class DepositSeeding
{
    private const string saltDataKey = "serverguard:depositsalt";
    private static readonly AccessTools.FieldRef<GenPartial, LCGRandom> getChunkRand = AccessTools.FieldRefAccess<GenPartial, LCGRandom>("chunkRand");
    private static readonly AccessTools.FieldRef<GenDeposits, MapLayerBase> getVerticalDistortTop = AccessTools.FieldRefAccess<GenDeposits, MapLayerBase>("verticalDistortTop");
    private static readonly AccessTools.FieldRef<GenDeposits, MapLayerBase> getVerticalDistortBottom = AccessTools.FieldRefAccess<GenDeposits, MapLayerBase>("verticalDistortBottom");
    private readonly ICoreServerAPI api;
    private readonly ServerGuardConfig config;
    private readonly long salt;

    public DepositSeeding(ICoreServerAPI api, ServerGuardConfig config)
    {
        this.api = api;
        this.config = config;
        ISaveGame saveGame = api.WorldManager.SaveGame;
        byte[]? stored = saveGame.GetData(saltDataKey);
        if (stored?.Length == sizeof(long))
        {
            salt = BitConverter.ToInt64(stored, 0);
        }
        else
        {
            salt = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(sizeof(long)));
            saveGame.StoreData(saltDataKey, BitConverter.GetBytes(salt));
        }
    }

    public void Reseed(GenDeposits deposits)
    {
        if (!config.ConcealOre) return;
        
        long salted = api.WorldManager.Seed ^ salt;
        getChunkRand(deposits) = new LCGRandom(salted);
        getVerticalDistortBottom(deposits) = GenMaps.GetDepositVerticalDistort(salted + 12);
        getVerticalDistortTop(deposits) = GenMaps.GetDepositVerticalDistort(salted + 28);
    }
}
