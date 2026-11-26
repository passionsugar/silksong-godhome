using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;

namespace Godhome;

/// <summary>
/// [Godhome] 调试探针（Debug.Enabled 打开、Debug.Probe 非空时才装上）。只记日志、写文件，不改游戏状态。
/// Debug.Probe 是逗号分隔的开关：
/// - hud：HUD 滑入滑出的 FSM（GameCameras.hudCanvasSlideOut）每换一次状态记一行，带触发的事件；
///   谁调了 GameCameras.HUDIn / HUDOut、谁广播了 HUD IN / HUD OUT（带调用栈）；第一次见到这个 FSM 时整个写到 BepInEx\Godhome\probe-hud-fsm.txt。
/// - audio：每秒记一次游戏最终输出的音量（AudioListener.GetOutputData 的均方根，dBFS）；
///   换场景后记一次场景管理器挂的音频快照（名字、所属混音器，和丝之歌自己的角色混音器是不是同一个）和 AudioManager 的角色快照等待状态。
/// - arena=场景名前缀：进到这个场景后 10 秒内每 0.1 秒记大黄蜂的位置、速度、血量；0、1、3、6 秒各列一次大黄蜂附近的地面碰撞体和伤害区；
///   这些场景里所有 FSM 的状态变化（带事件名）。
/// </summary>
internal sealed class Probe : MonoBehaviour
{
    private bool hud;
    private bool audioOn;
    private string arenaPrefix = "";

    private string lastHud = "";
    private bool hudDumped;
    private float nextAudio;
    private readonly float[] buf = new float[1024];
    private string lastScene = "";

    private static Harmony? harmony;

    internal static void Install(GameObject host)
    {
        string cfg = GodhomeConfig.DebugProbe.Value.Trim();
        if (!GodhomeConfig.DebugMode.Value || cfg.Length == 0)
        {
            return;
        }
        Probe p = host.AddComponent<Probe>();
        installed = cfg;
        foreach (string part in cfg.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            if (part == "hud")
            {
                p.hud = true;
            }
            else if (part == "audio")
            {
                p.audioOn = true;
            }
            else if (part.StartsWith("arena=", StringComparison.Ordinal))
            {
                p.arenaPrefix = part.Substring("arena=".Length);
            }
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 探针 / probe: hud={p.hud} audio={p.audioOn} arena={(p.arenaPrefix.Length > 0 ? p.arenaPrefix : "-")}");
        if (p.hud && harmony == null)
        {
            try
            {
                harmony = new Harmony("local.silksong.godhome.probe");
                harmony.Patch(AccessTools.Method(typeof(GameCameras), nameof(GameCameras.HUDIn)), prefix: new HarmonyMethod(typeof(Probe), nameof(HudInPrefix)));
                harmony.Patch(AccessTools.Method(typeof(GameCameras), nameof(GameCameras.HUDOut)), prefix: new HarmonyMethod(typeof(Probe), nameof(HudOutPrefix)));
                harmony.Patch(AccessTools.Method(typeof(PlayMakerFSM), nameof(PlayMakerFSM.BroadcastEvent), new[] { typeof(string) }), prefix: new HarmonyMethod(typeof(Probe), nameof(BroadcastPrefix)));
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 探针：HUD 的补丁没打上 / probe: hud patches failed: {e.Message}");
            }
        }
        SceneManager.sceneLoaded += p.OnSceneLoaded;
    }

    private void OnDestroy() => SceneManager.sceneLoaded -= OnSceneLoaded;

    private static string Caller(int skip) =>
        string.Join(" < ", new System.Diagnostics.StackTrace(skip, false).GetFrames()?.Take(5)
            .Select(f => f.GetMethod() is System.Reflection.MethodBase m ? $"{m.DeclaringType?.Name}.{m.Name}" : "?") ?? Array.Empty<string>());

    private static void HudInPrefix()
    {
        if (Allowed(GameManager.UnsafeInstance))
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 探针 HUD：HUDIn（{Scene()}）← {Caller(2)}");
        }
    }

