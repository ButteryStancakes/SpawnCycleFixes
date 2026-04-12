using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SpawnCycleFixes
{
    public static class Utilities
    {
        // Experimentation, Assurance, Vow, Gordion, March, Adamance, Rend, Dine, Offense, Titan, Artifice, Liquidation, Embrion
        internal const int NUM_LEVELS = 13;

        internal static bool IsVanillaLevel()
        {
            return StartOfRound.Instance.currentLevelID < NUM_LEVELS;
        }

        public static void SpawnProbabilitiesPostProcess(ref List<int> spawnProbabilities, List<SpawnableEnemyWithRarity> enemies)
        {
            if (spawnProbabilities.Count != enemies.Count)
                Plugin.Logger.LogWarning("SpawnProbabilities is a different size from the current enemies list. This should never happen outside of mod conflicts!");

            for (int i = 0; i < spawnProbabilities.Count && i < enemies.Count; i++)
            {
                EnemyType enemyType = enemies[i].enemyType;
                // prevent old birds from eating up spawns when there are no dormant nests left
                if (enemyType.requireNestObjectsToSpawn && spawnProbabilities[i] > 0 && !Object.FindObjectsByType<EnemyAINestSpawnObject>(FindObjectsSortMode.None).Any(nest => nest.enemyType == enemyType))
                {
                    Plugin.Logger.LogDebug($"Enemy \"{enemyType.enemyName}\" has no nests present on map");
                    if (RoundManager.Instance.currentMaxOutsidePower <= RoundManager.Instance.currentOutsideEnemyPowerNoDeaths)
                    {
                        spawnProbabilities[i] = 0;
                        Plugin.Logger.LogDebug($"Enemy \"{enemyType.enemyName}\" spawning disabled");
                    }
                    else
                        Plugin.Logger.LogDebug($"Enemy \"{enemyType.enemyName}\" ignored, as natural spawns are still not finished");
                }
                // prevents spawn weight from exceeding "maximum"
                else if (spawnProbabilities[i] > 100 && (Plugin.configLimitSpawnChance.Value == MoonFilter.Always || (Plugin.configLimitSpawnChance.Value == MoonFilter.VanillaMoonsOnly && IsVanillaLevel())))
                {
                    Plugin.Logger.LogDebug($"Enemy \"{enemyType.enemyName}\" is exceeding maximum spawn weight ({spawnProbabilities[i]} > 100)");
                    if (enemies[i].rarity <= 100)
                        spawnProbabilities[i] = 100;
                    else // special case for Cadavers on Adamance...
                        Plugin.Logger.LogDebug($"Enemy \"{enemyType.enemyName}\" ignored; base weight of {enemies[i].rarity}");
                }
            }
        }

        public static void UpdateEnemySpawnVariables(EnemyType enemyType)
        {
            if (enemyType.name == "RadMech" && !Plugin.configLimitOldBirds.Value)
                return;

            if (enemyType != null)
            {
                enemyType.numberSpawned++;
                if (enemyType.isDaytimeEnemy)
                    RoundManager.Instance.currentDaytimeEnemyPower += enemyType.PowerLevel;
                else if (enemyType.isDaytimeEnemy)
                    RoundManager.Instance.currentOutsideEnemyPower += enemyType.PowerLevel;
                else
                    RoundManager.Instance.currentEnemyPower += enemyType.PowerLevel;
            }
        }
    }
}
