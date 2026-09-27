using BeaverBuddies.Players;
using HarmonyLib;
using Timberborn.AutomationBuildings;
using Timberborn.Buildings;
using Timberborn.Demolishing;
using Timberborn.EntitySystem;
using Timberborn.ScienceSystem;
using Timberborn.WellbeingUI;
using Timberborn.WorkSystem;
using Timberborn.Workshops;

namespace BeaverBuddies.Science
{
    // ---- Keeping the per-player points in step with the total ----

    [HarmonyPatch(typeof(ScienceService), nameof(ScienceService.AddPoints))]
    class ScienceServiceAddPointsPatcher
    {
        static void Postfix(int amount)
        {
            PlayerScienceService.Instance?.OnPointsAdded(amount);
        }
    }

    [HarmonyPatch(typeof(ScienceService), nameof(ScienceService.SubtractPoints))]
    class ScienceServiceSubtractPointsPatcher
    {
        static void Postfix(int amount)
        {
            PlayerScienceService.Instance?.OnPointsSubtracted(amount);
        }
    }

    // ---- Earning: workshops produce science for their district's owner ----

    [HarmonyPatch(typeof(Manufactory), nameof(Manufactory.FinishProduction))]
    class ManufactoryFinishProductionPatcher
    {
        static void Prefix(Manufactory __instance, out ScienceContext.Scope? __state)
        {
            var service = PlayerScienceService.Instance;
            __state = service == null ? null : ScienceContext.Use(PlayerScienceService.GetOwner(__instance));
        }

        static void Postfix(ScienceContext.Scope? __state)
        {
            __state?.Dispose();
        }
    }

    /**
     * Demolishing something that gives science (the reward is added when
     * it's deleted) earns it for the owner of the demolishing beaver's
     * district, not the shared pool.
     */
    [HarmonyPatch(typeof(DemolishExecutor), nameof(DemolishExecutor.PerformActionOnComplete))]
    class DemolishExecutorSciencePatcher
    {
        static void Prefix(DemolishExecutor __instance, out ScienceContext.Scope? __state)
        {
            var service = PlayerScienceService.Instance;
            __state = service == null ? null
                : ScienceContext.Use(BorderProtection.OwnerOf(__instance.GetComponent<EntityComponent>()));
        }

        static void Postfix(ScienceContext.Scope? __state)
        {
            __state?.Dispose();
        }
    }

    // ---- Spending: unlocks are paid by whoever unlocks ----

    [HarmonyPatch(typeof(BuildingUnlockingService), nameof(BuildingUnlockingService.Unlockable))]
    class BuildingUnlockingServiceUnlockablePatcher
    {
        static bool Prefix(BuildingSpec buildingSpec, ref bool __result)
        {
            var service = PlayerScienceService.Instance;
            if (service == null) return true;
            __result = service.AvailablePoints(PlayerScienceService.CurrentPayer) >= buildingSpec.ScienceCost;
            return false;
        }
    }

    [HarmonyPatch(typeof(WorkplaceUnlockingService), nameof(WorkplaceUnlockingService.Unlockable))]
    class WorkplaceUnlockingServiceUnlockablePatcher
    {
        static bool Prefix(WorkplaceUnlockingService __instance, UnlockableWorkerType unlockableWorkerType, ref bool __result)
        {
            var service = PlayerScienceService.Instance;
            if (service == null) return true;
            __result = service.AvailablePoints(PlayerScienceService.CurrentPayer)
                >= __instance.GetUnlockCost(unlockableWorkerType);
            return false;
        }
    }

    // Outside of a replayed event (single player), the local player pays
    [HarmonyPatch(typeof(BuildingUnlockingService), nameof(BuildingUnlockingService.Unlock))]
    class BuildingUnlockingServiceUnlockPayerPatcher
    {
        static void Prefix(out ScienceContext.Scope? __state)
        {
            __state = ScienceContext.Active ? null : ScienceContext.Use(PlayerIdentity.LocalID);
        }

        static void Postfix(ScienceContext.Scope? __state)
        {
            __state?.Dispose();
        }
    }

    [HarmonyPatch(typeof(WorkplaceUnlockingService), nameof(WorkplaceUnlockingService.Unlock))]
    class WorkplaceUnlockingServiceUnlockPayerPatcher
    {
        static void Prefix(out ScienceContext.Scope? __state)
        {
            __state = ScienceContext.Active ? null : ScienceContext.Use(PlayerIdentity.LocalID);
        }

        static void Postfix(ScienceContext.Scope? __state)
        {
            __state?.Dispose();
        }
    }

    // ---- Buildings that consume or read science use their district owner's ----

    [ManualMethodOverwrite]
    /*
     * 09/26/2026 (Timberborn 1.1)
        private void RefillScience()
        {
            if (_scienceService.SciencePoints >= ScienceUsedPerHour)
            {
                AddPoints(ScienceUsedPerHour);
            }
            UpdateNotEnoughScience();
        }
     */
    [HarmonyPatch(typeof(ScienceNeedingBuilding), nameof(ScienceNeedingBuilding.RefillScience))]
    class ScienceNeedingBuildingRefillPatcher
    {
        static bool Prefix(ScienceNeedingBuilding __instance)
        {
            var service = PlayerScienceService.Instance;
            if (service == null) return true;

            string owner = PlayerScienceService.GetOwner(__instance);
            if (service.AvailablePoints(owner) >= __instance.ScienceUsedPerHour)
            {
                using (ScienceContext.Use(owner))
                {
                    __instance.AddPoints(__instance.ScienceUsedPerHour);
                }
            }
            __instance.UpdateNotEnoughScience();
            return false;
        }
    }

    [ManualMethodOverwrite]
    /*
     * 09/26/2026 (Timberborn 1.1)
        public void Sample()
        {
            SampledSciencePoints = _scienceService.SciencePoints;
            UpdateOutputState();
        }
     */
    [HarmonyPatch(typeof(ScienceCounter), nameof(ScienceCounter.Sample))]
    class ScienceCounterSamplePatcher
    {
        static bool Prefix(ScienceCounter __instance)
        {
            var service = PlayerScienceService.Instance;
            if (service == null) return true;

            __instance.SampledSciencePoints = service.AvailablePoints(PlayerScienceService.GetOwner(__instance));
            __instance.UpdateOutputState();
            return false;
        }
    }

    // ---- UI: the top bar shows what this player can spend ----

    [HarmonyPatch(typeof(BasicStatisticsPanel), nameof(BasicStatisticsPanel.UpdateScience))]
    class BasicStatisticsPanelUpdateSciencePatcher
    {
        static void Postfix(BasicStatisticsPanel __instance)
        {
            var service = PlayerScienceService.Instance;
            if (service == null || !service.Enabled) return;
            __instance._scienceCount.text = __instance._loc.T(__instance._sciencePhrase,
                service.AvailablePoints(PlayerIdentity.LocalID));
        }
    }
}
