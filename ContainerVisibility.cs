using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace ServerGuard;

public class ContainerVisibility(ServerGuardConfig config)
{
    private long redacted;

    public long Redacted => Interlocked.Read(ref redacted);

    // Covers both getBlockEntityPacket (initial delivery) and BlockEntityToPacket (dirty updates).
    // An opener already gets live contents through the same packets their own inventory uses.
    public void Mask(BlockEntity blockEntity, Packet_BlockEntity packet)
    {
        if (!config.ConcealContainers || blockEntity is not BlockEntityGenericTypedContainer) return;
        TreeAttribute tree = new();
        tree.FromBytes(packet.Data);
        ITreeAttribute? inventory = tree.GetTreeAttribute("inventory");
        if (inventory == null) return;
        TreeAttribute redactedInventory = new();
        redactedInventory.SetInt("qslots", inventory.GetInt("qslots"));
        redactedInventory["slots"] = new TreeAttribute();
        tree["inventory"] = redactedInventory;
        packet.SetData(tree.ToBytes());
        Interlocked.Increment(ref redacted);
    }
}
