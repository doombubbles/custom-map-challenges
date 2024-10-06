using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Api;
using BTD_Mod_Helper.Api.Components;
using BTD_Mod_Helper.Api.Enums;
using BTD_Mod_Helper.Api.Helpers;
using BTD_Mod_Helper.Api.ModOptions;
using BTD_Mod_Helper.Extensions;
using CustomMapChallenges;
using Il2CppAssets.Scripts;
using Il2CppAssets.Scripts.Data;
using Il2CppAssets.Scripts.Data.MapSets;
using Il2CppAssets.Scripts.Models;
using Il2CppAssets.Scripts.Models.Difficulty;
using Il2CppAssets.Scripts.Models.Profile;
using Il2CppAssets.Scripts.Models.ServerEvents;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.Menu;
using Il2CppAssets.Scripts.Unity.UI_New;
using Il2CppAssets.Scripts.Unity.UI_New.ChallengeEditor;
using Il2CppAssets.Scripts.Unity.UI_New.DailyChallenge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppAssets.Scripts.Unity.UI_New.InGame.EditorMenus;
using Il2CppAssets.Scripts.Unity.UI_New.Popups;
using Il2CppNinjaKiwi.Common;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using static System.IO.SearchOption;
using Path = System.IO.Path;
using TaskScheduler = BTD_Mod_Helper.Api.TaskScheduler;
using Version = Il2CppSystem.Version;

#pragma warning disable CS4014

[assembly:
    MelonInfo(typeof(CustomMapChallengesMod), ModHelperData.Name, ModHelperData.Version, ModHelperData.RepoOwner)]
[assembly: MelonGame("Ninja Kiwi", "BloonsTD6")]

namespace CustomMapChallenges;

public class CustomMapChallengesMod : BloonsTD6Mod
{
    public record CustomMapRecord(
        string Name,
        string Id,
        string FilePath,
        CustomMapModel Model,
        MapDifficulty Difficulty);

    public static readonly ModSettingEnum<SpecialConditionType> ShowCounterInBrowserMaps = new(SpecialConditionType.None)
    {
        description = "Manually enables either the Cash or Tier counter for when playing browser maps. (Changes take effect when loading into a match)",
        labelFunction = type => type.ToString().Spaced().Replace(":", ""),
        icon = VanillaSprites.LeastCashIcon
    };
    
    public static readonly ModSettingFolder MapsFolder =
        new(Path.Combine(Application.persistentDataPath, nameof(CustomMapChallenges)))
        {
            customValidation = newMapsFolder => Path.GetFullPath(newMapsFolder) != Path.GetFullPath(VanillaMapsFolder),
            onSave = newMapsFolder =>
            {
                if (watcher?.Path == newMapsFolder) return;
                ModHelper.Msg<CustomMapChallengesMod>("Changing Maps Directory");

                watcher?.Dispose();
                CustomMaps?.Clear();
                SetupMapsFolder(newMapsFolder);
                LoadAllMaps(newMapsFolder);
            }
        };

    public static string VanillaMapsFolder => Path.Combine(FileIOHelper.sandboxRoot, "MapEditor_ContentData");

    public static readonly ModSettingButton OpenCustomMapsFolder = new(() => Process.Start(new ProcessStartInfo
    {
        FileName = MapsFolder,
        UseShellExecute = true,
        Verb = "open"
    }))
    {
        buttonText = "Open"
    };

    public static readonly ModSettingButton OpenVanillaMapsFolder = new(() => Process.Start(new ProcessStartInfo
    {
        FileName = VanillaMapsFolder,
        UseShellExecute = true,
        Verb = "open"
    }))
    {
        buttonText = "Open"
    };

    private static FileSystemWatcher? watcher;

    public static readonly Dictionary<string, CustomMapRecord> CustomMaps = new();

    internal const string SpriteAtlas = "MapImages";

    /// <summary>
    /// Fix map difficulty
    /// </summary>
    /// <param name="result"></param>
    public override void OnNewGameModel(GameModel result)
    {
        var customMap = InGameData.CurrentGame?.dcModel?.eventID ?? "";
        if (CustomMaps.TryGetValue(customMap, out var record))
        {
            result.map.mapDifficulty = (int) record.Difficulty;
        }
    }

    /// <summary>
    /// Setup and load maps
    /// </summary>
    public override void OnTitleScreen()
    {
        SetupMapsFolder(MapsFolder);
        LoadAllMaps(MapsFolder);
    }

