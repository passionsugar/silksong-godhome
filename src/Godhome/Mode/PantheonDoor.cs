using System.Linq;
using UnityEngine;

namespace Godhome;

/// <summary>
/// [Godhome] 万神殿前厅里的一扇门（照前作神居 GG_Atrium 的 BossSequenceDoor）：门上挂着万神殿的徽记，
/// 两侧四个束缚标记（完成过哪种束缚就亮哪个），徽记上方一枚纹章（完成过才有：全束缚金色，无伤身后一圈光）。
/// 要先完成别的万神殿的门（第五扇）被骨笼封着，没有交互。
/// </summary>
internal sealed class PantheonDoor : MonoBehaviour
{
    internal PantheonDef Def = null!;
    internal float Floor;
    internal int Level;
    internal float Size = 1f;
    internal PantheonDoorInteract? Interact;
    internal SpriteRenderer? Panel;
    internal SpriteRenderer? BackGlow;
    internal SpriteRenderer? Emblem;
    internal SpriteRenderer? Plaque;
    internal SpriteRenderer? Core;
    internal SpriteRenderer? CoreGlow;
    internal SpriteRenderer? Cage;
    internal readonly SpriteRenderer?[] Marks = new SpriteRenderer?[4];

    internal float X => transform.position.x;

    /// <summary>还被骨笼封着（要先完成的万神殿没有都完成）。</summary>
    internal bool Locked { get; private set; }

    internal void Refresh()
    {
        Locked = !ModeController.PantheonUnlocked(Def);
        if (Interact != null)
        {
            // 封着的门没有提示、不能交互（关掉交互组件，和藏起原房间的交互物体一样）。
            Interact.enabled = !Locked;
        }
        AtriumLayout.RefreshDoor(this);
    }
}

/// <summary>门的交互：“挑战”提示，按下后打开万神殿的挑战面板（选束缚）。</summary>
internal sealed class PantheonDoorInteract : InteractableBase
{
    internal PantheonDoor Door = null!;

    public override void Interact()
    {
        ModeController? mc = ModeController.Instance;
        if (mc == null || mc.State != LoomState.Lobby || LoomUI.Instance == null || Sanctum.InteractionBlocked || Door == null || Door.Locked)
        {
            return;
        }
        DisableInteraction();
        RecordControlVersion();
        LoomUI.Instance.OpenPantheon(Door);
    }

    /// <summary>面板关掉（取消或开始）时交还控制。</summary>
    internal void Release() => EnableInteraction();
}

/// <summary>
/// [Godhome] 万神殿前厅的画面（在忆像长廊右边，单独一间，三层）：和圣所、长廊同一套织者遗迹的画法（SanctumLayout 的工具）。
/// 下层：回圣所的丝门、第一、二扇门；中层：第三、四扇门；上层：第五扇门（更大，身后一扇钟室大窗）。层与层之间走丝线升降。
/// </summary>
internal static class AtriumLayout
{
    private static readonly Color Bone = new(0.8f, 0.77f, 0.7f, 1f);
    private static readonly Color Dark = new(0.2f, 0.2f, 0.25f, 0.85f);
    private static readonly Color Silver = new(0.9f, 0.92f, 1f, 1f);

    /// <summary>按目标高度放一个精灵（底边对齐 bottom）。</summary>
    private static SpriteRenderer? PutH(Transform t, string atlas, string name, float x, float y, float z, float height, Color tint, bool bottom = false)
    {
        Sprite? sp = SanctumArt.Get(atlas, name);
        if (sp == null)
        {
            return null;
        }
        float s = height / Mathf.Max(0.01f, sp.bounds.size.y);
        return bottom
            ? SanctumLayout.PutBottom(t, atlas, name, x, y, z, s, 0f, tint)
            : SanctumLayout.Put(t, atlas, name, x, y, z, s, 0f, tint);
    }

