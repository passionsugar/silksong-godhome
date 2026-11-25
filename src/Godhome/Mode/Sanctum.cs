using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Godhome;

/// <summary>
/// [Godhome] 织忆圣所（第二版·3 重做）：神居存档在这里醒来，和原来的任何地图都不一样。
///
/// 做法：寄住在大厅场景（存档、复活、读档要一个真实存在的场景名），场景加载时（SceneManager.sceneLoaded，
/// 物体的 Start 还没跑、大黄蜂还没放进来）把原来的渲染物全部藏起来、碰撞体全部关掉，只留长椅（复活点）和门（进场用）；
/// 马上搭好新地形和镜头锁定区（Prepare）。到场以后再从游戏图集里借精灵把画面搭出来（BuildRoutine，见 SanctumArt）。
///
/// 布局（x 往右，地面 FloorY 是长椅脚下）：
/// - 圣所本体 x 0..64：中央是长椅和身后的“织忆之机”（织者符文、光柱、垂下的灵丝），两侧古织者像，右边丝门通往长廊。
/// - 忆像长廊 x 120 起：上下两层（像前作的神居），每层一排雕像。左端有丝线升降（上下层之间传送）和回圣所的丝门，
///   两层之间走左端的丝线升降。雕像用图鉴立绘当“忆像”，底座、拉杆、符文都是游戏里织者遗迹的原图。
/// 圣所和长廊之间用丝线传送（画面渐暗 → 瞬移 → 渐亮），同一个场景里，不切场景。
/// </summary>
internal static class Sanctum
{
    private const int TerrainLayer = 8;
    private const string RootName = "Godhome Sanctum";

    // ── 布局 ──
    internal const float HubLeft = 0f;
    internal const float HubRight = 64f;
    internal const float HubGateX = 56f;
    internal const float AltarX = 31f;
    internal const float HallLeft = 120f;
    internal const float Spacing = 7.5f;
    internal const float FirstStatue = 16f;
    internal const float UpperRise = 15f;
    internal const float SlabThick = 1.5f;
    internal const float CamAbove = 5.8f;
    internal const float HallGateOffset = 4f;
    internal const float LiftOffset = 10f;

    /// <summary>升降落点离升降中心多远（落在另一头升降的交互区外面，免得一到就弹出提示）。</summary>
    internal const float LiftLanding = 2.4f;

    // ── 万神殿前厅（长廊右边单独一间，三层）。镜头 x 不能小于 14.6（CameraController），所以只能往右放。 ──
    /// <summary>圣所里去前厅的丝门（原来那尊古织者像的位置）。</summary>
    internal const float HubAtriumGateX = 8f;
    internal const float AtriumGap = 40f;
    internal const float AtriumWidth = 66f;
    internal const float AtriumRise = 15f;
    internal const float AtriumGateOffset = 6f;
    internal const float AtriumLiftA = 12f;
    internal const float AtriumLiftB = 57f;
    internal const float AtriumDoorA = 25f;
    internal const float AtriumDoorB = 43f;
    internal const float AtriumDoorTop = 34f;

    /// <summary>回程目标的前缀：“door:万神殿 id” 表示回到那扇门前（和雕像共用回程门那条路，见 ReturnGateName）。</summary>
    internal const string DoorPrefix = "door:";

    /// <summary>加载时建好的回程门（门名 → 门），给日志和巡视核对用。</summary>
    internal static readonly Dictionary<string, TransitionPoint> ReturnGates = new();

    internal static readonly List<Statue> Statues = new();

    internal static readonly List<PantheonDoor> Doors = new();

    internal static float FloorY { get; private set; } = 105f;
    internal static float UpperY => FloorY + UpperRise;
    internal static float HallRight { get; private set; }

    internal static float AtriumLeft => HallRight + AtriumGap;
    internal static float AtriumRight => AtriumLeft + AtriumWidth;
    internal static float AtriumTopFloor => FloorY + 2f * AtriumRise;

    /// <summary>新地形、镜头已经全部搭好（场景加载时）。搭到一半出错会撤回，不算准备好（Codex 审查 v0.6.0 #9）。</summary>
    internal static bool Prepared => root != null && prepared;

    /// <summary>画面（美术、雕像、交互）搭过了（可能不完整，见 BuildOk）。</summary>
    internal static bool Built { get; private set; }

    /// <summary>画面完整：雕像、四个传送点都搭好，没有出错。</summary>
    internal static bool BuildOk { get; private set; }

    /// <summary>正在丝线传送（期间不响应别的交互）。</summary>
    internal static bool Busy { get; private set; }

    /// <summary>
    /// 丝门、雕像、拉杆、名录共用的互斥判断（Codex 审查 v0.6.0 #8）：正在传送、名录或挑战面板开着时都不响应。
    /// </summary>
    internal static bool InteractionBlocked => Busy || (LoomUI.Instance != null && (LoomUI.Instance.ChallengeBusy || LoomUI.Instance.ResultBusy));

    /// <summary>搭好时的说明（日志、巡视报告用）。</summary>
    internal static string Note { get; private set; } = "";

    internal static Scene LobbySceneRef { get; private set; }

    private static GameObject? root;
    private static bool prepared;
    private static Transform? art;
    private static Sprite? white;
    private static SpriteRenderer? reference;
    private static int interactLayer;
    private static int hitLayer;
    private static int lockLayer;
    private static GameObject? bench;
    private static CustomSceneManager? sceneManager;

    // 准备时登记的原房间（Codex 审查 v0.6.0 #14）：到场后只按这些复查，出错时按这些撤回。
    private static readonly List<GameObject> originalRoots = new();
    private static readonly List<Renderer> hidden = new();
    private static readonly List<Collider2D> offColliders = new();
    private static readonly List<InteractableBase> offInteract = new();

    // 每次搭画面、每次传送一个编号：过期的协程不再动手、不揭别人的黑幕（Codex 审查 v0.6.0 #3、#4）。
    private static int buildToken;
    private static int travelToken;
    private static readonly List<RaycastHit2D> floorHits = new();

    internal static CameraLockArea? HubArea { get; private set; }
    internal static CameraLockArea? HallArea { get; private set; }
    internal static CameraLockArea? AtriumArea { get; private set; }

    /// <summary>镜头边界放宽到最右边的前厅、最高的前厅上层。</summary>
    internal static float WantXLimit => AtriumRight - 14.6f;
    internal static float WantYLimit => Mathf.Max(UpperY, AtriumTopFloor) + CamAbove + 0.5f;

    // ───────────────────────────── 场景加载时：藏原图、搭地形 ─────────────────────────────

    /// <summary>
    /// 大厅场景刚加载（SceneManager.sceneLoaded，物体 Awake 已跑、Start 未跑、大黄蜂还没放进来）。
    /// 同一个场景实例只做一次。
    /// </summary>
    internal static void Prepare(Scene scene)
    {
        if (root != null && root.scene == scene)
        {
            return;
        }
        // 新的大厅实例：上一个大厅的状态全部作废（物体跟着旧场景销毁）。
        ResetState();
        LobbySceneRef = scene;
        try
        {
            PrepareInner(scene);
            prepared = root != null;
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogError($"[Godhome] 圣所准备失败，撤回 / sanctum prepare failed, rolling back: {e}");
            Rollback();
        }
    }

    private static void ResetState()
    {
        Statues.Clear();
        Doors.Clear();
        ReturnGates.Clear();
        AtriumArea = null;
        originalRoots.Clear();
        hidden.Clear();
        offColliders.Clear();
        offInteract.Clear();
        root = null;
        prepared = false;
        art = null;
        bench = null;
        sceneManager = null;
        reference = null;
        HubArea = null;
        HallArea = null;
        Built = false;
        BuildOk = false;
        Busy = false;
        travelToken++;
    }

    /// <summary>准备到一半出错：原房间的渲染、碰撞、交互全部打开，删掉半成品（光照的改动留着，只影响颜色）。</summary>
    private static void Rollback()
    {
        foreach (Renderer r in hidden)
        {
            if (r != null)
            {
                r.enabled = true;
            }
        }
        foreach (Collider2D c in offColliders)
        {
            if (c != null)
            {
                c.enabled = true;
            }
        }
        foreach (InteractableBase ib in offInteract)
        {
            if (ib != null)
            {
                ib.enabled = true;
            }
        }
        if (root != null)
        {
            Object.Destroy(root);
        }
        Scene keep = LobbySceneRef;
        ResetState();
        LobbySceneRef = keep;
    }

