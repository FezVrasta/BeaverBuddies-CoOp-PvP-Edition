using HarmonyLib;
using System;
using Timberborn.BaseComponentSystem;
using Timberborn.CharactersUI;
using Timberborn.DwellingSystemUI;
using Timberborn.FactionSystem;
using Timberborn.GameDistricts;
using Timberborn.WorkSystemUI;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaverBuddies.Factions
{
    /**
     * An empty worker or dweller slot shows a beaver or bot of the game's
     * faction. In a mixed game that's the host's for every building, so it
     * shows the one of the building's own faction.
     */
    public static class WorkerSlotFaction
    {
        /**
         * The building's template's faction, or for a template more than
         * one faction has, its district's: that's where its workers and
         * dwellers come from.
         */
        public static FactionSpec Of(BaseComponent building)
        {
            if (!building) return null;
            string id = FactionRules.OfTemplate(building);
            if (id == null)
            {
                DistrictBuilding districtBuilding = building.GetComponent<DistrictBuilding>();
                DistrictCenter district = districtBuilding ? districtBuilding.GetDistrictOrConstructionDistrict() : null;
                if (district) id = FactionRules.OfDistrict(district);
            }
            return id == null ? null : FactionRules.GetFaction(id);
        }

        // After the game's ShowAdultEmpty or ShowBotEmpty: the building's faction's instead
        public static void ShowEmpty(CharacterButton button, BaseComponent building, bool bot) =>
            ShowEmpty(button, building, faction => bot ? faction.BotAvatar.Asset : faction.Avatar.Asset);

        public static void ShowEmpty(CharacterButton button, BaseComponent building, Func<FactionSpec, Sprite> avatar)
        {
            FactionSpec faction = Of(building);
            if (faction != null) button._button.style.backgroundImage = new StyleBackground(avatar(faction));
        }
    }

    [HarmonyPatch(typeof(WorkerView), nameof(WorkerView.ShowEmpty), typeof(bool))]
    class WorkerViewFactionPatcher
    {
        static void Postfix(WorkerView __instance) =>
            WorkerSlotFaction.ShowEmpty(__instance._characterButton, __instance._workplaceWorkerType,
                __instance._workplaceWorkerType && __instance._workerTypeHelper.IsBotWorkerType(__instance._workplaceWorkerType.WorkerType));
    }

    // A dweller view doesn't know its building, so its empty slots are redone here
    [HarmonyPatch(typeof(DwellingUserFragment), nameof(DwellingUserFragment.UpdateViews))]
    class DwellingUserFragmentFactionPatcher
    {
        static void Postfix(DwellingUserFragment __instance)
        {
            if (!__instance._dwelling) return;
            int filled = __instance._dwelling.NumberOfDwellers;
            for (int i = filled; i < __instance._views.Count; i++)
            {
                DwellerView view = __instance._views[i];
                WorkerSlotFaction.ShowEmpty(view._characterButton, __instance._dwelling,
                    faction => view._isChildSlot ? faction.ChildAvatar.Asset : faction.Avatar.Asset);
            }
        }
    }

    // A bot's portrait is its own faction's, like a beaver's (see BeaverEntityBadgeAvatarPatcher)
    [HarmonyPatch(typeof(Timberborn.BotsUI.BotEntityBadge), nameof(Timberborn.BotsUI.BotEntityBadge.GetEntityAvatar))]
    class BotEntityBadgeAvatarPatcher
    {
        static void Postfix(Timberborn.BotsUI.BotEntityBadge __instance, ref Sprite __result)
        {
            FactionSpec faction = FactionRules.GetFaction(FactionRules.OfCharacter(__instance));
            if (faction != null) __result = faction.BotAvatar.Asset;
        }
    }
}