    internal static void BuildAtrium(Transform art, float f)
    {
        Transform t = new GameObject("Atrium").transform;
        t.SetParent(art, false);
        float l = Sanctum.AtriumLeft, r = Sanctum.AtriumRight, rise = Sanctum.AtriumRise;
        float top = f + 3f * rise;

        // 远景：自己的一块天幕（圣所那块不一定盖得到这么远），暖色远光，尖塔剪影。
        SanctumLayout.Plane(t, "Atrium Sky", l - 60f, f - 40f, r + 60f, top + 40f, 45f, new Color(0.010f, 0.011f, 0.018f));
        for (float x = l - 10f; x < r + 20f; x += 22f)
        {
            SanctumLayout.PutBottom(t, "weave", "weaver_shrine_bg_spires", x, f - 3f, 24f, 2.4f, 0f, SanctumLayout.StoneFar, flip: ((int)x / 22) % 2 == 1);
        }
        SanctumLayout.Put(t, "core_glows", "white_fader", (l + r) / 2f, f + 22f, 32f, 9f, 9f, new Color(0.42f, 0.34f, 0.22f, 0.26f));
        SanctumLayout.BackWall(t, l - 12f, r + 12f, f - 4f, top + 4f, 12f, SanctumLayout.StoneDim);

        float mid = l + Sanctum.AtriumDoorA + (Sanctum.AtriumDoorB - Sanctum.AtriumDoorA) / 2f;
        for (int k = 0; k < 3; k++)
        {
            float fy = f + k * rise;
            float height = rise - Sanctum.SlabThick;
            // 两端和两扇门之间的立柱，柱上挂灯。
            float[] pillars = k < 2 ? new[] { l + 2.5f, mid, r - 2.5f } : new[] { l + 2.5f, r - 2.5f };
            foreach (float px in pillars)
            {
                SanctumLayout.PutBottom(t, "weave", "Loom_Room_0026_14", px, fy, 3f, 0.88f, height / 9.16f * 0.98f, SanctumLayout.Stone);
                float ly = fy + height * 0.62f;
                SanctumLayout.Put(t, "weave", "loom_break_lamp", px, ly, 2.8f, 0.55f, 0f, new Color(0.6f, 0.6f, 0.66f));
                SanctumLayout.Pulse(SanctumLayout.Put(t, "core_glows", "lantern_glow_000", px, ly - 0.15f, 2.7f, 0.55f, 0f, SanctumLayout.Gold(0.75f)), 2.6f, 0.65f, 1f, 0.05f, px * 0.05f);
            }
            // 地面、楼板底下的小拱
            SanctumLayout.FloorStrip(t, k == 0 ? l - 6f : l, k == 0 ? r + 6f : r, fy, new Color(0.5f, 0.5f, 0.57f));
            if (k > 0)
            {
                for (float x = l; x < r - 3f; x += 7f)
                {
                    SanctumLayout.PutTop(t, "weave", "Loom_Room_0020_20", x + 3.5f, fy - Sanctum.SlabThick + 0.15f, 0.3f, 1f, 1f, new Color(0.36f, 0.36f, 0.43f));
                }
            }
            // 左端发光的纹章
            SanctumLayout.Pulse(SanctumLayout.Put(t, "weave", "crest_shrine_shield_0001_1", l + 1.6f, fy + 6.5f, 1.5f, 0.75f, 0f, SanctumLayout.Gold(0.55f)), 5f, 0.6f, 1f, 0f, k * 0.4f);
            SanctumLayout.Motes(t, new Rect(l + 1f, fy, r - l - 2f, height - 1f), 2f, SanctumLayout.Gold(0.5f), 40, 0.1f, 0.2f, 0.3f);
        }
        // 上层的大拱和第五扇门身后的钟室大窗、光。
        float uy = f + 2f * rise;
        float cx = l + Sanctum.AtriumDoorTop;
        SanctumLayout.Put(t, "core_glows", "white_light", cx, uy + 8.5f, 11.6f, 3.2f, 3.4f, SanctumLayout.Gold(0.5f));
        SanctumLayout.Put(t, "bshrine_window", "Hornet_Core__0075_bell_room_window", cx, uy + 8f, 11f, 0.85f, 0f, new Color(0.9f, 0.78f, 0.55f));
        SanctumLayout.Spin(SanctumLayout.Put(t, "core_glows", "light_effect_v02", cx, uy + 8f, 10.8f, 1.6f, 0f, SanctumLayout.Gold(0.22f)), 3f);
        SanctumLayout.PutTop(t, "weave", "weaver_arch_details_0001_1", cx, top + 1.5f, 5f, 1.25f, 0f, new Color(0.30f, 0.30f, 0.37f));
        // 丝线升降：两层之间一束垂直的灵丝。
        float liftA = l + Sanctum.AtriumLiftA, liftB = l + Sanctum.AtriumLiftB;
        SanctumLayout.Pulse(SanctumLayout.PutBottom(t, "threads", "Thick_Silk_Vine_ambient", liftA, f, 1.2f, 0.9f, (rise + 3f) / 14.33f, SanctumLayout.Silk(0.4f)), 3f, 0.5f, 1f);
        SanctumLayout.Pulse(SanctumLayout.PutBottom(t, "threads", "Thick_Silk_Vine_ambient", liftB, f + rise, 1.2f, 0.9f, (rise + 3f) / 14.33f, SanctumLayout.Silk(0.4f)), 3f, 0.5f, 1f, 0f, 0.5f);
        // 前景：两侧的黑色柱影。
        SanctumLayout.PutBottom(t, "weave", "Loom_wall_0000_4", l + 4f, f - 6f, -5f, 2.6f, 0f, SanctumLayout.Black);
        SanctumLayout.PutBottom(t, "weave", "Loom_wall_0000_4", r - 4f, f - 6f, -5f, 2.6f, 0f, SanctumLayout.Black, flip: true);
    }

