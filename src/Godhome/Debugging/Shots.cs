using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Godhome;

/// <summary>[Godhome] 自检截图：缩到 800 宽存成 JPG（50 个 Boss 每个四张，原尺寸 PNG 太大）。</summary>
internal static class Shots
{
    private const int Width = 800;

    /// <summary>
    /// 不管屏幕多大（比如远程桌面是竖屏），把游戏主镜头单独画进一张 w×h 的图（16:9）存成 JPG。
    /// 只有场景画面，没有界面（名牌、面板）。画不出来就退回普通截屏。
    /// </summary>
    internal static IEnumerator CaptureWorld(string path, int w = 1440, int h = 810)
    {
        // 录宣传片时不截图（镜头都画进录像的图里了）。
        if (PromoRecorder.Recording)
        {
            yield break;
        }
        yield return new WaitForEndOfFrame();
        GameCameras gc = GameCameras.SilentInstance;
        Camera? hud = gc != null ? gc.hudCamera : null;
        // 游戏画面是几台镜头按深度叠起来画的：直接画到屏幕上的（没有自己的目标贴图）、除了界面镜头，按深度依次画进同一张图。
        var cams = new List<Camera>();
        foreach (Camera c in Camera.allCameras)
        {
            if (c != null && c.enabled && c.targetTexture == null && c != hud)
            {
                cams.Add(c);
            }
        }
        cams.Sort((a, b) => a.depth.CompareTo(b.depth));
        if (!loggedCams)
        {
            loggedCams = true;
            GodhomePlugin.Log.LogInfo("[Godhome] 离屏截图的镜头 / offscreen capture cameras: " + string.Join(", ", cams.ConvertAll(c => $"{c.name}(depth {c.depth}, clear {c.clearFlags}, mask 0x{c.cullingMask:X})")));
        }
        bool ok = false;
        if (cams.Count > 0)
        {
            RenderTexture? rt = null;
            Texture2D? tex = null;
            RenderTexture? prev = RenderTexture.active;
            // 宽高比原来是自动算的（等于视口的宽高比）还是手动设的：还原时照原来的方式（Codex 审计 0.10.3 DBG01：
            // 以前一律写回数值，自动的镜头变成了手动，之后窗口大小变了还用旧比例）。
            var saved = cams.ConvertAll(c => (c.targetTexture, c.rect, c.aspect,
                auto: c.pixelHeight > 0 && Mathf.Abs(c.aspect - c.pixelWidth / (float)c.pixelHeight) < 0.002f));
            try
            {
                rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
                RenderTexture.active = rt;
                GL.Clear(true, true, Color.black);
                foreach (Camera c in cams)
                {
                    c.targetTexture = rt;
                    c.rect = new Rect(0f, 0f, 1f, 1f);
                    c.aspect = w / (float)h;
                    c.Render();
                }
                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToJPG(85));
                ok = true;
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 离屏截图失败，改用截屏 / offscreen capture failed, using a screen capture: {e.Message}");
            }
            finally
            {
                for (int i = 0; i < cams.Count; i++)
                {
                    if (cams[i] != null)
                    {
                        cams[i].targetTexture = saved[i].targetTexture;
                        cams[i].rect = saved[i].rect;
                        if (saved[i].auto)
                        {
                            cams[i].ResetAspect();
                        }
                        else
                        {
                            cams[i].aspect = saved[i].aspect;
                        }
                    }
                }
                RenderTexture.active = prev;
                if (rt != null)
                {
                    RenderTexture.ReleaseTemporary(rt);
                }
                if (tex != null)
                {
                    Object.Destroy(tex);
                }
            }
        }
        if (!ok)
        {
            yield return CaptureScreen(path, null, w);
        }
    }

    private static bool loggedCams;
    private static string lastUiCams = "";

    /// <summary>
    /// 连界面一起截（游戏的菜单界面画在界面镜头上）：所有直接画到屏幕的镜头（包括界面镜头）这一帧都改画进一张 w×h 的图，
    /// 等一帧让界面按这个尺寸重新排版、画完，再读出来存成 JPG。这一帧屏幕上是黑的（只在巡视里用）。
    /// </summary>
    internal static IEnumerator CaptureWithUi(string path, int w = 1440, int h = 810)
    {
        if (PromoRecorder.Recording)
        {
            yield break;
        }
        yield return new WaitForEndOfFrame();
        // 上一次没还原的（不该有）先还原。
        RestoreCameras();
        var cams = new List<Camera>();
        foreach (Camera c in Camera.allCameras)
        {
            // 视口是零大小的镜头平时什么都不画（藏起来的菜单镜头之类）：不能拉成全屏画进来（截到过标题菜单）。
            // 已知：读档后头一两张连界面截图偶尔是开机时的标题菜单（显存里的旧画面）；那时界面画布上没有看得见的东西（ViewInfo.UiVisible），
            // 玩家看不到，只是截图的问题。试过不碰 Darkness Camera，没用（换到别的那一张），已撤回。
            if (c != null && c.enabled && c.targetTexture == null && c.rect.width > 0.001f && c.rect.height > 0.001f)
            {
                cams.Add(c);
            }
        }
        string camList = string.Join(", ", Camera.allCameras.Where(c => c != null).Select(c =>
            $"{c.name}(depth {c.depth}, clear {c.clearFlags}, rect {c.rect.x:0.##},{c.rect.y:0.##},{c.rect.width:0.##}x{c.rect.height:0.##}, mask 0x{c.cullingMask:X}, rt {(c.targetTexture != null ? c.targetTexture.name : "-")}{(cams.Contains(c) ? "" : ", skipped")})"));
        if (camList != lastUiCams)
        {
            lastUiCams = camList;
            GodhomePlugin.Log.LogInfo("[Godhome] 连界面截图的镜头 / capture-with-UI cameras: " + camList);
        }
        if (cams.Count == 0)
        {
            yield return CaptureScreen(path, null, w);
            yield break;
        }
        // 改第一台镜头之前就登记好怎么还原：跨帧期间巡视被停（宿主停用、销毁）也能由 RestoreCameras 还原（Codex 审查 v0.7.0 #6）。
        redirectRt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
        // 新建的临时图里是显存里的旧内容（每轮第一张截到过开机时的标题菜单）：先清成黑的，镜头没画到的地方就是黑的。
        RenderTexture? before = RenderTexture.active;
        RenderTexture.active = redirectRt;
        GL.Clear(true, true, Color.black);
        RenderTexture.active = before;
        bool ready = true;
        try
        {
            foreach (Camera c in cams)
            {
                // 镜头的宽高比本来是自动算的（等于视口的宽高比）还是手动设的：还原时照原来的方式。
                bool auto = c.pixelHeight > 0 && Mathf.Abs(c.aspect - c.pixelWidth / (float)c.pixelHeight) < 0.002f;
                redirected.Add(new CamState { Cam = c, Target = c.targetTexture, Rect = c.rect, Aspect = c.aspect, AutoAspect = auto });
                c.targetTexture = redirectRt;
                c.rect = new Rect(0f, 0f, 1f, 1f);
                c.ResetAspect();
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 连界面截图准备失败 / capture with UI setup failed: {e.Message}");
            ready = false;
        }
        if (!ready)
        {
            RestoreCameras();
            yield break;
        }
        try
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            ReadOut(redirectRt, path, w, h);
        }
        finally
        {
            RestoreCameras();
        }
    }

    private static void ReadOut(RenderTexture? rt, string path, int w, int h)
    {
        if (rt == null)
        {
            return;
        }
        RenderTexture? prev = RenderTexture.active;
        Texture2D? tex = null;
        try
        {
            RenderTexture.active = rt;
            tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToJPG(85));
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 连界面截图失败 / capture with UI failed: {e.Message}");
        }
        finally
        {
            RenderTexture.active = prev;
            if (tex != null)
            {
                Object.Destroy(tex);
            }
        }
    }

    private sealed class CamState
    {
        internal Camera Cam = null!;
        internal RenderTexture? Target;
        internal Rect Rect;
        internal float Aspect;
        internal bool AutoAspect;
    }

    // 正在改画进临时图的镜头和那张临时图。
    private static readonly List<CamState> redirected = new();
    private static RenderTexture? redirectRt;

    /// <summary>
    /// 把 CaptureWithUi 改过的镜头还原（重复调没关系）。跨帧期间游戏已经给某台镜头换了目标（不是我们的临时图了），就保留游戏的。
    /// 宽高比按原来的方式还：原来是自动的就 ResetAspect，手动的才写回原值。
    /// </summary>
    internal static void RestoreCameras()
    {
        foreach (CamState s in redirected)
        {
            try
            {
                Camera c = s.Cam;
                if (c == null || c.targetTexture != redirectRt)
                {
                    continue;
                }
                c.targetTexture = s.Target;
                c.rect = s.Rect;
                if (s.AutoAspect)
                {
                    c.ResetAspect();
                }
                else
                {
                    c.aspect = s.Aspect;
                }
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 还原镜头出错 / camera restore failed: {e.Message}");
            }
        }
        redirected.Clear();
        if (redirectRt != null)
        {
            RenderTexture.ReleaseTemporary(redirectRt);
            redirectRt = null;
        }
    }

    /// <summary>
    /// 自检截图（连界面）：离屏画进一张 1440×810 的图（CaptureWithUi 的默认尺寸），存到 path，成功时把文件名加进 names。
    /// 不用屏幕截图：远程桌面断开时屏幕截图是全黑的（2026-11-24 早上一整轮 200 张全黑，按亮度核对过）。
    /// 以前有个 width 参数（默认 800）但根本没传下去（Codex 审计 0.10.3 DBG02），去掉了，免得以为能改尺寸。
    /// </summary>
    internal static IEnumerator Capture(string path, List<string>? names)
    {
        DateTime before = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        yield return CaptureWithUi(path);
        if (File.Exists(path) && File.GetLastWriteTimeUtc(path) != before)
        {
            names?.Add(Path.GetFileName(path));
        }
    }

    /// <summary>屏幕截图（离屏画不出来时的退路）：在这一帧画完后截屏，存到 path。失败只记日志。</summary>
    private static IEnumerator CaptureScreen(string path, List<string>? names, int width = Width)
    {
        yield return new WaitForEndOfFrame();
        Texture2D? full = null;
        Texture2D? small = null;
        RenderTexture? rt = null;
        RenderTexture? prev = RenderTexture.active;
        try
        {
            full = ScreenCapture.CaptureScreenshotAsTexture();
            int w = Mathf.Min(width, full.width);
            int h = Mathf.Max(1, full.height * w / Mathf.Max(1, full.width));
            rt = RenderTexture.GetTemporary(w, h, 0);
            Graphics.Blit(full, rt);
            RenderTexture.active = rt;
            small = new Texture2D(w, h, TextureFormat.RGB24, false);
            small.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            small.Apply();
            File.WriteAllBytes(path, small.EncodeToJPG(80));
            names?.Add(Path.GetFileName(path));
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 截图失败 / screenshot failed {path}: {e.Message}");
        }
        finally
        {
            RenderTexture.active = prev;
            if (rt != null)
            {
                RenderTexture.ReleaseTemporary(rt);
            }
            if (full != null)
            {
                Object.Destroy(full);
            }
            if (small != null)
            {
                Object.Destroy(small);
            }
        }
    }
}
