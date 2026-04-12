using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace SpawnCycleFixes
{
    internal enum MoonFilter
    {
        Off,
        VanillaMoonsOnly,
        Always
    }

    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    [BepInDependency(GUID_LOBBY_COMPATIBILITY, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal const string PLUGIN_GUID = "butterystancakes.lethalcompany.spawncyclefixes", PLUGIN_NAME = "Spawn Cycle Fixes", PLUGIN_VERSION = "1.1.0";
        internal static new ManualLogSource Logger;

        const string GUID_LOBBY_COMPATIBILITY = "BMX.LobbyCompatibility";

        internal static ConfigEntry<bool> configConsistentSpawnTimes, configLimitOldBirds, configMaskHornetsPower/*, configUpdateFormulas*/;
        internal static ConfigEntry<MoonFilter> configLimitSpawnChance;

        void Awake()
        {
            Logger = base.Logger;

            if (Chainloader.PluginInfos.ContainsKey(GUID_LOBBY_COMPATIBILITY))
            {
                Logger.LogInfo("CROSS-COMPATIBILITY - Lobby Compatibility detected");
                LobbyCompatibility.Init();
            }

            configConsistentSpawnTimes = Config.Bind(
                "Miscellaneous",
                "Consistent Spawn Times",
                true,
                "(REQUIRES RESTART) Fixes two spawn waves occurring at the start of each day, and also fixes vent timers overlapping future spawn waves which delays them until later in the day.\nWith this setting enabled, spawn waves will always occur at 7:39 AM, 9:00 AM, 11:00 AM, 1:00 PM, 3:00 PM, 5:00 PM, 7:00 PM, 9:00 PM, and 11:00 PM.");

            configLimitSpawnChance = Config.Bind(
                "Miscellaneous",
                "Limit Spawn Chance",
                MoonFilter.VanillaMoonsOnly,
                "Prevents enemy spawn weight from exceeding 100 (the intended maximum) if its spawn curves would normally allow it to do so.\nThis will prevent some enemy types (ex: baboon hawks on Adamance, nutcrackers on Rend) from spawning out of control on certain maps.");

            /*configUpdateFormulas = Config.Bind(
                "Miscellaneous",
                "Update Formulas",
                false,
                "(EXPERIMENTAL) Updates outside/daytime spawns to use the new adjusted inside curve formula from v80. This will probably change the number of enemies that spawn per day, but should feel pretty subtle and is just more consistent with vanilla's inside spawn behavior. Likely to cause incompatibilities with other mods so use at your own risk.");*/

            configLimitOldBirds = Config.Bind(
                "Enemies",
                "Limit Old Birds",
                true,
                "When unplugging the apparatus, any Old Birds that spawn will now add to the power count and number of Old Birds, preventing outside spawns from \"overflowing\" past the intended maximum values.\nOld Birds will also be blocked from spawning once all the dormant ones on the map have \"woken up\", preventing an issue where they waste other enemy slots and then immediately despawn.");

            configMaskHornetsPower = Config.Bind(
                "Enemies",
                "Mask Hornets Power",
                false,
                "Mask hornets do not add power level since they spawn in a non-standard way, from killing butlers. Enabling this will fix that.\nIn vanilla, mask hornets and butlers have the same power level (of 2), so enabling this will prevent enemies from spawning to replace dead butlers.");

            new Harmony(PLUGIN_GUID).PatchAll();

            Logger.LogInfo($"{PLUGIN_NAME} v{PLUGIN_VERSION} loaded");
        }
    }
}