    /// <summary>门的画：门框、门面的织者符文、徽记、束缚标记、纹章、（封着时）骨笼。颜色在 RefreshDoor 里按完成情况改。</summary>
    internal static void DressDoor(PantheonDoor d, Transform t)
    {
        float x = d.X, f = d.Floor, s = d.Size;
        d.BackGlow = SanctumLayout.Put(t, "core_glows", "white_light", x, f + 4.6f * s, 1.6f, 1.35f * s, 1.9f * s, SanctumLayout.Gold(0.2f));
        SanctumLayout.Pulse(d.BackGlow, 6f, 0.7f, 1f, 0f, x * 0.02f);
        d.Panel = PutH(t, "weaver_rune_gate", "B_shrine_weaver_rune_0002_b1", x, f + 3.9f * s, 1.2f, 6.4f * s, new Color(0.6f, 0.58f, 0.68f, 0.9f));
        SanctumLayout.Pulse(d.Panel, 5f, 0.75f, 1f, 0f, x * 0.03f);
        foreach (float side in new[] { -1f, 1f })
        {
            SanctumLayout.PutBottom(t, "weave", "Loom_Room_0026_14", x + side * 3.4f * s, f, 1f, 0.7f * s, 0.86f * s, SanctumLayout.Stone);
        }
        SanctumLayout.PutBottom(t, "weave", "weaver_arch_details_0000_1", x, f + 7.6f * s, 0.95f, 0.8f * s, 0f, new Color(0.42f, 0.42f, 0.5f));
        SanctumLayout.Put(t, "weave", "loom_floor_small_glow", x, f + 0.2f, 0.5f, 1.1f * s, 1f, SanctumLayout.Gold(0.6f));
        // 徽记：成就图标（原图自带深色底），挂在发光的纹章盾上。
        float ey = f + 10.4f * s;
        d.Plaque = PutH(t, "weave", "crest_shrine_shield_0001_1", x, ey, 0.95f, 3.4f * s, SanctumLayout.Gold(0.45f));
        SanctumLayout.Pulse(d.Plaque, 5f, 0.6f, 1f, 0f, x * 0.05f);
        if (!string.IsNullOrEmpty(d.Def.Emblem))
        {
            d.Emblem = PutH(t, "achievement_icons_hornet", d.Def.Emblem, x, ey, 0.9f, 2.3f * s, Color.white);
        }
        // 束缚标记：徽记两侧各两个（织针、面具 | 工具、灵丝）。
        float[] mx = { -2.75f, -1.85f, 1.85f, 2.75f };
        for (int i = 0; i < 4; i++)
        {
            d.Marks[i] = PutH(t, PantheonMenu.Icons[i].Atlas, PantheonMenu.Icons[i].Sprite, x + mx[i] * s, ey, 0.9f, 0.85f * s, Dark);
        }
        // 纹章（完成过才显示）
        d.CoreGlow = PutH(t, "crest", "Crest__0014_hunter_lvl4_equip_glow", x, ey + 2.3f * s, 0.92f, 1.9f * s, SanctumLayout.Gold(0.6f));
        SanctumLayout.Pulse(d.CoreGlow, 3.5f, 0.55f, 1f, 0.04f);
        d.Core = PutH(t, "crest", "Crest__0014_hunter_lvl4", x, ey + 2.3f * s, 0.9f, 1.3f * s, Silver);
        // 骨笼（封着的门）：盖在门前面（比门近、比大黄蜂远）。
        if (d.Def.Requires.Length > 0)
        {
            d.Cage = PutH(t, "cage", "Bonechurch_middle_bits_0013_cage_back", x, f - 0.1f, 0.6f, 8.2f * s, Bone, bottom: true);
        }
    }

