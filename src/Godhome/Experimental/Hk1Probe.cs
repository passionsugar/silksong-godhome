using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using UnitySceneRepacker;
using Object = UnityEngine.Object;

namespace Godhome;

/// <summary>
/// [Godhome] 试验（配置 Debug.Hk1Probe）：前作（空洞骑士，装在本机）的神居场景能不能在丝之歌里直接加载。
/// 用 UnitySceneRepacker（jakobhellermann）把前作的场景文件重新打包成丝之歌能读的包（第一次要打包，结果缓存在
/// BepInEx\cache\unity-scene-repacker），叠加加载进来，然后：
/// 1. 记下场景里有什么：物体、组件（哪些脚本绑到了丝之歌的同名类、哪些丢了）、着色器、FSM、加载时的报错；
/// 2. 关掉前作的门（碰到会让游戏去读前作的场景名），把别的场景先藏起来，把大黄蜂放到前作场景里走几个点截图；
/// 3. 卸掉，再试下一个。
/// 只读前作的游戏文件，不改。结果写进 BepInEx\Godhome\hk1probe-*.md，截图在 shots\hk1-*。
/// </summary>
internal static class Hk1Probe
{
    internal const string RepackerId = "io.github.jakobhellermann.unityscenerepacker";

    internal static bool Running { get; private set; }

    /// <summary>UnitySceneRepacker 插件装了没有（没装就不碰它的类型）。</summary>
    internal static bool Available => Chainloader.PluginInfos.ContainsKey(RepackerId);

