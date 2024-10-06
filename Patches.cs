using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Api;
using BTD_Mod_Helper.Api.Components;
using BTD_Mod_Helper.Extensions;
using HarmonyLib;
using Il2CppAssets.Scripts.Models.Difficulty;
using Il2CppAssets.Scripts.Models.Profile;
using Il2CppAssets.Scripts.Models.ServerEvents;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.Player;
using Il2CppAssets.Scripts.Unity.UI_New;
using Il2CppAssets.Scripts.Unity.UI_New.ChallengeEditor;
using Il2CppAssets.Scripts.Unity.UI_New.DailyChallenge;
using Il2CppAssets.Scripts.Unity.UI_New.GameOver;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppAssets.Scripts.Unity.UI_New.InGame.EditorMenus;
using Il2CppAssets.Scripts.Unity.UI_New.Odyssey;
using Il2CppNinjaKiwi.GUTS.Models.ContentBrowser;
using Il2CppSystem.Threading.Tasks;
using UnityEngine;
using UnityEngine.U2D;
using TaskScheduler = BTD_Mod_Helper.Api.TaskScheduler;

namespace CustomMapChallenges;

/// <summary>
/// Get map thumbnails
/// </summary>
[HarmonyPatch(typeof(SpriteAtlas), nameof(SpriteAtlas.GetSprite))]
internal static class SpriteAtlas_GetSprite
{
    [HarmonyPrefix]
    private static bool Prefix(SpriteAtlas __instance, ref string name, ref Sprite __result)
    {
        if (__instance.name != CustomMapChallengesMod.SpriteAtlas ||
            !CustomMapChallengesMod.CustomMaps.ContainsKey(name)) return true;
        try
        {
            if (CustomMapChallengesMod.HasThumbnail(name, out var key))
            {
                var thumbnailBytes = MapEditorThumbnails.storageManager.GetBytes(key);
                var texture = MapEditorThumbnails.DecodeImage(thumbnailBytes);
                texture.mipMapBias = -1;

                __result = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f));

                return false;
            }
        }
        catch (Exception e)
        {
#if DEBUG
            ModHelper.Warning<CustomMapChallengesMod>(e);
#endif
        }

        ModHelper.Warning<CustomMapChallengesMod>(
            $"Unable to get thumbnail for {name}, they may still be fetching in the background");

        name = "MapSelectBlonsMapButton";
        return true;
    }
}

/// <summary>
/// Unlock maps
/// </summary>
[HarmonyPatch(typeof(Btd6Player), nameof(Btd6Player.IsMapUnlocked))]
internal static class Btd6Player_IsMapUnlocked
{
    [HarmonyPrefix]
    internal static bool Prefix(string map, ref bool __result)
    {
        if (!CustomMapChallengesMod.CustomMaps.ContainsKey(map)) return true;

        __result = true;
        return false;
    }
}

/// <summary>
/// Make custom maps always show up in the map lists
/// </summary>
[HarmonyPatch(typeof(MapInfoManager), nameof(MapInfoManager.HasCompletedMap))]
internal static class MapInfoManager_HasCompletedMap
{
    [HarmonyPrefix]
    internal static bool Prefix(string map, ref bool __result)
    {
        if (!CustomMapChallengesMod.CustomMaps.ContainsKey(map)) return true;

        __result = true;
        return false;
    }
}

/// <summary>
/// Fix loading into game
/// </summary>
[HarmonyPatch(typeof(UI), nameof(UI.LoadGame))]
internal static class UI_LoadGame
{
    [HarmonyPrefix]
    internal static void Prefix(MapSaveDataModel? mapSaveData)
    {
        var data = InGameData.Editable;
        var map = mapSaveData?.dailyChallengeEventID ?? data.selectedMap;

        if (CustomMapChallengesMod.CustomMaps.TryGetValue(map, out var customMap))
        {
            data.selectedMap = "BaseEditorMap";
            data.dcModel = data.dcModel?.Clone() ?? DailyChallengeModel.CreateDefaultEditorModel();
            data.dcModel.eventID = map;
            data.dcModel.customMapModel = customMap.Model;
            if (Game.Version.Major <= 41)
            {
                data.dcModel.chalType = ChallengeType.CustomMapPlay;
            }
        }

        if (data.dcModel is {customMapModel: not null, leastTiersUsed: <= 0, leastCashUsed: <= 0})
        {
            switch ((SpecialConditionType) CustomMapChallengesMod.ShowCounterInBrowserMaps)
            {
                case SpecialConditionType.LeastCash:
                    data.dcModel.leastCashUsed = 99999999;
                    break;
                case SpecialConditionType.LeastTiers:
                    data.dcModel.leastTiersUsed = 99999999;
                    break;
                case SpecialConditionType.None:
                default:
                    break;
            }
        }
    }
}

