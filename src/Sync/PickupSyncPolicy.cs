using System;

namespace SilverPinesCoop.Sync
{
    internal static class PickupSyncPolicy
    {
        // Generic interaction states can consume loot without a pickup-specific data class.
        public static bool AllowsWorldEntry(string key, string typeName)
        {
            if (IsPickupState(key) || IsPickupState(typeName))
                return false;

            var separator = typeName.IndexOf('/');
            var owner = separator < 0 ? typeName : typeName.Substring(0, separator);
            return Contains(owner, "Door") || Contains(owner, "Gate") || Contains(owner, "Puzzle") ||
                   Contains(owner, "Switch") || Contains(owner, "Lever") || Contains(owner, "Elevator") ||
                   Contains(owner, "Lift");
        }

        public static bool IsPickupState(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            var normalized = name.Replace("_", "").Replace("-", "").Replace(" ", "");
            return Contains(normalized, "Pickup") || Contains(normalized, "Picked") ||
                   Contains(normalized, "Taken") || Contains(normalized, "Loot") ||
                   Contains(normalized, "Collect") || Contains(normalized, "ItemsRemaining") ||
                   Contains(normalized, "RemainingItems") || Contains(normalized, "ItemAcquired") ||
                   Contains(normalized, "ItemReceived") || Contains(normalized, "Reward") ||
                   Contains(normalized, "Money") || Contains(normalized, "Inventory");
        }

        public static bool AllowsInventory(bool syncWorld, bool sharedPickups, string allowedKeys, string key)
        {
            if (!syncWorld || string.IsNullOrEmpty(key))
                return false;
            if (sharedPickups)
                return true;

            foreach (var allowedKey in allowedKeys.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(allowedKey, key, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool Contains(string value, string word) => value.Contains(word, StringComparison.OrdinalIgnoreCase);
    }
}
