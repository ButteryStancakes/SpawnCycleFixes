using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SpawnCycleFixes
{
    public static class Utilities
    {
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
