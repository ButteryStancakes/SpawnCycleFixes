using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Unity.Netcode;
using UnityEngine;

namespace SpawnCycleFixes
{
    [HarmonyPatch]
    static class Patches
    {
        static readonly FieldInfo SPAWN_PROBABILITIES = AccessTools.Field(typeof(RoundManager), nameof(RoundManager.SpawnProbabilities));
        static readonly FieldInfo CURRENT_LEVEL = AccessTools.Field(typeof(RoundManager), nameof(RoundManager.currentLevel));
        static readonly MethodInfo SPAWN_PROBABILITIES_POST_PROCESS = AccessTools.Method(typeof(Utilities), nameof(Utilities.SpawnProbabilitiesPostProcess));

        [HarmonyPatch(typeof(LungProp), nameof(LungProp.DisconnectFromMachinery), MethodType.Enumerator)]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> LungProp_Trans_DisconnectFromMachinery(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = instructions.ToList();

            MethodInfo spawnEnemyGameObject = AccessTools.Method(typeof(RoundManager), nameof(RoundManager.SpawnEnemyGameObject));
            for (int i = 2; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt && (MethodInfo)codes[i].operand == spawnEnemyGameObject)
                {
                    codes.InsertRange(i + 2,
                    [
                        new(OpCodes.Ldloc_1),
                        new(OpCodes.Ldfld, AccessTools.Field(typeof(LungProp), nameof(LungProp.radMechEnemyType))),
                        new(OpCodes.Call, AccessTools.Method(typeof(Utilities), nameof(Utilities.UpdateEnemySpawnVariables)))
                    ]);
                    Plugin.Logger.LogDebug("Transpiler (Radiation warning): Add Old Bird values after spawning");
                    //i++;
                    return codes;
                }
            }

            Plugin.Logger.LogError("Radiation warning transpiler failed");
            return instructions;
        }

        static IEnumerable<CodeInstruction> TransSpawnRandomEnemy(List<CodeInstruction> codes, string firstTime, string enemies, string id)
        {
            FieldInfo firstTimeSpawning = AccessTools.Field(typeof(RoundManager), firstTime);
            for (int i = 2; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Stfld && (FieldInfo)codes[i].operand == firstTimeSpawning)
                {
                    codes.InsertRange(i - 2, [
                        new(OpCodes.Ldarg_0),
                        new(OpCodes.Ldflda, SPAWN_PROBABILITIES),
                        new(OpCodes.Ldarg_0),
                        new(OpCodes.Ldfld, CURRENT_LEVEL),
                        new(OpCodes.Ldfld, AccessTools.Field(typeof(SelectableLevel), enemies)),
                        new(OpCodes.Call, SPAWN_PROBABILITIES_POST_PROCESS)
                    ]);
                    Plugin.Logger.LogDebug($"Transpiler ({id}): Post process probabilities");
                    //i += 6;
                    return codes;
                }
            }

