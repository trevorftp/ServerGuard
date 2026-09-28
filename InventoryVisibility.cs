using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace ServerGuard;

public class InventoryVisibility(ServerGuardConfig config, EntityVisibility entities)
{
    // InventoryPlayerHotbar.skillSlotIndex and offHandSlotIndex. Both always render on the character model.
    private const int hotbarSkillSlot = 10;
    private const int hotbarOffhandSlot = 11;
    private static readonly Packet_ItemStack empty = new() { ItemClass = -1 };
    [ThreadStatic] private static bool rebuilding;
    private readonly ConditionalWeakTable<Packet_Server, IServerPlayer> redactedOwners = new();
    private long redacted;

    public long Redacted => Interlocked.Read(ref redacted);

    // Reuses the same visibility decision real-player entity concealment already makes.
    // Has no effect unless ConcealPlayers is also on.
    public bool CanSee(ConnectedClient toClient, IServerPlayer owningPlayer) => entities.CanSee(toClient, owningPlayer.Entity);

    public void Broadcast(ServerMain server, IServerPlayer owningPlayer, Packet_Server packet)
    {
        foreach (ConnectedClient client in server.Clients.Values)
        {
            if (client.Player == null || client.Player == owningPlayer || !client.State.ConnectedOrPlaying()) continue;
            if (CanSee(client, owningPlayer)) server.SendPacket(client.Id, packet);
        }
    }

    public void Mask(Packet_Server packet, IServerPlayer owningPlayer)
    {
        if (!config.ConcealPlayerInventory || rebuilding) return;
        foreach (Packet_InventoryContents inventory in packet.PlayerData.InventoryContents)
        {
            switch (inventory.InventoryClass)
            {
                case "hotbar":
                    maskHotbar(inventory.Itemstacks, packet.PlayerData.HotbarSlotId);
                    break;
                case "backpack":
                    maskBackpack(inventory.Itemstacks);
                    break;
            }
        }
        redactedOwners.AddOrUpdate(packet, owningPlayer);
        Interlocked.Increment(ref redacted);
    }

    public Packet_Server GetPacketFor(int clientId, Packet_Server packet)
    {
        if (!redactedOwners.TryGetValue(packet, out IServerPlayer? owner) || owner.ClientId != clientId) return packet;
        rebuilding = true;
        try
        {
            return ((ServerWorldPlayerData)owner.WorldData).ToPacketForOtherPlayers(owner);
        }
        finally
        {
            rebuilding = false;
        }
    }

    // Only the active slot and the two always-rendered slots stay visible.
    private static void maskHotbar(Packet_ItemStack[] stacks, int activeSlot)
    {
        for (int i = 0; i < stacks.Length; i++)
        {
            if (i != activeSlot && i != hotbarSkillSlot && i != hotbarOffhandSlot) stacks[i] = empty;
        }
    }

    // Keep the worn bag's identity for rendering.
    private static void maskBackpack(Packet_ItemStack[] stacks)
    {
        foreach (Packet_ItemStack stack in stacks)
        {
            if (stack.ItemClass != -1) stack.Attributes = null;
        }
    }
}
