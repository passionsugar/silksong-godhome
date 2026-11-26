using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Silksong.DataManager;

// I18N 在游戏语言没有对应文件时回退到中文。
[assembly: System.Resources.NeutralResourcesLanguage("ZH")]

namespace Godhome;

/// <summary>
/// [Godhome] 织忆圣所 / The Loom of Recollection：神居存档（第二版）。
/// 新建存档时选“织忆圣所”，一进来就是满配，在大厅挑战任意 Boss；每场战斗的改动在回大厅时还原，
/// 存档文件里不会出现战斗里的状态。普通存档里插件什么都不改。
/// </summary>
[BepInAutoPlugin(id: "local.silksong.godhome")]
[BepInDependency("org.silksong-modding.datamanager")]
[BepInDependency("org.silksong-modding.i18n")]
// 试验：读前作场景用（没装也能正常加载）。
[BepInDependency(Hk1Probe.RepackerId, BepInDependency.DependencyFlags.SoftDependency)]
public partial class GodhomePlugin : BaseUnityPlugin, ISaveDataMod<GodhomeSaveData>
{
    internal static GodhomePlugin Instance { get; private set; } = null!;

    internal static ManualLogSource Log { get; private set; } = null!;

    /// <summary>插件 dll 所在目录（里面有 data/ 和 languages/）。</summary>
    internal static string PluginDir { get; private set; } = "";

    /// <summary>DataManager 按存档槽读写。标题画面或该槽还没有数据时为 null。</summary>
    public GodhomeSaveData? SaveData { get; set; }

    /// <summary>当前存档槽的模式记录；没有就新建一份，下次游戏存档时由 DataManager 写盘。</summary>
    internal GodhomeSaveData Records => (SaveData ??= new GodhomeSaveData()).Normalized();

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        PluginDir = Path.GetDirectoryName(Info.Location) ?? Paths.PluginPath;
        GodhomeConfig.Bind(Config);
        // 试验：前作场景的脚本改绑要在第一次打包前登记（UnitySceneRepacker 的要求）。
        Hk1Gate.EarlyInit();
        BossCatalog.Load(Path.Combine(PluginDir, "data", "bosses.json"));
        StatueCatalog.Load(Path.Combine(PluginDir, "data", "bosses.json"));
        // 万神殿表按 id 找 Boss：要在 Boss 表之后读。
        PantheonCatalog.Load(Path.Combine(PluginDir, "data", "pantheons.json"));
        GodhomeSave.LoadKit(Path.Combine(PluginDir, "data", "kit.json"));
        // 前作神居：雕像怎么合并、特殊雕像、五扇门接哪个万神殿（要在雕像表、万神殿表之后读）。
        Hk1Hall.Load(Path.Combine(PluginDir, "data", "hk1.json"));
        PatchAll();
        gameObject.AddComponent<ModeController>();
        gameObject.AddComponent<LoomUI>();
        gameObject.AddComponent<DebugTools>();
        Probe.Install(gameObject);
        Log.LogInfo($"[Godhome] {Name} {Version} loaded / 已加载：{BossCatalog.All.Count} 个 Boss，调试模式 / debug = {GodhomeConfig.DebugMode.Value}");
    }

    /// <summary>只给调试用的补丁（录宣传片的输入、画面比例，淡入的追踪）。调试关着时不打（审查）：每帧的输入更新上不挂东西，
    /// 游戏更新后这些目标变了也不会连累别的补丁。</summary>
    private static readonly HashSet<string> DebugOnlyPatches = new(StringComparer.Ordinal)
    {
        nameof(PuppetPatch), nameof(PromoAspectPatch), nameof(FadeSceneInTrace),
    };

    /// <summary>和 Harmony.PatchAll 一样逐个类打补丁（出错照样抛出、插件不加载），只是调试关着时跳过调试补丁。</summary>
    private void PatchAll()
    {
        var harmony = new Harmony(Id);
        bool debug = GodhomeConfig.DebugMode.Value;
        foreach (Type t in AccessTools.GetTypesFromAssembly(typeof(GodhomePlugin).Assembly))
        {
            if (!debug && DebugOnlyPatches.Contains(t.Name))
            {
                continue;
            }
            harmony.CreateClassProcessor(t).Patch();
        }
    }
}