    private static void HudOutPrefix()
    {
        if (Allowed(GameManager.UnsafeInstance))
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 探针 HUD：HUDOut（{Scene()}）← {Caller(2)}");
        }
    }

    private static void BroadcastPrefix(string fsmEventName)
    {
        if (fsmEventName != null && fsmEventName.IndexOf("HUD", StringComparison.OrdinalIgnoreCase) >= 0 && Allowed(GameManager.UnsafeInstance))
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 探针 HUD：广播 / broadcast {fsmEventName}（{Scene()}）← {Caller(2)}");
        }
    }

    private static string Scene()
    {
        GameManager gm = GameManager.UnsafeInstance;
        return gm != null ? gm.GetSceneNameString() : SceneManager.GetActiveScene().name;
    }

    /// <summary>
    /// 只在 Safety.EnabledSlots 里的神居存档、配置还开着时探（读了普通存档或别的槽、运行中清空了 Debug.Probe 都停，Codex 审查第七轮 #5、复查 #4 #5）。
    /// </summary>
    private static bool Allowed(GameManager? gm) =>
        gm != null && GodhomeSave.IsGodhome(gm) && GodhomeConfig.IsSlotEnabled(gm.profileID)
        && GodhomeConfig.DebugMode.Value && SameConfig();

    /// <summary>启动时按哪份配置装的探针（探什么只在启动时定）。</summary>
    private static string installed = "";
    private static bool warnedChanged;

    /// <summary>
    /// 运行中改了 Debug.Probe（Codex 审计 0.10.3 DBG03）：探针只在启动时按配置装，改成别的不会照新的探，清空也只是停。
    /// 和启动时不一样就停下、记一次“要重启”，不再按旧配置继续探。
    /// </summary>
    private static bool SameConfig()
    {
        string now = GodhomeConfig.DebugProbe.Value.Trim();
        if (now == installed)
        {
            return now.Length > 0;
        }
        if (!warnedChanged)
        {
            warnedChanged = true;
            GodhomePlugin.Log.LogWarning($"[Godhome] 探针配置改了（{installed} → {(now.Length > 0 ? now : "空 / empty")}），停下；要重启游戏才按新的来 / probe config changed; stopped, restart the game to apply it");
        }
        return false;
    }

    private void Update()
    {
        GameManager gm = GameManager.UnsafeInstance;
        if (gm == null || !Allowed(gm))
        {
            return;
        }
        string scene = gm.GetSceneNameString();
        bool sceneChanged = scene != lastScene;
        lastScene = scene;
        if (hud && gm.IsGameplayScene())
        {
            SampleHud(sceneChanged);
            if (sceneChanged)
            {
                StartCoroutine(HudTree(scene));
            }
        }
        if (audioOn)
        {
            if (sceneChanged && gm.IsGameplayScene())
            {
                StartCoroutine(AudioState(scene));
            }
            if (Time.unscaledTime >= nextAudio)
            {
                nextAudio = Time.unscaledTime + 1f;
                AudioListener.GetOutputData(buf, 0);
                double sum = 0;
                for (int i = 0; i < buf.Length; i++)
                {
                    sum += buf[i] * buf[i];
                }
                double rms = Math.Sqrt(sum / buf.Length);
                ModeController? mc = ModeController.Instance;
                GodhomePlugin.Log.LogInfo($"[Godhome] 探针 音量 / probe output {20 * Math.Log10(rms + 1e-9):0.0} dBFS（{scene}，{mc?.State}，暂停 / paused {gm.IsGamePaused()}）");
            }
        }
    }

    private void SampleHud(bool sceneChanged)
    {
        GameCameras gc = GameCameras.SilentInstance;
        PlayMakerFSM? f = gc != null ? gc.hudCanvasSlideOut : null;
        if (f == null)
        {
            return;
        }
        // 等它初始化了再写：读没初始化的 FSM 的动作会让 PlayMaker 现场加载，日志里多出几十条报错。
        if (!hudDumped && f.Fsm != null && f.Fsm.Initialized)
        {
            hudDumped = true;
            try
            {
                var sb = new StringBuilder();
                FsmDump.Write(f, sb);
                foreach (PlayMakerFSM other in f.GetComponents<PlayMakerFSM>().Where(x => x != f && x.Fsm != null && x.Fsm.Initialized))
                {
                    FsmDump.Write(other, sb);
                }
                string dir = Path.Combine(Paths.BepInExRootPath, "Godhome");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "probe-hud-fsm.txt"), sb.ToString(), new UTF8Encoding(false));
                GodhomePlugin.Log.LogInfo($"[Godhome] 探针 HUD：FSM 写到 / dumped to probe-hud-fsm.txt（{FsmDump.PathOf(f.transform)}|{f.FsmName}）");
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 探针 HUD：写 FSM 出错 / dump failed: {e.Message}");
            }
        }
        string now = $"{f.ActiveStateName} visible={gc!.IsHudVisible}";
        if (now != lastHud || sceneChanged)
        {
            string ev = f.Fsm?.LastTransition?.EventName ?? "-";
            GodhomePlugin.Log.LogInfo($"[Godhome] 探针 HUD：{(now != lastHud ? lastHud + " → " : "")}{now}（事件 / event {ev}，{Scene()}，{ModeController.Instance?.State}，{Time.unscaledTime:0.00}s）");
            lastHud = now;
        }
    }

    /// <summary>换场景 2 秒后把 HUD 那一支的物体树记一次：开关、位置、缩放、渲染器（画到屏幕哪里），全局隐藏开关，界面镜头画到多少。</summary>
    private IEnumerator HudTree(string scene)
    {
        yield return new WaitForSecondsRealtime(2f);
        GameManager gm = GameManager.UnsafeInstance;
        GameCameras gc = GameCameras.SilentInstance;
        if (gm == null || gc == null || gm.GetSceneNameString() != scene || !Allowed(gm))
        {
            yield break;
        }
        var sb = new StringBuilder($"[Godhome] 探针 HUD 树 / probe hud tree {scene}（{ModeController.Instance?.State}）：");
        try
        {
            Camera cam = gc.hudCamera;
            sb.Append($"全局隐藏 / HudGlobalHide hidden {HudGlobalHide.IsHidden} reduced {HudGlobalHide.IsReduced}；");
            sb.Append($"界面镜头 / hud camera enabled {cam.enabled} active {cam.gameObject.activeInHierarchy} mask 0x{cam.cullingMask:X} ortho {cam.orthographic} size {cam.orthographicSize:0.##} pos {cam.transform.position} rect {cam.rect}；");
            sb.Append($"画到 / drawn {ViewInfo.HudCover()}%；");
            HUDCamera? hc = cam.GetComponent<HUDCamera>();
            GameObject? play = hc != null ? hc.GameplayChild : null;
            sb.Append($"游戏界面 / gameplay child {(play != null ? $"{play.name} self {play.activeSelf} hier {play.activeInHierarchy}" : "-")}；");
            foreach (HudGlobalHide h in FindObjectsByType<HudGlobalHide>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                sb.Append($"\n  全局隐藏物体 / HudGlobalHide {FsmDump.PathOf(h.transform)} active {h.gameObject.activeInHierarchy} local {h.transform.localPosition} scale {h.transform.localScale}");
            }
            PlayMakerFSM? slide = gc.hudCanvasSlideOut;
            if (slide != null)
            {
                Transform canvas = slide.transform;
                sb.Append($"\n  上层 / parents:");
                for (Transform? t = canvas; t != null; t = t.parent)
                {
                    sb.Append($" [{t.name} self {t.gameObject.activeSelf} local {t.localPosition} scale {t.localScale}]");
                }
                foreach (Transform child in canvas)
                {
                    Renderer[] rs = child.GetComponentsInChildren<Renderer>(true);
                    Renderer[] on = rs.Where(r => r.enabled && r.gameObject.activeInHierarchy).ToArray();
                    Renderer? first = on.FirstOrDefault(r => (cam.cullingMask & (1 << r.gameObject.layer)) != 0) ?? on.FirstOrDefault();
                    string fr = "-";
                    if (first != null)
                    {
                        Vector3 vp = cam.WorldToViewportPoint(first.bounds.center);
                        Color col = first is SpriteRenderer s ? s.color : (first.sharedMaterial != null && first.sharedMaterial.HasProperty("_Color") ? first.sharedMaterial.color : Color.white);
                        fr = $"{FsmDump.PathOf(first.transform)} layer {first.gameObject.layer} {first.sortingLayerName}/{first.sortingOrder} a {col.a:0.##} visible {first.isVisible} viewport ({vp.x:0.##},{vp.y:0.##},{vp.z:0.#}) size {first.bounds.size.x:0.##}x{first.bounds.size.y:0.##}";
                    }
                    sb.Append($"\n  {child.name} self {child.gameObject.activeSelf} hier {child.gameObject.activeInHierarchy} local {child.localPosition} scale {child.localScale} 渲染器 / renderers {on.Length}/{rs.Length} 第一个 / first {fr}");
                }
                // 血（面具）、丝轴画没画：子物体开没开、开着的渲染器多少；HUD 上每个 FSM 停在哪个状态。
                Transform? health = canvas.Find("Health");
                if (health != null)
                {
                    sb.Append("\n  血的子物体 / health children:");
                    foreach (Transform c in health)
                    {
                        Renderer[] rs = c.GetComponentsInChildren<Renderer>(true);
                        sb.Append($" [{c.name} {(c.gameObject.activeSelf ? "on" : "off")} {rs.Count(r => r.enabled && r.gameObject.activeInHierarchy)}/{rs.Length}]");
                    }
                }
                sb.Append("\n  HUD 的 FSM / hud fsms:");
                foreach (PlayMakerFSM pm in canvas.GetComponentsInChildren<PlayMakerFSM>(true))
                {
                    string rel = pm.transform == canvas ? "." : FsmDump.PathOf(pm.transform).Substring(FsmDump.PathOf(canvas).Length + 1);
                    sb.Append($" [{rel}|{pm.FsmName}={(pm.enabled && pm.gameObject.activeInHierarchy ? pm.ActiveStateName : "(off)")}]");
                }
            }
            SilkSpool? spool = SilkSpool.Instance;
            if (spool != null)
            {
                GameObject? sp = Traverse.Create(spool).Field("spoolParent").GetValue<GameObject>();
                sb.Append($"\n  丝轴 / spool {FsmDump.PathOf(spool.transform)} spoolParent {(sp != null ? $"self {sp.activeSelf} hier {sp.activeInHierarchy}" : "-")} hasDrawnSpool {Traverse.Create(spool).Field("hasDrawnSpool").GetValue<bool>()}");
            }
            else
            {
                sb.Append("\n  丝轴 / spool: 没有 / no SilkSpool.Instance");
            }
            HeroController hero = HeroController.SilentInstance;
            if (hero != null)
            {
                sb.Append($" hasSilkSpoolAppeared {Traverse.Create(hero).Field("hasSilkSpoolAppeared").GetValue<bool>()}");
            }
            PlayerData pd = PlayerData.instance;
            if (pd != null)
            {
                sb.Append($" 血 / health {pd.health}/{pd.maxHealth} 丝 / silk {pd.silk}/{pd.CurrentSilkMaxBasic}");
            }
            // 界面镜头单独画一张（底色洋红，没画到的地方就是洋红）。第一次再把 HUD 下所有 FSM 整个写到文件。
            string dir = Path.Combine(Paths.BepInExRootPath, "Godhome", "probe-hud");
            Directory.CreateDirectory(dir);
            sb.Append($"\n  界面镜头单独画 / hud camera alone: {SaveHudCamera(cam, Path.Combine(dir, $"{++hudShot:00}-{scene}.jpg"))}");
            if (!hudAllDumped && play != null)
            {
                hudAllDumped = true;
                var all = new StringBuilder();
                int n = 0;
                // 没初始化过的（关着的模板之类）不写：读它们的动作会让 PlayMaker 现场加载，日志里多出几百条
                // “Fsm not initialized / NullReferenceException”（2026-11-26 实测 552 条，全是这一步打出来的）。
                foreach (PlayMakerFSM pm in play.GetComponentsInChildren<PlayMakerFSM>(true).Where(p => p.Fsm != null && p.Fsm.Initialized))
                {
                    FsmDump.Write(pm, all);
                    n++;
                }
                File.WriteAllText(Path.Combine(dir, "hud-all-fsm.txt"), all.ToString(), new UTF8Encoding(false));
                sb.Append($"\n  HUD 下 {n} 个 FSM 写到 / {n} hud fsms dumped to probe-hud\\hud-all-fsm.txt");
            }
        }
        catch (Exception e)
        {
            sb.Append(" 出错 / error: " + e.Message);
        }
        GodhomePlugin.Log.LogInfo(sb.ToString());
    }

    private bool hudAllDumped;
    private int hudShot;

    /// <summary>界面镜头单独画进一张 w×h 的图（先清成洋红）存成 JPG；返回画到的比例或出错原因。只读，画完把镜头还原。</summary>
    private static string SaveHudCamera(Camera c, string path, int w = 960, int h = 540)
    {
        if (c == null || !c.enabled || c.targetTexture != null)
        {
            return "量不了 / n/a";
        }
        RenderTexture rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
        RenderTexture? prevActive = RenderTexture.active;
        Rect prevRect = c.rect;
        float prevAspect = c.aspect;
        bool auto = c.pixelHeight > 0 && Mathf.Abs(c.aspect - c.pixelWidth / (float)c.pixelHeight) < 0.002f;
        Texture2D? tex = null;
        try
        {
            RenderTexture.active = rt;
            GL.Clear(true, true, new Color(1f, 0f, 1f, 1f));
            c.targetTexture = rt;
            c.rect = new Rect(0f, 0f, 1f, 1f);
            c.aspect = w / (float)h;
            c.Render();
            RenderTexture.active = rt;
            tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            Color32[] px = tex.GetPixels32();
            int drawn = px.Count(q => !(q.r > 240 && q.g < 16 && q.b > 240));
            File.WriteAllBytes(path, tex.EncodeToJPG(85));
            return $"{drawn * 1000 / Mathf.Max(1, px.Length) / 10f:0.0}% → {Path.GetFileName(path)}";
        }
        catch (Exception e)
        {
            return "出错 / error: " + e.Message;
        }
        finally
        {
            c.targetTexture = null;
            c.rect = prevRect;
            if (auto)
            {
                c.ResetAspect();
            }
            else
            {
                c.aspect = prevAspect;
            }
            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(rt);
            if (tex != null)
            {
                Destroy(tex);
            }
        }
    }

    private static bool AudioStatic(string field) => Traverse.Create(typeof(AudioManager)).Field(field).GetValue<bool>();

    private IEnumerator AudioState(string scene)
    {
        for (int i = 0; i < 2; i++)
        {
            yield return new WaitForSecondsRealtime(i == 0 ? 0.5f : 3f);
            GameManager gm = GameManager.UnsafeInstance;
            if (gm == null || gm.GetSceneNameString() != scene || !Allowed(gm))
            {
                yield break;
            }
            CustomSceneManager? sm = gm.sm;
            AudioMixer? ours = gm.actorSnapshotUnpaused != null ? gm.actorSnapshotUnpaused.audioMixer : null;
            string Snap(AudioMixerSnapshot? s) => s == null ? "-" : $"{s.name}@{(s.audioMixer != null ? s.audioMixer.name + "#" + s.audioMixer.GetInstanceID() : "?")}{(ours != null && s.audioMixer == ours ? "(丝之歌角色混音器 / ours)" : "")}";
            string fields = sm == null ? "没有场景管理器 / no sm"
                : $"music {Snap(Traverse.Create(sm).Field("musicSnapshot").GetValue<AudioMixerSnapshot>())}，atmos {Snap(sm.atmosSnapshot)}，enviro {Snap(sm.enviroSnapshot)}，actor {Snap(sm.actorSnapshot)}，shade {Snap(sm.shadeSnapshot)}";
            AudioManager am = gm.AudioManager;
            string musicNow = "-";
            string playing = "-";
            try
            {
                musicNow = $"{(am.CurrentMusicCue != null ? am.CurrentMusicCue.name : "-")}："
                    + string.Join(", ", am.MusicSources.Select(a => $"{(a.clip != null ? a.clip.name : "-")} {(a.isPlaying ? "放 / on" : "停 / off")} v{a.volume:0.##}"));
                playing = string.Join(", ", FindObjectsByType<AudioSource>(FindObjectsSortMode.None)
                    .Where(a => a != null && a.isPlaying)
                    .GroupBy(a => a.outputAudioMixerGroup != null ? $"{a.outputAudioMixerGroup.audioMixer.name}#{a.outputAudioMixerGroup.audioMixer.GetInstanceID()}/{a.outputAudioMixerGroup.name}" : "-")
                    .Select(g => $"{g.Key} ×{g.Count()}"));
            }
            catch (Exception e)
            {
                musicNow = "出错 / error: " + e.Message;
            }
            GodhomePlugin.Log.LogInfo($"[Godhome] 探针 音频 / probe audio {scene} +{(i == 0 ? 0.5f : 3.5f):0.0}s：{fields}；"
                + $"丝之歌角色混音器 / our actor mixer {(ours != null ? ours.name + "#" + ours.GetInstanceID() : "-")}；"
                + $"等待恢复 / waitingToUnpause {AudioStatic("waitingToUnpause")} levelIsReady {AudioStatic("levelIsReady")} csmReady {AudioStatic("customSceneManagerIsReady")}；"
                + $"当前音乐 / music now {musicNow}；在放的声源 / playing sources {playing}");
        }
    }

    private void OnSceneLoaded(Scene s, LoadSceneMode mode)
    {
        GameManager gm = GameManager.UnsafeInstance;
        if (arenaPrefix.Length > 0 && s.name.StartsWith(arenaPrefix, StringComparison.Ordinal) && !arenaRunning && gm != null && Allowed(gm))
        {
            StartCoroutine(Arena(s.name));
        }
    }

    private bool arenaRunning;

    private IEnumerator Arena(string first)
    {
        arenaRunning = true;
        GodhomePlugin.Log.LogInfo($"[Godhome] 探针 场地 / probe arena: {first} 加载了 / loaded");
        float t0 = Time.unscaledTime;
        var states = new Dictionary<PlayMakerFSM, string>();
        float nextList = 0f;
        float nextPos = 0f;
        int listed = 0;
        float[] listAt = { 0f, 1f, 3f, 6f };
        int hpWas = -1;
        bool heroIn = false;
        float tHero = 0f;
        while (Time.unscaledTime - t0 < 25f)
        {
            HeroController hc = HeroController.SilentInstance;
            GameManager gm = GameManager.UnsafeInstance;
            if (!Allowed(gm))
            {
                break;
            }
            bool inScene = hc != null && gm != null && gm.GetSceneNameString().StartsWith(arenaPrefix, StringComparison.Ordinal) && !gm.IsInSceneTransition;
            if (inScene && !heroIn)
            {
                heroIn = true;
                tHero = Time.unscaledTime;
                GodhomePlugin.Log.LogInfo($"[Godhome] 探针 场地：大黄蜂进来了 / hero in at {hc!.transform.position}（加载后 / after load {tHero - t0:0.00}s）");
            }
            float th = heroIn ? Time.unscaledTime - tHero : -1f;
            if (th > 10f)
            {
                break;
            }
            // FSM 状态变化。
            if (Time.unscaledTime >= nextList)
            {
                nextList = Time.unscaledTime + 1f;
                foreach (PlayMakerFSM pm in FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (pm != null && pm.gameObject.scene.name.StartsWith(arenaPrefix, StringComparison.Ordinal) && !states.ContainsKey(pm))
                    {
                        states[pm] = "";
                    }
                }
            }
            foreach (PlayMakerFSM pm in states.Keys.ToList())
            {
                if (pm == null)
                {
                    states.Remove(pm!);
                    continue;
                }
                string st = pm.isActiveAndEnabled ? pm.ActiveStateName ?? "-" : "(off)";
                if (st != states[pm])
                {
                    if (states[pm].Length > 0 || heroIn)
                    {
                        GodhomePlugin.Log.LogInfo($"[Godhome] 探针 场地 FSM {th:0.00}s：{FsmDump.PathOf(pm.transform)}|{pm.FsmName} {states[pm]} → {st}（事件 / event {pm.Fsm?.LastTransition?.EventName ?? "-"}）");
                    }
                    states[pm] = st;
                }
            }
            if (heroIn && hc != null)
            {
                if (Time.unscaledTime >= nextPos)
                {
                    nextPos = Time.unscaledTime + 0.1f;
                    Rigidbody2D rb = hc.GetComponent<Rigidbody2D>();
                    int hp = PlayerData.instance != null ? PlayerData.instance.health : -1;
                    GodhomePlugin.Log.LogInfo($"[Godhome] 探针 场地 {th:0.00}s：大黄蜂 / hero {hc.transform.position.x:0.00},{hc.transform.position.y:0.00} 速度 / v {(rb != null ? rb.linearVelocity.ToString() : "-")} 血 / hp {hp}{(hpWas >= 0 && hp != hpWas ? $"（{hpWas}→{hp}）" : "")} 着地 / grounded {hc.cState.onGround} 操作 / control {!hc.controlReqlinquished}");
                    hpWas = hp;
                }
                if (listed < listAt.Length && th >= listAt[listed])
                {
                    ListGround(hc.transform.position, th);
                    listed++;
                }
            }
            yield return null;
        }
        GodhomePlugin.Log.LogInfo("[Godhome] 探针 场地：结束 / done");
        arenaRunning = false;
    }

    private void ListGround(Vector3 hero, float th)
    {
        var sb = new StringBuilder();
        int n = 0;
        foreach (Collider2D c in FindObjectsByType<Collider2D>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (c == null || !c.gameObject.scene.name.StartsWith(arenaPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            Bounds b = c.bounds;
            bool near = b.max.x > hero.x - 20f && b.min.x < hero.x + 20f && b.max.y > hero.y - 12f && b.min.y < hero.y + 12f;
            if (!near)
            {
                continue;
            }
            Component? dmg = c.GetComponent("DamageHero");
            bool terrain = c.gameObject.layer == 8;
            if (!terrain && dmg == null)
            {
                continue;
            }
            if (n++ < 60)
            {
                string hz = dmg != null ? $" 伤害 / damage {Traverse.Create(dmg).Field("hazardType").GetValue()}" : "";
                sb.Append($"\n    {FsmDump.PathOf(c.transform)} [{c.GetType().Name}{(c.isTrigger ? " trigger" : "")}{(terrain ? " terrain" : "")}{hz}] 开 / on {c.enabled && c.gameObject.activeInHierarchy} "
                    + $"x {b.min.x:0.0}..{b.max.x:0.0} y {b.min.y:0.0}..{b.max.y:0.0}");
            }
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 探针 场地 地面 / ground {th:0.0}s（大黄蜂 / hero {hero.x:0.0},{hero.y:0.0}，{n} 个 / colliders）：{sb}");
    }
}