    internal static void RefreshDoor(PantheonDoor d)
    {
        GodhomeSaveData rec = GodhomePlugin.Instance.Records;
        PantheonRecord? pr = rec.Pantheons.TryGetValue(d.Def.Id, out PantheonRecord? x) ? x : null;
        bool done = pr != null && pr.Completed;
        bool all = pr != null && pr.AllBindings;
        bool noHits = pr != null && pr.NoHits;
        Set(d.Emblem, d.Locked ? new Color(0.25f, 0.25f, 0.3f, 1f) : done ? Color.white : new Color(0.62f, 0.62f, 0.68f, 1f));
        Set(d.Panel, d.Locked ? new Color(0.3f, 0.3f, 0.36f, 0.85f) : new Color(0.6f, 0.58f, 0.68f, 0.9f));
        Set(d.BackGlow, d.Locked ? SanctumLayout.Gold(0.05f) : SanctumLayout.Gold(done ? 0.3f : 0.18f));
        Set(d.Plaque, d.Locked ? new Color(0.4f, 0.4f, 0.45f, 0.35f) : SanctumLayout.Gold(done ? 0.6f : 0.4f));
        for (int i = 0; i < 4; i++)
        {
            bool on = pr != null && pr.Bound(PantheonMenu.Order[i]);
            Set(d.Marks[i], on ? (all ? new Color(1f, 0.84f, 0.5f, 1f) : Silver) : Dark);
        }
        if (d.Core != null)
        {
            d.Core.gameObject.SetActive(done);
            Set(d.Core, all ? new Color(1f, 0.84f, 0.5f, 1f) : Silver);
        }
        if (d.CoreGlow != null)
        {
            d.CoreGlow.gameObject.SetActive(done && noHits);
        }
        if (d.Cage != null)
        {
            d.Cage.gameObject.SetActive(d.Locked);
        }
    }

    private static void Set(SpriteRenderer? sr, Color c)
    {
        if (sr == null)
        {
            return;
        }
        sr.color = c;
        sr.GetComponent<SanctumPulse>()?.Rebase();
    }

    /// <summary>门的个数和位置：前四扇两两一层（下层、中层），其余在上层中间。</summary>
    internal static (float X, int Level) Spot(int i)
    {
        if (i < 4)
        {
            return (Sanctum.AtriumLeft + (i % 2 == 0 ? Sanctum.AtriumDoorA : Sanctum.AtriumDoorB), i / 2);
        }
        int extra = PantheonCatalog.All.Count - 4;
        float spread = 14f;
        float x0 = Sanctum.AtriumLeft + Sanctum.AtriumDoorTop - spread * (extra - 1) / 2f;
        return (x0 + spread * (i - 4), 2);
    }

    internal static PantheonDoor? Find(string id) => Sanctum.Doors.FirstOrDefault(d => d != null && d.Def.Id == id);
}
