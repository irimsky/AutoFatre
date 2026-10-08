using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;

namespace AutoFatre;

public sealed class InventoryCounter(IGameInventory inventory)
{
    private static readonly GameInventoryType[] PlayerBags =
    [
        GameInventoryType.Inventory1,
        GameInventoryType.Inventory2,
        GameInventoryType.Inventory3,
        GameInventoryType.Inventory4,
        GameInventoryType.KeyItems,
    ];

    public int Count(uint baseItemId)
    {
        int total = 0;
        foreach (GameInventoryType bag in PlayerBags)
        {
            foreach (ref readonly GameInventoryItem item in inventory.GetInventoryItems(bag))
            {
                if (!item.IsEmpty && item.BaseItemId == baseItemId)
                    total += item.Quantity;
            }
        }

        return total;
    }

    /// <summary>Space for normal-quality purchases in the four ordinary bags, including partial stacks.</summary>
    public int PurchaseCapacity(uint itemId, uint stackSize)
    {
        long capacity = 0;
        int stack = (int)Math.Clamp(stackSize, 1u, 99999u);
        foreach (GameInventoryType bag in PlayerBags.Take(4))
        foreach (ref readonly GameInventoryItem item in inventory.GetInventoryItems(bag))
        {
            if (item.IsEmpty) capacity += stack;
            else if (item.BaseItemId == itemId && !item.IsHq && !item.IsCollectable)
                capacity += Math.Max(0, stack - item.Quantity);
        }
        return (int)Math.Min(capacity, int.MaxValue);
    }

    /// <summary>Copies the regular-bag item counts for detecting an item awarded by a FATE.</summary>
    public IReadOnlyDictionary<uint, int> Snapshot()
    {
        Dictionary<uint, int> counts = [];
        foreach (GameInventoryType bag in PlayerBags)
        {
            foreach (ref readonly GameInventoryItem item in inventory.GetInventoryItems(bag))
            {
                if (item.IsEmpty)
                    continue;
                counts[item.BaseItemId] = counts.GetValueOrDefault(item.BaseItemId) + item.Quantity;
            }
        }

        return counts;
    }

    public uint? FindIncreasedItem(IReadOnlyDictionary<uint, int> before, out int increase)
    {
        uint? result = null;
        increase = 0;
        foreach ((uint itemId, int count) in this.Snapshot())
        {
            int delta = count - before.GetValueOrDefault(itemId);
            if (delta <= increase)
                continue;
            result = itemId;
            increase = delta;
        }

        return result;
    }
}
