using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Godhome;

/// <summary>
/// [Godhome] 把 Boss 的图鉴立绘“雕”成石像。
///
/// 前作的神居雕像（反编译里还留着 BossStatue / BossStatueTrophyPlaque / BossStatueLever 这些类）是每个 Boss 专门画的
/// 石雕精灵；丝之歌没有这套美术，前作的素材又不能用。所以用丝之歌自己的图鉴立绘在运行时雕：
/// - 读像素：图集贴图不可读，先用 GPU 把整页拷进临时 RenderTexture，再只读回这张精灵占的那一块；
///   按精灵自己的网格三角形把像素画出来（不管图集怎么打包、旋转都对）。
/// - 雕刻：颜色压成一种石料；原画的细黑线当刻痕，大块黑色当深色石料；透明度模糊出体积，按左上方的光打出浮雕明暗；
///   边缘压暗、迎光的外缘一道亮边；石头颗粒。
/// - 结果是我们自己生成的贴图和精灵（不改游戏的任何东西），按立绘缓存，回标题时销毁。
/// </summary>
internal static class StatueCarver
{
    private const int MaxPixels = 400;
    private const int MinPixels = 300;
    private const int Pad = 10;

    /// <summary>
    /// 前作神居用的石像：前作手办屋的雕像是深色石料（实测前作雕像贴图的亮度中位数≈0、亮的 5% 在 0.27~0.32），
    /// 用会受光的材质（Sprites-Diffuse），靠头顶的光柱和两盏点光把迎光的边缘照亮。我们的石像在那里也照这个做，
    /// 不然浅色石料在同样的灯下一片白、没有体积。织忆圣所（不受光的材质）还是浅色石料。
    /// 一次进存档里不会变（回标题时缓存清掉）。
    /// </summary>
    private static bool Dark => Hk1Lobby.Active;

    /// <summary>
    /// 要雕的一张画：贴图上的一块（按网格三角形取，图集怎么打包、旋转都对）。
    /// 可以是图鉴立绘（Sprite），也可以是 tk2d 动作帧（Tk2dArt）。Pair = 雕成一前一后两尊（“双生”的形态）。
    /// </summary>
    internal sealed class Source
    {
        internal readonly string Key;
        internal readonly string Name;
        internal readonly Texture Texture;
        internal readonly Vector2[] Verts;
        internal readonly Vector2[] Uvs;
        internal readonly int[] Tris;
        internal readonly Bounds Bounds;
        internal readonly bool Premultiplied;
        internal bool Pair;

        internal Source(string key, string name, Texture texture, Vector2[] verts, Vector2[] uvs, int[] tris, Bounds bounds, bool premultiplied = false)
        {
            Key = key;
            Name = name;
            Texture = texture;
            Verts = verts;
            Uvs = uvs;
            Tris = tris;
            Bounds = bounds;
            Premultiplied = premultiplied;
        }

        internal static Source FromSprite(string key, Sprite s)
        {
            ushort[] t = s.triangles;
            var tris = new int[t.Length];
            for (int i = 0; i < t.Length; i++)
            {
                tris[i] = t[i];
            }
            return new Source(key, s.name, s.texture, s.vertices, s.uv, tris, s.bounds);
        }
    }

    private static readonly Dictionary<string, Sprite?> cache = new(StringComparer.Ordinal);
    private static readonly List<Texture2D> made = new();

    /// <summary>这次搭建雕了几尊、失败几尊、几轮、用时（日志、巡视用）。</summary>
    internal static string Note { get; private set; } = "";

    private static int sessOk, sessFailed, sessRounds;
    private static float sessStart;

    /// <summary>一次搭建开始雕（可能分几轮调 CarveAll）。</summary>
    internal static void BeginSession()
    {
        sessOk = sessFailed = sessRounds = 0;
        sessStart = Time.realtimeSinceStartup;
    }

    internal static void EndSession()
    {
        Note = $"石像 / statues carved: {sessOk}，失败 / failed {sessFailed}，{sessRounds} 轮 / round(s)，{Time.realtimeSinceStartup - sessStart:0.00}s，缓存共 / cached {cache.Count}";
        GodhomePlugin.Log.LogInfo($"[Godhome] {Note}");
    }

    internal static int Count => made.Count;

