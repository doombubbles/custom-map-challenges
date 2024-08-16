using BTD_Mod_Helper.Api;
using Il2CppAssets.Scripts.Data.MapSets;
using Il2CppAssets.Scripts.Models.Map;
using Il2CppAssets.Scripts.Models.ServerEvents;
using Il2CppNewtonsoft.Json;
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
        JsonConvert.SerializeObject(area).Contains($"\"areaType\":{(int)AreaType.water}");
}