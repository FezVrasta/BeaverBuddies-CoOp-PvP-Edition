using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using Timberborn.DistributionSystem;
using Timberborn.DistributionSystemUI;
using Timberborn.Goods;
using Timberborn.Yielding;
using Timberborn.YieldingUI;
using UnityEngine.UIElements;

namespace BeaverBuddies.Factions
{
    /**
     * A mixed game has both factions' goods on every machine, and the
     * game's goods lists show them all. Each player's lists only have
     * their own faction's goods (see FactionRules.GoodAllowed); the
     * stores themselves take only their faction's (see ArmoryStoreGoodsPatcher).
     */
    [ManualMethodOverwrite]
    /*
     * 10/10/2026 (Timberborn 1.1)
        List<ImportGoodIcon> list = new List<ImportGoodIcon>();
        foreach (GoodGroupSpec goodGroupSpec in _goodsGroupSpecService.GoodGroupSpecs)
        {
            list.AddRange(CreateImportGoodsGroup(parent, goodGroupSpec));
        }
        return list;
     */
    [HarmonyPatch(typeof(ImportGoodIconFactory), nameof(ImportGoodIconFactory.CreateImportGoods))]
    class ImportGoodsPatcher
    {
        // The District Crossing's imported goods: this player's faction's, without a group left empty
        static bool Prefix(ImportGoodIconFactory __instance, VisualElement parent, ref IEnumerable<ImportGoodIcon> __result)
        {
            string faction = LocalFaction.Id;
            var list = new List<ImportGoodIcon>();
            foreach (GoodGroupSpec group in __instance._goodsGroupSpecService.GoodGroupSpecs)
            {
                var goods = __instance._goodService.GetGoodsForGroup(group.Id).Where(g => FactionRules.GoodAllowed(faction, g)).ToList();
                if (goods.Count == 0) continue;
                VisualElement groupElement = __instance._visualElementLoader.LoadVisualElement("Game/EntityPanel/ImportGoodsGroup");
                groupElement.Q<Image>("Icon").sprite = group.Icon.Asset;
                parent.Add(groupElement);
                VisualElement items = groupElement.Q<VisualElement>("Items");
                list.AddRange(goods.Select(good => __instance.CreateImportGoodIcon(items, good)));
            }
            __result = list;
            return false;
        }
    }

    [HarmonyPatch(typeof(DistrictDistributionSetting), nameof(DistrictDistributionSetting.GetGoodDistributionSettingsForGroup))]
    class DistributionGoodsPatcher
    {
        // The Distribution tab's goods for a district: its faction's (only that tab asks)
        static void Postfix(DistrictDistributionSetting __instance, ref IEnumerable<GoodDistributionSetting> __result)
        {
            DistrictDistributionSetting setting = __instance;
            __result = __result.Where(s => FactionRules.GoodAllowedIn(setting, s.GoodId)).ToList();
        }
    }

    [HarmonyPatch(typeof(YieldRemovingBuilding), nameof(YieldRemovingBuilding.GetAllowedYielders))]
    class YieldRemovingYieldersPatcher
    {
        // A tapper's shack or lumberjack works the other faction's trees only for goods its faction keeps (no maple syrup for the Iron Teeth)
        static void Postfix(YieldRemovingBuilding __instance, ref IEnumerable<IYielderDecorable> __result)
        {
            YieldRemovingBuilding building = __instance;
            __result = __result.Where(y => FactionRules.GoodAllowedIn(building, y.Yielder.Yield.Id)).ToList();
            // A gatherer flag also leaves out the other faction's plants: their fruit is a good every faction keeps (mangrove fruit)
            if (!building.HasComponent<Timberborn.Gathering.GathererFlag>()) return;
            var factions = MixedFactionsService.FactionsOf(Specs.Name(building));
            if (factions == null) return;
            __result = __result.Where(y => MixedFactionsService.GetTemplateFaction(Specs.Name((Timberborn.BlueprintSystem.ComponentSpec)y)) is not string f
                || factions.Contains(f)).ToList();
        }
    }

    [HarmonyPatch(typeof(YieldTooltipFactory), nameof(YieldTooltipFactory.AddBuildings))]
    class YieldTooltipBuildingsPatcher
    {
        // "Gathered by" lists this player's faction's buildings, not each faction's twin of the same flag
        static bool Prefix(YieldTooltipFactory __instance, VisualElement parent, YielderSpec yielderSpec)
        {
            if (!__instance._templates.TryGetValue(yielderSpec.ResourceGroup, out var templates)) return true;
            foreach (var item in templates.Where(LocalFaction.CanBuild))
            {
                VisualElement element = __instance._visualElementLoader.LoadVisualElement("Game/TooltipBuildingItem");
                element.Q<Image>("Icon").sprite = item.Icon.Asset;
                element.Q<Label>("Name").text = __instance._loc.T(item.DisplayNameLocKey);
                parent.Add(element);
            }
            return false;
        }
    }
}
