using BTD_Mod_Helper.Api;
using Il2Cpp;
using Il2CppAssets.Scripts.Data.MapSets;
using Il2CppAssets.Scripts.Models.ContentBrowser;
using Il2CppAssets.Scripts.Models.Map;
using Il2CppAssets.Scripts.Models.ServerEvents;
using Il2CppNewtonsoft.Json;
using UnityEngine;
using static System.Reflection.BindingFlags;

namespace CustomMapChallenges;

public static class VersionCompat
{
    public static void SetMapSprite(this MapDetails mapDetails, string guid)
    {
        var mapSprite = typeof(MapDetails).GetProperty(nameof(mapDetails.mapSprite), Instance | Public)!;
        var createSpriteReference = typeof(ModContent).GetMethod(nameof(ModContent.CreateSpriteReference))!;
        var spriteReference = createSpriteReference.Invoke(null, [guid]);
        mapSprite.SetMethod!.Invoke(mapDetails, [spriteReference]);
    }

    public static bool IsWater(this MapEditorAreaData area) =>
        JsonConvert.SerializeObject(area).Contains($"\"areaType\":{(int) AreaType.water}");

    public static void ShowIfCustom(GameObject mapImage, bool on)
    {
        var map = mapImage.GetComponent<MapSelectDisplay>().displayedMap;
        mapImage.SetActive(!on || CustomMapChallengesMod.CustomMaps.ContainsKey(map));
    }
}