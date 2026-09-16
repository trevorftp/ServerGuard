using System.Threading;

namespace ServerGuard;

public class InventoryVisibility(ServerGuardConfig config)
{
    // InventoryPlayerHotbar.skillSlotIndex and offHandSlotIndex. Both render on the character model regardless of the active slot.
    private const int hotbarSkillSlot = 10;
    private const int hotbarOffhandSlot = 11;
    private static readonly Packet_ItemStack empty = new() { ItemClass = -1 };
    private long redacted;

    public long Redacted => Interlocked.Read(ref redacted);

    // ServerWorldPlayerData.ToPacketForOtherPlayers never returns a packet to the owning player, so every call here is clearly for a different client.
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

    // Only the active and always-rendered slots stay visible. The rest of the hotbar is nobody else's business.
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
