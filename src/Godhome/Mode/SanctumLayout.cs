using System.Collections;
using UnityEngine;

namespace Godhome;

/// <summary>
/// [Godhome] 织忆圣所和忆像长廊的画面：全部用游戏图集里的精灵（SanctumArt），按坐标摆。
/// 风格：远古织者的黑银遗迹（织巢 weave、织者记忆 memory_weaver / memory_first_weaver），
/// 加上摇篮（cradle）的灵丝织球和白光、暖金色的光晕和光柱 —— 辉煌、神圣、古老、神秘。
/// z 越大越远（透视镜头，远处有视差、看起来更小）；负的 z 在大黄蜂前面（前景剪影）。
/// </summary>
internal static class SanctumLayout
{
    internal static int Placed { get; private set; }

    /// <summary>拉杆在雕像中心右边多远（拉杆受击框也按它放，拉杆的图没借到也一样能敲）。</summary>
    internal const float LeverOffset = 3.3f;

    // 色调
    internal static readonly Color Stone = new(0.60f, 0.59f, 0.67f);
    internal static readonly Color StoneDim = new(0.36f, 0.36f, 0.44f);
    internal static readonly Color StoneFar = new(0.10f, 0.105f, 0.14f);
    internal static readonly Color Black = new(0.012f, 0.012f, 0.018f);
    internal static Color Gold(float a) => new(1f, 0.83f, 0.5f, a);
    internal static Color Silk(float a) => new(0.9f, 0.93f, 1f, a);

    // ───────────────────────────── 摆放工具 ─────────────────────────────

    /// <summary>放一个精灵，(x, y) 是它看得见部分的中心。找不到精灵返回 null（日志在 SanctumArt 里记一次）。</summary>
    internal static SpriteRenderer? Put(Transform parent, string atlas, string name, float x, float y, float z, float sx = 1f, float sy = 0f, Color? tint = null, bool flip = false, float rot = 0f)
    {
        Sprite? sp = SanctumArt.Get(atlas, name);
        if (sp == null)
        {
            return null;
        }
        if (sy == 0f)
        {
            sy = sx;
        }
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localScale = new Vector3(flip ? -sx : sx, sy, 1f);
        go.transform.rotation = Quaternion.Euler(0f, 0f, rot);
        Vector3 c = sp.bounds.center;
        go.transform.position = new Vector3(x - c.x * (flip ? -sx : sx), y - c.y * sy, z);
        SpriteRenderer sr = Sanctum.Paint(go, sp, tint ?? Color.white);
        Placed++;
        return sr;
    }

    /// <summary>底边对齐到 bottom。</summary>
    internal static SpriteRenderer? PutBottom(Transform parent, string atlas, string name, float x, float bottom, float z, float sx = 1f, float sy = 0f, Color? tint = null, bool flip = false)
    {
        Sprite? sp = SanctumArt.Get(atlas, name);
        if (sp == null)
        {
            return null;
        }
        float h = sp.bounds.size.y * (sy == 0f ? sx : sy);
        return Put(parent, atlas, name, x, bottom + h / 2f, z, sx, sy, tint, flip);
    }

    /// <summary>顶边对齐到 top。</summary>
    internal static SpriteRenderer? PutTop(Transform parent, string atlas, string name, float x, float top, float z, float sx = 1f, float sy = 0f, Color? tint = null, bool flip = false)
    {
        Sprite? sp = SanctumArt.Get(atlas, name);
        if (sp == null)
        {
            return null;
        }
        float h = sp.bounds.size.y * (sy == 0f ? sx : sy);
        return Put(parent, atlas, name, x, top - h / 2f, z, sx, sy, tint, flip);
    }

    private static T? With<T>(SpriteRenderer? sr) where T : Component => sr != null ? sr.gameObject.AddComponent<T>() : null;

    internal static void Pulse(SpriteRenderer? sr, float period, float min = 0.6f, float max = 1f, float scaleAmp = 0f, float phase = 0f)
    {
        SanctumPulse? p = With<SanctumPulse>(sr);
        if (p != null)
        {
            p.Period = period;
            p.MinAlpha = min;
            p.MaxAlpha = max;
            p.ScaleAmp = scaleAmp;
            p.Phase = phase;
        }
    }

    internal static void Spin(SpriteRenderer? sr, float dps)
    {
        SanctumSpin? s = With<SanctumSpin>(sr);
        if (s != null)
        {
            s.DegreesPerSecond = dps;
        }
    }

