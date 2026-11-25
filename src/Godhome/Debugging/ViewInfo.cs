using System;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Godhome;

/// <summary>
/// [Godhome] 调试：画面为什么黑（只记日志，不改任何东西）。
/// 丝之歌的暗处：场景管理器的 darknessLevel / 人所在的 DarknessRegion 决定暗度，角色光 FSM（vignetteFSM 的 “Darkness Level”）
/// 按暗度调人身边那圈光；Darkness Camera 只画第 1 层（挖光的图）进一张图，暗处的遮罩按这张图挖出亮的地方（反编译
/// DarknessRegion.cs、DarknessCameraEffect.cs）。挖光的图没有、或者暗度不对，就整屏黑、只剩画在最前面的前景。
/// </summary>
internal static class ViewInfo
{
    /// <summary>开着的根画布：渲染方式、用哪台镜头、排序、算上父物体 CanvasGroup 的透明度；UIManager 的状态。</summary>
    internal static string Canvases()
    {
        var sb = new StringBuilder();
        try
        {
            UIManager? ui = UIManager.instance;
            if (ui != null)
            {
                Traverse t = Traverse.Create(ui);
                sb.Append($"UIManager uiState {t.Field("uiState").GetValue()} menuState {t.Field("menuState").GetValue()}");
            }
            foreach (Canvas c in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                if (c == null || !c.isActiveAndEnabled || !c.isRootCanvas)
                {
                    continue;
                }
                CanvasGroup own = c.GetComponent<CanvasGroup>();
                float alpha = own != null ? own.alpha : 1f;
                // 画布下面开着、而且看得见（透明度 > 0）的子物体，列前几个，认得出是哪个界面。
                var shown = c.GetComponentsInChildren<CanvasGroup>(false).Where(g => g.alpha > 0.01f).Take(4).Select(g => g.name);
                sb.Append($"；画布 / canvas {SceneTools.PathOf(c.transform)} {c.renderMode} cam {(c.worldCamera != null ? c.worldCamera.name : "-")} layer {c.gameObject.layer} sort {c.sortingLayerName}/{c.sortingOrder} alpha {alpha:0.00} shown [{string.Join(", ", shown)}]");
            }
        }
        catch (Exception e)
        {
            sb.Append(" 出错 / error " + e.Message);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 盖在人所在位置上的、开着的渲染器，按画的先后从前往后排（排序层、层内顺序、离镜头远近），列前 max 个：
    /// 名字、排序、颜色、材质、大小。用来找整屏黑的时候是什么挡在前面。
    /// </summary>
    internal static string Covering(int max = 20)
    {
        var sb = new StringBuilder();
        try
        {
            HeroController? h = HeroController.SilentInstance;
            if (h == null)
            {
                return "-";
            }
            Vector3 p = h.transform.position;
            var list = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)
                .Where(r => r != null && r.enabled && r.gameObject.activeInHierarchy && r.isVisible
                    && p.x >= r.bounds.min.x && p.x <= r.bounds.max.x && p.y >= r.bounds.min.y && p.y <= r.bounds.max.y)
                .OrderByDescending(r => SortingLayer.GetLayerValueFromID(r.sortingLayerID))
                .ThenByDescending(r => r.sortingOrder)
                .ThenBy(r => r.transform.position.z)
                .ToList();
            sb.Append($"盖住人的 / covering the hero {list.Count}: ");
            sb.Append(string.Join(" | ", list.Take(max).Select(r =>
            {
                string col = r is SpriteRenderer sr ? $" color {sr.color}" + (sr.sprite != null ? $" sprite {sr.sprite.name}" : "") : "";
                string mat = r.sharedMaterial != null ? r.sharedMaterial.name : "-";
                return $"{SceneTools.PathOf(r.transform)} [{r.GetType().Name}] {r.sortingLayerName}/{r.sortingOrder} z {r.transform.position.z:0.0} layer {r.gameObject.layer} size {r.bounds.size.x:0}x{r.bounds.size.y:0} mat {mat}{col} scene {r.gameObject.scene.name}";
            })));
        }
        catch (Exception e)
        {
            sb.Append(" 出错 / error " + e.Message);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 盖住人的、排在 Over 层的大块图（场景里的遮罩）：往上三层每层的组件、FSM 当前状态，看它平时是靠什么揭开的。
    /// </summary>
    internal static string MaskOwners()
    {
        var sb = new StringBuilder();
        try
        {
            HeroController? h = HeroController.SilentInstance;
            if (h == null)
            {
                return "-";
            }
            Vector3 p = h.transform.position;
            foreach (SpriteRenderer r in Object.FindObjectsByType<SpriteRenderer>(FindObjectsSortMode.None))
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || r.sortingLayerName != "Over" || r.gameObject.scene.name == "DontDestroyOnLoad"
                    || r.bounds.size.x < 8f || !(p.x >= r.bounds.min.x && p.x <= r.bounds.max.x && p.y >= r.bounds.min.y && p.y <= r.bounds.max.y))
                {
                    continue;
                }
                sb.Append($"；遮罩 / mask {SceneTools.PathOf(r.transform)} a{r.color.a:0.00}:");
                Transform? t = r.transform;
                for (int i = 0; i < 4 && t != null; i++, t = t.parent)
                {
                    string comps = string.Join(",", t.GetComponents<Component>().Where(c => c != null && c is not Transform).Select(c =>
                        c is PlayMakerFSM f ? $"FSM {f.FsmName}@{f.ActiveStateName}" : c.GetType().Name));
                    sb.Append($" [{t.name}{(t.gameObject.activeSelf ? "" : " off")}: {comps}]");
                }
            }
        }
        catch (Exception e)
        {
            sb.Append(" 出错 / error " + e.Message);
        }
        return sb.Length == 0 ? "-" : sb.ToString();
    }

    internal static string Darkness()
    {
        var sb = new StringBuilder();
        try
        {
            HeroController? h = HeroController.SilentInstance;
            GameManager? gm = GameManager.SilentInstance;
            CustomSceneManager? sm = gm != null ? gm.sm : null;
            sb.Append($"场景暗度 / scene darkness {(sm != null ? sm.darknessLevel.ToString() : "-")}");
            if (h != null && h.vignetteFSM != null)
            {
                HutongGames.PlayMaker.FsmInt? lvl = h.vignetteFSM.FsmVariables.FindFsmInt("Darkness Level");
                sb.Append($"，角色光 / vignette fsm {h.vignetteFSM.ActiveStateName} level {(lvl != null ? lvl.Value.ToString() : "-")}");
            }
            if (h != null && h.vignette != null)
            {
                sb.Append($"，vignette {(h.vignette.enabled ? "on" : "off")}/{(h.vignette.gameObject.activeInHierarchy ? "active" : "inactive")} scale {h.vignette.transform.lossyScale}");
            }
            foreach (DarknessRegion r in Object.FindObjectsByType<DarknessRegion>(FindObjectsSortMode.None))
            {
                int level = Traverse.Create(r).Field("darknessLevel").GetValue<int>();
                sb.Append($"；暗区 / region {SceneTools.PathOf(r.transform)} level {level} inside {r.IsInside}");
            }
            if (h != null)
            {
                Vector3 hp = h.transform.position;
                var cut = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)
                    .Where(r => r != null && r.gameObject.layer == 1 && r.enabled && r.gameObject.activeInHierarchy).ToList();
                var near = cut.Where(r => r.bounds.SqrDistance(hp) < 100f).ToList();
                sb.Append($"；挖光的图 / cut-outs (layer 1) {cut.Count}，人附近 / near hero {near.Count}: "
                    + string.Join(", ", near.Take(8).Select(r => SceneTools.PathOf(r.transform) + (r is SpriteRenderer sr ? $" a{sr.color.a:0.00}" : ""))));
            }
        }
        catch (Exception e)
        {
            sb.Append(" 出错 / error " + e.Message);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 每台直接画到屏幕的镜头单独画一次进一张小图（先清成品红）：画到的比例、画到的地方的平均颜色。
    /// 看整屏黑是场景镜头自己画黑了，还是界面镜头上有东西盖住了。画完把镜头的目标、视口、宽高比还原。
    /// </summary>
    internal static string CameraLuma(int w = 160, int h = 90)
    {
        var sb = new StringBuilder("镜头单画 / per camera:");
        foreach (Camera c in Camera.allCameras.Where(c => c != null).OrderBy(c => c.depth).ToList())
        {
            if (!c.enabled || c.targetTexture != null || c.rect.width < 0.001f || c.rect.height < 0.001f)
            {
                continue;
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
                if (auto)
                {
                    c.ResetAspect();
                }
                c.Render();
                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                Color32[] px = tex.GetPixels32();
                int drawn = 0;
                double r = 0, g = 0, b = 0;
                foreach (Color32 q in px)
                {
                    if (q.r > 240 && q.g < 16 && q.b > 240)
                    {
                        continue;
                    }
                    drawn++;
                    r += q.r;
                    g += q.g;
                    b += q.b;
                }
                int n = Mathf.Max(1, drawn);
                sb.Append($" {c.name}(clear {c.clearFlags}) 画到 / drawn {drawn * 100 / Mathf.Max(1, px.Length)}% 平均 / mean ({r / n:0},{g / n:0},{b / n:0})；");
            }
            catch (Exception e)
            {
                sb.Append($" {c.name}: {e.Message}；");
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
                    Object.Destroy(tex);
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 界面镜头单独画一次，画到的比例（0~100）。正常时只有血、丝这些小图标；整屏被白光、黑幕盖着时接近 100。量不了返回 -1。
    /// </summary>
    internal static int HudCover(int w = 160, int h = 90)
    {
        Camera? c = GameCameras.instance != null ? GameCameras.instance.hudCamera : null;
        if (c == null || !c.enabled || c.targetTexture != null)
        {
            return -1;
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
            if (auto)
            {
                c.ResetAspect();
            }
            c.Render();
            RenderTexture.active = rt;
            tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            Color32[] px = tex.GetPixels32();
            int drawn = px.Count(q => !(q.r > 240 && q.g < 16 && q.b > 240));
            return drawn * 100 / Mathf.Max(1, px.Length);
        }
        catch (Exception)
        {
            return -1;
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
                Object.Destroy(tex);
            }
        }
    }

    /// <summary>界面镜头（HudCamera）画的东西里不透明的大块：渲染器（按层）和 UI 图形（画布用这台镜头），带路径、颜色、父物体的 FSM 状态。</summary>
    internal static string HudOpaque(int max = 12)
    {
        var sb = new StringBuilder("界面镜头上的不透明大块 / opaque on HudCamera:");
        try
        {
            Camera? hud = GameCameras.instance != null ? GameCameras.instance.hudCamera : null;
            if (hud == null)
            {
                return sb.Append(" 没有 HudCamera").ToString();
            }
            int n = 0;
            foreach (Renderer r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || (hud.cullingMask & (1 << r.gameObject.layer)) == 0)
                {
                    continue;
                }
                Color col = r is SpriteRenderer s ? s.color : (r.sharedMaterial != null && r.sharedMaterial.HasProperty("_Color") ? r.sharedMaterial.color : Color.white);
                Vector3 size = r.bounds.size;
                if (col.a < 0.3f || size.x < 10f || size.y < 6f)
                {
                    continue;
                }
                if (n++ >= max)
                {
                    break;
                }
                sb.Append($" [{Path(r.transform)} {r.GetType().Name} {r.sortingLayerName}/{r.sortingOrder} size {size.x:0}x{size.y:0} color {col} sprite {(r is SpriteRenderer s2 && s2.sprite != null ? s2.sprite.name : "-")}{Fsms(r.transform)}]");
            }
            foreach (UnityEngine.UI.Graphic g in Object.FindObjectsByType<UnityEngine.UI.Graphic>(FindObjectsSortMode.None))
            {
                if (g == null || !g.enabled || !g.gameObject.activeInHierarchy || g.canvas == null)
                {
                    continue;
                }
                Canvas root = g.canvas.rootCanvas;
                if (root.renderMode == RenderMode.ScreenSpaceOverlay || (root.renderMode == RenderMode.ScreenSpaceCamera && root.worldCamera != hud))
                {
                    continue;
                }
                float a = g.color.a;
                foreach (CanvasGroup cg in g.GetComponentsInParent<CanvasGroup>())
                {
                    a *= cg.alpha;
                    if (cg.ignoreParentGroups)
                    {
                        break;
                    }
                }
                RectTransform rt = g.rectTransform;
                RectTransform crt = (RectTransform)root.transform;
                Vector2 gs = Vector2.Scale(rt.rect.size, rt.lossyScale);
                Vector2 cs = Vector2.Scale(crt.rect.size, crt.lossyScale);
                if (a < 0.3f || cs.x <= 0f || gs.x < cs.x * 0.5f || gs.y < cs.y * 0.5f)
                {
                    continue;
                }
                if (n++ >= max)
                {
                    break;
                }
                sb.Append($" [{Path(g.transform)} {g.GetType().Name} color {g.color} alpha {a:0.00} size {gs.x / cs.x:0.00}x{gs.y / cs.y:0.00} of canvas {root.name}{Fsms(g.transform)}]");
            }
            if (n == 0)
            {
                sb.Append(" 没有 / none");
            }
        }
        catch (Exception e)
        {
            sb.Append($" 出错 / failed: {e.Message}");
        }
        return sb.ToString();

        static string Path(Transform t)
        {
            string s = t.name;
            for (Transform? p = t.parent; p != null; p = p.parent)
            {
                s = p.name + "/" + s;
            }
            return s;
        }

        static string Fsms(Transform t)
        {
            var parts = new System.Collections.Generic.List<string>();
            Transform? cur = t;
            for (int i = 0; i < 3 && cur != null; i++, cur = cur.parent)
            {
                foreach (PlayMakerFSM f in cur.GetComponents<PlayMakerFSM>())
                {
                    parts.Add($"{cur.name}:{f.FsmName}@{f.ActiveStateName}");
                }
            }
            return parts.Count > 0 ? " fsm " + string.Join(",", parts) : "";
        }
    }

    /// <summary>一个转场门：customFade、上面和子物体上每个 FSM 的当前状态、每个状态的动作和跳转（查前作回程门谁来淡入）。</summary>
    internal static string GateFsms(string gate)
    {
        var sb = new StringBuilder($"门 / gate {gate}:");
        try
        {
            TransitionPoint? tp = TransitionPoint.TransitionPoints.FirstOrDefault(x => x != null && x.name == gate);
            if (tp == null)
            {
                return sb.Append(" 找不到 / not found").ToString();
            }
            sb.Append($" customFade={tp.customFade} active={tp.gameObject.activeInHierarchy} pos=({tp.transform.position.x:0.0}, {tp.transform.position.y:0.0})");
            Transform root = tp.transform.parent != null ? tp.transform.parent : tp.transform;
            foreach (PlayMakerFSM f in root.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                sb.Append($" ‖ FSM {f.gameObject.name}:{f.FsmName}@{f.ActiveStateName} enabled={f.enabled}");
                foreach (HutongGames.PlayMaker.FsmState st in f.FsmStates)
                {
                    string acts = string.Join(",", st.Actions.Select(a => a == null ? "null" : a.GetType().Name));
                    string tr = string.Join(",", st.Transitions.Select(x => $"{x.EventName}:{x.ToState}"));
                    sb.Append($" [{st.Name}: {acts} → {tr}]");
                }
            }
        }
        catch (Exception e)
        {
            sb.Append($" 出错 / failed: {e.Message}");
        }
        return sb.ToString();
    }

    /// <summary>游戏界面画布（_UIManager/UICanvas）里现在真的看得见的图形（算上所有父 CanvasGroup 的透明度）：查截图里的标题菜单是不是真的画在屏幕上。</summary>
    internal static string UiVisible(int max = 12)
    {
        var sb = new StringBuilder("界面画布上看得见的 / visible on UICanvas:");
        try
        {
            UIManager? ui = UIManager.instance;
            Canvas? canvas = ui != null ? ui.GetComponentsInChildren<Canvas>(true).FirstOrDefault(c => c.name == "UICanvas") : null;
            if (canvas == null)
            {
                return sb.Append(" 没有 UICanvas").ToString();
            }
            int n = 0, total = 0;
            foreach (UnityEngine.UI.Graphic g in canvas.GetComponentsInChildren<UnityEngine.UI.Graphic>(false))
            {
                if (!g.enabled || g.canvasRenderer == null || g.canvasRenderer.cull)
                {
                    continue;
                }
                float a = g.color.a * g.canvasRenderer.GetAlpha();
                foreach (CanvasGroup cg in g.GetComponentsInParent<CanvasGroup>())
                {
                    a *= cg.alpha;
                    if (cg.ignoreParentGroups)
                    {
                        break;
                    }
                }
                if (a < 0.05f)
                {
                    continue;
                }
                total++;
                if (n++ < max)
                {
                    string path = g.name;
                    for (Transform? q = g.transform.parent; q != null && q != canvas.transform; q = q.parent)
                    {
                        path = q.name + "/" + path;
                    }
                    sb.Append($" [{path} {g.GetType().Name} a={a:0.00}{(g is UnityEngine.UI.Text tx ? " \"" + tx.text + "\"" : g is TMPro.TMP_Text tm ? " \"" + tm.text + "\"" : "")}]");
                }
            }
            sb.Append($" 共 / total {total}");
        }
        catch (Exception e)
        {
            sb.Append($" 出错 / failed: {e.Message}");
        }
        return sb.ToString();
    }}