    /// <summary>缓存里的石像是哪种石头雕的（前作神居的暗石头 / 圣所的浅石头）。</summary>
    private static bool cacheDark;

    /// <summary>已经雕好的石像（没雕过或没雕成返回 null，不在这里同步雕）。石头换了（前作神居用不了、退回圣所）不算雕好。</summary>
    internal static Sprite? Get(string? key) => key != null && cacheDark == Dark && cache.TryGetValue(key, out Sprite? s) ? s : null;

    /// <summary>
    /// 把这些画都雕好（已经雕好的跳过）。同一页贴图只拷一次；每雕几尊让一帧。alive 返回 false 就停。
    /// 只缓存雕成的：没雕成的这次由调用方换下一个候选，下次搭建还会再试（Codex 审查 v0.6.3 #3/#4）。
    /// </summary>
    internal static IEnumerator CarveAll(IEnumerable<Source> sources, Func<bool> alive)
    {
        float t0 = Time.realtimeSinceStartup;
        // 缓存是另一种石头雕的（前作神居中途用不了，退回圣所）：作废重雕，不拿暗石头的放进圣所（Codex 审查）。
        if (cacheDark != Dark)
        {
            if (cache.Count > 0)
            {
                GodhomePlugin.Log.LogInfo($"[Godhome] 石头换了，石像重雕 / statue stone changed (dark={Dark}), re-carving");
                ReleaseAll();
            }
            cacheDark = Dark;
        }
        var todo = new List<Source>();
        foreach (Source s in sources)
        {
            if (s != null && s.Texture != null && !cache.ContainsKey(s.Key) && !todo.Any(x => x.Key == s.Key))
            {
                todo.Add(s);
            }
        }
        if (todo.Count == 0)
        {
            yield break;
        }
        // 同一页贴图的放在一起，一页只拷一次。
        todo.Sort((a, b) => a.Texture.GetInstanceID().CompareTo(b.Texture.GetInstanceID()));
        int ok = 0, failed = 0;
        RenderTexture? rt = null;
        Texture? page = null;
        RenderTexture? prev = RenderTexture.active;
        try
        {
            float frameStart = Time.realtimeSinceStartup;
            foreach (Source src in todo)
            {
                if (!alive())
                {
                    break;
                }
                Sprite? carved = null;
                try
                {
                    Texture tex = src.Texture;
                    if (page != tex || rt == null)
                    {
                        // 换页：先还旧的并清空引用，新的分配、拷好以后才算数（Codex 审查 v0.6.3 低 #1）。
                        if (rt != null)
                        {
                            RenderTexture.active = prev;
                            RenderTexture.ReleaseTemporary(rt);
                            rt = null;
                            page = null;
                        }
                        RenderTexture next = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                        rt = next;
                        Graphics.Blit(tex, next);
                        page = tex;
                    }
                    carved = Carve(src, rt);
                }
                catch (Exception e)
                {
                    GodhomePlugin.Log.LogWarning($"[Godhome] 雕石像失败 / statue carving failed for {src.Key}: {e.Message}");
                }
                if (carved != null)
                {
                    cache[src.Key] = carved;
                    ok++;
                }
                else
                {
                    failed++;
                }
                // 一帧最多花 12 毫秒（黑幕挡着，但别让游戏卡住）。
                if (Time.realtimeSinceStartup - frameStart > 0.012f)
                {
                    RenderTexture.active = prev;
                    yield return null;
                    frameStart = Time.realtimeSinceStartup;
                }
            }
        }
        finally
        {
            RenderTexture.active = prev;
            if (rt != null)
            {
                RenderTexture.ReleaseTemporary(rt);
            }
        }
        sessOk += ok;
        sessFailed += failed;
        sessRounds++;
        GodhomePlugin.Log.LogInfo($"[Godhome] 雕石像一轮 / carving round: {ok}，失败 / failed {failed}，{Time.realtimeSinceStartup - t0:0.00}s");
    }

