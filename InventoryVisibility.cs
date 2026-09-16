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

    // ToPacketForOtherPlayers never returns a packet to the owning player.
    // All the calls here are for a different client.
    public void Mask(Packet_Server packet)
    {
        if (!config.ConcealPlayerInventory) return;
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
        Interlocked.Increment(ref redacted);
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