    /// <summary>
    /// Sets up the map folder system
    /// </summary>
    public static void SetupMapsFolder(string mapsFolder)
    {
        // Create directories
        Directory.CreateDirectory(mapsFolder);
        foreach (var difficulty in Enum.GetValues<MapDifficulty>())
        {
            Directory.CreateDirectory(Path.Combine(mapsFolder, difficulty.ToString()!));
        }

        // Watch files
        watcher = new FileSystemWatcher(mapsFolder);
        watcher.IncludeSubdirectories = true;
        watcher.Created += (_, args) => TaskScheduler.ScheduleTask(() => LoadMapFromPath(args.FullPath));
        watcher.Deleted += (_, args) => TaskScheduler.ScheduleTask(() => DeleteMapFromPath(args.FullPath));
        watcher.Renamed += (_, args) => TaskScheduler.ScheduleTask(() =>
        {
            DeleteMapFromPath(args.OldFullPath);
            LoadMapFromPath(args.FullPath);
        });
        watcher.Error += (_, args) => ModHelper.Warning<CustomMapChallengesMod>(args.GetException());
        watcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// Load all Maps in the maps folder
    /// </summary>
    public static void LoadAllMaps(string mapsFolder)
    {
        ModHelper.Msg<CustomMapChallengesMod>($"Loading maps from {mapsFolder}");
        foreach (var file in new DirectoryInfo(mapsFolder).EnumerateFiles("*", AllDirectories))
        {
            if (file.Extension != "" && file.Extension != ".json") continue;

            LoadMapFromPath(file.FullName);
        }
    }

    /// <summary>
    /// Load a custom map into the game from the given file path
    /// </summary>
    /// <param name="path"></param>
    public static void LoadMapFromPath(string path)
    {
        if ((Path.HasExtension(path) && Path.GetExtension(path) != ".json") || Directory.Exists(path)) return;

        var name = Path.GetFileNameWithoutExtension(path);
        var id = ModContent.GetId<CustomMapChallengesMod>(name);
        var storage = PlayerContentManager.mapDataStorage;

        var text = Path.HasExtension(path) ? File.ReadAllText(path) : storage.decode.Invoke(storage.ReadAllBytes(path));
        var mapEditorModel = storage.serialiser.Deserialize<MapEditorModel>(text);

        if (!Enum.TryParse(new FileInfo(path).Directory?.Name, out MapDifficulty mapDifficulty))
            mapDifficulty = MapDifficulty.Intermediate;
        var customMapModel = mapEditorModel.customMapModel;
        CustomMaps[id] = new CustomMapRecord(name, id, path, mapEditorModel.customMapModel, mapDifficulty);

        LocalizationManager.Instance.textTable[id] = mapEditorModel.name;

        var mapDetails = new MapDetails
        {
            id = id,
            difficulty = mapDifficulty,
            unlockDifficulty = mapDifficulty,
            hasWater = customMapModel.AreaData.Any(area => area.IsWater()),
            mapMusic = customMapModel.musicTrack,
            isBrowserOnly = Game.Version.CompareTo(new Version(ModHelperData.WorksOnVersion)) >= 0
        };
        mapDetails.SetMapSprite($"{SpriteAtlas}[{id}]");

        if (!HasThumbnail(id, out _))
        {
            DownloadThumbnail(name).ContinueWith(task =>
            {
                if (task is {IsCompletedSuccessfully: true, Result: true})
                {
                    ModHelper.Msg<CustomMapChallengesMod>($"Fetched thumbnail for {id}");
                    return;
                }

                TaskScheduler.ScheduleTask(() =>
                {
                    // Bug with calling il2cpp async methods that have results, so we're doing this I guess
                    try
                    {
                        MapEditorThumbnails.InvalidateMapThumbnail(id);

                        // ReSharper disable once Unity.IncorrectMonoBehaviourInstantiation
                        new MapEditorScreen().LoadMapThumbnailAsync(id, customMapModel).ContinueWith(
                            new Action<Il2CppSystem.Threading.Tasks.Task>(t =>
                            {
                                if (t.IsCompletedSuccessfully)
                                {
                                    ModHelper.Msg<CustomMapChallengesMod>($"Generated thumbnail for {id}");
                                }
                                else
                                {
                                    ModHelper.Warning<CustomMapChallengesMod>($"Failed to get thumbnail for {id}");
                                }
                            }));
                    }
                    catch (Exception e)
                    {
                        ModHelper.Warning<CustomMapChallengesMod>(e);
                    }
                });
            });
        }

        AddMapToGame(mapDetails);
        ModHelper.Msg<CustomMapChallengesMod>($"Loaded map {id} for {mapDifficulty} difficulty");
    }

    /// <summary>
    /// Add MapDetails to list
    /// </summary>
    /// <param name="mapDetails"></param>
    private static void AddMapToGame(MapDetails mapDetails)
    {
        var maps = GameData.Instance.mapSet.Maps;
        var mapList = maps.items.ToList();

        mapList.RemoveAll(details => details.id == mapDetails.id);

        var index = mapList.FindLastIndex(details => details.difficulty == mapDetails.difficulty);
        mapList.Insert(index + 1, mapDetails);

        maps.items = mapList.ToArray();
    }

    /// <summary>
    /// Unloads a map that was deleted from its file path
    /// </summary>
    /// <param name="path"></param>
    public static void DeleteMapFromPath(string path)
    {
        if ((Path.HasExtension(path) && Path.GetExtension(path) != ".json") || Directory.Exists(path)) return;

        var id = ModContent.GetId<CustomMapChallengesMod>(Path.GetFileNameWithoutExtension(path));

        if (!CustomMaps.TryGetValue(id, out var map)) return;

        var maps = GameData.Instance.mapSet.Maps;
        maps.items = maps.items.Where(m => m.id != map.Id).ToArray();

        CustomMaps.Remove(id);

        ModHelper.Msg<CustomMapChallengesMod>($"Deleted map {id}");
    }

    public static bool HasThumbnail(string map, out string key)
    {
        key = map + "_large";
        if (!File.Exists(MapEditorThumbnails.storageManager.GetFilePath(key)))
        {
            key = map.Replace(ModContent.GetInstance<CustomMapChallengesMod>().IDPrefix, "") + "_large";
        }

        Directory.CreateDirectory(MapEditorThumbnails.storageManager.GetFilePath(""));

        return File.Exists(MapEditorThumbnails.storageManager.GetFilePath(key));
    }

    public static async Task<bool> DownloadThumbnail(string map)
    {
        using var client = new HttpClient();

        var response = await client.GetAsync($"https://data.ninjakiwi.com/btd6/maps/map/{map}/preview");

        var bytes = await response.Content.ReadAsByteArrayAsync();

        if (bytes.Length < 1024)
        {
            return false;
        }

        var encodedBytes = MapEditorThumbnails.Shuffle(bytes);
        var path = MapEditorThumbnails.storageManager.GetFilePath(map + "_large");

        await File.WriteAllBytesAsync(path, encodedBytes);

        return true;
    }

    public static void CreateChallengeUI(GameMenu screen, string id, string? baseId = null)
    {
        var difficulties = Enum.GetValues<MapDifficulty>().ToList();

        var panel = screen.gameObject.AddModHelperPanel(new Info("CustomMapChallenge", 350, 450)
        {
            Pivot = new Vector2(0.5f, 0)
        });
        var button = panel.AddButton(new Info("Icon", 350, 350)
            {
                Anchor = new Vector2(0.5f, 0),
                Pivot = new Vector2(0.5f, 0)
            },
            VanillaSprites.EditChallengeIcon,
            new Action(() =>
            {
                if (screen.Is<MapEditorScreen>())
                {
                    DailyChallengeManager.Player.Data.challengeEditorModel.map = id;
                    MenuManager.instance.OpenMenu(SceneNames.ChallengeEditorPlay);
                }
                else
                {
                    PopupScreen.instance.SafelyQueue(popupScreen => popupScreen.ShowOkPopup(
                        """
                        Map Difficulty primarily affects Hero Level up speed.
                        Beginner: 1.0x XP, Intermediate: 1.1x XP,
                        Advanced 1.2x XP, Expert: 1.3x XP.w
                        Custom Maps in the browser always use Intermediate.
                        """
                    ));
                }
            })
        );
        var text = panel.AddText(new Info("Text", 350, 150)
        {
            Y = -25,
            Anchor = new Vector2(0.5f, 0),
            Pivot = new Vector2(0.5f, 0)
        }, screen.Is<MapEditorScreen>() ? "Challenge Editor" : "Map Difficulty", 60f);
        if (screen.Is<ChallengeEditorPlay>() && Game.Version.Major >= 44 && Game.Version.Minor >= 1)
        {
            button.RectTransform.localPosition = new Vector3(0, 450, 0);
            text.RectTransform.localPosition = new Vector3(0, 775, 0);
        }

        var dropdown = panel.AddDropdown(new Info("Dropdown", 450, 100)
            {
                Anchor = new Vector2(0.5f, 1),
                Pivot = new Vector2(0.5f, 1)
            },
            new[] {"Don't Include"}.Concat(difficulties.Select(difficulty => difficulty.ToString())).ToIl2CppList(),
            750, new Action<int>(newValue =>
            {
                var map = CustomMaps.GetValueOrDefault(id);

                button.Button.interactable = newValue != 0;

                if (newValue == 0 && map != null)
                {
                    File.Delete(map.FilePath);
                    if (screen.Is(out ChallengeEditorPlay challengeEditorPlay))
                    {
                        DailyChallengeManager.Player.Data.challengeEditorModel.map = "Tutorial";
                        if (MenuManager.instance.IsMenuOpenOrOpening(SceneNames.MapEditorUI))
                        {
                            MenuManager.instance.CloseCurrentMenu();
                        }
                        else
                        {
                            panel.gameObject.Destroy();
                            challengeEditorPlay.shareBtn.transform.parent.gameObject.SetActive(true);
                            screen.ReOpen(null);
                        }
                    }

                    return;
                }

                var difficulty = difficulties[newValue - 1];
                if (screen.Is<ChallengeEditorPlay>())
                {
                    button.Image.SetSprite(VanillaSprites.ByName[$"Map{difficulty.ToString()}Btn"]);
                }

                if (map == null)
                {
                    File.Copy(Path.Combine(VanillaMapsFolder, baseId!),
                        Path.Combine(MapsFolder, difficulty.ToString(), baseId!), true);
                }
                else if (difficulty != map.Difficulty)
                {
                    File.Move(map.FilePath, Path.Combine(MapsFolder, difficulty.ToString(),
                        Path.GetFileName(map.FilePath)), true);
                }
            }), VanillaSprites.BlueInsertPanelRound
        );
        var currentValue = CustomMaps.TryGetValue(id, out var map) ? difficulties.IndexOf(map.Difficulty) + 1 : 0;
        button.Button.interactable = currentValue != 0;
        if (map != null && screen.Is<ChallengeEditorPlay>())
        {
            button.Image.SetSprite(VanillaSprites.ByName[$"Map{map.Difficulty.ToString()}Btn"]);
        }

        dropdown.Dropdown.SetValue(currentValue);
        dropdown.Arrow.RectTransform.Rotate(180f, 0, 0);

        var template = dropdown.Dropdown.template;
        template.pivot = new Vector2(0.5f, 0);
        template.anchorMin = new Vector2(0.5f, 1);
        template.anchorMax = new Vector2(0.5f, 1);
        template.localPosition = new Vector3(0, 0, 0);

        var positionMatcher = panel.AddComponent<MatchLocalPosition>();
        positionMatcher.transformToCopy = CommonForegroundScreen.instance.changeHeroButton.transform;
        positionMatcher.scale = new Vector3(-1, 1, 1);
    }

    public static void CreateMapSelectUI(GameObject mapSelectPanel)
    {
        if (mapSelectPanel.transform.GetComponentFromChildrenByName<ModHelperPanel>("CustomFilterPanel")) return;

        var panel = mapSelectPanel.AddModHelperPanel(new Info("CustomFilterPanel", 500, 250)
        {
            Anchor = new Vector2(0, 0),
            Pivot = new Vector2(0, 0),
            Position = new Vector2(50, 25)
        });

        var checkbox = panel.AddCheckbox(new Info("Checkbox", 0, 50, 250, 250), false, VanillaSprites.MapEditorBtn,
            new Action<bool>(
                (on) =>
                {
                    foreach (var image in mapSelectPanel.transform.GetComponentsInChildren<Image>(true)
                                 .Where(image => image.name == "MapImage"))
                    {
                        var mapImage = image.transform.parent.gameObject;
                        try
                        {
                            VersionCompat.ShowIfCustom(mapImage, on);
                        }
                        catch (Exception)
                        {
                            mapImage.SetActive(!on || string.IsNullOrEmpty(image.sprite.name));
                        }
                    }
                }));

        checkbox.AddText(new Info("Text", 0, -140, 400, 150), "Custom Only", 55f);
    }

    public static bool ShowingCustom;

    public static void CreateContentBrowserUI(ContentBrowser contentBrowser)
    {
        if (contentBrowser.transform.GetComponentFromChildrenByName<ModHelperPanel>("CustomFilterPanel")) return;

        var panel = contentBrowser.transform.Find("Container").gameObject.AddModHelperPanel(
            new Info("CustomFilterPanel", 250, 250)
            {
                Anchor = new Vector2(1, 0),
                Pivot = new Vector2(0, 0),
                Position = new Vector2(85, 50)
            });

        panel.AddButton(new Info("FilterCustomButton", 0, 50, 250, 250), VanillaSprites.EditChallengeIcon, new Action(
            () =>
            {
                var tabSettings = ContentBrowser.BrowserSettings.tabSettings[contentBrowser.SelectedTab].custom;

                var challengeIds = CustomMaps.Values
                    .Where(record => !Path.HasExtension(record.FilePath))
                    .Select(record => record.Name)
                    .ToIl2CppList()
                    .Cast<Il2CppSystem.Collections.Generic.IEnumerable<string>>();

                contentBrowser.SetSearchView(true);
                contentBrowser.LoadChallengesWithIds(challengeIds, "custom", tabSettings);
                ShowingCustom = true;
            })
        );

        panel.AddText(new Info("Text", 0, -90, 400, 150), "Current Challenges", 55f);
    }
}