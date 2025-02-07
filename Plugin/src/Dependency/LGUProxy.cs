using System.Runtime.CompilerServices;
using MoreShipUpgrades.Managers;
using MoreShipUpgrades.Misc.Upgrades;
using MoreShipUpgrades.UpgradeComponents.TierUpgrades.Player;

namespace QuickItemScan.Dependency
{
    public static class LGUProxy
    {
        private static bool? _enabled;

        public static bool Enabled
        {
            get
            {
                _enabled ??= BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.malco.lethalcompany.moreshipupgrades");
                return _enabled.Value;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public static float GetScanRangeIncrease(ScanNodeProperties node)
        {
            if (!BaseUpgrade.GetActiveUpgrade(BetterScanner.UPGRADE_NAME))
                return 0f;

            if (node.headerText is "Main entrance" or "Ship")
                return UpgradeBus.Instance.PluginConfiguration.BetterScannerUpgradeConfiguration
                    .OutsideNodesRangeIncrease.Value;

            return UpgradeBus.Instance.PluginConfiguration.BetterScannerUpgradeConfiguration.NodeRangeIncrease.Value;
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public static bool ShouldSkipLOSCheck(ScanNodeProperties node)
        {
            if (!BaseUpgrade.GetActiveUpgrade(BetterScanner.UPGRADE_NAME))
                return false;

            var isEnemy = node.nodeType == 1;

            var hasRequiredLevel = BaseUpgrade.GetUpgradeLevel(BetterScanner.UPGRADE_NAME) == 2;

            var canSeeEnemy = UpgradeBus.Instance.PluginConfiguration.BetterScannerUpgradeConfiguration.SeeEnemiesThroughWalls.Value;

            return !isEnemy || (hasRequiredLevel && canSeeEnemy);
        }
        
    }
}