/// <summary>
/// Fix the challenge id in saves to be the custom map id
/// </summary>
[HarmonyPatch(typeof(InGame), nameof(InGame.ChallengeId), MethodType.Getter)]
internal static class InGame_ChallengeId
{
    [HarmonyPostfix]
    internal static void Postfix(InGame __instance, ref string __result)
    {
        if (string.IsNullOrEmpty(__result) && InGameData.CurrentGame?.dcModel?.customMapModel != null)
        {
            __result = InGameData.CurrentGame.dcModel.eventID;
        }
    }
}

[HarmonyPatch]
internal static class BossEventScreen_GetChallengeSave
{
    private static readonly MethodBase? Method = AccessTools.Method(typeof(BossEventScreen), "GetChallengeSave");

    [HarmonyPrepare]
    internal static bool Prepare() => Method != null;

    [HarmonyTargetMethod]
    internal static MethodBase TargetMethod() => Method!;

    [HarmonyPostfix]
    internal static void Postfix(BossEventScreen __instance)
    {
        if (CustomMapChallengesMod.CustomMaps.ContainsKey(__instance.selectedChallengeSave?.dailyChallengeEventID ??
                                                          ""))
        {
            __instance.selectedChallengeSave!.mapName = __instance.selectedChallengeSave.dailyChallengeEventID;
        }
    }
}

/// <summary>
/// Stop broken Stamps from breaking entire map
/// </summary>
[HarmonyPatch(typeof(StampDisplay), nameof(StampDisplay.LoadData))]
internal static class StampDisplay_LoadData
{
    [HarmonyPrefix]
    internal static bool Prefix(MapEditorStampData? stampData, ref Task __result)
    {
        if (stampData?.Def() != null) return true;

        __result = Task.CompletedTask;
        return false;
    }
}

/// <summary>
/// Add challenge/difficulty button in Map Editor Screen
/// </summary>
[HarmonyPatch(typeof(MapEditorScreen), nameof(MapEditorScreen.Open))]
internal static class MapEditorScreen_Open
{
    [HarmonyPostfix]
    internal static void Postfix(MapEditorScreen __instance, Il2CppSystem.Object? data)
    {
        if (__instance.isCreationMode || data == null) return;

        TaskScheduler.ScheduleTask(() =>
            {
                var baseId = __instance.playerContent.Id;
                var id = ModContent.GetId<CustomMapChallengesMod>(baseId);
                CustomMapChallengesMod.CreateChallengeUI(__instance, id, baseId);
            },
            () => __instance.playerContent != null);
    }
}

/// <summary>
/// Add difficulty button in Challenge Screen, no sharing
/// </summary>
[HarmonyPatch(typeof(ChallengeEditorPlay), nameof(ChallengeEditorPlay.Open))]
internal static class ChallengeEditorPlay_Open
{
    [HarmonyPostfix]
    internal static void Postfix(ChallengeEditorPlay __instance)
    {
        if (CustomMapChallengesMod.CustomMaps.ContainsKey(__instance.dcm.map))
        {
            var id = __instance.dcm.map;
            CustomMapChallengesMod.CreateChallengeUI(__instance, id);
            var shareBtn = (Component?) __instance.GetType().GetProperty("shareBtn")?.GetValue(__instance);
            if (shareBtn != null)
            {
                shareBtn.transform.parent.gameObject.SetActive(false);
            }
        }
    }
}

/// <summary>
/// No sharing with custom maps
/// </summary>
[HarmonyPatch(typeof(OdysseyEditor), nameof(OdysseyEditor.PopulateMaps))]
internal static class OdysseyEditor_PopulateMaps
{
    [HarmonyPostfix]
    internal static void Postfix(OdysseyEditor __instance)
    {
        var shareBtn = (Component?) __instance.GetType().GetProperty("shareBtn")?.GetValue(__instance);
        if (shareBtn != null && shareBtn.gameObject != null)
        {
            __instance.shareBtn.gameObject.SetActive(
                !__instance.OdysseyEditorDifficultyRules.challenges.Any(dcm =>
                    CustomMapChallengesMod.CustomMaps.ContainsKey(dcm.map)));
        }
    }
}

/// <summary>
/// Filter custom maps for map select
/// </summary>
[HarmonyPatch(typeof(Il2CppAssets.Scripts.Unity.UI_New.MapSelect.MapSelectPanel),
    nameof(Il2CppAssets.Scripts.Unity.UI_New.MapSelect.MapSelectPanel.Initialise))]
internal static class MapSelectPanel_Initialise
{
    [HarmonyPostfix]
    internal static void Postfix(MonoBehaviour __instance)
    {
        CustomMapChallengesMod.CreateMapSelectUI(__instance.gameObject);
    }
}