    private static void PrepareInner(Scene scene)
    {
        originalRoots.AddRange(scene.GetRootGameObjects());
        List<GameObject> all = AllObjects(scene);
        bench = all.FirstOrDefault(g => g.GetComponent<RestBench>() != null);
        if (bench == null)
        {
            GodhomePlugin.Log.LogWarning("[Godhome] 大厅里找不到长椅，不搭圣所 / no bench in the lobby scene, sanctum skipped");
            return;
        }
        // 地面：长椅脚下（原地形还开着的时候量）。
        FloorY = MeasureFloor(bench.transform.position, scene);
        int n = StatueCatalog.All.Count;
        int lower = (n + 1) / 2;
        HallRight = HallLeft + FirstStatue + Mathf.Max(0, lower - 1) * Spacing + 6f;

        PickReferences(all);
        HideOriginal(all);
        ApplyAtmosphere(all);

        root = new GameObject(RootName);
        SceneManager.MoveGameObjectToScene(root, scene);
        BuildTerrain();
        BuildCameraAreas();
        BuildReturnGates();
        root.AddComponent<SanctumCamera>();
        Note = $"圣所 / sanctum: 地面 / floor y={FloorY:0.00}，长廊 / hall x {HallLeft:0}..{HallRight:0.0}，两层 / floors {FloorY:0.0} & {UpperY:0.0}；回程门 / return gates {ReturnGates.Count}；藏起原图 / hid {hidden.Count} renderers, {offColliders.Count} colliders";
        GodhomePlugin.Log.LogInfo($"[Godhome] {Note}");
    }

    /// <summary>雕像的回程门名（打完从 door_dreamReturn 回来时，bossReturnEntryGate 写它）。万神殿的门见 DoorPrefix。</summary>
    internal static string? ReturnGateName(string spot) =>
        spot.StartsWith(DoorPrefix, StringComparison.Ordinal)
            ? "door_pantheon_" + spot.Substring(DoorPrefix.Length)
            : "door_statue_" + spot;

    /// <summary>雕像 i 的位置（和 BuildArt 摆雕像的算法一样：一半在下层，一半在上层）。</summary>
    internal static (float X, float Floor, bool Upper) StatueSpot(int i)
    {
        int lower = (StatueCatalog.All.Count + 1) / 2;
        bool up = i >= lower;
        int k = up ? i - lower : i;
        return (HallLeft + FirstStatue + k * Spacing, up ? UpperY : FloorY, up);
    }

    /// <summary>
    /// 回程门（前作打完回到雕像前用的就是这个）：每尊雕像、每扇万神殿的门前各一个门型 TransitionPoint，
    /// 名字带 door、不能交互、没有碰撞体，子物体是危险复活点。站的位置和以前回程站的一样：雕像左边 2 格、门左边 3.6 格，朝右。
    /// 场景加载时就建好：游戏随后按 door_dreamReturn → bossReturnEntryGate 找门（GameManager.cs:3313、3365）。
    /// </summary>
    private static void BuildReturnGates()
    {
        Transform parent = new GameObject("Return Gates").transform;
        parent.SetParent(root!.transform, false);
        for (int i = 0; i < StatueCatalog.All.Count; i++)
        {
            (float x, float floor, _) = StatueSpot(i);
            ReturnGate(parent, ReturnGateName(StatueCatalog.All[i].Id)!, new Vector2(x - 2f, floor + 1f));
        }
        for (int i = 0; i < PantheonCatalog.All.Count; i++)
        {
            (float x, int level) = AtriumLayout.Spot(i);
            float size = level >= 2 ? 1.2f : 1f;
            ReturnGate(parent, ReturnGateName(DoorPrefix + PantheonCatalog.All[i].Id)!, new Vector2(x - 3.6f * size, FloorY + level * AtriumRise + 1f));
        }
    }

    private static void ReturnGate(Transform parent, string name, Vector2 at)
    {
        var go = new GameObject(name);
        go.SetActive(false);
        go.transform.SetParent(parent, false);
        go.transform.position = new Vector3(at.x, at.y, 0f);
        var marker = new GameObject("Hazard Respawn Marker");
        marker.transform.SetParent(go.transform, false);
        HazardRespawnMarker hrm = marker.AddComponent<HazardRespawnMarker>();
        Traverse.Create(hrm).Field("respawnFacingDirection").SetValue(HazardRespawnMarker.FacingDirection.Right);
        TransitionPoint tp = go.AddComponent<TransitionPoint>();
        tp.isADoor = true;
        tp.isInactive = true;
        tp.dontWalkOutOfDoor = true;
        tp.alwaysEnterRight = true;
        tp.respawnMarker = hrm;
        tp.targetScene = "";
        tp.entryPoint = "";
        go.SetActive(true);
        ReturnGates[name] = tp;
    }

    /// <summary>
    /// 长椅脚下的地面（Codex 审查 v0.6.0 #12）：地形层、不算触发器、只算这个大厅场景里的碰撞体，取最近的一个。
    /// </summary>
    private static float MeasureFloor(Vector2 bp, Scene scene)
    {
        var filter = new ContactFilter2D();
        filter.SetLayerMask(1 << TerrainLayer);
        filter.useTriggers = false;
        int n = Physics2D.Raycast(bp + Vector2.up, Vector2.down, filter, floorHits, 12f);
        bool found = false;
        RaycastHit2D best = default;
        for (int i = 0; i < n; i++)
        {
            RaycastHit2D h = floorHits[i];
            if (h.collider != null && !h.collider.isTrigger && h.collider.gameObject.scene == scene && (!found || h.distance < best.distance))
            {
                best = h;
                found = true;
            }
        }
        if (!found)
        {
            GodhomePlugin.Log.LogWarning("[Godhome] 长椅脚下没量到地面，按长椅高度估 / no ground under the bench, estimating from the bench");
        }
        return found ? best.point.y : bp.y - 0.7f;
    }

    private static List<GameObject> AllObjects(Scene scene)
    {
        var list = new List<GameObject>();
        foreach (GameObject r in scene.GetRootGameObjects())
        {
            foreach (Transform t in r.GetComponentsInChildren<Transform>(true))
            {
                list.Add(t.gameObject);
            }
        }
        return list;
    }

    private static bool UnderBench(Transform t) => bench != null && (t == bench.transform || t.IsChildOf(bench.transform));

    /// <summary>
    /// 原来的房间全部藏起来：渲染物关掉（长椅除外），碰撞体关掉（长椅、门除外），其他交互物体停用。
    /// 门留着：读档、从长廊入口回来都要按门名把人放进来（到场后再关）。
    /// </summary>
    private static void HideOriginal(List<GameObject> all)
    {
        foreach (GameObject g in all)
        {
            if (g != null && !UnderBench(g.transform))
            {
                HideOne(g, gate: g.GetComponentInParent<TransitionPoint>(true) != null);
            }
        }
    }

    /// <summary>藏起一个原房间物体：渲染器关掉；不是门的话碰撞体、交互也关掉。返回这次新关掉的个数。</summary>
    private static int HideOne(GameObject g, bool gate)
    {
        int n = 0;
        foreach (Renderer r in g.GetComponents<Renderer>())
        {
            if (r != null && r.enabled)
            {
                r.enabled = false;
                hidden.Add(r);
                n++;
            }
        }
        if (gate)
        {
            return n;
        }
        foreach (Collider2D c in g.GetComponents<Collider2D>())
        {
            if (c != null && c.enabled)
            {
                c.enabled = false;
                offColliders.Add(c);
                n++;
            }
        }
        foreach (InteractableBase ib in g.GetComponents<InteractableBase>())
        {
            if (ib != null && ib.enabled)
            {
                ib.enabled = false;
                offInteract.Add(ib);
                n++;
            }
        }
        return n;
    }

