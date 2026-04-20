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
        static readonly FieldInfo CURRENT_HOUR = AccessTools.Field(typeof(RoundManager), nameof(RoundManager.currentHour));
        static readonly FieldInfo TIME_SCRIPT = AccessTools.Field(typeof(RoundManager), nameof(RoundManager.timeScript));
        static readonly FieldInfo HOUR = AccessTools.Field(typeof(TimeOfDay), nameof(TimeOfDay.hour));
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
            return TransCurrentHour(/*TransSpawnRandomEnemy(*/instructions.ToList(), /*nameof(RoundManager.firstTimeSpawningEnemies), nameof(SelectableLevel.Enemies), "Spawner").ToList(),*/ "Spawner");
        }

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.SpawnRandomOutsideEnemy))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> RoundManager_Trans_SpawnRandomOutsideEnemy(IEnumerable<CodeInstruction> instructions)
        {
            return TransSpawnRandomEnemy(instructions.ToList(), nameof(RoundManager.firstTimeSpawningOutsideEnemies), nameof(SelectableLevel.OutsideEnemies), "Outside spawner");
        }

        /*[HarmonyPatch(typeof(RoundManager), nameof(RoundManager.SpawnRandomDaytimeEnemy))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> RoundManager_Trans_SpawnRandomDaytimeEnemy(IEnumerable<CodeInstruction> instructions)
        {
            return TransSpawnRandomEnemy(instructions.ToList(), nameof(RoundManager.firstTimeSpawningDaytimeEnemies), nameof(SelectableLevel.DaytimeEnemies), "Daytime spawner");
        }*/

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

                if (__instance.timeScript.currentLevelWeather == LevelWeatherType.Eclipsed)
                {
                    __instance.minEnemiesToSpawn = (int)__instance.timeScript.currentWeatherVariable;
                    __instance.minOutsideEnemiesToSpawn = (int)__instance.timeScript.currentWeatherVariable;
                    Plugin.Logger.LogDebug("Applied bonus spawns for eclipse");
                }

                if (__instance.playersManager.isChallengeFile)
                {
                    if (__instance.playersManager.daysPlayersSurvivedInARow > 0)
                        Plugin.Logger.LogDebug("Cancelled survival streak for challenge moon");

                    __instance.playersManager.daysPlayersSurvivedInARow = 0;
                }
                else if (__instance.minEnemiesToSpawn == 0 && __instance.timeScript.daysUntilDeadline <= 2 && __instance.playersManager.daysPlayersSurvivedInARow >= 5)
                {
                    __instance.minEnemiesToSpawn = 1;
                    Plugin.Logger.LogDebug("Applied bonus spawns for 5 day survival streak");
                }

                __instance.SpawnDaytimeEnemiesOutside();
                Plugin.Logger.LogDebug("Early daytime spawn cycle (for bees/sapsucker)");

                __instance.SpawnEnemiesOutside();
                Plugin.Logger.LogDebug("Early outside spawn cycle (for eclipses)");

                // doesn't work until 9 AM anyway, but just in case he changes it...
                __instance.SpawnWeedEnemies();
                Plugin.Logger.LogDebug("Early outside spawn cycle (for weeds)");
            }
        }

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.AssignRandomEnemyToVent))]
        [HarmonyPrefix]
        static bool RoundManager_Pre_AssignRandomEnemyToVent(RoundManager __instance, ref EnemyVent vent, ref float spawnTime, ref bool __result)
        {
            // this is needed, actually, to avoid group spawns being overridden
            if (vent.occupied)
            {
                Plugin.Logger.LogDebug($"A new enemy tried to occupy vent with \"{vent.enemyType.enemyName}\" already inside");

                List<EnemyVent> vents = __instance.allEnemyVents.Where(enemyVent => !enemyVent.occupied).ToList();

                if (vents.Count < 1)
                {
                    Plugin.Logger.LogDebug("Enemy spawn cancelled because all vents on the map are occupied");
                    __result = false;
                    return false;
                }

                vent = vents[__instance.EnemySpawnRandom.Next(0, vents.Count)];
                Plugin.Logger.LogDebug("Enemy successfully reassigned to another empty vent");
            }

            // no longer necessary, because of TransCurrentHour
            /*if (Plugin.configConsistentSpawnTimes.Value)
            {
                float origTime = spawnTime;
                spawnTime = Mathf.Clamp(spawnTime - __instance.timeScript.lengthOfHours, (__instance.timeScript.lengthOfHours * __instance.timeScript.hour) + 10f, __instance.timeScript.lengthOfHours * (__instance.currentHour + 1));
                Plugin.Logger.LogDebug($"Vent spawn time adjusted: {origTime} -> {spawnTime}");
            }*/

            return true;
        }

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.AssignRandomEnemyToVent))]
        [HarmonyPostfix]
        static void RoundManager_Post_AssignRandomEnemyToVent(RoundManager __instance, EnemyVent vent, bool __result)
        {
            // returning false means no enemy was able to spawn
            if (!__result)
                return;

            EnemyType enemy = vent?.enemyType;
            if (enemy == null)
            {
                Plugin.Logger.LogWarning("AssignRandomEnemyToVent completed without assigning an enemy to the vent. This shouldn't happen");
                return;
            }

            if (vent.enemyTypeIndex < 0 || vent.enemyTypeIndex > __instance.currentLevel.Enemies.Count || !__instance.currentLevel.Enemies.Any(spawnableEnemyWithRarity => spawnableEnemyWithRarity.enemyType == enemy))
            {
                Plugin.Logger.LogWarning("AssignRandomEnemyToVent assigned an enemy with an invalid index. This shouldn't happen");
                return;
            }

            if (enemy.spawnInGroupsOf > 1)
            {
                Plugin.Logger.LogDebug($"Enemy \"{enemy.enemyName}\" spawned in vent, requesting group of {enemy.spawnInGroupsOf}");

                int spawnsLeft = enemy.spawnInGroupsOf - 1;
                List<EnemyVent> vents = __instance.allEnemyVents.Where(enemyVent => !enemyVent.occupied).ToList();

                while (spawnsLeft > 0)
                {
                    if (vents.Count <= 0)
                    {
                        Plugin.Logger.LogDebug($"Can't spawn additional \"{enemy.enemyName}\" (all vents are occupied)");
                        break;
                    }

                    if (enemy.numberSpawned >= enemy.MaxCount)
                    {
                        Plugin.Logger.LogDebug($"Can't spawn additional \"{enemy.enemyName}\" ({enemy.MaxCount} have already spawned)");
                        break;
                    }

                    if (enemy.PowerLevel > __instance.currentMaxInsidePower - __instance.currentEnemyPower)
                    {
                        Plugin.Logger.LogDebug($"Can't spawn additional \"{enemy.enemyName}\" ({__instance.currentEnemyPower} + {enemy.PowerLevel} would exceed max power level of {__instance.currentMaxInsidePower})");
                        break;
                    }

                    int time = (int)vent.spawnTime;
                    EnemyVent vent2 = vents[__instance.EnemySpawnRandom.Next(0, vents.Count)];

                    __instance.currentEnemyPower += enemy.PowerLevel;
                    __instance.currentEnemyPowerNoDeaths += enemy.PowerLevel;
                    vent2.enemyType = enemy;
                    vent2.enemyTypeIndex = vent.enemyTypeIndex;
                    vent2.occupied = true;
                    vent2.spawnTime = time;
                    if (__instance.timeScript.hour - __instance.currentHour <= 0)
                        vent2.SyncVentSpawnTimeClientRpc(time, vent.enemyTypeIndex);
                    enemy.numberSpawned++;

                    __instance.enemySpawnTimes.Add(time);
                    vents.Remove(vent2);

                    Plugin.Logger.LogDebug($"Spawning additional \"{enemy.enemyName}\" in vent");
                    spawnsLeft--;
                }

                if (spawnsLeft < enemy.spawnInGroupsOf - 1)
                    __instance.enemySpawnTimes.Sort();
            }
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
                return true;

            if (!__instance.IsServer)
                return false;

            if (__instance.timeScript.currentLevelWeather == LevelWeatherType.Eclipsed)
            {
                __instance.minOutsideEnemiesToSpawn = (int)__instance.timeScript.currentWeatherVariable;
                Plugin.Logger.LogDebug("Predictor: Factor in eclipse rates");
            }

            __instance.enemyNestSpawnObjects.Clear();
            float currentOutsideEnemyPower = 0f;
            int currentOutsideEnemyDiversityLevel = 0;
            bool firstTimeSpawningOutsideEnemies = true;
            System.Random outsideEnemySpawnRandom = new(__instance.playersManager.randomMapSeed + 41);
            System.Random nestRandom = new(__instance.playersManager.randomMapSeed + 42);

            for (int i = 0; i < __instance.timeScript.numberOfHours / __instance.hourTimeBetweenEnemySpawnBatches; i++)
            {
                // NEW - spawn times: 7:39 AM, 9:00 AM, 11:00 AM, 1:00 PM...
                float timeUpToCurrentHour = i == 0 ? TimeOfDay.startingGlobalTime : ((i * __instance.hourTimeBetweenEnemySpawnBatches) + 1) * __instance.timeScript.lengthOfHours;
                Plugin.Logger.LogDebug($"Predictor: Spawn wave at time {timeUpToCurrentHour}");
                float normalizedHour = (int)(Mathf.Floor(timeUpToCurrentHour / __instance.timeScript.lengthOfHours) * __instance.timeScript.lengthOfHours) / __instance.timeScript.totalTime;

                float baseAmount = __instance.currentLevel.outsideEnemySpawnChanceThroughDay.Evaluate(normalizedHour);
                Plugin.Logger.LogDebug($"Predictor: Base amount is {baseAmount}");
                /*if (Plugin.configUpdateFormulas.Value)
                    baseAmount -= 1f;*/
                if (__instance.playersManager.isChallengeFile)
                    baseAmount += 1f;
                float adjustedAmount = baseAmount + (Mathf.Abs(__instance.timeScript.daysUntilDeadline - 3) / 1.6f);
                Plugin.Logger.LogDebug($"Predictor: Adjusted amount is {adjustedAmount} ({(__instance.playersManager.isChallengeFile ? "challenge" : $"{__instance.timeScript.daysUntilDeadline} days left")})");
                int enemiesToSpawn = /*Plugin.configUpdateFormulas.Value ? Mathf.RoundToInt(Mathf.Clamp(Mathf.Lerp(adjustedAmount - 3f, baseAmount + 3f, (float)outsideEnemySpawnRandom.NextDouble()), __instance.minOutsideEnemiesToSpawn, 20f)) :*/ Mathf.Clamp(outsideEnemySpawnRandom.Next((int)(adjustedAmount - 3f), (int)(baseAmount + 3f)), __instance.minOutsideEnemiesToSpawn, 20);
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
                        {
                            enemyType.numberSpawned = 0;
                            enemyType.hasSpawnedAtLeastOne = false;
                        }

                        if (enemyType.PowerLevel > __instance.currentMaxOutsidePower - currentOutsideEnemyPower || (enemyType.numberSpawned < 1 && enemyType.DiversityPowerLevel > __instance.currentMaxOutsideDiversityLevel - currentOutsideEnemyDiversityLevel) || enemyType.numberSpawned >= enemyType.MaxCount || enemyType.spawningDisabled)
                            __instance.SpawnProbabilities.Add(0);
                        else
                        {
                            int spawnProbability;
                            if (__instance.increasedOutsideEnemySpawnRateIndex == k)
                                spawnProbability = 100;
                            else
                            {
                                if (enemyType.useNumberSpawnedFalloff)
                                    spawnProbability = (int)(__instance.currentLevel.OutsideEnemies[k].rarity * enemyType.probabilityCurve.Evaluate(normalizedHour) * enemyType.numberSpawnedFalloff.Evaluate(enemyType.numberSpawned / 10f));
                                else
                                    spawnProbability = (int)(__instance.currentLevel.OutsideEnemies[k].rarity * enemyType.probabilityCurve.Evaluate(normalizedHour));
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
                            if (enemyType2.numberSpawned < 1)
                                currentOutsideEnemyDiversityLevel += enemyType2.DiversityPowerLevel;
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

        static IEnumerable<CodeInstruction> TransCurrentHour(List<CodeInstruction> codes, string id)
        {
            if (!Plugin.configConsistentSpawnTimes.Value)
                return codes;

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Ldfld && (FieldInfo)codes[i].operand == CURRENT_HOUR)
                {
                    codes[i].operand = HOUR;
                    codes.Insert(i, new(OpCodes.Ldfld, TIME_SCRIPT));
                    i++;
                }
            }

            Plugin.Logger.LogDebug($"Transpiler ({id}): Correct time of day");
            return codes;
        }

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.PlotOutEnemiesForNextHour))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> RoundManager_Trans_PlotOutEnemiesForNextHour(IEnumerable<CodeInstruction> instructions)
        {
            return TransCurrentHour(instructions.ToList(), "Inside spawns");
        }

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.SpawnEnemiesOutside))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> RoundManager_Trans_SpawnEnemiesOutside(IEnumerable<CodeInstruction> instructions)
        {
            return TransCurrentHour(instructions.ToList(), "Outside spawns");
        }

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.SpawnDaytimeEnemiesOutside))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> RoundManager_Trans_SpawnDaytimeEnemiesOutside(IEnumerable<CodeInstruction> instructions)
        {
            return TransCurrentHour(instructions.ToList(), "Daytime spawns");
        }

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.SpawnWeedEnemies))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> RoundManager_Trans_SpawnWeedEnemies(IEnumerable<CodeInstruction> instructions)
        {
            return TransCurrentHour(instructions.ToList(), "Weed spawns");
        }

        // TODO: these should be transpilers...
        /*
        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.SpawnEnemiesOutside))]
        [HarmonyPrefix]
        public static bool RoundManager_Pre_SpawnEnemiesOutside(RoundManager __instance)
        {
            if (!Plugin.configUpdateFormulas.Value || __instance.currentOutsideEnemyPower > __instance.currentMaxOutsidePower)
                return true;

            float timeUpToCurrentHour = __instance.timeScript.lengthOfHours * __instance.timeScript.hour;

            float amount = __instance.currentLevel.outsideEnemySpawnChanceThroughDay.Evaluate(timeUpToCurrentHour) - 1f;
            if (__instance.playersManager.isChallengeFile)
                amount += 1f;

            int enemiesToSpawn = Mathf.RoundToInt(Mathf.Clamp(Mathf.Lerp(amount + (Mathf.Abs(__instance.timeScript.daysUntilDeadline - 3) / 1.6f) - 3f, amount + 3f, (float)__instance.OutsideEnemySpawnRandom.NextDouble()), __instance.minOutsideEnemiesToSpawn, 20f));

            int enemiesSpawned = 0;
            while (enemiesSpawned < enemiesToSpawn && __instance.SpawnRandomOutsideEnemy(timeUpToCurrentHour))
                enemiesSpawned++;

            return false;
        }

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.SpawnDaytimeEnemiesOutside))]
        [HarmonyPrefix]
        public static bool RoundManager_Pre_SpawnDaytimeEnemiesOutside(RoundManager __instance)
        {
            if (!Plugin.configUpdateFormulas.Value || __instance.currentLevel.DaytimeEnemies == null || __instance.currentLevel.DaytimeEnemies.Count <= 0 || __instance.currentDaytimeEnemyPower > __instance.currentLevel.maxDaytimeEnemyPowerCount)
                return true;

            float timeUpToCurrentHour = __instance.timeScript.lengthOfHours * __instance.timeScript.hour;

            float amount = __instance.currentLevel.daytimeEnemySpawnChanceThroughDay.Evaluate(timeUpToCurrentHour) - 1f;

            int enemiesToSpawn = Mathf.RoundToInt(Mathf.Clamp(Mathf.Lerp(amount - __instance.currentLevel.daytimeEnemiesProbabilityRange, amount + __instance.currentLevel.daytimeEnemiesProbabilityRange, (float)__instance.DaytimeEnemySpawnRandom.NextDouble()), 0f, 20f));

            if (enemiesToSpawn < 1)
                return false;

            __instance.GetOutsideAINodes();

            int enemiesSpawned = 0;
            while (enemiesSpawned < enemiesToSpawn && __instance.SpawnRandomDaytimeEnemy(timeUpToCurrentHour))
                enemiesSpawned++;

            return false;
        }
        */
    }
}