/// <summary>
/// Filter custom maps for map select
/// </summary>
[HarmonyPatch(typeof(ChallengeEditor), nameof(ChallengeEditor.MapSelectClicked))]
internal static class ChallengeEditor_MapSelectClicked
{
    [HarmonyPostfix]
    internal static void Postfix(ChallengeEditor __instance)
    {
        CustomMapChallengesMod.CreateMapSelectUI(__instance.transform.GetComponentsInChildren<RectTransform>()
            .Last(transform => transform.name.Contains("MapSelect")).gameObject);
    }
}

/// <summary>
/// Filter custom maps for map select
/// </summary>
[HarmonyPatch]
internal static class MapSelectPanel_Open
{
    private static readonly Type? MapSelectPanel = AccessTools.AllTypes()
        .FirstOrDefault(type => type.FullName == "Il2CppAssets.Scripts.Unity.UI_New.DailyChallenge.MapSelectPanel");

    [HarmonyPrepare]
    internal static bool Prepare() => MapSelectPanel != null;

    [HarmonyTargetMethod]
    internal static MethodBase TargetMethod() =>
        MapSelectPanel!.GetMethods().First(info => info.Name.StartsWith("Open"));

    [HarmonyPostfix]
    internal static void Postfix(MonoBehaviour __instance)
    {
        CustomMapChallengesMod.CreateMapSelectUI(__instance.gameObject);
    }
}

/// <summary>
/// Enabled challenge enabled filtering in content browser
/// </summary>
[HarmonyPatch(typeof(ContentBrowser), nameof(ContentBrowser.Open))]
internal static class ContentBrowser_Open
{
    [HarmonyPrefix]
    internal static void Postfix(ContentBrowser __instance)
    {
        if (__instance.enabled)
        {
            CustomMapChallengesMod.CreateContentBrowserUI(__instance);
        }
    }
}

[HarmonyPatch(typeof(ContentBrowser), nameof(ContentBrowser.ShowChallenges), typeof(SelectionType))]
internal static class ContentBrowser_ShowChallenges
{
    [HarmonyPrefix]
    internal static void Prefix(ContentBrowser __instance)
    {
        CustomMapChallengesMod.ShowingCustom = false;
    }
}

/// <summary>
/// Enable easy map challenge deleting
/// </summary>
[HarmonyPatch(typeof(MapPanel), nameof(MapPanel.Init))]
internal static class ContentBrowserPanel_ResetDeleteButtonState
{
    [HarmonyPostfix]
    internal static void Postfix(MapPanel __instance)
    {
        if (!CustomMapChallengesMod.ShowingCustom) return;

        var id = ModContent.GetId<CustomMapChallengesMod>(__instance.playerContent.Id);
        if (CustomMapChallengesMod.CustomMaps.ContainsKey(id))
        {
            TaskScheduler.ScheduleTask(() => __instance.deleteBtn.gameObject.SetActive(true));
        }
    }
}

/// <summary>
/// Enable easy map challenge deleting
/// </summary>
[HarmonyPatch(typeof(ContentBrowserPanel), nameof(ContentBrowserPanel.DeleteClicked))]
internal static class ContentBrowserPanel_DeleteClicked
{
    [HarmonyPrefix]
    internal static bool Prefix(ContentBrowserPanel __instance)
    {
        var contentBrowser = __instance.browser.Cast<ContentBrowser>();
        if (contentBrowser.SelectedTab == ContentType.Map &&
            contentBrowser.CurrentSelectionType == SelectionType.Custom)
        {
            var id = ModContent.GetId<CustomMapChallengesMod>(__instance.playerContent.Id);
            if (CustomMapChallengesMod.CustomMaps.TryGetValue(id, out var map))
            {
                File.Delete(map.FilePath);
                __instance.gameObject.SetActive(false);
            }
        }

        return true;
    }
}

/// <summary>
/// Update for possible map challenge deletion
/// </summary>
[HarmonyPatch(typeof(ContentBrowser), nameof(ContentBrowser.ReOpen))]
internal static class ContentBrowser_ReOpen
{
    [HarmonyPostfix]
    internal static void Postfix(ContentBrowser __instance)
    {
        if (CustomMapChallengesMod.ShowingCustom)
        {
            __instance.transform.GetComponentFromChildrenByName<ModHelperButton>("FilterCustomButton").Button.Press();
        }
    }
}

/// <summary>
/// Fix Defeat and Victory Screens for old versions
/// </summary>
[HarmonyPatch(typeof(InGame), nameof(InGame.IsUserPlayMode))]
internal static class InGame_IsUserPlayMode
{
    [HarmonyPostfix]
    internal static void Postfix(InGame __instance, ref bool __result)
    {
        if (Game.Version.Major <= 41 &&
            (__instance.matchWon || __instance.matchLost) &&
            CustomMapChallengesMod.CustomMaps.ContainsKey(InGameData.CurrentGame.dcModel?.eventID ?? ""))
        {
            __result = false;
        }
    }
}