            Plugin.Logger.LogError($"{id} transpiler failed");
            return codes;
        }

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.AssignRandomEnemyToVent))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> RoundManager_Trans_AssignRandomEnemyToVent(IEnumerable<CodeInstruction> instructions)
        {
            return TransSpawnRandomEnemy(instructions.ToList(), nameof(RoundManager.firstTimeSpawningEnemies), nameof(SelectableLevel.Enemies), "Spawner");
        }

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.SpawnRandomOutsideEnemy))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> RoundManager_Trans_SpawnRandomOutsideEnemy(IEnumerable<CodeInstruction> instructions)
        {
            return TransSpawnRandomEnemy(instructions.ToList(), nameof(RoundManager.firstTimeSpawningOutsideEnemies), nameof(SelectableLevel.OutsideEnemies), "Outside spawner");
        }

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.SpawnRandomDaytimeEnemy))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> RoundManager_Trans_SpawnRandomDaytimeEnemy(IEnumerable<CodeInstruction> instructions)
        {
            return TransSpawnRandomEnemy(instructions.ToList(), nameof(RoundManager.firstTimeSpawningDaytimeEnemies), nameof(SelectableLevel.DaytimeEnemies), "Daytime spawner");
        }

        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.SubtractFromPowerLevel))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        static void EnemyAI_Pre_SubtractFromPowerLevel(EnemyAI __instance, ref object[] __state)
        {
            __state =
            [
                __instance.removedPowerLevel,
                RoundManager.Instance.currentEnemyPower,
                RoundManager.Instance.currentOutsideEnemyPower,
                RoundManager.Instance.currentDaytimeEnemyPower
            ];
        }

        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.SubtractFromPowerLevel))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        static void EnemyAI_Post_SubtractFromPowerLevel(EnemyAI __instance, object[] __state)
        {
            if ((bool)__state[0] || !__instance.removedPowerLevel)
                return;

            if (__instance is ButlerEnemyAI butlerEnemyAI && Plugin.configMaskHornetsPower.Value)
            {
                float powerLevel = butlerEnemyAI.butlerBeesEnemyType.PowerLevel;
                if (powerLevel <= 0f || butlerEnemyAI.enemyType.PowerLevel <= 0f)
                    return;

                float currentEnemyPower = (float)__state[1];
                float currentOutsideEnemyPower = (float)__state[2];
                float currentDaytimeEnemyPower = (float)__state[3];

                if (RoundManager.Instance.currentEnemyPower < currentEnemyPower)
                {
                    Plugin.Logger.LogDebug("Butler died and subtracted inside power");

                    RoundManager.Instance.currentEnemyPower += powerLevel;
                    Plugin.Logger.LogDebug("Mask hornets added inside power");

                    if (!RoundManager.Instance.cannotSpawnMoreInsideEnemies)
                    {
                        if (RoundManager.Instance.currentEnemyPower >= RoundManager.Instance.currentMaxInsidePower)
                        {
                            RoundManager.Instance.cannotSpawnMoreInsideEnemies = true;
                            Plugin.Logger.LogDebug($"Mask hornets canceled vent spawns again ({RoundManager.Instance.currentEnemyPower} > {RoundManager.Instance.currentMaxInsidePower})");
                        }
                    }

                    return;
                }

                if (RoundManager.Instance.currentOutsideEnemyPower < currentOutsideEnemyPower)
                {
                    Plugin.Logger.LogDebug("Butler died and subtracted outside power");

                    RoundManager.Instance.currentOutsideEnemyPower += powerLevel;
                    Plugin.Logger.LogDebug("Mask hornets added outside power");

                    return;
                }

                if (RoundManager.Instance.currentDaytimeEnemyPower < currentDaytimeEnemyPower)
                {
                    Plugin.Logger.LogDebug("Butler died and subtracted daytime power");

                    RoundManager.Instance.currentDaytimeEnemyPower += powerLevel;
                    Plugin.Logger.LogDebug("Mask hornets added daytime power");

                    return;
                }

                Plugin.Logger.LogWarning("Butler died, unable to determine power type");
            }
        }

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.BeginEnemySpawning))]
        [HarmonyPrefix]
        static void RoundManager_Pre_BeginEnemySpawning(RoundManager __instance)
        {
            if (Plugin.configConsistentSpawnTimes.Value && __instance.IsServer && __instance.allEnemyVents.Length != 0 && __instance.currentLevel.maxEnemyPowerCount > 0)
            {
                __instance.currentHour += __instance.hourTimeBetweenEnemySpawnBatches;
                Plugin.Logger.LogDebug("First spawn cycle occurring - force timer for next spawn cycle");

                if (TimeOfDay.Instance.currentLevelWeather == LevelWeatherType.Eclipsed)
                {
                    __instance.minEnemiesToSpawn = (int)TimeOfDay.Instance.currentWeatherVariable;
                    __instance.minOutsideEnemiesToSpawn = (int)TimeOfDay.Instance.currentWeatherVariable;
                    Plugin.Logger.LogDebug("Applied bonus spawns for eclipse");
                }

                if (StartOfRound.Instance.isChallengeFile)
                {
                    if (StartOfRound.Instance.daysPlayersSurvivedInARow > 0)
                        Plugin.Logger.LogDebug("Cancelled survival streak for challenge moon");

                    StartOfRound.Instance.daysPlayersSurvivedInARow = 0;
                }
                else if (__instance.minEnemiesToSpawn == 0 && TimeOfDay.Instance.daysUntilDeadline <= 2 && StartOfRound.Instance.daysPlayersSurvivedInARow >= 5)
                {
                    __instance.minEnemiesToSpawn = 1;
                    Plugin.Logger.LogDebug("Applied bonus spawns for 5 day survival streak");
                }

                __instance.SpawnDaytimeEnemiesOutside();
                Plugin.Logger.LogDebug("Early daytime spawn cycle (for bees/sapsucker)");

                __instance.SpawnEnemiesOutside();
                Plugin.Logger.LogDebug("Early outside spawn cycle (for eclipses)");
            }
        }

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.AssignRandomEnemyToVent))]
        [HarmonyPrefix]
        static void RoundManager_Pre_AssignRandomEnemyToVent(RoundManager __instance, ref float spawnTime)
        {
            if (!Plugin.configConsistentSpawnTimes.Value)
                return;

            float origTime = spawnTime;
            spawnTime = Mathf.Clamp(spawnTime - __instance.timeScript.lengthOfHours, (__instance.timeScript.lengthOfHours * __instance.timeScript.hour) + 10f, __instance.timeScript.lengthOfHours * (__instance.currentHour + 1));
            Plugin.Logger.LogDebug($"Vent spawn time adjusted: {origTime} -> {spawnTime}");
        }

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.PlotOutEnemiesForNextHour))]
        [HarmonyPostfix]
        static void RoundManager_Post_PlotOutEnemiesForNextHour(RoundManager __instance)
        {
            if (__instance.IsServer)
            {
                __instance.enemySpawnTimes.Clear();
                foreach (EnemyVent enemyVent in __instance.allEnemyVents)
                {
                    if (enemyVent.occupied)
                        __instance.enemySpawnTimes.Add((int)enemyVent.spawnTime);
                }
                if (__instance.enemySpawnTimes.Count > 0)
                {
                    __instance.enemySpawnTimes.Sort();
                    __instance.currentEnemySpawnIndex = 0;
                }
            }
        }

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.PredictAllOutsideEnemies))]
        [HarmonyPrefix]
        static bool RoundManager_Pre_PredictAllOutsideEnemies(RoundManager __instance)
        {
            if (!Plugin.configConsistentSpawnTimes.Value)
                return false;

            if (!__instance.IsServer)
                return false;

            if (TimeOfDay.Instance.currentLevelWeather == LevelWeatherType.Eclipsed)
            {
                __instance.minOutsideEnemiesToSpawn = (int)TimeOfDay.Instance.currentWeatherVariable;
                Plugin.Logger.LogDebug("Predictor: Factor in eclipse rates");
            }

            __instance.enemyNestSpawnObjects.Clear();
            float currentOutsideEnemyPower = 0f;
            bool firstTimeSpawningOutsideEnemies = true;
            System.Random outsideEnemySpawnRandom = new(__instance.playersManager.randomMapSeed + 41);
            System.Random nestRandom = new(__instance.playersManager.randomMapSeed + 42);

            for (int i = 0; i < __instance.timeScript.numberOfHours / __instance.hourTimeBetweenEnemySpawnBatches; i++)
            {
                // NEW - spawn times: 7:39 AM, 9:00 AM, 11:00 AM, 1:00 PM...
                float timeUpToCurrentHour = i == 0 ? TimeOfDay.startingGlobalTime : ((i * __instance.hourTimeBetweenEnemySpawnBatches) + 1) * __instance.timeScript.lengthOfHours;
                Plugin.Logger.LogDebug($"Predictor: Spawn wave at time {timeUpToCurrentHour}");
                float normalizedTimeOfDay = timeUpToCurrentHour / __instance.timeScript.totalTime;

                float baseAmount = __instance.currentLevel.outsideEnemySpawnChanceThroughDay.Evaluate(normalizedTimeOfDay);
                Plugin.Logger.LogDebug($"Predictor: Base amount is {baseAmount}");
                if (StartOfRound.Instance.isChallengeFile)
                    baseAmount += 1f;
                baseAmount += Mathf.Abs(__instance.timeScript.daysUntilDeadline - 3) / 1.6f;
                Plugin.Logger.LogDebug($"Predictor: Adjusted amount is {baseAmount} ({(StartOfRound.Instance.isChallengeFile ? "challenge" : $"{__instance.timeScript.daysUntilDeadline} days left")})");
                int enemiesToSpawn = Mathf.Clamp(outsideEnemySpawnRandom.Next((int)(baseAmount - 3f), (int)(baseAmount + 3f)), __instance.minOutsideEnemiesToSpawn, 20);
                Plugin.Logger.LogDebug($"Predictor: Spawning {enemiesToSpawn} enemies");

                for (int j = 0; j < enemiesToSpawn; j++)
                {
                    __instance.SpawnProbabilities.Clear();
                    int weight = 0;
                    for (int k = 0; k < __instance.currentLevel.OutsideEnemies.Count; k++)
                    {
                        EnemyType enemyType = __instance.currentLevel.OutsideEnemies[k].enemyType;
                        Plugin.Logger.LogDebug($"Predictor: Processing \"{enemyType.name}\"");

                        if (firstTimeSpawningOutsideEnemies)
                            enemyType.numberSpawned = 0;

                        if (enemyType.PowerLevel > __instance.currentMaxOutsidePower - currentOutsideEnemyPower || enemyType.numberSpawned >= enemyType.MaxCount || enemyType.spawningDisabled)
                            __instance.SpawnProbabilities.Add(0);
                        else
                        {
                            int spawnProbability;
                            if (__instance.increasedOutsideEnemySpawnRateIndex == k)
                                spawnProbability = 100;
                            else
                            {
                                if (enemyType.useNumberSpawnedFalloff)
                                    spawnProbability = (int)(__instance.currentLevel.OutsideEnemies[k].rarity * enemyType.probabilityCurve.Evaluate(normalizedTimeOfDay) * enemyType.numberSpawnedFalloff.Evaluate(enemyType.numberSpawned / 10f));
                                else
                                    spawnProbability = (int)(__instance.currentLevel.OutsideEnemies[k].rarity * enemyType.probabilityCurve.Evaluate(normalizedTimeOfDay));

                                // NEW: cap at 100 weight
                                if (spawnProbability > 100 && (Plugin.configLimitSpawnChance.Value == MoonFilter.Always || (Plugin.configLimitSpawnChance.Value == MoonFilter.VanillaMoonsOnly && Utilities.IsVanillaLevel())))
                                {
                                    Plugin.Logger.LogDebug($"Predictor: \"{enemyType.name}\" exceeding 100 weight ({spawnProbability})");
                                    spawnProbability = 100;
                                }
                            }

                            __instance.SpawnProbabilities.Add(spawnProbability);
                            weight += spawnProbability;
                            Plugin.Logger.LogDebug($"Predictor: \"{enemyType.name}\" at {spawnProbability} weight ({enemyType.numberSpawned} spawned)");
                        }
                    }

                    firstTimeSpawningOutsideEnemies = false;
                    Plugin.Logger.LogDebug($"Predictor: {weight} total weight");

                    if (weight > 0)
                    {
                        int randomWeightedIndex = __instance.GetRandomWeightedIndex(__instance.SpawnProbabilities.ToArray(), outsideEnemySpawnRandom);
                        EnemyType enemyType2 = __instance.currentLevel.OutsideEnemies[randomWeightedIndex].enemyType;
                        // NEW: handle group spawning
                        int spawnInGroupsOf = Mathf.Max(enemyType2.spawnInGroupsOf, 1);
                        for (int num = 0; num < spawnInGroupsOf; num++)
                        {
                            if (enemyType2.PowerLevel > __instance.currentMaxOutsidePower - currentOutsideEnemyPower)
                                break;

                            Plugin.Logger.LogDebug($"Predictor: Tracking \"{enemyType2.name}\"");
                            currentOutsideEnemyPower += enemyType2.PowerLevel;
                            enemyType2.numberSpawned++;

                            if (enemyType2.nestSpawnPrefab != null && (!enemyType2.useMinEnemyThresholdForNest || (enemyType2.nestsSpawned < 1 && enemyType2.numberSpawned > enemyType2.minEnemiesToSpawnNest)))
                            {
                                Plugin.Logger.LogDebug($"Predictor: Spawning \"{enemyType2.name}\" nest");
                                __instance.SpawnNestObjectForOutsideEnemy(enemyType2, nestRandom);
                            }
                        }
                    }
                }
            }

            __instance.enemyNestSpawnObjects.TrimExcess();

            List<NetworkObjectReference> networkObjectsList = [];
            for (int l = 0; l < __instance.enemyNestSpawnObjects.Count; l++)
            {
                NetworkObject networkObject = __instance.enemyNestSpawnObjects[l].GetComponent<NetworkObject>();
                if (networkObject != null)
                    networkObjectsList.Add(networkObject);
            }
            if (networkObjectsList.Count > 0)
                __instance.SyncNestSpawnObjectsOrderServerRpc(networkObjectsList.ToArray());

            Plugin.Logger.LogDebug($"Predictor: Complete");

            return false;
        }

        [HarmonyPatch(typeof(QuickMenuManager), nameof(QuickMenuManager.Start))]
        [HarmonyPostfix]
        static void QuickMenuManager_Post_Start(QuickMenuManager __instance)
        {
            EnemyType radMech = __instance.testAllEnemiesLevel.OutsideEnemies.FirstOrDefault(outsideEnemy => outsideEnemy.enemyType.name == "RadMech")?.enemyType;
            if (radMech != null)
            {
                radMech.requireNestObjectsToSpawn = Plugin.configLimitOldBirds.Value;
                Plugin.Logger.LogDebug($"{radMech}.requireNestObjectsToSpawn: {radMech.requireNestObjectsToSpawn}");
            }
        }
    }
}