    /// <summary>
    /// 到场以后再藏一遍：有的物体 Start/OnEnable 里会重新打开渲染器、碰撞体（只藏画面会留下隐形墙）。
    /// 只查准备时登记的原房间（Codex 审查 v0.6.0 #14），后来生成到这个场景里的东西（特效之类）不碰。
    /// 门也在这时关掉（圣所没有出口）。
    /// </summary>
    private static void HideAgain()
    {
        int again = 0;
        var gates = new List<GameObject>();
        foreach (GameObject r0 in originalRoots)
        {
            if (r0 == null)
            {
                continue;
            }
            foreach (Transform t in r0.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || UnderBench(t))
                {
                    continue;
                }
                bool gate = t.GetComponentInParent<TransitionPoint>(true) != null;
                again += HideOne(t.gameObject, gate);
                if (gate && t.GetComponent<TransitionPoint>() != null && t.gameObject.activeSelf)
                {
                    gates.Add(t.gameObject);
                }
            }
        }
        foreach (GameObject g in gates)
        {
            g.SetActive(false);
        }
        if (again > 0)
        {
            GodhomePlugin.Log.LogInfo($"[Godhome]   到场后又关掉 {again} 个原房间的渲染 / 碰撞 / 交互 / disabled {again} more original renderers/colliders/interactables after arrival");
        }
    }

    /// <summary>
    /// 圣所的光：不用原房间（织巢）的地图区调色和黑暗度，换成暖白的环境光、金色的大黄蜂光晕、稍暖的调色曲线、黑暗度 0。
    /// 依据（反编译 CustomSceneManager）：Start 里 mapZone 不是 NONE 且没勾 overrideColorSettings 时，按地图区默认值
    /// 覆盖调色字段，再 UpdateScene 写进 GameCameras 的调色和环境光；darknessLevel 在 Start 写进暗角的
    /// “Darkness Control” FSM，Update 里再 SetDarkness。这里在 Start 之前改字段，所以第一帧就是圣所的光。
    /// 换场景时下一个场景的 CustomSceneManager 会照常设回它自己的值。
    /// </summary>
    private static void ApplyAtmosphere(List<GameObject> all)
    {
        sceneManager = all.Select(g => g.GetComponent<CustomSceneManager>()).FirstOrDefault(c => c != null);
        if (sceneManager == null)
        {
            GodhomePlugin.Log.LogWarning("[Godhome] 大厅里找不到 CustomSceneManager，光照保持原样 / no CustomSceneManager in the lobby; lighting unchanged");
            return;
        }
        // 原房间的调色标记（GradeMarker）按大黄蜂离它多远把它自己的调色（SceneColorManager 的 B 组）混进来，
        // 织巢的是偏绿的那种：在 Start 之前关掉（OnDisable 会把它从活动列表里拿掉）。
        int markers = 0;
        foreach (GradeMarker gmk in all.Select(g => g.GetComponent<GradeMarker>()).Where(c => c != null))
        {
            gmk.enableGrade = false;
            gmk.enabled = false;
            markers++;
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 圣所光照：关掉调色标记 {markers} 个 / sanctum lighting: disabled {markers} grade marker(s)");
        CustomSceneManager m = sceneManager;
        Traverse.Create(m).Field("overrideColorSettings").SetValue(true);
        m.saturation = 0.92f;
        m.redChannel = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        m.greenChannel = AnimationCurve.Linear(0f, 0f, 1f, 0.97f);
        m.blueChannel = AnimationCurve.Linear(0f, 0.02f, 1f, 0.9f);
        m.defaultColor = new Color(1f, 0.95f, 0.86f, 1f);
        m.defaultIntensity = 1f;
        m.heroLightColor = new Color(1f, 0.86f, 0.62f, 0.5f);
        m.heroSaturationOffset = 0f;
        m.blurPlaneVibranceOffset = 0f;
        m.darknessLevel = 0;
        m.noLantern = false;
        // 地图区的环境粒子（织巢是绿色的浮尘）不要，圣所有自己的金色浮尘（SanctumMotes）。
        m.noParticles = true;
        // 场景黑边（CustomSceneManager.Start → DrawBlackBorders 按 sceneBordersMask 在原房间四周摆黑条）：一条都不要。
        m.sceneBordersMask = 0;
    }

    /// <summary>到场后再套一次（准备晚了、Start 已经跑过的情况；平时只是重复写同样的值）。</summary>
    private static void ReapplyAtmosphere()
    {
        if (sceneManager == null)
        {
            return;
        }
        try
        {
            sceneManager.UpdateScene();
            GameCameras cams = GameCameras.SilentInstance;
            if (cams != null && cams.sceneColorManager != null)
            {
                // 只用 A 组（我们的值）：混合系数归零。
                cams.sceneColorManager.SetFactor(0f);
                cams.sceneColorManager.UpdateScript(forceUpdate: true);
            }
            GameObject vignette = GameObject.FindGameObjectWithTag("Vignette");
            PlayMakerFSM? fsm = vignette != null ? FSMUtility.LocateFSM(vignette, "Darkness Control") : null;
            if (fsm != null)
            {
                FSMUtility.SetInt(fsm, "Darkness Level", 0);
                fsm.SendEvent("RESET");
            }
            HeroController hero = HeroController.SilentInstance;
            if (hero != null)
            {
                hero.SetDarkness(0);
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 圣所光照设置出错 / sanctum lighting failed: {e.Message}");
        }
        // 环境粒子：镜头定位到大黄蜂时（SceneParticlesController.OnPositionedAtHero）按 noParticles 开关；
        // 准备晚了的话那时读到的还是原值，这里直接关掉（Codex 审查 v0.6.0 #14）。
        try
        {
            GameCameras cams = GameCameras.SilentInstance;
            SceneParticlesController? sp = cams != null ? Traverse.Create(cams).Field("sceneParticles").GetValue<SceneParticlesController>() : null;
            if (sp != null)
            {
                sp.DisableParticles();
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 关环境粒子出错 / disabling scene particles failed: {e.Message}");
        }
    }

    private static void PickReferences(List<GameObject> all)
    {
        reference = bench != null ? bench.GetComponent<SpriteRenderer>() : null;
        interactLayer = bench != null ? bench.layer : 0;
        GameObject? br = all.FirstOrDefault(g => g.GetComponent<Breakable>() != null);
        hitLayer = br != null ? br.layer : 19;
        GameObject? la = all.FirstOrDefault(g => g.GetComponent<CameraLockArea>() != null);
        lockLayer = la != null ? la.layer : 0;
    }

    // ───────────────────────────── 地形 ─────────────────────────────

    private static void BuildTerrain()
    {
        Transform t = new GameObject("Terrain").transform;
        t.SetParent(root!.transform, false);
        float f = FloorY;
        Color fill = new(0.018f, 0.018f, 0.024f);
        // 圣所本体
        Solid(t, "Hub Floor", HubLeft - 12f, f - 14f, HubRight + 12f, f, fill);
        Solid(t, "Hub Wall L", HubLeft - 12f, f, HubLeft, f + 32f, fill);
        Solid(t, "Hub Wall R", HubRight, f, HubRight + 12f, f + 32f, fill);
        Solid(t, "Hub Ceiling", HubLeft - 12f, f + 24f, HubRight + 12f, f + 34f, fill);
        // 长廊
        float l = HallLeft, r = HallRight, u = UpperY;
        Solid(t, "Hall Floor", l - 12f, f - 14f, r + 12f, f, fill);
        Solid(t, "Hall Wall L", l - 12f, f, l, u + 24f, fill);
        Solid(t, "Hall Wall R", r, f, r + 12f, u + 24f, fill);
        // 楼板一直铺到右墙：两层之间只走左端的丝线升降（原来右端的台阶上不去、下不来，删了）。
        Solid(t, "Hall Slab", l, u - SlabThick, r, u, fill);
        Solid(t, "Hall Ceiling", l - 12f, u + 15f, r + 12f, u + 26f, fill);
        // 万神殿前厅：三层，楼板铺满（层与层之间只走丝线升降）。
        float al = AtriumLeft, ar = AtriumRight, top = AtriumTopFloor;
        Solid(t, "Atrium Floor", al - 12f, f - 14f, ar + 12f, f, fill);
        Solid(t, "Atrium Wall L", al - 12f, f, al, top + 26f, fill);
        Solid(t, "Atrium Wall R", ar, f, ar + 12f, top + 26f, fill);
        Solid(t, "Atrium Slab 1", al, f + AtriumRise - SlabThick, ar, f + AtriumRise, fill);
        Solid(t, "Atrium Slab 2", al, top - SlabThick, ar, top, fill);
        Solid(t, "Atrium Ceiling", al - 12f, top + 16f, ar + 12f, top + 28f, fill);
    }

    /// <summary>实心地形：地形层上的盒子碰撞体，加一块纯色底（暗色的“实体”，上面再盖美术）。</summary>
    private static GameObject Solid(Transform parent, string name, float x0, float y0, float x1, float y1, Color color, bool visible = true)
    {
        var go = new GameObject(name) { layer = TerrainLayer };
        go.transform.SetParent(parent, false);
        go.transform.position = new Vector3((x0 + x1) / 2f, (y0 + y1) / 2f, 0.4f);
        BoxCollider2D col = go.AddComponent<BoxCollider2D>();
        col.size = new Vector2(x1 - x0, y1 - y0);
        if (visible)
        {
            var vis = new GameObject("Fill");
            vis.transform.SetParent(go.transform, false);
            vis.transform.localScale = new Vector3(x1 - x0, y1 - y0, 1f);
            Paint(vis, White, color);
        }
        return go;
    }

    // ───────────────────────────── 镜头 ─────────────────────────────

    private static void BuildCameraAreas()
    {
        float f = FloorY;
        HubArea = LockArea("Hub Camera", HubLeft, f - 6f, HubRight, f + 26f, HubLeft + 14.6f, HubRight - 14.6f, f + CamAbove, f + CamAbove);
        HallArea = LockArea("Hall Camera", HallLeft, f - 6f, HallRight, UpperY + 16f, HallLeft + 14.6f, HallRight - 14.6f, f + CamAbove, UpperY + CamAbove);
        AtriumArea = LockArea("Atrium Camera", AtriumLeft, f - 6f, AtriumRight, AtriumTopFloor + 16f, AtriumLeft + 14.6f, AtriumRight - 14.6f, f + CamAbove, AtriumTopFloor + CamAbove);
        Sanctum.ExtendCamera();
    }

    private static CameraLockArea LockArea(string name, float x0, float y0, float x1, float y1, float cxMin, float cxMax, float cyMin, float cyMax)
    {
        var go = new GameObject(name) { layer = lockLayer };
        go.SetActive(false);
        go.transform.SetParent(root!.transform, false);
        go.transform.position = new Vector3((x0 + x1) / 2f, (y0 + y1) / 2f, 0f);
        BoxCollider2D box = go.AddComponent<BoxCollider2D>();
        box.isTrigger = true;
        box.size = new Vector2(x1 - x0, y1 - y0);
        CameraLockArea la = go.AddComponent<CameraLockArea>();
        la.positionSpace = Space.World;
        la.cameraXMin = cxMin;
        la.cameraXMax = cxMax;
        la.cameraYMin = cyMin;
        la.cameraYMax = cyMax;
        la.lookYMin = cyMin;
        la.lookYMax = cyMax;
        go.SetActive(true);
        return la;
    }

    /// <summary>镜头边界放宽到长廊（游戏按地块图大小限制镜头：CameraController.GetTilemapInfo，Weave_07 只有 60×128）。</summary>
    internal static bool ExtendCamera()
    {
        GameCameras cams = GameCameras.SilentInstance;
        if (cams == null || cams.cameraController == null || !Prepared)
        {
            return false;
        }
        CameraController cc = cams.cameraController;
        bool changed = false;
        if (cc.xLimit < WantXLimit - 0.01f)
        {
            cc.xLimit = WantXLimit;
            changed = true;
        }
        if (cc.yLimit < WantYLimit - 0.01f)
        {
            cc.yLimit = WantYLimit;
            changed = true;
        }
        return changed;
    }

    // ───────────────────────────── 到场以后：搭画面 ─────────────────────────────

    /// <summary>美术要用的图集（文件名去掉 .spriteatlas.bundle）。</summary>
    internal static readonly string[] Atlases =
    {
        "weave", "core_glows", "weaver_rune_gate", "memory_weaver", "memory_first_weaver", "threads", "weave_gate",
        "bshrine_window", "f_weaver", "hud_final_halo", "cradle", "memory_cradle",
        // 雕像旁的拉杆：游戏里最常见的那种骨柄拉杆（ant_lever_animated，骸底东边、猎者小径的 Lever 都用它）。
        "ant",
        // 万神殿：束缚图标（物品栏的织针、面具、工具包、丝轴，工具栏上锁的丝）、门上的徽记（成就图标）、
        // 结算和门上的纹章（猎者纹章）、第五扇门的骨笼（骸底教堂的笼子）。
        "inventory", "achievement_icons_hornet", "crest", "cage",
        // 面具束缚的图标：物品栏里完整的面具（Inv_0030_inv_health_backboard_mask_full）只在 ui 图集里。
        "ui",
    };

    /// <summary>
    /// 到了大厅：借图集、搭画面和雕像，最后揭开遮罩（从雕像开的战，人已经由游戏从回程门放在雕像前了）。
    /// alive 返回 false（离开大厅、换了存档）就停。整个过程中遮罩挡着，出错也会揭开。
    /// </summary>
    internal static IEnumerator BuildRoutine(Func<bool> lobbyAlive)
    {
        // 同一时间只有最新的一个搭建任务算数；它只管自己开始时的那个大厅根节点（Codex 审查 v0.6.0 #3）。
        int token = ++buildToken;
        GameObject? owner = null;
        bool Current() => token == buildToken && lobbyAlive();
        bool Mine() => Current() && owner != null && root == owner;
        // 黑幕：这个任务还有效就一直挡着（看门狗不揭），超过上限才揭（Codex 审查 v0.6.0 #7）。
        LoomUI.Instance?.Cover(1f, () => Current() && (owner == null || root == owner));
        // 这个任务自己借的动作帧包：只还自己的，过期了也不碰别的任务借的（Codex 审查 v0.6.3 #1）。
        Tk2dArt.Lease? lease = null;
        try
        {
            if (!Prepared)
            {
                // 加载时没准备（比如插件晚加载）：只在神居存档、活动场景确实是大厅时补（Codex 审查 v0.6.0 #1）。
                Scene s = SceneManager.GetActiveScene();
                GameManager gm = GameManager.UnsafeInstance;
                if (gm == null || !GodhomeSave.IsGodhome(gm) || s.name != GodhomeConfig.LobbyScene.Value)
                {
                    GodhomePlugin.Log.LogWarning($"[Godhome] 圣所没准备好，也不能补 / sanctum not prepared and cannot be prepared here ({s.name})");
                    yield break;
                }
                GodhomePlugin.Log.LogInfo($"[Godhome] 圣所没有在加载时准备好，现在补 / sanctum not prepared at load time, preparing now ({s.name})");
                Prepare(s);
            }
            if (!Prepared)
            {
                yield break;
            }
            owner = root;
            if (!Built)
            {
                // 拉杆的原版受击音效（游戏的音效包，待在大厅期间一直借着）：和图集同时借，不挡黑幕（Codex 审查 v0.7.0 低 #3）。
                // 没借到只是没声音，不影响搭建；借到的引用一取到就登记，离开大厅统一还。
                ModeController? host = ModeController.Instance;
                if (host != null)
                {
                    host.StartCoroutine(SanctumAudio.Load(Mine));
                }
                yield return SanctumArt.Load(Atlases, Mine);
                if (!Mine())
                {
                    yield break;
                }
                // 雕像：把每尊（每个版本）的画雕成石像，黑幕下做完（第一次进大厅要几秒，之后有缓存）。
                // 同一个 Boss 的其他形态用游戏的 tk2d 动作帧（另一个姿势）：先借动作帧包，雕完马上还。
                HashSet<string> groups = Tk2dGroupsNeeded();
                lease = Tk2dArt.Begin();
                if (groups.Count > 0)
                {
                    yield return Tk2dArt.Load(lease, groups, Mine);
                }
                if (!Mine())
                {
                    yield break;
                }
                yield return CarveStatues(lease, Mine);
                Tk2dArt.Release(lease, "雕完石像 / statues carved");
                if (!Mine())
                {
                    yield break;
                }
                // 等图集期间别的任务可能已经搭好了：再看一次（只有当前任务能走到这里）。
                if (!Built)
                {
                    try
                    {
                        BuildArt();
                    }
                    catch (Exception e)
                    {
                        GodhomePlugin.Log.LogError($"[Godhome] 搭圣所画面失败 / failed to build the sanctum art: {e}");
                        Built = true;
                        BuildOk = false;
                    }
                    if (!BuildOk)
                    {
                        ModeController.Instance?.ShowBanner(L.Get("ERR_NEED_RELOAD"), 8f);
                    }
                }
            }
            if (!Mine())
            {
                yield break;
            }
            HideAgain();
            ReapplyAtmosphere();
        }
        finally
        {
            // 自己借的动作帧包一定还（重复调没关系）。
            Tk2dArt.Release(lease, token == buildToken ? "搭建结束 / build finished" : "搭建任务过期 / build superseded");
            // 只揭自己这一次的黑幕：过期的任务（新的搭建已经开始、或者已经换了大厅实例）不碰。
            if (token == buildToken && (owner == null || root == owner))
            {
                LoomUI.Instance?.FadeCover(0f, 0.8f);
            }
        }
    }

    /// <summary>
    /// 搭画面。每一部分单独兜错（Codex 审查 v0.6.0 #9）：装饰出错不影响雕像和传送点；
    /// 都搭好、没出错才算完整（BuildOk），否则记日志、提示重新读档。
    /// </summary>
    private static void BuildArt()
    {
        Statues.Clear();
        if (art != null)
        {
            Object.Destroy(art.gameObject);
        }
        Transform artRoot = new GameObject("Art").transform;
        artRoot.SetParent(root!.transform, false);
        art = artRoot;
        int errors = 0;
        void Section(string what, Action build)
        {
            try
            {
                build();
            }
            catch (Exception e)
            {
                errors++;
                GodhomePlugin.Log.LogError($"[Godhome] 搭圣所出错 / sanctum build error ({what}): {e}");
            }
        }
        Section("hub", () => SanctumLayout.BuildHub(artRoot, FloorY));
        Section("hall", () => SanctumLayout.BuildHall(artRoot, FloorY));

        // 雕像：一半在下层，一半在上层，按幕排列（位置和回程门一样按 StatueSpot 算）。
        int n = StatueCatalog.All.Count;
        int lower = (n + 1) / 2;
        for (int i = 0; i < n; i++)
        {
            (float x, float floor, bool up) = StatueSpot(i);
            StatueDef def = StatueCatalog.All[i];
            Section("statue " + def.Id, () => Statues.Add(MakeStatue(def, x, floor, up)));
        }

        Section("bench", RestyleBench);

        // 万神殿前厅和门。
        Section("atrium", () => AtriumLayout.BuildAtrium(artRoot, FloorY));
        Doors.Clear();
        for (int i = 0; i < PantheonCatalog.All.Count; i++)
        {
            PantheonDef pd = PantheonCatalog.All[i];
            int k = i;
            Section("door " + pd.Id, () => Doors.Add(MakeDoor(pd, k)));
        }

        // 传送：圣所 ⇄ 长廊入口（“进入”，照前作神居的门），长廊下层 ⇄ 上层；圣所 ⇄ 万神殿前厅，前厅三层之间。
        float f = FloorY;
        int gates = 0;
        void Gate(string name, Vector2 at, InteractableBase.PromptLabels label, Vector2 target, bool faceRight)
            => Section(name, () => { SilkGate.Make(artRoot, name, at, label, target, faceRight, interactLayer); gates++; });
        Gate("Gate Hub→Hall", new Vector2(HubGateX, f), InteractableBase.PromptLabels.Enter, new Vector2(HallLeft + HallGateOffset + 2.5f, f), true);
        Gate("Gate Hall→Hub", new Vector2(HallLeft + HallGateOffset, f), InteractableBase.PromptLabels.Enter, new Vector2(HubGateX - 3f, f), false);
        Gate("Lift Up", new Vector2(HallLeft + LiftOffset, f), InteractableBase.PromptLabels.Ascend, new Vector2(HallLeft + LiftOffset + LiftLanding, UpperY), true);
        Gate("Lift Down", new Vector2(HallLeft + LiftOffset, UpperY), InteractableBase.PromptLabels.Descend, new Vector2(HallLeft + LiftOffset + LiftLanding, f), true);
        float al = AtriumLeft, a1 = f + AtriumRise, a2 = AtriumTopFloor;
        Gate("Gate Hub→Atrium", new Vector2(HubAtriumGateX, f), InteractableBase.PromptLabels.Enter, new Vector2(al + AtriumGateOffset + 2.5f, f), true);
        Gate("Gate Atrium→Hub", new Vector2(al + AtriumGateOffset, f), InteractableBase.PromptLabels.Enter, new Vector2(HubAtriumGateX + 3f, f), true);
        Gate("Atrium Lift Up 1", new Vector2(al + AtriumLiftA, f), InteractableBase.PromptLabels.Ascend, new Vector2(al + AtriumLiftA + LiftLanding, a1), true);
        Gate("Atrium Lift Down 1", new Vector2(al + AtriumLiftA, a1), InteractableBase.PromptLabels.Descend, new Vector2(al + AtriumLiftA + LiftLanding, f), true);
        Gate("Atrium Lift Up 2", new Vector2(al + AtriumLiftB, a1), InteractableBase.PromptLabels.Ascend, new Vector2(al + AtriumLiftB - LiftLanding, a2), false);
        Gate("Atrium Lift Down 2", new Vector2(al + AtriumLiftB, a2), InteractableBase.PromptLabels.Descend, new Vector2(al + AtriumLiftB - LiftLanding, a1), false);

        Built = true;
        BuildOk = errors == 0 && gates == 10 && Statues.Count == n && Doors.Count == PantheonCatalog.All.Count;
        int sprites = Statues.Count(s => s.HasSprite);
        Note = $"圣所 / sanctum: 地面 / floor y={FloorY:0.00}，长廊 / hall x {HallLeft:0}..{HallRight:0.0}（两层 / floors {FloorY:0.0} & {UpperY:0.0}）；雕像 / statues {Statues.Count}（下 / lower {lower}，上 / upper {n - lower}），有立绘 / with art {sprites}；万神殿前厅 / atrium x {AtriumLeft:0.0}..{AtriumRight:0.0}，门 / doors {Doors.Count}（封着 / locked {Doors.Count(d => d.Locked)}）；传送点 / gates {gates}；美术 / art {SanctumLayout.Placed} 件，缺 / missing {SanctumArt.MissingSprites}；藏起原图 / hid {hidden.Count}"
            + (BuildOk ? "" : $"；不完整 / INCOMPLETE（出错 / errors {errors}）");
        if (BuildOk)
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] {Note}");
        }
        else
        {
            GodhomePlugin.Log.LogError($"[Godhome] {Note}");
        }
        foreach (Statue s in Statues.Where(s => !s.HasSprite))
        {
            GodhomePlugin.Log.LogWarning($"[Godhome]   雕像没有图鉴立绘 / no journal art for statue {s.Def.Id}");
        }
    }

    /// <summary>
    /// 当前大厅长椅（本体和子物体，包括没启用的 Lit）现在用的精灵，“物体=精灵”逗号隔开（巡视核对用）。
    /// 每次都从当前的长椅读，不留上一个大厅的结果（Codex 审查 v0.7.0 低 #5）。
    /// </summary>
    internal static string BenchSprite
    {
        get
        {
            if (bench == null)
            {
                return "";
            }
            return string.Join(", ", bench.GetComponentsInChildren<SpriteRenderer>(true)
                .Where(sr => sr != null && sr.sprite != null)
                .Select(sr => sr.name + "=" + sr.sprite.name));
        }
    }

    /// <summary>
    /// 长椅：原房间的织者长椅带青苔（weave/weaver_bench_moss，长椅本体和 Lit 子物体各一张）。
    /// 换成同一张画布（275×152、轴心居中）、没有青苔的织者长椅 weave/weaver_bench，长椅的碰撞、坐下的位置都不变。
    /// </summary>
    private static void RestyleBench()
    {
        if (bench == null)
        {
            return;
        }
        Sprite? clean = SanctumArt.Get("weave", "weaver_bench");
        if (clean == null)
        {
            return;
        }
        int n = 0;
        foreach (SpriteRenderer sr in bench.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (sr != null && sr.sprite != null && sr.sprite.name.IndexOf("weaver_bench_moss", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                sr.sprite = clean;
                n++;
            }
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 长椅 / bench: 换成 / now {BenchSprite}（换了 / replaced {n} 张 / sprite(s)）");
    }

    // ───────────────────────────── 画图的小工具 ─────────────────────────────

    internal static Sprite White
    {
        get
        {
            if (white == null)
            {
                var tex = new Texture2D(4, 4) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
                var px = new Color[16];
                for (int i = 0; i < px.Length; i++)
                {
                    px[i] = Color.white;
                }
                tex.SetPixels(px);
                tex.Apply();
                white = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
                white.hideFlags = HideFlags.HideAndDontSave;
            }
            return white;
        }
    }

    internal static Material? RefMaterial => reference != null ? reference.sharedMaterial : null;
    internal static int RefLayer => reference != null ? reference.sortingLayerID : 0;
    internal static int RefOrder => reference != null ? reference.sortingOrder : 0;

    internal static SpriteRenderer Paint(GameObject go, Sprite? sprite, Color color, int order = 0)
    {
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = color;
        if (reference != null)
        {
            sr.sharedMaterial = reference.sharedMaterial;
            sr.sortingLayerID = reference.sortingLayerID;
            sr.sortingOrder = reference.sortingOrder + order;
        }
        else
        {
            sr.sortingOrder = order;
        }
        return sr;
    }

    // ───────────────────────────── 万神殿的门 ─────────────────────────────

    private static PantheonDoor MakeDoor(PantheonDef def, int index)
    {
        (float x, int level) = AtriumLayout.Spot(index);
        float floor = FloorY + level * AtriumRise;
        var go = new GameObject("Door " + def.Id);
        go.transform.SetParent(art!, false);
        go.transform.position = new Vector3(x, floor, 0.9f);
        try
        {
            PantheonDoor d = go.AddComponent<PantheonDoor>();
            d.Def = def;
            d.Floor = floor;
            d.Level = level;
            d.Size = level >= 2 ? 1.2f : 1f;
            AtriumLayout.DressDoor(d, go.transform);
            // 交互（“挑战”），和雕像一样：加完组件再设提示文字。
            var trig = new GameObject("Challenge") { layer = interactLayer };
            trig.transform.SetParent(go.transform, false);
            trig.transform.position = new Vector3(x, floor + 2f, 0f);
            BoxCollider2D tc = trig.AddComponent<BoxCollider2D>();
            tc.isTrigger = true;
            tc.size = new Vector2(3.4f, 4f);
            var marker = new GameObject("Prompt Marker");
            marker.transform.SetParent(go.transform, false);
            marker.transform.position = new Vector3(x, floor + 7.6f * d.Size, 0f);
            PantheonDoorInteract di = trig.AddComponent<PantheonDoorInteract>();
            di.Door = d;
            di.PromptMarker = marker.transform;
            di.InteractLabel = InteractableBase.PromptLabels.Challenge;
            d.Interact = di;
            d.Refresh();
            return d;
        }
        catch
        {
            Object.Destroy(go);
            throw;
        }
    }

    internal static PantheonDoor? FindDoor(string id) => Doors.FirstOrDefault(d => d != null && d.Def.Id == id);

    // ───────────────────────────── 雕像 ─────────────────────────────

    private static Statue MakeStatue(StatueDef def, float x, float floor, bool upper)
    {
        var go = new GameObject("Statue " + def.Id);
        go.transform.SetParent(art!, false);
        go.transform.position = new Vector3(x, floor, 0.8f);
        try
        {
            return DressAndWire(go, def, x, floor, upper);
        }
        catch
        {
            // 搭到一半的雕像不留在场景里。
            Object.Destroy(go);
            throw;
        }
    }

    private static Statue DressAndWire(GameObject go, StatueDef def, float x, float floor, bool upper)
    {
        Statue s = go.AddComponent<Statue>();
        s.Def = def;
        s.Floor = floor;
        s.Upper = upper;
        SanctumLayout.DressStatue(s, go.transform, x, floor);

        // 交互（“挑战”）。InteractableBase.Awake 里的 OnValidate 会改提示文字，所以加完组件再设。
        var trig = new GameObject("Challenge") { layer = interactLayer };
        trig.transform.SetParent(go.transform, false);
        trig.transform.position = new Vector3(x, floor + 2f, 0f);
        BoxCollider2D tc = trig.AddComponent<BoxCollider2D>();
        tc.isTrigger = true;
        tc.size = new Vector2(3.2f, 4f);
        var marker = new GameObject("Prompt Marker");
        marker.transform.SetParent(go.transform, false);
        marker.transform.position = new Vector3(x, floor + 7.2f, 0f);
        StatueInteract si = trig.AddComponent<StatueInteract>();
        si.Statue = s;
        si.PromptMarker = marker.transform;
        si.InteractLabel = InteractableBase.PromptLabels.Challenge;
        s.Interact = si;

        // 拉杆（有几个版本才有）：织者遗迹里的拉杆，用织针敲它切换版本，像前作那样。
        // 受击框不看拉杆的图有没有借到（Codex 审查 v0.6.0 #9）。
        if (def.Variants.Count > 1)
        {
            var hitGo = new GameObject("Lever Hit") { layer = hitLayer };
            hitGo.transform.SetParent(go.transform, false);
            hitGo.transform.position = new Vector3(x + SanctumLayout.LeverOffset, floor + 1.4f, 0f);
            BoxCollider2D hc = hitGo.AddComponent<BoxCollider2D>();
            hc.isTrigger = true;
            hc.size = new Vector2(1.4f, 2.8f);
            hitGo.AddComponent<StatueLever>().Statue = s;
        }
        s.Init();
        return s;
    }

    // ── 石像用什么画 ──
    // 按优先级试，第一个雕得出来的算数：
    //   1. bosses.json 的 statueArt，按顺序：
    //      "tk2d:组/集合/帧"  游戏的 tk2d 动作帧（同一个 Boss 的另一个形态用另一个姿势），例如 tk2d:laceboss/Lace Cln/lace_engarde0000
    //      "pair:图鉴名"      这条图鉴立绘雕成一前一后两尊（成对出现的形态）
    //      "图鉴名"           这条图鉴的立绘
    //   2. journal 里的每一条图鉴；
    //   3. 前面都没有图鉴立绘时，按显示名找到的那条。
    // 某个候选没雕成，这次搭建就换下一个；下次搭建还会从头试（Codex 审查 v0.6.3 #3/#4）。

    private const string Tk2dPrefix = "tk2d:";
    private const string PairPrefix = "pair:";

    private static bool IsSpecial(string spec) =>
        spec.StartsWith(Tk2dPrefix, StringComparison.OrdinalIgnoreCase) || spec.StartsWith(PairPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>图鉴立绘的雕刻缓存键：名字加实例编号（不同的图同名也不会混，Codex 审查 v0.6.3 #6）。缓存回标题就清，编号在一局里不变。</summary>
    private static string SpriteKey(string kind, Sprite sp) => kind + sp.name + "#" + sp.GetInstanceID();

    /// <summary>一个版本的石像候选（按优先级，键不重复）：（雕刻缓存的键，怎么取来源）。tk2d 的来源要从这次搭建借到的包里取。</summary>
    private static IEnumerable<(string Key, Func<StatueCarver.Source?> Make)> ArtCandidates(BossDef b, Tk2dArt.Lease? lease)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool anyJournal = false;
        foreach (string spec in b.StatueArt)
        {
            if (spec.StartsWith(Tk2dPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string[] p = spec.Substring(Tk2dPrefix.Length).Split('/');
                if (p.Length == 3)
                {
                    string key = Tk2dPrefix + string.Join("/", p);
                    if (seen.Add(key))
                    {
                        yield return (key, () => Tk2dArt.Get(lease, p[0], p[1], p[2], key));
                    }
                }
            }
            else if (spec.StartsWith(PairPrefix, StringComparison.OrdinalIgnoreCase))
            {
                Sprite? sp = JournalRecordSprite(spec.Substring(PairPrefix.Length));
                if (sp != null)
                {
                    string key = SpriteKey(PairPrefix, sp);
                    if (seen.Add(key))
                    {
                        yield return (key, () =>
                        {
                            StatueCarver.Source src = StatueCarver.Source.FromSprite(key, sp);
                            src.Pair = true;
                            return src;
                        });
                    }
                }
            }
            else
            {
                Sprite? sp = JournalRecordSprite(spec);
                if (sp != null)
                {
                    anyJournal = true;
                    string key = SpriteKey("journal:", sp);
                    if (seen.Add(key))
                    {
                        yield return (key, () => StatueCarver.Source.FromSprite(key, sp));
                    }
                }
            }
        }
        foreach (string name in b.Journal)
        {
            Sprite? sp = JournalRecordSprite(name);
            if (sp != null)
            {
                anyJournal = true;
                string key = SpriteKey("journal:", sp);
                if (seen.Add(key))
                {
                    yield return (key, () => StatueCarver.Source.FromSprite(key, sp));
                }
            }
        }
        if (!anyJournal)
        {
            Sprite? sp = DisplayNameSprite(b);
            if (sp != null)
            {
                string key = SpriteKey("journal:", sp);
                if (seen.Add(key))
                {
                    yield return (key, () => StatueCarver.Source.FromSprite(key, sp));
                }
            }
        }
    }

    /// <summary>这个版本雕好的石像（按候选顺序第一个雕成的）；还没雕或都没雕成返回 null。</summary>
    internal static Sprite? CarvedStatue(BossDef b)
    {
        foreach ((string key, _) in ArtCandidates(b, null))
        {
            Sprite? s = StatueCarver.Get(key);
            if (s != null)
            {
                return s;
            }
        }
        return null;
    }

    /// <summary>
    /// 前作神居的手办屋（Hk1Hall）也用这些石像：借动作帧包、雕全部雕像（每个版本）、还包。
    /// 雕好的有缓存（回标题才清），已经都雕过就很快。
    /// </summary>
    internal static IEnumerator CarveAllRoutine(Func<bool> alive)
    {
        Tk2dArt.Lease? lease = Tk2dArt.Begin();
        try
        {
            HashSet<string> groups = Tk2dGroupsNeeded();
            if (groups.Count > 0)
            {
                yield return Tk2dArt.Load(lease, groups, alive);
            }
            if (!alive())
            {
                yield break;
            }
            yield return CarveStatues(lease, alive);
        }
        finally
        {
            Tk2dArt.Release(lease, "雕完石像（前作手办屋）/ statues carved (hk1 hall)");
        }
    }

    /// <summary>要借哪些组的动作帧包：每个版本排在“已经雕成的那个”前面的 tk2d 候选（优先的还没雕成就再试）。</summary>
    private static HashSet<string> Tk2dGroupsNeeded()
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (StatueDef d in StatueCatalog.All)
        {
            foreach (BossDef v in d.Variants)
            {
                foreach ((string key, _) in ArtCandidates(v, null))
                {
                    if (StatueCarver.Get(key) != null)
                    {
                        break;
                    }
                    if (key.StartsWith(Tk2dPrefix, StringComparison.Ordinal))
                    {
                        groups.Add(key.Substring(Tk2dPrefix.Length).Split('/')[0]);
                    }
                }
            }
        }
        return groups;
    }

    /// <summary>
    /// 雕全部雕像（每个版本）：一轮里每个版本交出它下一个没试过的候选，一起雕；
    /// 没雕成的下一轮换下一个候选，直到每个版本都雕成或候选用完（Codex 审查 v0.6.3 #4）。
    /// </summary>
    private static IEnumerator CarveStatues(Tk2dArt.Lease lease, Func<bool> alive)
    {
        StatueCarver.BeginSession();
        var tried = new HashSet<string>(StringComparer.Ordinal);
        for (int round = 0; round < 8 && alive(); round++)
        {
            var batch = new List<StatueCarver.Source>();
            foreach (StatueDef d in StatueCatalog.All)
            {
                foreach (BossDef v in d.Variants)
                {
                    StatueCarver.Source? src = NextSource(v, lease, tried);
                    if (src != null && !batch.Any(x => x.Key == src.Key))
                    {
                        batch.Add(src);
                    }
                }
            }
            if (batch.Count == 0)
            {
                break;
            }
            foreach (StatueCarver.Source s in batch)
            {
                tried.Add(s.Key);
            }
            yield return StatueCarver.CarveAll(batch, alive);
        }
        StatueCarver.EndSession();
    }

    /// <summary>这个版本下一个要雕的：已经有雕成的就没有；试过（雕过或取不到来源）的跳过。</summary>
    private static StatueCarver.Source? NextSource(BossDef v, Tk2dArt.Lease lease, HashSet<string> tried)
    {
        foreach ((string key, Func<StatueCarver.Source?> make) in ArtCandidates(v, lease))
        {
            if (StatueCarver.Get(key) != null)
            {
                return null;
            }
            if (tried.Contains(key))
            {
                continue;
            }
            StatueCarver.Source? src = null;
            try
            {
                src = make();
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 取石像的画出错 / statue art source failed {key}: {e.Message}");
            }
            if (src != null)
            {
                return src;
            }
            tried.Add(key);
        }
        return null;
    }

    /// <summary>按图鉴名取立绘。</summary>
    private static Sprite? JournalRecordSprite(string name)
    {
        try
        {
            EnemyJournalRecord rec = EnemyJournalManager.GetRecord(name);
            return rec != null && rec.EnemySprite != null ? rec.EnemySprite : null;
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 查图鉴立绘出错 / journal lookup failed for {name}: {e.Message}");
            return null;
        }
    }

    /// <summary>按显示名（中文或英文）找图鉴立绘。</summary>
    private static Sprite? DisplayNameSprite(BossDef b)
    {
        try
        {
            foreach (EnemyJournalRecord rec in EnemyJournalManager.GetAllEnemies())
            {
                if (rec == null || rec.EnemySprite == null)
                {
                    continue;
                }
                string shown = rec.DisplayName.ToString(false) ?? "";
                if (shown.Length > 0 && (shown == b.NameZh || shown == b.NameEn || shown.Replace(" ", "") == b.NameZh.Replace(" ", "")))
                {
                    return rec.EnemySprite;
                }
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 查图鉴立绘出错 / journal lookup failed for {b.Id}: {e.Message}");
        }
        return null;
    }

    /// <summary>图鉴立绘（没雕成石像时退回显示用）：表里写的图鉴名优先（statueArt 里的 tk2d:、pair: 不算），没写就按显示名找。</summary>
    internal static Sprite? JournalSprite(BossDef b)
    {
        foreach (string name in b.StatueArt.Where(a => !IsSpecial(a)).Concat(b.Journal))
        {
            Sprite? sp = JournalRecordSprite(name);
            if (sp != null)
            {
                return sp;
            }
        }
        return DisplayNameSprite(b);
    }

    internal static Statue? Find(string id) => Statues.FirstOrDefault(s => s != null && s.Def.Id == id);

    // ───────────────────────────── 站位、传送 ─────────────────────────────

    internal static void StandAt(HeroController hero, float x, float floor)
    {
        Collider2D? col = hero.GetComponent<Collider2D>();
        float feet = col != null ? hero.transform.position.y - col.bounds.min.y : 1f;
        Transform t = hero.transform;
        t.position = new Vector3(x, floor + feet + 0.05f, t.position.z);
        if (hero.Body != null)
        {
            hero.Body.linearVelocity = Vector2.zero;
        }
        Physics2D.SyncTransforms();
        GameCameras cams = GameCameras.SilentInstance;
        if (cams != null && cams.cameraController != null)
        {
            cams.cameraController.PositionToHero(false);
        }
    }

    /// <summary>
    /// 丝线传送：大黄蜂身上起一团丝光，画面渐暗，瞬移，画面渐亮。全程交互挡着（SilkGate.Interact 里 DisableInteraction）。
    /// </summary>
    internal static IEnumerator SilkTravel(SilkGate gate)
    {
        // 这一次传送持有：编号、大厅根节点、角色（Codex 审查 v0.6.0 #4）。死亡、离开大厅、模式重置、换了大厅实例都会让它作废：
        // 不再挪人；控制权由丝门按游戏的 ControlVersion 规则交还（期间别处收走过控制就不还）。
        int token = ++travelToken;
        Busy = true;
        GameObject? owner = root;
        HeroController hero = HeroController.instance;
        bool Valid() => token == travelToken && owner != null && root == owner && gate != null
            && ModeController.Instance != null && ModeController.Instance.State == LoomState.Lobby
            && hero != null && HeroController.SilentInstance == hero && !hero.cState.dead;
        bool faded = false;
        try
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 丝线传送 / silk travel: {gate.name} → ({gate.Target.x:0.0}, {gate.Target.y:0.0})");
            SanctumLayout.TravelBurst(hero.transform.position);
            LoomUI.Instance?.FadeCover(1f, 0.45f);
            yield return WaitUnpaused(0.5f, Valid);
            if (!Valid())
            {
                GodhomePlugin.Log.LogInfo("[Godhome] 丝线传送取消 / silk travel cancelled");
                yield break;
            }
            StandAt(hero, gate.Target.x, gate.Target.y);
            if (gate.FaceRight)
            {
                hero.FaceRight();
            }
            else
            {
                hero.FaceLeft();
            }
            yield return WaitUnpaused(0.15f, Valid);
            if (Valid())
            {
                SanctumLayout.TravelBurst(hero.transform.position);
            }
            LoomUI.Instance?.FadeCover(0f, 0.5f);
            faded = true;
            yield return WaitUnpaused(0.3f, Valid);
        }
        finally
        {
            // 黑幕是这次传送拉下的、大厅还是那个大厅，才揭（换了大厅实例的黑幕归新大厅的搭建任务管）。
            if (!faded && owner != null && root == owner)
            {
                LoomUI.Instance?.FadeCover(0f, 0.5f);
            }
            if (token == travelToken)
            {
                Busy = false;
            }
            if (gate != null)
            {
                gate.Release();
            }
        }
    }

    private static bool Paused()
    {
        GameManager gm = GameManager.UnsafeInstance;
        return gm != null && gm.IsGamePaused();
    }

    /// <summary>等 seconds 秒（只数没暂停的时间）；valid 返回 false 就提前结束。</summary>
    private static IEnumerator WaitUnpaused(float seconds, Func<bool> valid)
    {
        float t = 0f;
        while (t < seconds && valid())
        {
            if (!Paused())
            {
                t += Time.unscaledDeltaTime;
            }
            yield return null;
        }
    }

    /// <summary>大厅场景卸载、回标题时清掉静态引用（Codex 审查 v0.5.0 #13）。物体跟着场景销毁。</summary>
    internal static void Forget()
    {
        ResetState();
        LobbySceneRef = default;
    }

    /// <summary>
    /// 每秒再藏一次场景黑边（CustomSceneManager.DrawBlackBorders 摆在场景根上的黑条）。
    /// 准备时已经把 sceneBordersMask 清零，这里只兜准备晚了的情况；只看大厅场景的根物体（Codex 审查 v0.6.0 #17）。
    /// </summary>
    internal static void HideBorders()
    {
        if (!LobbySceneRef.IsValid() || !LobbySceneRef.isLoaded)
        {
            return;
        }
        foreach (GameObject g in LobbySceneRef.GetRootGameObjects())
        {
            if (g == null || !g.name.StartsWith("SceneBorder", StringComparison.Ordinal))
            {
                continue;
            }
            foreach (Renderer r in g.GetComponentsInChildren<Renderer>())
            {
                if (r != null && r.enabled)
                {
                    r.enabled = false;
                }
            }
        }
    }
}

/// <summary>
/// 圣所的镜头：边界保持放宽（游戏进场后会按地块图把 xLimit/yLimit 改回去），原房间的锁定区不再生效。
/// 边界改了而当前锁定区是我们的，就重新锁一次让锁定值按新边界算（CameraController.LockToArea 会把超出边界的值截掉）。
/// </summary>
internal sealed class SanctumCamera : MonoBehaviour
{
    private float nextBorderCheck;
    private bool logged;

    /// <summary>边界放宽过、还没按新边界重新锁（镜头冻结时先记着，解冻后补上，Codex 审查 v0.6.0 #13）。</summary>
    private bool relock;

    private void LateUpdate()
    {
        GameCameras cams = GameCameras.SilentInstance;
        // 只有当前大厅实例的这一个在干活（转场重叠时旧大厅的不动镜头）。
        if (cams == null || cams.cameraController == null || !Sanctum.Prepared || gameObject.scene != Sanctum.LobbySceneRef)
        {
            return;
        }
        CameraController cc = cams.cameraController;
        relock |= Sanctum.ExtendCamera();
        List<CameraLockArea>? zones = cc.lockZoneList;
        if (zones != null)
        {
            // 只解除原大厅的锁定区，别的场景的不碰；没有要解除的就不复制列表（Codex 审查 v0.6.0 #13、#17）。
            bool Foreign(CameraLockArea? z) => z == null || (z != Sanctum.HubArea && z != Sanctum.HallArea && z != Sanctum.AtriumArea && z.gameObject.scene == Sanctum.LobbySceneRef);
            bool any = false;
            for (int i = 0; i < zones.Count && !any; i++)
            {
                any = Foreign(zones[i]);
            }
            if (any)
            {
                foreach (CameraLockArea z in zones.ToArray())
                {
                    if (!Foreign(z))
                    {
                        continue;
                    }
                    if (!logged)
                    {
                        logged = true;
                        GodhomePlugin.Log.LogInfo($"[Godhome] 圣所镜头：解除原房间的锁定区 / sanctum camera: releasing old lock {(z != null ? z.name : "null")}");
                    }
                    if (z != null)
                    {
                        cc.ReleaseLock(z);
                    }
                    else
                    {
                        zones.Remove(z!);
                    }
                }
            }
            if (relock && cc.mode != CameraController.CameraMode.FROZEN)
            {
                relock = false;
                foreach (CameraLockArea? z in new[] { Sanctum.HubArea, Sanctum.HallArea, Sanctum.AtriumArea })
                {
                    if (z != null && zones.Contains(z))
                    {
                        cc.LockToArea(z);
                    }
                }
            }
        }
        if (Time.unscaledTime >= nextBorderCheck)
        {
            nextBorderCheck = Time.unscaledTime + 1f;
            Sanctum.HideBorders();
        }
    }
}

/// <summary>丝门 / 丝线升降：交互后丝线传送到目标位置。</summary>
internal sealed class SilkGate : InteractableBase
{
    internal Vector2 Target;
    internal bool FaceRight;

    internal static SilkGate Make(Transform parent, string name, Vector2 at, PromptLabels label, Vector2 target, bool faceRight, int layer)
    {
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(parent, false);
        go.transform.position = new Vector3(at.x, at.y + 1.6f, 0f);
        BoxCollider2D c = go.AddComponent<BoxCollider2D>();
        c.isTrigger = true;
        c.size = new Vector2(2.6f, 3.2f);
        var marker = new GameObject("Prompt Marker");
        marker.transform.SetParent(go.transform, false);
        marker.transform.position = new Vector3(at.x, at.y + 5.2f, 0f);
        SilkGate g = go.AddComponent<SilkGate>();
        g.Target = target;
        g.FaceRight = faceRight;
        g.PromptMarker = marker.transform;
        g.InteractLabel = label;
        SanctumLayout.DressGate(go.transform, at, label);
        return g;
    }

    public override void Interact()
    {
        ModeController? mc = ModeController.Instance;
        if (mc == null || mc.State != LoomState.Lobby || Sanctum.InteractionBlocked)
        {
            return;
        }
        DisableInteraction();
        // 记下收走控制后的 ControlVersion：途中别处（死亡、转场）再收走过控制，EnableInteraction 就不交还（游戏自己的规则）。
        RecordControlVersion();
        mc.StartCoroutine(Sanctum.SilkTravel(this));
    }

    internal void Release() => EnableInteraction();
}

/// <summary>一尊雕像：当前选中的版本、忆像、通关标记、拉杆。</summary>
internal sealed class Statue : MonoBehaviour
{
    internal StatueDef Def = null!;
    internal BossDef Current = null!;
    internal float Floor;
    internal bool Upper;
    internal SpriteRenderer? Figure;
    internal SpriteRenderer? Aura;
    internal SpriteRenderer? Halo;
    internal float FigureBase;
    internal readonly SpriteRenderer?[] Marks = new SpriteRenderer?[3];
    internal SpriteRenderer? Lever;

    /// <summary>正在播的拉杆动画（新的一段开始前停掉旧的）。</summary>
    internal Coroutine? LeverAnim;
    internal readonly List<SpriteRenderer> Pips = new();
    internal StatueInteract? Interact;
    internal bool HasSprite;

    /// <summary>现在显示的是雕好的石像（不是退回的图鉴立绘）。</summary>
    internal bool IsStone;

    internal float X => transform.position.x;

    internal bool CurrentIsBlack => Def.IsBlack(Current);

    internal void Init()
    {
        GodhomeSaveData rec = GodhomePlugin.Instance.Records;
        Current = rec.StatueVariant.TryGetValue(Def.Id, out string? id) && Def.Variants.FirstOrDefault(v => v.Id == id) is BossDef b
            ? b
            : Def.Variants[0];
        Refresh();
    }

    /// <summary>正在换像（拉杆后石像沉下去、新的升上来）。</summary>
    internal bool Swapping => swap != null;

    private Coroutine? swap;

    /// <summary>
    /// 拉杆：换到下一个版本。save = 记进模式存档（巡视预览时不记，Codex 审查 v0.5.0 #8）。
    /// 像前作的神居雕像那样换像：石像抖动、扬尘、沉进底座，换成另一尊再升上来（SanctumLayout.SwapStatue）。
    /// </summary>
    internal void Cycle(bool save = true, bool towardRight = true)
    {
        if (Def.Variants.Count < 2 || Swapping)
        {
            return;
        }
        int i = Def.Variants.IndexOf(Current);
        Current = Def.Variants[(i + 1) % Def.Variants.Count];
        if (save)
        {
            GodhomePlugin.Instance.Records.StatueVariant[Def.Id] = Current.Id;
        }
        SanctumLayout.PlayLever(this, towardRight);
        swap = StartCoroutine(SanctumLayout.SwapStatue(this));
        GodhomePlugin.Log.LogInfo($"[Godhome] 拉杆 / lever {Def.Id} → {Current.Id}{(CurrentIsBlack ? "（黑线 / black thread）" : "")}{(save ? "" : "（不记存档 / not saved）")}");
    }

    /// <summary>直接换成这个版本（不播换像动画；正在换像就停下，拉杆也立回去）。</summary>
    internal void SetCurrent(BossDef b, bool save)
    {
        if (swap != null)
        {
            StopCoroutine(swap);
            swap = null;
        }
        SanctumLayout.StopLever(this);
        Current = b;
        if (save)
        {
            GodhomePlugin.Instance.Records.StatueVariant[Def.Id] = b.Id;
        }
        Refresh();
    }

    /// <summary>换像动画结束（SanctumLayout.SwapStatue 最后调）。</summary>
    internal void EndSwap() => swap = null;

    internal void Refresh() => SanctumLayout.RefreshStatue(this);
}

/// <summary>雕像的交互：“挑战”提示，按下后打开挑战面板（选难度）。</summary>
internal sealed class StatueInteract : InteractableBase
{
    internal Statue Statue = null!;

    public override void Interact()
    {
        ModeController? mc = ModeController.Instance;
        if (mc == null || mc.State != LoomState.Lobby || LoomUI.Instance == null || Sanctum.InteractionBlocked)
        {
            return;
        }
        DisableInteraction();
        RecordControlVersion();
        LoomUI.Instance.OpenChallenge(Statue);
    }

    /// <summary>面板关掉（取消或开战）时交还控制。</summary>
    internal void Release() => EnableInteraction();
}

/// <summary>雕像旁的拉杆：被织针打中时切换版本（游戏的 Lever 也是这样接受织针的：IHitResponder）。</summary>
internal sealed class StatueLever : MonoBehaviour, IHitResponder
{
    internal Statue Statue = null!;
    private float lastHit = -10f;

    public IHitResponder.HitResponse Hit(HitInstance damageInstance)
    {
        if (!damageInstance.IsFirstHit || !damageInstance.IsNailDamage)
        {
            return IHitResponder.Response.None;
        }
        if (Time.unscaledTime - lastHit < 0.35f || ModeController.Instance?.State != LoomState.Lobby || Sanctum.InteractionBlocked || Statue.Swapping)
        {
            return IHitResponder.Response.None;
        }
        lastHit = Time.unscaledTime;
        // 和游戏的 Lever 一样按 GetHitDirection 定倒向：往右打（人在左边）往右倒；往上打往右倒、往下打往左倒。
        bool right = damageInstance.GetHitDirection(HitInstance.TargetType.Regular) switch
        {
            HitInstance.HitDirection.Left => false,
            HitInstance.HitDirection.Down => false,
            _ => true,
        };
        Statue.Cycle(towardRight: right);
        return IHitResponder.Response.GenericHit;
    }
}