    /// <summary>一块纯色（大面积的暗底，不算“画”的东西）。</summary>
    internal static void Plane(Transform parent, string name, float x0, float y0, float x1, float y1, float z, Color c)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = new Vector3((x0 + x1) / 2f, (y0 + y1) / 2f, z);
        go.transform.localScale = new Vector3(x1 - x0, y1 - y0, 1f);
        Sanctum.Paint(go, Sanctum.White, c);
    }

    internal static void Motes(Transform parent, Rect area, float z, Color tint, int count, float min, float max, float speed)
    {
        var go = new GameObject("Motes");
        go.transform.SetParent(parent, false);
        SanctumMotes m = go.AddComponent<SanctumMotes>();
        m.Area = area;
        m.Z = z;
        m.Sprite = SanctumArt.Get("core_glows", "lantern_glow_000");
        m.Tint = tint;
        m.Count = count;
        m.MinSize = min;
        m.MaxSize = max;
        m.Speed = speed;
        m.Material = Sanctum.RefMaterial;
        m.SortingLayer = Sanctum.RefLayer;
        m.SortingOrder = Sanctum.RefOrder;
    }

    // ───────────────────────────── 共用的背景 ─────────────────────────────

    /// <summary>后墙：织者遗迹的肋纹墙板铺满一片。</summary>
    internal static void BackWall(Transform t, float x0, float x1, float y0, float y1, float z, Color tint)
    {
        const float s = 1.2f;
        const float w = 4.2f * s - 0.05f;
        const float h = 5.81f * s - 0.05f;
        for (float y = y0; y < y1; y += h)
        {
            for (float x = x0; x < x1; x += w)
            {
                Put(t, "weave", "crest_shrine_mid_wall_type_02", x + w / 2f, y + h / 2f, z, s, 0f, tint);
            }
        }
    }

    /// <summary>地面的织者石带（上沿和地面齐）。</summary>
    internal static void FloorStrip(Transform t, float x0, float x1, float floor, Color tint)
    {
        for (float x = x0; x < x1; x += 15.4f)
        {
            Put(t, "weave", "Crest_Shrine_0000_1_floor_wide", x + 7.7f, floor - 0.42f, 0.25f, 1f, 1f, tint);
        }
    }

    // ───────────────────────────── 圣所本体 ─────────────────────────────

    internal static void BuildHub(Transform art, float f)
    {
        Placed = 0;
        Transform t = new GameObject("Hub").transform;
        t.SetParent(art, false);
        float ax = Sanctum.AltarX;

        // 最远：一整块深色天幕（圣所和长廊共用），暖色的远光。
        Plane(art, "Sky", -60f, f - 40f, 420f, f + 70f, 45f, new Color(0.010f, 0.011f, 0.018f));
        Put(t, "core_glows", "white_fader", ax, f + 12f, 32f, 9f, 6f, new Color(0.42f, 0.34f, 0.22f, 0.32f));
        Put(t, "core_glows", "Fog", ax - 16f, f + 2f, 28f, 9f, 3f, new Color(0.35f, 0.38f, 0.48f, 0.10f));
        Put(t, "core_glows", "Fog", ax + 18f, f + 15f, 28f, 9f, 3f, new Color(0.35f, 0.38f, 0.48f, 0.08f));
        // 远处的织者尖塔剪影
        foreach (float x in new[] { -6f, 14f, 48f, 68f })
        {
            PutBottom(t, "weave", "weaver_shrine_bg_spires", x, f - 3f, 24f, 2.4f, 0f, StoneFar, flip: x > ax);
        }

        // 后墙和三扇窗：中间是青铜大窗（光从后面透出来），两侧是织者高窗。
        BackWall(t, -10f, 76f, f - 4f, f + 26f, 12f, StoneDim);
        Put(t, "core_glows", "white_light", ax, f + 11f, 11.6f, 3.4f, 3.4f, Gold(0.55f));
        Put(t, "bshrine_window", "Hornet_Core__0075_bell_room_window", ax, f + 9.5f, 11f, 0.9f, 0f, new Color(0.9f, 0.78f, 0.55f));
        foreach (float x in new[] { 9.5f, 52.5f })
        {
            Put(t, "core_glows", "white_light", x, f + 10f, 10.6f, 1.7f, 2.4f, Silk(0.22f));
            Put(t, "weave", "weaver_window_medium_0000_1", x, f + 10f, 10f, 0.72f, 0f, new Color(0.46f, 0.46f, 0.54f), flip: x > ax);
        }

        // 光柱：从穹顶斜照到织机上。
        float[] dx = { -5f, 0f, 5f };
        float[] rot = { -14f, 0f, 14f };
        for (int i = 0; i < 3; i++)
        {
            SpriteRenderer? beam = Put(t, "core_glows", "water_fog", ax + dx[i], f + 13f, 7f, 7.5f, 6.8f, Gold(i == 1 ? 0.16f : 0.11f), rot: rot[i]);
            Pulse(beam, 7f + i, 0.55f, 1f, 0f, i * 0.31f);
        }

        // 穹顶：中间的大拱、两角的拱肋。
        // （镜头固定在 f+5.8，看得到的范围大约 f-2.5 到 f+14；远的东西会往画面中心收。）
        Put(t, "weave", "weaver_arch_details_0001_1", ax, f + 16f, 5f, 1.4f, 0f, new Color(0.30f, 0.30f, 0.37f));

        // 立柱
        foreach (float x in new[] { 19f, 43f })
        {
            PutBottom(t, "weave", "Loom_Room_0028_12", x, f, 3f, 1.45f, 0f, Stone);
        }
        foreach (float x in new[] { 2.5f, 61.5f })
        {
            PutBottom(t, "weave", "Loom_Room_0025_15", x, f, 3f, 1.5f, 0f, Stone);
        }

        BuildAltar(t, ax, f);

        // 古织者像（织者记忆里的剪影），站在底座上，背后一圈银光。
        // （x=8 原来也有一尊，现在那里是去万神殿前厅的丝门。）
        (float X, string Name, bool Flip)[] elders = { (15.5f, "weaver_memory_silhouettes_0005_1", false), (45f, "weaver_memory_silhouettes_0002_1", true) };
        foreach ((float x, string n, bool flip) in elders)
        {
            SpriteRenderer? plinth = PutBottom(t, "weave", "weaver_heat_lamp_0005_1_plinth", x, f, 2.6f, 0.95f, 0f, new Color(0.36f, 0.36f, 0.42f));
            float top = plinth != null ? plinth.bounds.max.y - 0.2f : f + 2.1f;
            // 背光：织者像是剪影，靠身后的金光勾出轮廓。
            Pulse(Put(t, "core_glows", "white_light", x, top + 3.6f, 2.9f, 1.15f, 1.55f, Gold(0.42f)), 6f, 0.7f, 1f, 0f, x * 0.05f);
            // 原图本身是黑色带高光的织者像，颜色压太暗高光就没了。
            PutBottom(t, "memory_weaver", n, x, top, 2.7f, 1.12f, 0f, new Color(0.82f, 0.8f, 0.78f), flip: flip);
        }

        // 灯：织者的长灯柱，灯头一点暖光。
        foreach (float x in new[] { 23.5f, 38.5f })
        {
            SpriteRenderer? lamp = PutBottom(t, "weave", "junk__0015_weave_lamp", x, f, 1.5f, 1f, 0f, new Color(0.5f, 0.5f, 0.56f));
            float top = lamp != null ? lamp.bounds.max.y - 0.35f : f + 7.2f;
            Put(t, "core_glows", "lantern_glow_000", x, top, 1.45f, 0.75f, 0f, Gold(0.9f));
            Pulse(Put(t, "core_glows", "white_light", x, top, 1.6f, 0.45f, 0f, Gold(0.4f)), 2.3f, 0.7f, 1f, 0.06f, x * 0.1f);
        }

        // 地面
        FloorStrip(t, -10f, 76f, f, new Color(0.5f, 0.5f, 0.57f));

        // 前景：两侧的黑色柱影、垂下的淡淡灵丝、底边的暗色碎石。
        // （前景离镜头近，视差更大：要放在镜头走得到的范围里面才看得到。）
        PutBottom(t, "weave", "Loom_wall_0000_4", 4f, f - 6f, -5f, 2.6f, 0f, Black);
        PutBottom(t, "weave", "Loom_wall_0000_4", 60f, f - 6f, -5f, 2.6f, 0f, Black, flip: true);
        foreach (float x in new[] { 8f, 27f, 36f, 56f })
        {
            Pulse(PutTop(t, "threads", "Thick_Silk_Vine_ambient", x, f + 16f, -3.5f, 1f, 0.8f, Silk(0.16f), flip: x > ax), 6f, 0.5f, 1f, 0f, x * 0.07f);
        }

        Motes(t, new Rect(1f, f, 62f, 20f), 2f, Gold(0.55f), 45, 0.1f, 0.22f, 0.32f);
        Motes(t, new Rect(1f, f, 62f, 20f), 4f, Silk(0.35f), 20, 0.08f, 0.16f, 0.22f);
    }

    /// <summary>“织忆之机”：长椅身后，摇篮的灵丝织球悬在织者底座上，身后是织者符文和放射的纹章光，上方垂下灵丝。</summary>
    private static void BuildAltar(Transform t, float ax, float f)
    {
        float cy = f + 10.6f;
        // 最后面：大符文、放射纹章
        Pulse(Put(t, "weaver_rune_gate", "B_shrine_weaver_rune_0002_b1", ax, cy + 0.6f, 6.5f, 0.9f, 0f, new Color(1f, 0.92f, 0.76f, 0.42f)), 5f, 0.6f, 1f);
        Pulse(Put(t, "cradle", "Bell_Screen_Icons_0000_crest_glow", ax, cy, 6f, 3.2f, 0f, Gold(0.42f)), 4f, 0.55f, 1f, 0.04f);
        Spin(Put(t, "core_glows", "light_effect_v02", ax, cy, 5.6f, 1.15f, 0f, Gold(0.32f)), 4f);
        // 织球悬在一缕灵丝上；身后几道琴弦一样的经线从穹顶一直垂到台基（在织球后面）。
        PutTop(t, "cradle", "Cradle__0011_web_hang", ax, f + 15.6f, 5.4f, 0.7f, 0f, Silk(0.55f));
        for (int i = -2; i <= 2; i++)
        {
            if (i == 0)
            {
                continue;
            }
            Pulse(PutTop(t, "memory_weaver", "memory_harp_string0000", ax + i * 1.35f, f + 17f, 5.3f, 1f, 2.74f, Silk(0.42f)), 3f + i * 0.4f, 0.4f, 1f, 0f, i * 0.2f);
        }
        // 底座：织者的角形台基 + 摇篮的纹饰托座
        PutBottom(t, "weave", "weaver_shrine_statues_0001_1", ax, f, 3.6f, 1.05f, 0f, new Color(0.42f, 0.41f, 0.46f));
        PutBottom(t, "cradle", "Cradle_Bell_Hub_0006_1", ax, f + 3.3f, 3.4f, 1.05f, 0f, new Color(0.46f, 0.45f, 0.52f));
        // 织球
        // （摇篮的织球原图约 21 单位宽，这里缩到 5 单位左右。）
        SpriteRenderer? orb = Put(t, "cradle", "Cradle__0021_orb_outer", ax, cy, 5f, 0.245f, 0f, new Color(1f, 0.97f, 0.9f, 0.95f));
        Pulse(orb, 6f, 0.85f, 1f, 0.03f);
        Pulse(Put(t, "cradle", "Cradle__0022_orb_silk_mid", ax, cy, 5.1f, 0.3f, 0f, Gold(0.55f)), 3.2f, 0.4f, 1f);
        Spin(Put(t, "cradle", "Cradle__0024_orb_backing", ax, cy, 5.2f, 0.26f, 0f, new Color(1f, 0.9f, 0.75f, 0.35f)), -3f);
        // 地上的光
        Put(t, "weave", "loom_floor_large_glow", ax, f + 0.5f, 0.6f, 1.1f, 0.9f, Gold(0.75f));
        Pulse(Put(t, "cradle", "soul_snare_trap_base_0009_base_rune", ax, f + 0.15f, 0.55f, 1f, 1f, Gold(0.5f)), 4.5f, 0.5f, 1f);
    }

    // ───────────────────────────── 忆像长廊 ─────────────────────────────

    internal static void BuildHall(Transform art, float f)
    {
        Transform t = new GameObject("Hall").transform;
        t.SetParent(art, false);
        float l = Sanctum.HallLeft, r = Sanctum.HallRight, u = Sanctum.UpperY;
        // 楼板铺到右墙（右端原来的台阶删了）。
        float slabR = r;
        int n = StatueCatalog.All.Count;
        int lower = (n + 1) / 2;
        float first = l + Sanctum.FirstStatue;

        // 远景
        for (float x = l - 10f; x < r + 20f; x += 22f)
        {
            PutBottom(t, "weave", "weaver_shrine_bg_spires", x, f - 3f, 24f, 2.4f, 0f, StoneFar, flip: ((int)x / 22) % 2 == 0);
        }
        for (float x = l; x < r + 10f; x += 40f)
        {
            Put(t, "core_glows", "white_fader", x + 20f, f + 14f, 32f, 7f, 6f, new Color(0.38f, 0.31f, 0.21f, 0.22f));
        }
        BackWall(t, l - 12f, r + 12f, f - 4f, u + 18f, 12f, StoneDim);

        // 每个雕像位后面一扇高窗，透出暖光；雕像之间立柱、挂灯。
        for (int floor = 0; floor < 2; floor++)
        {
            float fy = floor == 0 ? f : u;
            int count = floor == 0 ? lower : n - lower;
            float height = floor == 0 ? Sanctum.UpperRise - Sanctum.SlabThick : 14.5f;
            for (int k = 0; k < count; k++)
            {
                float sx = first + k * Sanctum.Spacing;
                Put(t, "core_glows", "white_light", sx, fy + 7f, 10.5f, 1.05f, 1.6f, Gold(0.2f));
                Put(t, "weave", "weaver_window_medium_0000_1", sx, fy + 7f, 10f, 0.56f, 0f, new Color(0.42f, 0.42f, 0.5f), flip: k % 2 == 1);
            }
            for (int k = 0; k <= count; k++)
            {
                float px = first - Sanctum.Spacing / 2f + k * Sanctum.Spacing;
                PutBottom(t, "weave", "Loom_Room_0026_14", px, fy, 3f, 0.88f, height / 9.16f * 0.98f, Stone);
                float ly = fy + height * 0.62f;
                Put(t, "weave", "loom_break_lamp", px, ly, 2.8f, 0.55f, 0f, new Color(0.6f, 0.6f, 0.66f));
                Pulse(Put(t, "core_glows", "lantern_glow_000", px, ly - 0.15f, 2.7f, 0.55f, 0f, Gold(0.75f)), 2.6f, 0.65f, 1f, 0.05f, k * 0.37f);
            }
            // 天花：上层是大拱，下层是楼板底下的小拱
            for (int k = 0; k < count; k++)
            {
                float sx = first + k * Sanctum.Spacing;
                if (floor == 1)
                {
                    PutTop(t, "weave", "weaver_arch_details_0000_1", sx, u + 15f, 4f, 0.9f, 0f, new Color(0.28f, 0.28f, 0.34f));
                }
                else
                {
                    PutTop(t, "weave", "loom_arch_tiny_flat", sx, u - Sanctum.SlabThick, 2f, 1.25f, 0f, new Color(0.32f, 0.32f, 0.38f));
                }
            }
            Motes(t, new Rect(l + 1f, fy, (floor == 0 ? r : slabR) - l - 2f, floor == 0 ? 12f : 13f), 2f, Gold(0.5f), 60, 0.1f, 0.2f, 0.3f);
        }

        // 地面、楼板
        FloorStrip(t, l - 6f, r + 6f, f, new Color(0.5f, 0.5f, 0.57f));
        FloorStrip(t, l, slabR, u, new Color(0.5f, 0.5f, 0.57f));
        for (float x = l; x < slabR - 3f; x += 7f)
        {
            PutTop(t, "weave", "Loom_Room_0020_20", x + 3.5f, u - Sanctum.SlabThick + 0.15f, 0.3f, 1f, 1f, new Color(0.36f, 0.36f, 0.43f));
        }
        // 两端的发光纹章
        Pulse(Put(t, "weave", "crest_shrine_shield_0001_1", l + 1.6f, f + 6.5f, 1.5f, 0.75f, 0f, Gold(0.55f)), 5f, 0.6f, 1f);
        Pulse(Put(t, "weave", "crest_shrine_shield_0001_1", l + 1.6f, u + 6.5f, 1.5f, 0.75f, 0f, Gold(0.55f)), 5f, 0.6f, 1f, 0f, 0.5f);
        // 丝线升降：两层之间一束垂直的灵丝
        float lx = l + Sanctum.LiftOffset;
        Pulse(PutBottom(t, "threads", "Thick_Silk_Vine_ambient", lx, f, 1.2f, 0.9f, (u - f + 3f) / 14.33f, Silk(0.4f)), 3f, 0.5f, 1f);
    }

    // ───────────────────────────── 门、升降 ─────────────────────────────

    internal static void DressGate(Transform t, Vector2 at, InteractableBase.PromptLabels label)
    {
        float x = at.x, f = at.y;
        if (label == InteractableBase.PromptLabels.Enter)
        {
            // 丝门：门框是织者的冠形拱，中间一道流动的丝光（终局之门的光帧）。
            var frames = new System.Collections.Generic.List<Sprite>();
            for (int i = 0; i < 8; i++)
            {
                Sprite? s = SanctumArt.Get("weaver_rune_gate", $"Final_Boss_Gate000{i}");
                if (s != null)
                {
                    frames.Add(s);
                }
            }
            Pulse(Put(t, "weave_gate", "song_door_glow_front", x, f + 3.6f, 1.0f, 0.95f, 0f, Gold(0.5f)), 3f, 0.6f, 1f);
            SpriteRenderer? portal = PutBottom(t, "weaver_rune_gate", "Final_Boss_Gate0004", x, f + 0.1f, 0.9f, 1.1f, 0.78f, new Color(1f, 0.94f, 0.82f, 0.9f));
            if (portal != null && frames.Count > 0)
            {
                portal.gameObject.AddComponent<SanctumFrames>().Frames = frames.ToArray();
            }
            PutBottom(t, "weave", "weaver_scroll_walls_back_0000_1", x - 3f, f, 1.1f, 0.85f, 0f, new Color(0.42f, 0.42f, 0.48f));
            PutBottom(t, "weave", "weaver_scroll_walls_back_0000_1", x + 3f, f, 1.1f, 0.85f, 0f, new Color(0.42f, 0.42f, 0.48f), flip: true);
            Put(t, "weave", "weaver_lift_deatails_0004_1", x, f + 8.3f, 1.15f, 0.92f, 0f, new Color(0.45f, 0.44f, 0.5f));
            Put(t, "weave", "loom_floor_small_glow", x, f + 0.2f, 0.5f, 1f, 1f, Gold(0.7f));
        }
        else
        {
            // 升降：地上一圈符文光，上方的小拱
            Pulse(Put(t, "weave", "loom_floor_large_glow", x, f + 0.4f, 0.55f, 0.5f, 0.7f, Silk(0.7f)), 2.5f, 0.5f, 1f);
            Put(t, "weave", "loom_arch_tiny_cap", x, f + 4.6f, 1.2f, 0.8f, 0f, new Color(0.45f, 0.45f, 0.52f));
            Pulse(Put(t, "weaver_rune_gate", "B_shrine_weaver_rune_0003_s2", x, f + 2.4f, 1.3f, 0.75f, 0f, Silk(0.45f)), 3.5f, 0.5f, 1f);
        }
    }

    // ───────────────────────────── 雕像 ─────────────────────────────

    // 石像的大小：最高 FigureHeight、最宽 FigureWidth（大黄蜂约 2.4 高，前作的神居雕像也是两三倍高）。
    private const float FigureHeight = 5.2f;
    private const float FigureWidth = 5.4f;
    private const float PairWidth = 6.0f;

    /// <summary>换像时石像沉下去多深（沉到底座和地面后面，同时淡出）。</summary>
    private const float SinkDepth = 3f;

    private static readonly Color StatueAura = new(0.85f, 0.9f, 1f, 0.09f);
    private static readonly Color StoneTint = new(0.86f, 0.87f, 0.92f, 1f);

    internal static void DressStatue(Statue s, Transform t, float x, float fy)
    {
        PutBottom(t, "weave", "weaver_shrine_statues_0001_1", x, fy, 0.85f, 0.56f, 0f, new Color(0.56f, 0.55f, 0.61f));
        s.FigureBase = fy + 1.3f;
        // 石像身后一团很淡的银光（不再是转动的光轮：石像不发光，是被照亮的）。
        s.Aura = Put(t, "core_glows", "white_light", x, fy + 3.9f, 1.3f, 1.05f, 1.15f, StatueAura);
        Pulse(s.Aura, 6f, 0.8f, 1f, 0f, x * 0.013f);
        // 顶上斜照下来的一束光，照在石像上。
        s.Halo = Put(t, "core_glows", "water_fog", x + 0.4f, fy + 8.2f, 1.35f, 1.5f, 2.6f, Gold(0.08f), rot: -6f);
        var fig = new GameObject("Figure");
        fig.transform.SetParent(t, false);
        s.Figure = Sanctum.Paint(fig, null, Color.white);
        for (int i = 0; i < 3; i++)
        {
            s.Marks[i] = Put(t, "weave", "weaver_heat_lamp_core_anim0000", x - 0.75f + i * 0.75f, fy + 0.62f, 0.8f, 0.42f, 0f, Color.gray);
        }
        if (s.Def.Variants.Count > 1)
        {
            float lx = x + LeverOffset;
            s.Lever = PutLever(t, lx, fy);
            for (int i = 0; i < s.Def.Variants.Count; i++)
            {
                SpriteRenderer? pip = Put(t, "core_glows", "lantern_glow_000", lx - 0.28f * (s.Def.Variants.Count - 1) + 0.56f * i, fy + 3.55f, 0.65f, 0.16f, 0f, Gold(0.3f));
                if (pip != null)
                {
                    s.Pips.Add(pip);
                }
            }
        }
    }

    // ───────────────────────────── 拉杆 ─────────────────────────────

    // 游戏里最常见的骨柄拉杆（ant 图集的 ant_lever_animated0000..0012，同一张 399x291 的画布、轴心在画布中心）：
    // 0000 立着；0001..0006 往左倒下再弹回（从右边敲）；0007..0012 往右倒下再弹回（从左边敲）。
    private const string LeverAtlas = "ant";
    private const string LeverIdle = "ant_lever_animated0000";
    private const float LeverScale = 0.85f;
    private static readonly Color LeverTint = new(0.84f, 0.83f, 0.88f, 1f);

    /// <summary>放拉杆：底下的圆球落在地面上（所有帧共用一个轴心，之后只换图、不挪位置）。</summary>
    private static SpriteRenderer? PutLever(Transform t, float lx, float fy)
    {
        Sprite? sp = SanctumArt.Get(LeverAtlas, LeverIdle);
        if (sp == null)
        {
            return null;
        }
        var go = new GameObject("Lever");
        go.transform.SetParent(t, false);
        go.transform.localScale = new Vector3(LeverScale, LeverScale, 1f);
        // 立着那帧：圆球在轴心右下（画布 x≈205、离底边 12 像素）。按网格包围盒算，圆球底部稍微陷进地面一点。
        Bounds b = sp.bounds;
        go.transform.position = new Vector3(lx - b.center.x * LeverScale, fy - b.min.y * LeverScale - 0.04f, 0.7f);
        Placed++;
        return Sanctum.Paint(go, sp, LeverTint);
    }

    /// <summary>拉杆被敲：按敲的方向倒下再弹回（游戏 Lever 的 Hit Direction：从左边敲往右倒）。上一段动画没播完就先停掉（Codex 审查 v0.6.3 低 #3）。</summary>
    internal static void PlayLever(Statue s, bool towardRight)
    {
        StopLever(s);
        if (s.Lever != null)
        {
            s.LeverAnim = s.StartCoroutine(LeverFrames(s, s.Lever, towardRight));
        }
        // 游戏骨柄拉杆的受击音效（Lever.hitSound = bone_switch），在拉杆的位置播。
        SanctumAudio.PlayLever(new Vector3(s.X + LeverOffset, s.Floor + 1f, 0f));
    }

    /// <summary>停掉拉杆动画、立回去。</summary>
    internal static void StopLever(Statue s)
    {
        if (s.LeverAnim != null)
        {
            s.StopCoroutine(s.LeverAnim);
            s.LeverAnim = null;
        }
        Sprite? idle = s.Lever != null ? SanctumArt.Get(LeverAtlas, LeverIdle) : null;
        if (idle != null)
        {
            s.Lever!.sprite = idle;
        }
    }

    /// <summary>拉杆现在显示的那一帧（巡视核对用）。</summary>
    internal static string LeverFrame(Statue s) => s.Lever != null && s.Lever.sprite != null ? s.Lever.sprite.name : "";

    private static IEnumerator LeverFrames(Statue s, SpriteRenderer lever, bool towardRight)
    {
        int first = towardRight ? 7 : 1;
        // 倒下快、躺平的两帧停久一点、弹回来慢一点。
        float[] hold = { 0.04f, 0.05f, 0.09f, 0.08f, 0.06f, 0.05f };
        for (int i = 0; i < 6; i++)
        {
            if (lever == null)
            {
                yield break;
            }
            Sprite? sp = SanctumArt.Get(LeverAtlas, $"ant_lever_animated{first + i:0000}");
            if (sp != null)
            {
                lever.sprite = sp;
            }
            yield return new WaitForSeconds(hold[i]);
        }
        if (lever != null)
        {
            Sprite? idle = SanctumArt.Get(LeverAtlas, LeverIdle);
            if (idle != null)
            {
                lever.sprite = idle;
            }
        }
        if (s != null)
        {
            s.LeverAnim = null;
        }
    }

    internal static void RefreshStatue(Statue s)
    {
        BossDef cur = s.Current;
        bool black = s.CurrentIsBlack;
        // 雕好的石像（StatueCarver，黑幕下已经雕完；同一个 Boss 的不同形态各有各的）；没雕成才去找图鉴立绘退回显示。
        Sprite? stone = Sanctum.CarvedStatue(cur) ?? Sanctum.CarvedStatue(s.Def.Variants[0]);
        Sprite? sprite = stone != null ? null : Sanctum.JournalSprite(cur) ?? Sanctum.JournalSprite(s.Def.Variants[0]);
        s.HasSprite = stone != null || sprite != null;
        s.IsStone = stone != null;
        if (s.Figure != null)
        {
            Transform ft = s.Figure.transform;
            s.Figure.sprite = stone ?? sprite ?? SanctumArt.Get("memory_weaver", "weaver_memory_silhouettes_0001_1");
            Sprite? shown = s.Figure.sprite;
            if (shown != null)
            {
                Vector2 size = shown.bounds.size;
                // 双生的石像（两尊一前一后）可以宽一些，但别盖到右边的拉杆。
                float maxW = shown.name.StartsWith("Statue pair:", System.StringComparison.Ordinal) ? PairWidth : FigureWidth;
                float scale = Mathf.Min(FigureHeight / Mathf.Max(0.01f, size.y), maxW / Mathf.Max(0.01f, size.x));
                ft.localScale = new Vector3(scale, scale, 1f);
                float bottom = shown.bounds.min.y * scale;
                float cx = shown.bounds.center.x * scale;
                ft.position = new Vector3(s.X - cx, s.FigureBase - bottom + 0.02f, 1.0f);
            }
            // 石像本色是暖灰的石头；黑线版是暗紫的黑石。没雕成时退回：立绘褪成银白，没有立绘是暗色剪影。
            s.Figure.color = stone != null
                ? (black ? new Color(0.5f, 0.42f, 0.62f, 1f) : StoneTint)
                : black ? new Color(0.62f, 0.5f, 0.74f, 1f) : (sprite != null ? new Color(0.86f, 0.88f, 0.97f, 1f) : new Color(0.2f, 0.2f, 0.25f, 1f));
        }
        SetColor(s.Aura, black ? new Color(0.5f, 0.16f, 0.62f, 0.22f) : StatueAura);
        SetColor(s.Halo, black ? new Color(0.7f, 0.45f, 0.95f, 0.08f) : Gold(0.08f));
        GodhomeSaveData rec = GodhomePlugin.Instance.Records;
        BossRecord? br = rec.Bosses.TryGetValue(cur.Id, out BossRecord? r) ? r : null;
        Color[] lit = { new(1f, 1f, 0.95f, 1f), new(1f, 0.8f, 0.32f, 1f), new(1f, 0.3f, 0.24f, 1f) };
        for (int i = 0; i < 3; i++)
        {
            SetColor(s.Marks[i], br != null && br.ClearsAt(i) > 0 ? lit[i] : new Color(0.16f, 0.16f, 0.2f, 0.95f));
        }
        int idx = s.Def.Variants.IndexOf(cur);
        for (int i = 0; i < s.Pips.Count; i++)
        {
            SetColor(s.Pips[i], i == idx ? (black ? new Color(0.8f, 0.5f, 1f, 0.95f) : Gold(0.95f)) : new Color(0.5f, 0.5f, 0.55f, 0.25f));
        }
    }

    private static void SetColor(SpriteRenderer? sr, Color c)
    {
        if (sr == null)
        {
            return;
        }
        sr.color = c;
        sr.GetComponent<SanctumPulse>()?.Rebase();
    }

    /// <summary>
    /// 拉杆换像（照前作神居的 BossStatue：抖动 → 沉下 → 换 → 升起，带扬尘）。
    /// 石像在底座和地面的后面（z 更远），沉下去时被它们挡住，同时淡出；用游戏时间，暂停时停住。
    /// </summary>
    internal static IEnumerator SwapStatue(Statue s)
    {
        SpriteRenderer? fig = s.Figure;
        if (fig == null)
        {
            s.Refresh();
            s.EndSwap();
            yield break;
        }
        Transform ft = fig.transform;
        Vector3 home = ft.position;
        Color c0 = fig.color;
        // 1. 抖动、底座上扬起灰尘
        Dust(s, 7);
        for (float t = 0f; t < 0.6f; t += Time.deltaTime)
        {
            if (fig == null)
            {
                yield break;
            }
            float amp = 0.035f + 0.05f * (t / 0.6f);
            ft.position = home + new Vector3(Random.Range(-amp, amp), Random.Range(-amp, amp) * 0.4f, 0f);
            yield return null;
        }
        // 2. 沉进底座
        for (float t = 0f; t < 0.7f; t += Time.deltaTime)
        {
            if (fig == null)
            {
                yield break;
            }
            float k = t / 0.7f;
            float e = k * k;
            ft.position = home + new Vector3(Random.Range(-0.03f, 0.03f) * (1f - k), -SinkDepth * e, 0f);
            fig.color = new Color(c0.r, c0.g, c0.b, c0.a * (1f - e));
            yield return null;
        }
        // 3. 换成另一尊（位置、大小、颜色按新的版本算），从底下升上来
        s.Refresh();
        Vector3 home2 = ft.position;
        Color c1 = fig.color;
        fig.color = new Color(c1.r, c1.g, c1.b, 0f);
        Dust(s, 9);
        for (float t = 0f; t < 0.85f; t += Time.deltaTime)
        {
            if (fig == null)
            {
                yield break;
            }
            float k = t / 0.85f;
            float e = 1f - (1f - k) * (1f - k);
            ft.position = home2 + new Vector3(0f, -SinkDepth * (1f - e), 0f);
            fig.color = new Color(c1.r, c1.g, c1.b, c1.a * Mathf.Clamp01(e * 1.4f));
            yield return null;
        }
        ft.position = home2;
        fig.color = c1;
        s.EndSwap();
    }

    /// <summary>底座上扬起一阵灰尘（换像时）。</summary>
    private static void Dust(Statue s, int n)
    {
        Sprite? puff = SanctumArt.Get("core_glows", "white_light");
        if (puff == null)
        {
            return;
        }
        for (int i = 0; i < n; i++)
        {
            var go = new GameObject("Statue Dust");
            go.transform.SetParent(s.transform, false);
            go.transform.position = new Vector3(s.X + Random.Range(-1.9f, 1.9f), s.FigureBase + Random.Range(-0.1f, 0.3f), 0.7f);
            SpriteRenderer sr = Sanctum.Paint(go, puff, new Color(0.62f, 0.6f, 0.57f, 0f), 1);
            go.AddComponent<SanctumDust>().Init(sr, Random.Range(0.22f, 0.42f), Random.Range(0.8f, 1.3f), new Vector2(Random.Range(-0.5f, 0.5f), Random.Range(0.5f, 1.3f)));
        }
    }

    // ───────────────────────────── 传送的丝光 ─────────────────────────────

    internal static void TravelBurst(Vector3 at)
    {
        var go = new GameObject("Silk Burst");
        Transform? parent = Sanctum.Statues.Count > 0 && Sanctum.Statues[0] != null ? Sanctum.Statues[0].transform.parent : null;
        if (parent != null)
        {
            go.transform.SetParent(parent, false);
        }
        go.AddComponent<SanctumBurst>().At = at;
    }
}