    /// <summary>回标题时销毁雕好的贴图（雕像只在大厅里，那时大厅已经卸载）。回大厅之间留着缓存，不用每次重雕。</summary>
    internal static void ReleaseAll()
    {
        if (cache.Count == 0 && made.Count == 0)
        {
            return;
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 石像贴图已销毁 / carved statue textures released ({made.Count})");
        foreach (Sprite? s in cache.Values)
        {
            if (s != null)
            {
                Object.Destroy(s);
            }
        }
        foreach (Texture2D t in made)
        {
            if (t != null)
            {
                Object.Destroy(t);
            }
        }
        cache.Clear();
        made.Clear();
    }

    // ───────────────────────────── 读像素 ─────────────────────────────

    private static Sprite? Carve(Source src, RenderTexture rt, float zoom = 1f)
    {
        Vector2[] verts = src.Verts;
        Vector2[] uvs = src.Uvs;
        int[] tris = src.Tris;
        if (verts.Length == 0 || tris.Length < 3 || uvs.Length < verts.Length)
        {
            return null;
        }
        int texW = rt.width, texH = rt.height;
        // 精灵在贴图上占的那一块（像素）
        float u0 = 1f, v0 = 1f, u1 = 0f, v1 = 0f;
        foreach (Vector2 uv in uvs)
        {
            u0 = Mathf.Min(u0, uv.x);
            v0 = Mathf.Min(v0, uv.y);
            u1 = Mathf.Max(u1, uv.x);
            v1 = Mathf.Max(v1, uv.y);
        }
        int rx = Mathf.Clamp(Mathf.FloorToInt(u0 * texW) - 1, 0, texW - 1);
        int ry = Mathf.Clamp(Mathf.FloorToInt(v0 * texH) - 1, 0, texH - 1);
        int rw = Mathf.Clamp(Mathf.CeilToInt(u1 * texW) + 1, rx + 1, texW) - rx;
        int rh = Mathf.Clamp(Mathf.CeilToInt(v1 * texH) + 1, ry + 1, texH) - ry;
        RenderTexture.active = rt;
        var region = new Texture2D(rw, rh, TextureFormat.RGBA32, false, false);
        Color32[] px;
        try
        {
            region.ReadPixels(new Rect(rx, ry, rw, rh), 0, 0, false);
            px = region.GetPixels32();
        }
        finally
        {
            Object.Destroy(region);
        }

        // 输出大小：按精灵的世界尺寸，最长边不超过 MaxPixels，也不超过原图的清晰度。
        // 清晰度按网格自己的包围盒和它在贴图上占的像素估（不受包围盒里透明留白影响；
        // tk2d 的帧可能在图集里转了 90 度，取两边里长的那条比，取样时按三角形自动转回来）（Codex 审查 v0.6.3 低）。
        Bounds b = src.Bounds;
        float bw = Mathf.Max(0.01f, b.size.x), bh = Mathf.Max(0.01f, b.size.y);
        Vector2 vmin = verts[0], vmax = verts[0];
        foreach (Vector2 v in verts)
        {
            vmin = Vector2.Min(vmin, v);
            vmax = Vector2.Max(vmax, v);
        }
        float meshLong = Mathf.Max(0.01f, Mathf.Max(vmax.x - vmin.x, vmax.y - vmin.y));
        float srcPpu = Mathf.Max(Mathf.Max((u1 - u0) * texW, (v1 - v0) * texH) / meshLong, 1f);
        float ppu = Mathf.Min(srcPpu, MaxPixels / Mathf.Max(bw, bh));
        // 原画太小的（有的立绘只有一百来像素）放大取样到最少 MinPixels：双线性放大是糊一点，但不会一格一格的，浮雕也有地方刻。
        // 按实心部分算：第一遍雕完实心部分还不够大（画在一大块透明里），按比例放大重雕一遍（zoom）。
        ppu = Mathf.Max(ppu, MinPixels / Mathf.Max(bw, bh)) * zoom;
        int w = Mathf.CeilToInt(bw * ppu) + Pad * 2;
        int h = Mathf.CeilToInt(bh * ppu) + Pad * 2;
        var r = new float[w * h];
        var g = new float[w * h];
        var bl = new float[w * h];
        var a = new float[w * h];
        Rasterize(verts, uvs, tris, b.min, ppu, texW, texH, rx, ry, rw, rh, px, w, h, r, g, bl, a, src.Premultiplied);

        Color32[] full = Dark ? DarkStone(r, g, bl, a, w, h, src.Name.GetHashCode()) : Stone(r, g, bl, a, w, h, src.Name.GetHashCode());
        if (src.Pair)
        {
            // 双生：同一尊再雕一个镜像的放在右后方（暗一些），前面那尊位置不变（轴心不用改）。
            full = PairUp(full, ref w, h);
        }
        // 裁到实心的部分（留 2 像素边）：摆放按包围盒算，这样石像的最低点正好落在底座上，不会悬空。
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (full[y * w + x].a >= 24)
                {
                    x0 = Mathf.Min(x0, x);
                    x1 = Mathf.Max(x1, x);
                    y0 = Mathf.Min(y0, y);
                    y1 = Mathf.Max(y1, y);
                }
            }
        }
        if (x1 < 0)
        {
            return null;
        }
        x0 = Mathf.Max(0, x0 - 2);
        y0 = Mathf.Max(0, y0 - 2);
        x1 = Mathf.Min(w - 1, x1 + 2);
        y1 = Mathf.Min(h - 1, y1 + 2);
        int tw = x1 - x0 + 1, th = y1 - y0 + 1;
        int solidLong = Mathf.Max(tw, th);
        if (zoom == 1f && solidLong < MinPixels * 0.85f)
        {
            return Carve(src, rt, Mathf.Min(4f, MinPixels / (float)Mathf.Max(1, solidLong)));
        }
        var outPx = new Color32[tw * th];
        for (int y = 0; y < th; y++)
        {
            Array.Copy(full, (y + y0) * w + x0, outPx, y * tw, tw);
        }
        var tex = new Texture2D(tw, th, TextureFormat.RGBA32, false, false)
        {
            name = "Godhome Statue " + src.Key,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.DontSave,
        };
        tex.SetPixels32(outPx);
        tex.Apply(false, true);
        made.Add(tex);
        // 轴心放在原精灵的原点上（和原立绘的世界位置一致）；摆放代码按包围盒算，不依赖它。
        float ox = Pad - b.min.x * ppu - x0, oy = Pad - b.min.y * ppu - y0;
        Sprite sp = Sprite.Create(tex, new Rect(0, 0, tw, th), new Vector2(ox / tw, oy / th), ppu, 0, SpriteMeshType.FullRect);
        sp.name = "Statue " + src.Key;
        return sp;
    }

    /// <summary>
    /// 一前一后两尊：画布加宽一半，后面那尊是镜像、压暗、往右挪半个身位，前面那尊盖在上面（按透明度叠）。
    /// 前面那尊在画布里的位置不变。
    /// </summary>
    private static Color32[] PairUp(Color32[] one, ref int w, int h)
    {
        int shift = w / 2;
        int w2 = w + shift;
        var outPx = new Color32[w2 * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Color32 c = one[y * w + (w - 1 - x)];
                if (c.a == 0)
                {
                    continue;
                }
                outPx[y * w2 + x + shift] = new Color32((byte)(c.r * 0.72f), (byte)(c.g * 0.72f), (byte)(c.b * 0.74f), c.a);
            }
        }
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Color32 f = one[y * w + x];
                if (f.a == 0)
                {
                    continue;
                }
                int k = y * w2 + x;
                Color32 d = outPx[k];
                float fa = f.a / 255f;
                float da = d.a / 255f * (1f - fa);
                float oa = fa + da;
                if (oa <= 0f)
                {
                    continue;
                }
                outPx[k] = new Color32(
                    (byte)((f.r * fa + d.r * da) / oa),
                    (byte)((f.g * fa + d.g * da) / oa),
                    (byte)((f.b * fa + d.b * da) / oa),
                    (byte)(oa * 255f));
            }
        }
        w = w2;
        return outPx;
    }

    /// <summary>按精灵网格的三角形，把图集上的像素（双线性取样）画到输出格子里。</summary>
    private static void Rasterize(Vector2[] verts, Vector2[] uvs, int[] tris, Vector3 min, float ppu,
        int texW, int texH, int rx, int ry, int rw, int rh, Color32[] px, int w, int h,
        float[] r, float[] g, float[] b, float[] a, bool premultiplied)
    {
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
            Vector2 p0 = (verts[i0] - (Vector2)min) * ppu + new Vector2(Pad, Pad);
            Vector2 p1 = (verts[i1] - (Vector2)min) * ppu + new Vector2(Pad, Pad);
            Vector2 p2 = (verts[i2] - (Vector2)min) * ppu + new Vector2(Pad, Pad);
            Vector2 q0 = new(uvs[i0].x * texW - rx, uvs[i0].y * texH - ry);
            Vector2 q1 = new(uvs[i1].x * texW - rx, uvs[i1].y * texH - ry);
            Vector2 q2 = new(uvs[i2].x * texW - rx, uvs[i2].y * texH - ry);
            float area = (p1.x - p0.x) * (p2.y - p0.y) - (p2.x - p0.x) * (p1.y - p0.y);
            if (Mathf.Abs(area) < 1e-6f)
            {
                continue;
            }
            int xMin = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p0.x, Mathf.Min(p1.x, p2.x))));
            int xMax = Mathf.Min(w - 1, Mathf.CeilToInt(Mathf.Max(p0.x, Mathf.Max(p1.x, p2.x))));
            int yMin = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p0.y, Mathf.Min(p1.y, p2.y))));
            int yMax = Mathf.Min(h - 1, Mathf.CeilToInt(Mathf.Max(p0.y, Mathf.Max(p1.y, p2.y))));
            for (int y = yMin; y <= yMax; y++)
            {
                for (int x = xMin; x <= xMax; x++)
                {
                    float cx = x + 0.5f, cy = y + 0.5f;
                    float w0 = ((p1.x - cx) * (p2.y - cy) - (p2.x - cx) * (p1.y - cy)) / area;
                    float w1 = ((p2.x - cx) * (p0.y - cy) - (p0.x - cx) * (p2.y - cy)) / area;
                    float w2 = 1f - w0 - w1;
                    if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f)
                    {
                        continue;
                    }
                    float sx = w0 * q0.x + w1 * q1.x + w2 * q2.x - 0.5f;
                    float sy = w0 * q0.y + w1 * q1.y + w2 * q2.y - 0.5f;
                    Sample(px, rw, rh, sx, sy, premultiplied, out float cr, out float cg, out float cb, out float ca);
                    int k = y * w + x;
                    r[k] = cr;
                    g[k] = cg;
                    b[k] = cb;
                    a[k] = ca;
                }
            }
        }
    }

    private static void Sample(Color32[] px, int rw, int rh, float x, float y, bool premultiplied, out float r, out float g, out float b, out float a)
    {
        int x0 = Mathf.Clamp(Mathf.FloorToInt(x), 0, rw - 1), y0 = Mathf.Clamp(Mathf.FloorToInt(y), 0, rh - 1);
        int x1 = Mathf.Min(x0 + 1, rw - 1), y1 = Mathf.Min(y0 + 1, rh - 1);
        float fx = Mathf.Clamp01(x - x0), fy = Mathf.Clamp01(y - y0);
        Color32 c00 = px[y0 * rw + x0], c10 = px[y0 * rw + x1], c01 = px[y1 * rw + x0], c11 = px[y1 * rw + x1];
        float w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
        if (premultiplied)
        {
            // 预乘透明度的贴图：颜色已经乘过透明度，按普通权重插值后只除一次透明度（Codex 审查 v0.6.3 #5）。
            float pa = (c00.a * w00 + c10.a * w10 + c01.a * w01 + c11.a * w11) / 255f;
            a = pa;
            if (pa <= 0.002f)
            {
                r = g = b = 0f;
                return;
            }
            r = Mathf.Clamp01((c00.r * w00 + c10.r * w10 + c01.r * w01 + c11.r * w11) / 255f / pa);
            g = Mathf.Clamp01((c00.g * w00 + c10.g * w10 + c01.g * w01 + c11.g * w11) / 255f / pa);
            b = Mathf.Clamp01((c00.b * w00 + c10.b * w10 + c01.b * w01 + c11.b * w11) / 255f / pa);
            return;
        }
        // 按透明度加权（避免透明像素的颜色渗进边缘）
        float a00 = c00.a * w00, a10 = c10.a * w10, a01 = c01.a * w01, a11 = c11.a * w11;
        float sa = a00 + a10 + a01 + a11;
        a = sa / 255f;
        if (sa <= 0f)
        {
            r = g = b = 0f;
            return;
        }
        r = (c00.r * a00 + c10.r * a10 + c01.r * a01 + c11.r * a11) / sa / 255f;
        g = (c00.g * a00 + c10.g * a10 + c01.g * a01 + c11.g * a11) / sa / 255f;
        b = (c00.b * a00 + c10.b * a10 + c01.b * a01 + c11.b * a11) / sa / 255f;
    }

    // ───────────────────────────── 雕刻 ─────────────────────────────

    private static Color32[] Stone(float[] r, float[] g, float[] b, float[] a, int w, int h, int seed)
    {
        int n = w * h;
        float s = Mathf.Max(w, h) / 512f;
        var lum = new float[n];
        var line = new float[n];
        var lumA = new float[n];
        for (int i = 0; i < n; i++)
        {
            lum[i] = 0.30f * r[i] + 0.59f * g[i] + 0.11f * b[i];
            line[i] = (1f - Smooth(0.06f, 0.30f, lum[i])) * Smooth(0.3f, 0.9f, a[i]);
            lumA[i] = lum[i] * a[i];
        }
        float[] lineBlur = Blur(line, w, h, 3f * s);
        float[] aBig = Blur(a, w, h, 14f * s);
        float[] aMid = Blur(a, w, h, 4f * s);
        float[] aAo = Blur(a, w, h, 5f * s);
        float[] aRim = Blur(a, w, h, 1.5f * s);
        float[] lumBlur = Blur(lumA, w, h, 2f * s);
        var height = new float[n];
        var groove = new float[n];
        var tone = new float[n];
        for (int i = 0; i < n; i++)
        {
            float area = Smooth(0.55f, 0.9f, lineBlur[i]);
            groove[i] = line[i] * (1f - 0.75f * area);
            float t = 0.6f + 0.3f * (lum[i] - 0.5f);
            tone[i] = t * (1f - area) + 0.32f * area;
            height[i] = aBig[i] + aMid[i] * 0.5f + lumBlur[i] * 0.3f - groove[i] * 0.3f;
        }
        // 左上方的光（贴图 y 向上）
        const float lx = -0.55f, ly = 0.65f, lz = 0.52f;
        float ln = Mathf.Sqrt(lx * lx + ly * ly + lz * lz);
        const float k = 9f;
        var rng = new System.Random(seed);
        float[] grain = Noise(w, h, rng);
        var outPx = new Color32[n];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (a[i] <= 0.002f)
                {
                    outPx[i] = new Color32(0, 0, 0, 0);
                    continue;
                }
                float gx = (height[y * w + Mathf.Min(x + 1, w - 1)] - height[y * w + Mathf.Max(x - 1, 0)]) * 0.5f;
                float gy = (height[Mathf.Min(y + 1, h - 1) * w + x] - height[Mathf.Max(y - 1, 0) * w + x]) * 0.5f;
                float nx = -gx * k, ny = -gy * k, nz = 1f;
                float nl = Mathf.Sqrt(nx * nx + ny * ny + nz * nz);
                float diff = Mathf.Clamp01((nx * lx + ny * ly + nz * lz) / (nl * ln));
                // 浮雕明暗拉开一些（远看也能看出是有体积的石头，不是平涂）。
                float shade = 0.3f + 0.9f * diff;
                shade *= 0.55f + 0.45f * Smooth(0.25f, 0.95f, aAo[i]);
                float v = tone[i] * shade * (0.88f + 0.24f * grain[i]);
                v *= 1f - 0.6f * groove[i];
                float egx = (aRim[y * w + Mathf.Min(x + 1, w - 1)] - aRim[y * w + Mathf.Max(x - 1, 0)]) * 0.5f;
                float egy = (aRim[Mathf.Min(y + 1, h - 1) * w + x] - aRim[Mathf.Max(y - 1, 0) * w + x]) * 0.5f;
                float rim = Mathf.Clamp01((-egx * lx - egy * ly) * 6f) * a[i];
                v = Mathf.Clamp01(v + 0.35f * rim * (1f - v));
                // 石头没有半透明的羽毛、烟雾：把透明度收紧成实心的边（原画柔和的外缘变成石料的轮廓）。
                float alpha = Smooth(0.1f, 0.55f, a[i]);
                // 中性偏冷的灰石（圣所的光是暖的，暖色石料在里面会变成奶黄色）。
                outPx[i] = new Color32((byte)(v * 0.88f * 255f), (byte)(v * 0.88f * 255f), (byte)(v * 0.9f * 255f), (byte)(alpha * 255f));
            }
        }
        return outPx;
    }

    /// <summary>
    /// 前作手办屋的深色石像（看前作原图和实机截图）：身体几乎是黑的，原画的轮廓线全黑；
    /// 迎着头顶光的面、上沿的外缘、刻痕的迎光一侧是浅灰的细亮边，亮处带点石头颗粒；整体上亮下暗，略偏暖。
    /// 亮度范围对着前作原图量的：中位数很低，最亮的一小部分到 0.3 左右，交给场景里的光去照亮。
    /// </summary>
    private static Color32[] DarkStone(float[] r, float[] g, float[] b, float[] a, int w, int h, int seed)
    {
        int n = w * h;
        float s = Mathf.Max(w, h) / 512f;
        var lum = new float[n];
        var line = new float[n];
        var lumA = new float[n];
        var solid = new List<float>();
        for (int i = 0; i < n; i++)
        {
            lum[i] = 0.30f * r[i] + 0.59f * g[i] + 0.11f * b[i];
            line[i] = (1f - Smooth(0.06f, 0.30f, lum[i])) * Smooth(0.3f, 0.9f, a[i]);
            lumA[i] = lum[i] * a[i];
            if (a[i] > 0.5f)
            {
                solid.Add(lum[i]);
            }
        }
        // 原画自己的明暗（白面具、亮色甲壳）按这尊的范围归一，只留一点点在石料里，认得出是谁。
        float lo = 0f, hi = 1f;
        if (solid.Count > 16)
        {
            solid.Sort();
            lo = solid[(int)(solid.Count * 0.05f)];
            hi = Mathf.Max(lo + 0.05f, solid[(int)(solid.Count * 0.95f)]);
        }
        float[] lineBlur = Blur(line, w, h, 3f * s);
        float[] aBig = Blur(a, w, h, 14f * s);
        float[] aMid = Blur(a, w, h, 4f * s);
        float[] aRim = Blur(a, w, h, 1.5f * s);
        float[] lumBlur = Blur(lumA, w, h, 2f * s);
        var height = new float[n];
        var groove = new float[n];
        for (int i = 0; i < n; i++)
        {
            float area = Smooth(0.55f, 0.9f, lineBlur[i]);
            groove[i] = line[i] * (1f - 0.75f * area);
            height[i] = aBig[i] + aMid[i] * 0.5f + lumBlur[i] * 0.35f - groove[i] * 0.35f;
        }
        // 光从头顶来（光柱），略偏左、朝前（贴图 y 向上）。
        const float lx = -0.25f, ly = 0.85f, lz = 0.45f;
        float ln = Mathf.Sqrt(lx * lx + ly * ly + lz * lz);
        const float k = 9f;
        var rng = new System.Random(seed);
        float[] grain = Noise(w, h, rng);
        var outPx = new Color32[n];
        for (int y = 0; y < h; y++)
        {
            float up = Mathf.Lerp(0.7f, 1.12f, y / (float)Mathf.Max(1, h - 1));
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (a[i] <= 0.002f)
                {
                    outPx[i] = new Color32(0, 0, 0, 0);
                    continue;
                }
                float gx = (height[y * w + Mathf.Min(x + 1, w - 1)] - height[y * w + Mathf.Max(x - 1, 0)]) * 0.5f;
                float gy = (height[Mathf.Min(y + 1, h - 1) * w + x] - height[Mathf.Max(y - 1, 0) * w + x]) * 0.5f;
                float nx = -gx * k, ny = -gy * k, nz = 1f;
                float nl = Mathf.Sqrt(nx * nx + ny * ny + nz * nz);
                float diff = Mathf.Clamp01((nx * lx + ny * ly + nz * lz) / (nl * ln));
                float egx = (aRim[y * w + Mathf.Min(x + 1, w - 1)] - aRim[y * w + Mathf.Max(x - 1, 0)]) * 0.5f;
                float egy = (aRim[Mathf.Min(y + 1, h - 1) * w + x] - aRim[Mathf.Max(y - 1, 0) * w + x]) * 0.5f;
                // 上沿迎光的外缘一道亮边；别的外缘一道弱一些的边（前作雕像的轮廓都有一圈浅色描边）。
                float rimUp = Mathf.Clamp01((-egx * lx - egy * ly) * 6f) * a[i];
                float edge = Mathf.Clamp01(Mathf.Sqrt(egx * egx + egy * egy) * 4f) * a[i];
                float own = Mathf.Clamp01((lum[i] - lo) / (hi - lo));
                // 系数按前作原图量的调过（2026-11-24 第二版）：前作中位数 0.00~0.01、亮的 20% 在 0.08~0.13 以上、
                // 亮的 5% 在 0.23~0.32 以上；第一版身体偏亮（0.03~0.05）、亮边偏弱（5% 只到 0.10~0.14）。
                float lit = Mathf.Pow(diff, 4f);
                float tex = 0.8f + 0.4f * grain[i];
                float v = 0.004f + 0.02f * own
                    + (0.3f * lit * (0.6f + 0.4f * own) + 0.45f * rimUp + 0.18f * edge) * tex;
                v *= up;
                v *= 1f - 0.85f * groove[i];
                v = Mathf.Clamp(v, 0f, 0.5f);
                float alpha = Smooth(0.1f, 0.55f, a[i]);
                outPx[i] = new Color32((byte)(v * 255f), (byte)(v * 0.93f * 255f), (byte)(v * 0.87f * 255f), (byte)(alpha * 255f));
            }
        }
        return outPx;
    }

    private static float Smooth(float e0, float e1, float x)
    {
        float t = Mathf.Clamp01((x - e0) / (e1 - e0));
        return t * t * (3f - 2f * t);
    }

    /// <summary>近似高斯模糊：三次可分离的盒式模糊。</summary>
    private static float[] Blur(float[] src, int w, int h, float sigma)
    {
        if (sigma < 0.5f)
        {
            return (float[])src.Clone();
        }
        int radius = Mathf.Max(1, Mathf.RoundToInt(sigma * 0.9f));
        float[] a = (float[])src.Clone();
        float[] tmp = new float[a.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxH(a, tmp, w, h, radius);
            BoxV(tmp, a, w, h, radius);
        }
        return a;
    }

    private static void BoxH(float[] src, float[] dst, int w, int h, int r)
    {
        float inv = 1f / (r * 2 + 1);
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            float sum = 0f;
            for (int x = -r; x <= r; x++)
            {
                sum += src[row + Mathf.Clamp(x, 0, w - 1)];
            }
            for (int x = 0; x < w; x++)
            {
                dst[row + x] = sum * inv;
                sum += src[row + Mathf.Min(x + r + 1, w - 1)] - src[row + Mathf.Max(x - r, 0)];
            }
        }
    }

    private static void BoxV(float[] src, float[] dst, int w, int h, int r)
    {
        float inv = 1f / (r * 2 + 1);
        for (int x = 0; x < w; x++)
        {
            float sum = 0f;
            for (int y = -r; y <= r; y++)
            {
                sum += src[Mathf.Clamp(y, 0, h - 1) * w + x];
            }
            for (int y = 0; y < h; y++)
            {
                dst[y * w + x] = sum * inv;
                sum += src[Mathf.Min(y + r + 1, h - 1) * w + x] - src[Mathf.Max(y - r, 0) * w + x];
            }
        }
    }

    /// <summary>三层值噪声（石头颗粒），0..1。</summary>
    private static float[] Noise(int w, int h, System.Random rng)
    {
        var outN = new float[w * h];
        (int Cell, float Amp)[] layers = { (8, 0.5f), (24, 0.3f), (64, 0.2f) };
        foreach ((int cell, float amp) in layers)
        {
            int gw = w / cell + 2, gh = h / cell + 2;
            var grid = new float[gw * gh];
            for (int i = 0; i < grid.Length; i++)
            {
                grid[i] = (float)rng.NextDouble();
            }
            for (int y = 0; y < h; y++)
            {
                float fy = y / (float)cell;
                int y0 = (int)fy;
                float ty = fy - y0;
                ty = ty * ty * (3f - 2f * ty);
                for (int x = 0; x < w; x++)
                {
                    float fx = x / (float)cell;
                    int x0 = (int)fx;
                    float tx = fx - x0;
                    tx = tx * tx * (3f - 2f * tx);
                    float a0 = Mathf.Lerp(grid[y0 * gw + x0], grid[y0 * gw + x0 + 1], tx);
                    float a1 = Mathf.Lerp(grid[(y0 + 1) * gw + x0], grid[(y0 + 1) * gw + x0 + 1], tx);
                    outN[y * w + x] += amp * Mathf.Lerp(a0, a1, ty);
                }
            }
        }
        return outN;
    }
}