    /// <summary>找前作的安装目录：配置 → 和丝之歌同一个 steamapps\common → 各个盘的 Steam 库。</summary>
    internal static string? FindGame()
    {
        var cands = new List<string>();
        string cfg = GodhomeConfig.Hk1GameDir.Value?.Trim() ?? "";
        if (cfg.Length > 0)
        {
            cands.Add(cfg);
        }
        try
        {
            string? gameDir = Path.GetDirectoryName(Application.dataPath);
            string? common = gameDir != null ? Path.GetDirectoryName(gameDir) : null;
            if (common != null)
            {
                cands.Add(Path.Combine(common, "Hollow Knight"));
            }
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady)
                {
                    continue;
                }
                string root = d.RootDirectory.FullName;
                foreach (string lib in new[] { "SteamLibrary", "Steam", Path.Combine("Program Files (x86)", "Steam"), Path.Combine("Program Files", "Steam") })
                {
                    string apps = Path.Combine(root, lib, "steamapps");
                    cands.Add(Path.Combine(apps, "common", "Hollow Knight"));
                    string vdf = Path.Combine(apps, "libraryfolders.vdf");
                    if (File.Exists(vdf))
                    {
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        {
                            cands.Add(Path.Combine(m.Groups[1].Value.Replace("\\\\", "\\"), "steamapps", "common", "Hollow Knight"));
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 找前作目录出错 / looking for Hollow Knight failed: {e.Message}");
        }
        return cands.FirstOrDefault(c => File.Exists(Path.Combine(c, "hollow_knight_Data", "globalgamemanagers")));
    }

    internal static IEnumerator Run(DebugTools host)
    {
        Running = true;
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string dir = Path.Combine(Paths.BepInExRootPath, "Godhome", "shots", "hk1-" + stamp);
        Directory.CreateDirectory(dir);
        var lines = new List<string> { $"# 前作场景试验 / HK1 scene probe {stamp}", "", $"- 版本 / version: {GodhomePlugin.Version}" };
        void Log(string s)
        {
            lines.Add("- " + s);
            GodhomePlugin.Log.LogInfo("[Godhome] 前作试验 / hk1 probe: " + s);
        }
        var logs = new List<(LogType Type, string Msg, string Stack)>();
        void OnLog(string msg, string st, LogType type)
        {
            if (type != LogType.Log)
            {
                lock (logs)
                {
                    logs.Add((type, msg, st));
                }
            }
        }
        Application.logMessageReceivedThreaded += OnLog;
        var hidden = new List<Component>();
        RepackedAssets? assets = null;
        int k = 0;
        try
        {
            HeroController hero = HeroController.instance;
            // 坐在长椅上先起身。
            float until = Time.unscaledTime + 15f;
            while (Time.unscaledTime < until && (PlayerData.instance.atBench || !hero.acceptingInput))
            {
                if (PlayerData.instance.atBench)
                {
                    PlayMakerFSM? bench = Object.FindObjectsByType<PlayMakerFSM>(FindObjectsSortMode.None).FirstOrDefault(f => f != null && f.FsmName == "Bench Control");
                    bench?.SendEvent("GET UP");
                }
                yield return new WaitForSecondsRealtime(0.5f);
            }
            Log($"起身 / standing: atBench={PlayerData.instance.atBench} acceptingInput={hero.acceptingInput}");

            string? game = FindGame();
            Log($"前作目录 / HK1 dir: {game ?? "没找到 / not found"}");
            if (game == null)
            {
                yield break;
            }
            // 场景名后可以写 @入口门（原生转场用），如 GG_Workshop@left1。
            var entries = GodhomeConfig.DebugHk1Probe.Value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Split('@')).Select(p => (Name: p[0], Gate: p.Length > 1 ? p[1] : "left1")).ToList();
            string mode = GodhomeConfig.DebugHk1ProbeMode.Value?.Trim().ToLowerInvariant() ?? "additive";
            bool additive = mode is "additive" or "both" or "";
            bool native = mode is "native" or "both";
            Log($"方式 / mode: {mode}（叠加 / additive={additive}，原生转场 / native={native}）");
            // 用自己的缓存名，不和正式导入（Hk1Assets.PackName）共用（Codex 审计 0.10.3 D05）：UnitySceneRepacker 按缓存目录 + 名字
            // 决定请求表、包和目录文件，两边请求的场景不同，共用一个名字会互相重写对方的缓存。
            assets = UnitySceneRepackerPlugin.RepackedAssets(game, Hk1Assets.PackName + "-probe");
            var reqs = entries.Select(e => (e.Name, e.Gate, Scene: assets.RequestScene(e.Name))).ToList();
            var sw = Stopwatch.StartNew();
            assets.Prepare();
            foreach ((string name, string _, RepackedScene rs) in reqs)
            {
                if (!additive)
                {
                    break;
                }
                int logStart;
                lock (logs)
                {
                    logStart = logs.Count;
                }
                double t0 = sw.Elapsed.TotalSeconds;
                AsyncOperationHandle<SceneInstance> h;
                try
                {
                    h = rs.Load(LoadSceneMode.Additive);
                }
                catch (Exception e)
                {
                    Log($"{name}: 开始加载就出错 / load threw: {e}");
                    continue;
                }
                until = Time.unscaledTime + 600f;
                while (!h.IsDone && Time.unscaledTime < until)
                {
                    yield return null;
                }
                if (!h.IsDone || h.Status != AsyncOperationStatus.Succeeded)
                {
                    Log($"{name}: 加载失败 / load failed: done={h.IsDone} status={h.Status} {h.OperationException}");
                    continue;
                }
                Scene sc = h.Result.Scene;
                Log($"{name}: 加载好 / loaded in {sw.Elapsed.TotalSeconds - t0:0.0}s（从开始算 / since start {sw.Elapsed.TotalSeconds:0.0}s）scene={sc.name} valid={sc.IsValid()} roots={sc.rootCount}");
                // 先关掉前作的门：碰到会让游戏去读前作的场景名。
                int tps = 0;
                foreach (TransitionPoint tp in Roots(sc).SelectMany(r => r.GetComponentsInChildren<TransitionPoint>(true)))
                {
                    tp.enabled = false;
                    foreach (Collider2D c in tp.GetComponents<Collider2D>())
                    {
                        c.enabled = false;
                    }
                    tps++;
                }
                Log($"{name}: 关掉的门 / doors disabled: {tps}");
                yield return null;
                yield return null;
                Inventory(sc, name, dir, Log);
                yield return new WaitForSecondsRealtime(1f);
                List<(LogType Type, string Msg, string Stack)> mine;
                lock (logs)
                {
                    mine = logs.Skip(logStart).ToList();
                }
                Log($"{name}: 加载后的日志 / log after load: " + string.Join(", ", mine.GroupBy(l => l.Type).Select(g => $"{g.Key} {g.Count()}")));
                foreach (var g in mine.GroupBy(l => Shorten(l.Msg)).OrderByDescending(g => g.Count()).Take(30))
                {
                    lines.Add($"  - ×{g.Count()} [{g.First().Type}] {g.Key}");
                }
                // 异常的调用栈（每种前 6 行，最多 8 种）。
                foreach (var g in mine.Where(l => l.Type == LogType.Exception).GroupBy(l => Shorten(l.Msg) + " @ " + FirstLines(l.Stack, 6)).Take(8))
                {
                    lines.Add($"  - 异常 / exception ×{g.Count()}: {g.Key}");
                }

                // 只看这个场景：别的场景的渲染器、碰撞体先关掉（物体本身不关，管理器照常跑），大黄蜂放进来走几个点截图。
                hidden = HideOthers(sc);
                Log($"{name}: 关掉别的场景的渲染器和碰撞体 / disabled other scenes' renderers and colliders: {hidden.Count}");
                yield return Tour(sc, name, dir, hero, Log, () => ++k);
                Unhide(hidden);
                AsyncOperationHandle<SceneInstance> u = Addressables.UnloadSceneAsync(h);
                until = Time.unscaledTime + 60f;
                // 卸完以后这个句柄会被自动释放，不能再读它的状态。
                while (u.IsValid() && !u.IsDone && Time.unscaledTime < until)
                {
                    yield return null;
                }
                Log($"{name}: 卸掉 / unloaded: loaded={sc.isLoaded}");
            }
            foreach ((string name, string gate, RepackedScene rs) in reqs)
            {
                if (!native)
                {
                    break;
                }
                // 入口写成 @- 的只叠加看，不原生进（不知道入口门叫什么）。
                if (gate == "-")
                {
                    continue;
                }
                yield return Native(name, gate, rs, dir, hero, Log, () => ++k);
                hero = HeroController.instance;
            }
        }
        finally
        {
            Hk1Gate.NativeScene = null;
            Application.logMessageReceivedThreaded -= OnLog;
            Unhide(hidden);
            assets?.Dispose();
            Running = false;
            string report = Path.Combine(Paths.BepInExRootPath, "Godhome", $"hk1probe-{stamp}.md");
            File.WriteAllText(report, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
            GodhomePlugin.Log.LogInfo($"[Godhome] 前作试验结束 / hk1 probe finished: {report}");
        }
        if (GodhomeConfig.DebugQuitAfterSelfTest.Value)
        {
            yield return new WaitForSecondsRealtime(5f);
            GodhomePlugin.Log.LogInfo("[Godhome] 前作试验后退出游戏 / quitting after the hk1 probe");
            Application.Quit();
        }
    }

    /// <summary>
    /// 原生转场：把重新打包的场景位置（IResourceLocation）交给游戏自己的转场（SceneLoadInfo.SceneResourceLocation），
    /// 从 gate 门进去，看游戏能不能像进自己的场景一样进前作的场景（镜头、地块图、入口门、场景管理器）；走几个点截图；再回大厅。
    /// </summary>
    private static IEnumerator Native(string name, string gate, RepackedScene rs, string dir, HeroController hero, Action<string> log, Func<int> next)
    {
        GameManager gm = GameManager.instance;
        ModeController? mc = ModeController.Instance;
        UnityEngine.ResourceManagement.ResourceLocations.IResourceLocation? loc = null;
        try
        {
            loc = rs.Locate();
        }
        catch (Exception e)
        {
            log($"{name}: 找不到位置 / locate failed: {e.Message}");
        }
        if (loc == null || mc == null)
        {
            yield break;
        }
        log($"{name}: 原生转场 / native transition → {name}@{gate}（位置 / location {loc.InternalId} provider {loc.ProviderId}）");
        Hk1Gate.NativeScene = name;
        var sw = Stopwatch.StartNew();
        gm.BeginSceneTransition(new GameManager.SceneLoadInfo
        {
            SceneName = name,
            EntryGateName = gate,
            SceneResourceLocation = loc,
            PreventCameraFadeOut = false,
            WaitForSceneTransitionCameraFade = true,
            Visualization = GameManager.SceneLoadVisualizations.Default,
            AlwaysUnloadUnusedAssets = true,
        });
        float until = Time.unscaledTime + 90f;
        while (Time.unscaledTime < until && !(gm.GetSceneNameString() == name && gm.GameState == GlobalEnums.GameState.PLAYING && HeroController.instance != null && HeroController.instance.acceptingInput))
        {
            yield return null;
        }
        if (HeroController.instance != null)
        {
            hero = HeroController.instance;
        }
        Scene sc = SceneManager.GetSceneByName(name);
        CameraController? cc = GameCameras.SilentInstance != null ? GameCameras.SilentInstance.cameraController : null;
        CustomSceneManager? sm = gm.sm;
        log($"{name}: 到了没有 / arrived: scene={gm.GetSceneNameString()} state={gm.GameState} 用时 / took {sw.Elapsed.TotalSeconds:0.0}s；" +
            $"大黄蜂 / hero ({hero.transform.position.x:0.0}, {hero.transform.position.y:0.0}) 能操作 / input={hero.acceptingInput} 入口 / entry={hero.GetEntryGateName()}；" +
            $"场景大小 / scene size {gm.sceneWidth}×{gm.sceneHeight}；镜头边界 / cam limits {(cc != null ? $"{cc.xLimit:0.0}×{cc.yLimit:0.0}" : "-")}；" +
            $"场景管理器 / scene manager {(sm != null ? $"{sm.name} in {sm.gameObject.scene.name}" : "没有 / none")}");
        if (gm.GetSceneNameString() != name || !sc.IsValid())
        {
            log($"{name}: 没进去，停止 / did not get in");
            Hk1Gate.NativeScene = null;
            yield break;
        }
        yield return new WaitForSecondsRealtime(1.5f);
        yield return Shots.CaptureWorld(Path.Combine(dir, $"{next():00}-native-{name}-arrive.jpg"));
        Inventory(sc, "native-" + name, dir, log);
        // 走几个点：先关掉门（碰到会切去前作的另一个场景，神居模式会改回大厅）。
        foreach (TransitionPoint tp in Roots(sc).SelectMany(r => r.GetComponentsInChildren<TransitionPoint>(true)))
        {
            tp.enabled = false;
            foreach (Collider2D c in tp.GetComponents<Collider2D>())
            {
                c.enabled = false;
            }
        }
        yield return Tour(sc, "native-" + name, dir, hero, log, next);
        // 回大厅：照常发一次转场，神居模式会改道到大厅的长椅。
        Hk1Gate.NativeScene = null;
        log($"{name}: 回大厅 / back to the lobby");
        gm.BeginSceneTransition(new GameManager.SceneLoadInfo
        {
            SceneName = GodhomeConfig.LobbyScene.Value,
            EntryGateName = GodhomeConfig.LobbyMarker.Value,
            PreventCameraFadeOut = false,
            WaitForSceneTransitionCameraFade = true,
            Visualization = GameManager.SceneLoadVisualizations.Default,
            AlwaysUnloadUnusedAssets = true,
        });
        until = Time.unscaledTime + 90f;
        while (Time.unscaledTime < until && !(mc.State == LoomState.Lobby && gm.GetSceneNameString() == GodhomeConfig.LobbyScene.Value && Sanctum.Built
            && HeroController.instance != null && HeroController.instance.acceptingInput))
        {
            yield return null;
        }
        log($"{name}: 回到大厅 / back: state={mc.State} scene={gm.GetSceneNameString()} 圣所 / sanctum built={Sanctum.Built} 前作场景还在 / hk1 scene still loaded={SceneManager.GetSceneByName(name).isLoaded}");
        yield return new WaitForSecondsRealtime(1f);
    }

    private static IEnumerable<GameObject> Roots(Scene sc) => sc.IsValid() && sc.isLoaded ? sc.GetRootGameObjects() : Enumerable.Empty<GameObject>();

    private static string Shorten(string msg)
    {
        string s = msg.Replace("\r", " ").Replace("\n", " ");
        // 物体名不同的同一种报错合在一起。
        s = Regex.Replace(s, "'[^']*'", "'…'");
        s = Regex.Replace(s, "\\(Clone\\)|\\d+", "#");
        return s.Length > 220 ? s.Substring(0, 220) + "…" : s;
    }

    /// <summary>记下场景里有什么：组件按类型数、丢失的脚本、着色器、精灵、FSM。另存一份物体树。</summary>
    private static void Inventory(Scene sc, string name, string dir, Action<string> log)
    {
        List<GameObject> all = Roots(sc).SelectMany(r => r.GetComponentsInChildren<Transform>(true)).Select(t => t.gameObject).ToList();
        int missing = 0;
        var missingOn = new List<string>();
        var types = new Dictionary<string, int>();
        foreach (GameObject go in all)
        {
            foreach (Component c in go.GetComponents<Component>())
            {
                if (c == null)
                {
                    missing++;
                    if (missingOn.Count < 40)
                    {
                        missingOn.Add(PathOf(go.transform));
                    }
                    continue;
                }
                Type t = c.GetType();
                string key = $"{t.FullName} [{t.Assembly.GetName().Name}]";
                types[key] = types.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        }
        log($"{name}: 物体 / objects {all.Count}（激活 / active {all.Count(g => g.activeInHierarchy)}），组件类型 / component types {types.Count}，丢失的脚本 / missing scripts {missing}");
        // 标签：游戏按标签找场景管理器（SceneManager）、地块图（TileMap）。前作的标签编号到了丝之歌是不是同一个名字？
        string TagOf(GameObject g)
        {
            try
            {
                return g.tag;
            }
            catch (Exception e)
            {
                return "出错 / error: " + e.GetType().Name;
            }
        }
        GameObject? smGo = all.FirstOrDefault(g => g.name == "_SceneManager");
        GameObject? tmGo = all.FirstOrDefault(g => g.GetComponent<tk2dTileMap>() != null);
        log($"{name}: 标签 / tags: _SceneManager={(smGo != null ? TagOf(smGo) + " (" + string.Join(",", smGo.GetComponents<Component>().Select(c => c == null ? "丢失 / missing" : c.GetType().Name)) + ")" : "-")}，" +
            $"地块图 / tilemap {(tmGo != null ? tmGo.name + "=" + TagOf(tmGo) : "-")}；按标签数 / by tag: " +
            string.Join(", ", all.GroupBy(TagOf).OrderByDescending(g => g.Count()).Take(12).Select(g => $"{g.Key} {g.Count()}")));
        var sb = new StringBuilder();
        sb.AppendLine($"# {name} 组件 / components");
        foreach (var kv in types.OrderByDescending(kv => kv.Value))
        {
            sb.AppendLine($"{kv.Value,6}  {kv.Key}");
        }
        sb.AppendLine();
        sb.AppendLine("# 丢失脚本的物体（前 40 个）/ objects with missing scripts (first 40)");
        missingOn.ForEach(p => sb.AppendLine(p));

        // 渲染：材质、着色器（不支持的、报错着色器）、精灵。
        List<Renderer> rends = all.SelectMany(g => g.GetComponents<Renderer>()).Where(r => r != null).ToList();
        var shaders = new Dictionary<string, int>();
        int nullMat = 0, badShader = 0;
        foreach (Renderer r in rends)
        {
            foreach (Material m in r.sharedMaterials)
            {
                if (m == null)
                {
                    nullMat++;
                    continue;
                }
                Shader? s = m.shader;
                string key = s == null ? "(null)" : s.name + (s.isSupported ? "" : " (不支持 / unsupported)");
                if (s == null || !s.isSupported || s.name == "Hidden/InternalErrorShader")
                {
                    badShader++;
                }
                shaders[key] = shaders.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        }
        List<SpriteRenderer> srs = rends.OfType<SpriteRenderer>().ToList();
        List<tk2dBaseSprite> tk = all.SelectMany(g => g.GetComponents<tk2dBaseSprite>()).Where(x => x != null).ToList();
        log($"{name}: 渲染器 / renderers {rends.Count}（精灵 / sprite {srs.Count}，没有图的 / no sprite {srs.Count(s => s.sprite == null)}；tk2d {tk.Count}，没有图集的 / no collection {tk.Count(t => t.Collection == null)}），" +
            $"空材质 / null materials {nullMat}，坏着色器 / bad shaders {badShader}，着色器种类 / shader kinds {shaders.Count}");
        sb.AppendLine();
        sb.AppendLine("# 着色器 / shaders");
        foreach (var kv in shaders.OrderByDescending(kv => kv.Value))
        {
            sb.AppendLine($"{kv.Value,6}  {kv.Key}");
        }

        // 碰撞（地形层 8）、镜头锁定区、FSM。
        List<Collider2D> cols = all.SelectMany(g => g.GetComponents<Collider2D>()).Where(c => c != null).ToList();
        log($"{name}: 碰撞体 / colliders {cols.Count}（地形层 / terrain layer {cols.Count(c => c.gameObject.layer == 8)}），镜头锁定区 / camera locks {all.Sum(g => g.GetComponents<CameraLockArea>().Length)}");
        List<PlayMakerFSM> fsms = all.SelectMany(g => g.GetComponents<PlayMakerFSM>()).Where(f => f != null).ToList();
        log($"{name}: FSM {fsms.Count}（激活 / active {fsms.Count(f => f.isActiveAndEnabled)}）");
        sb.AppendLine();
        sb.AppendLine("# FSM（物体 / FSM 名 / 当前状态）");
        foreach (PlayMakerFSM f in fsms.Take(200))
        {
            string state;
            try
            {
                state = f.ActiveStateName;
            }
            catch (Exception e)
            {
                state = "出错 / error: " + e.GetType().Name;
            }
            sb.AppendLine($"{PathOf(f.transform)} | {f.FsmName} | {state} | active={f.isActiveAndEnabled}");
        }

        // 物体树（前三层）。
        sb.AppendLine();
        sb.AppendLine("# 物体树（前三层）/ tree (3 levels)");
        foreach (GameObject r in Roots(sc))
        {
            Tree(r.transform, 0, sb);
        }
        File.WriteAllText(Path.Combine(dir, name + "-inventory.txt"), sb.ToString(), new UTF8Encoding(false));

        // 路径匹配的物体：FSM 整个写出来（Debug.Hk1ProbeFsm）。
        string pattern = GodhomeConfig.DebugHk1ProbeFsm.Value?.Trim() ?? "";
        if (pattern.Length > 0)
        {
            var fb = new StringBuilder();
            int dumped = 0;
            try
            {
                var re = new Regex(pattern);
                foreach (PlayMakerFSM f in fsms.Where(f => re.IsMatch(PathOf(f.transform))))
                {
                    try
                    {
                        FsmDump.Write(f, fb);
                    }
                    catch (Exception e)
                    {
                        fb.AppendLine($"## {PathOf(f.transform)} | {f.FsmName}: 出错 / error {e}");
                    }
                    fb.AppendLine();
                    dumped++;
                }
            }
            catch (Exception e)
            {
                fb.AppendLine("出错 / error: " + e);
            }
            File.WriteAllText(Path.Combine(dir, name + "-fsm.txt"), fb.ToString(), new UTF8Encoding(false));
            log($"{name}: 写出的 FSM / FSMs dumped: {dumped}");
        }
    }

    private static void Tree(Transform t, int depth, StringBuilder sb)
    {
        string comps = string.Join(", ", t.GetComponents<Component>().Select(c => c == null ? "(丢失 / missing)" : c.GetType().Name).Where(n => n != "Transform"));
        Vector3 p = t.position;
        sb.AppendLine($"{new string(' ', depth * 2)}{t.name}{(t.gameObject.activeSelf ? "" : " [关 / off]")} ({p.x:0.0}, {p.y:0.0}, {p.z:0.0}) {comps}");
        if (depth >= 2)
        {
            if (t.childCount > 0)
            {
                sb.AppendLine($"{new string(' ', depth * 2 + 2)}…{t.childCount} 个子物体 / children");
            }
            return;
        }
        foreach (Transform c in t)
        {
            Tree(c, depth + 1, sb);
        }
    }

    private static string PathOf(Transform t)
    {
        var parts = new List<string>();
        for (Transform? x = t; x != null; x = x.parent)
        {
            parts.Add(x.name);
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static string Safe(string s) => Regex.Replace(s, "[^A-Za-z0-9_\\-]", "_");

    private static string FirstLines(string s, int n) => string.Join(" | ", (s ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(n).Select(l => l.Trim()));

    /// <summary>
    /// 别的已加载场景（不含 DontDestroyOnLoad）里开着的渲染器、2D 碰撞体先关掉，返回关掉的，看完再开。
    /// 物体本身不关：场景管理器、镜头、暗角这些照常工作（上一次把根物体整个关掉，截图全黑）。
    /// </summary>
    private static List<Component> HideOthers(Scene keep)
    {
        var list = new List<Component>();
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene s = SceneManager.GetSceneAt(i);
            if (s == keep || !s.isLoaded)
            {
                continue;
            }
            foreach (GameObject r in s.GetRootGameObjects())
            {
                foreach (Renderer x in r.GetComponentsInChildren<Renderer>(true))
                {
                    if (x.enabled)
                    {
                        x.enabled = false;
                        list.Add(x);
                    }
                }
                foreach (Collider2D x in r.GetComponentsInChildren<Collider2D>(true))
                {
                    if (x.enabled)
                    {
                        x.enabled = false;
                        list.Add(x);
                    }
                }
            }
        }
        return list;
    }

    private static void Unhide(List<Component> list)
    {
        foreach (Component c in list)
        {
            if (c is Renderer r && r != null)
            {
                r.enabled = true;
            }
            else if (c is Collider2D col && col != null)
            {
                col.enabled = true;
            }
        }
        list.Clear();
    }

    /// <summary>
    /// 把大黄蜂放到前作场景里截图：先站在门口，再站到雕像（BossStatue）旁边（按横坐标均匀挑几尊）；没有雕像就按地块图均匀取点。
    /// 地面从这个点往下打射线找（只认这个场景里的地形层 8）。镜头边界按前作地块图放宽。
    /// </summary>
    private static IEnumerator Tour(Scene sc, string name, string dir, HeroController hero, Action<string> log, Func<int> next)
    {
        float w = 0f, h = 0f;
        tk2dTileMap? map = Roots(sc).SelectMany(r => r.GetComponentsInChildren<tk2dTileMap>(true)).FirstOrDefault();
        if (map != null)
        {
            w = map.width;
            h = map.height;
        }
        log($"{name}: 地块图 / tilemap {(map != null ? $"{map.name} {w}×{h}" : "没有 / none")}");
        if (w <= 0f)
        {
            w = 300f;
            h = 100f;
        }
        CameraController? cc = GameCameras.SilentInstance != null ? GameCameras.SilentInstance.cameraController : null;
        if (cc != null)
        {
            cc.xLimit = Mathf.Max(cc.xLimit, w - 14.6f);
            cc.yLimit = Mathf.Max(cc.yLimit, h - 8.3f);
        }
        float? Ground(float x, float fromY, float depth)
        {
            foreach (RaycastHit2D hit in Physics2D.RaycastAll(new Vector2(x, fromY), Vector2.down, depth, 1 << 8))
            {
                if (hit.collider != null && hit.collider.gameObject.scene == sc && !hit.collider.isTrigger)
                {
                    return hit.point.y;
                }
            }
            return null;
        }
        var spots = new List<(string Label, float X, float Y)>();
        foreach (TransitionPoint tp in Roots(sc).SelectMany(r => r.GetComponentsInChildren<TransitionPoint>(true)).Take(2))
        {
            Vector3 p = tp.transform.position;
            spots.Add(($"door-{tp.name}", p.x + (p.x < w / 2f ? 3f : -3f), p.y + 1f));
        }
        List<BossStatue> statues = Roots(sc).SelectMany(r => r.GetComponentsInChildren<BossStatue>(true)).OrderBy(s => s.transform.position.x).ToList();
        log($"{name}: 雕像 / statues {statues.Count}: " + string.Join(", ", statues.Select(s => $"{s.name}({s.transform.position.x:0},{s.transform.position.y:0})")));
        const int pick = 8;
        for (int i = 0; i < Math.Min(pick, statues.Count); i++)
        {
            BossStatue s = statues[statues.Count <= pick ? i : (int)Math.Round(i * (statues.Count - 1) / (double)(pick - 1))];
            Vector3 p = s.transform.position;
            spots.Add(($"statue-{s.name}", p.x - 2.5f, p.y + 2f));
        }
        if (statues.Count == 0)
        {
            for (int i = 0; i < 5; i++)
            {
                spots.Add(($"x{i}", Mathf.Lerp(12f, w - 12f, i / 4f), 20f));
            }
        }
        int shots = 0;
        foreach ((string label, float x, float y) in spots)
        {
            float? g = Ground(x, y + 1f, 25f) ?? Ground(x, y + 6f, 40f);
            if (g == null)
            {
                log($"{name}: {label} ({x:0.0}, {y:0.0}) 下面没有地面 / no ground");
                continue;
            }
            Sanctum.StandAt(hero, x, g.Value);
            yield return new WaitForSecondsRealtime(1.5f);
            Vector3 hp = hero.transform.position;
            string locks = cc == null || cc.lockZoneList == null ? "-" : string.Join(",", cc.lockZoneList.Where(z => z != null).Select(z => z.name));
            log($"{name}: {label} 地面 / ground {g.Value:0.0} → 大黄蜂 / hero ({hp.x:0.0}, {hp.y:0.0}) 镜头 / cam ({(cc != null ? cc.transform.position.x : 0f):0.0}, {(cc != null ? cc.transform.position.y : 0f):0.0}) mode={(cc != null ? cc.mode.ToString() : "-")} locks=[{locks}]");
            yield return Shots.CaptureWorld(Path.Combine(dir, $"{next():00}-{name}-{Safe(label)}.jpg"));
            shots++;
        }
        // 再拉远拍一张全景（镜头视野放大，拍完还原）。
        Camera? cam = GameCameras.SilentInstance != null ? GameCameras.SilentInstance.mainCamera : null;
        if (cam != null)
        {
            float mx = statues.Count > 0 ? statues[statues.Count / 2].transform.position.x - 2.5f : w / 2f;
            float my = statues.Count > 0 ? statues[statues.Count / 2].transform.position.y + 3f : 20f;
            float? g = Ground(mx, my, 25f);
            if (g != null)
            {
                Sanctum.StandAt(hero, mx, g.Value);
                yield return new WaitForSecondsRealtime(1.5f);
            }
            float fov = cam.fieldOfView, size = cam.orthographicSize;
            try
            {
                if (cam.orthographic)
                {
                    cam.orthographicSize = size * 2.5f;
                }
                else
                {
                    cam.fieldOfView = Mathf.Min(150f, fov * 2.5f);
                }
                yield return Shots.CaptureWorld(Path.Combine(dir, $"{next():00}-{name}-wide.jpg"));
                shots++;
            }
            finally
            {
                cam.fieldOfView = fov;
                cam.orthographicSize = size;
            }
        }
        log($"{name}: 截图 / shots {shots}");
    }
}