/// <summary>换像时的一小团灰尘：慢慢飘起、变大、淡出，自己销毁（用游戏时间）。</summary>
internal sealed class SanctumDust : MonoBehaviour
{
    private SpriteRenderer? sr;
    private float size;
    private float life;
    private Vector2 vel;
    private float t;

    internal void Init(SpriteRenderer r, float startSize, float seconds, Vector2 velocity)
    {
        sr = r;
        size = startSize;
        life = seconds;
        vel = velocity;
        transform.localScale = Vector3.one * size;
    }

    private void Update()
    {
        t += Time.deltaTime;
        float k = t / Mathf.Max(0.01f, life);
        if (k >= 1f || sr == null)
        {
            Destroy(gameObject);
            return;
        }
        transform.position += (Vector3)(vel * Time.deltaTime);
        vel *= 1f - 1.5f * Time.deltaTime;
        transform.localScale = Vector3.one * size * (1f + 1.2f * k);
        Color c = sr.color;
        c.a = 0.55f * Mathf.Sin(Mathf.PI * Mathf.Min(1f, k * 1.3f)) * (1f - k);
        sr.color = c;
    }
}

/// <summary>传送时大黄蜂身上的一团丝光：金色光晕放大淡出，几道灵丝向上散开。自己销毁。</summary>
internal sealed class SanctumBurst : MonoBehaviour
{
    internal Vector3 At;
    private SpriteRenderer? glow;
    private SpriteRenderer? silk;
    private float t0;

    private void Start()
    {
        t0 = Time.unscaledTime;
        glow = SanctumLayout.Put(transform, "core_glows", "white_light", At.x, At.y + 0.5f, -0.5f, 0.5f, 0f, new Color(1f, 0.85f, 0.55f, 0.9f));
        silk = SanctumLayout.Put(transform, "threads", "Thick_Silk_Vine_ambient", At.x, At.y + 3f, -0.6f, 0.6f, 0.5f, new Color(0.95f, 0.95f, 1f, 0.8f));
    }

    private void Update()
    {
        float k = (Time.unscaledTime - t0) / 0.7f;
        if (k >= 1f)
        {
            Destroy(gameObject);
            return;
        }
        if (glow != null)
        {
            glow.transform.localScale = Vector3.one * Mathf.Lerp(0.5f, 1.8f, k);
            Color c = glow.color;
            c.a = 0.9f * (1f - k);
            glow.color = c;
        }
        if (silk != null)
        {
            silk.transform.localScale = new Vector3(0.6f, Mathf.Lerp(0.3f, 1.2f, k), 1f);
            Color c = silk.color;
            c.a = 0.8f * (1f - k);
            silk.color = c;
        }
    }
}
