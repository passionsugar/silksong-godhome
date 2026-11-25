using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Godhome;

/// <summary>
/// [Godhome] 万神殿门前的挑战面板（照前作 BossDoorChallengeUI）：标题、说明、一排四个束缚按钮、“开始”，按返回离开。
/// 用游戏自己的“模式选择”界面复制一份（MenuClone）：束缚按钮是它的大按钮改小（选中时的光、左右花饰光标、闪光都是原版的），
/// “开始”是它底下“返回”按钮的复制品。
/// 束缚按钮照前作 BossDoorChallengeUIBindingButton：确认键套上 / 解开（按钮不失去焦点），镜头小震一下；
/// 四个都套上时照前作 UpdateAllButtons：镜头中震、四个一起变金。
/// 图标用丝之歌的物品栏图：织针（_0007_nail_upgrade_icon）、面具（Inv_0030_inv_health_backboard_mask_1）、
/// 工具包（Inv_0015_tool_pouch_original）、丝轴（Inv_Hornet_Spool_0001_1_full）；套上时盖一层工具栏上锁时的丝（UI_tool_slot_lock_weave0000）。
/// </summary>
internal static class PantheonMenu
{
    internal static readonly Binding[] Order = { Binding.Needle, Binding.Mask, Binding.Tools, Binding.Silk };

    /// <summary>
    /// 四个束缚的图标（图集, 精灵）：物品栏里的织针、完整的面具（ui 图集；inventory 里的 mask_1 只是一块碎片）、
    /// 工具包、丝轴。_0007_nail_upgrade_icon 带左右箭头，第一次实机截图里像碎掉的图，换掉了。
    /// </summary>
    internal static readonly (string Atlas, string Sprite)[] Icons =
    {
        ("inventory", "Hornet_Inv_pane_icons_0003_needle_sharpened"),
        ("ui", "Inv_0030_inv_health_backboard_mask_full"),
        ("inventory", "Inv_0015_tool_pouch_original"),
        ("inventory", "Inv_Hornet_Spool_0001_1_full"),
    };

    /// <summary>封印和封印的光（工具栏上锁的丝）所在的图集。</summary>
    internal const string IconAtlas = "inventory";
    internal const string SealSprite = "UI_tool_slot_lock_weave0000";
    internal const string GlowSprite = "UI_tool_slot_lock_fill_glow0000";

    private static readonly float[] RowX = { -570f, -190f, 190f, 570f };
    private static readonly Color Gold = new(1f, 0.84f, 0.5f, 1f);
    private static readonly Color BoundIcon = new(0.52f, 0.52f, 0.58f, 1f);
    private static readonly Color NameDone = new(1f, 0.86f, 0.56f, 1f);

    private static readonly MenuClone clone = new("Pantheon Challenge Screen");
    private static readonly MenuButton?[] binds = new MenuButton?[4];
    private static readonly Image?[] icons = new Image?[4];
    private static readonly Image?[] seals = new Image?[4];
    private static readonly Image?[] glows = new Image?[4];
    private static readonly Text?[] names = new Text?[4];
    private static Text? desc;
    private static Text? hint;
    private static MenuButton? begin;
    private static MenuButton? back;

    private static Binding chosen;
    private static bool allBefore;
    private static PantheonDef? def;
    private static PantheonRecord? record;
    private static Action<Binding>? onBegin;
    private static Action? onCancel;

    internal static bool Open => clone.Open;

    internal static bool Busy => clone.Busy;

    internal static int Session => clone.Session;

    internal static string Note { get; private set; } = "";

    /// <summary>现在套上的束缚（巡视核对用）。</summary>
    internal static Binding Chosen => chosen;

    internal static string TitleText => clone.Title != null ? clone.Title.text.TrimEnd('\n') : "";

    internal static string HintText => hint != null ? hint.text : "";

    /// <summary>当前选中：0..3 束缚，4 开始，5 返回，-1 没选中面板里的按钮（巡视核对用）。</summary>
    internal static int Selected
    {
        get
        {
            GameObject? sel = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (sel == null)
            {
                return -1;
            }
            for (int i = 0; i < 4; i++)
            {
                if (binds[i] != null && sel == binds[i]!.gameObject)
                {
                    return i;
                }
            }
            if (begin != null && sel == begin.gameObject)
            {
                return 4;
            }
            return back != null && sel == back.gameObject ? 5 : -1;
        }
    }

    /// <summary>打开面板，返回这次打开的编号（大于 0），失败返回 0（调用方退回简易面板）。</summary>
    internal static int Show(MonoBehaviour runner, PantheonDef p, PantheonRecord? rec, Action<Binding> beginCb, Action cancelCb)
    {
        if (clone.Busy)
        {
            return 0;
        }
        if (!clone.Ensure(Build, out string note))
        {
            Note = note;
            return 0;
        }
        Note = note;
        try
        {
            def = p;
            record = rec;
            onBegin = beginCb;
            onCancel = cancelCb;
            chosen = Binding.None;
            allBefore = false;
            clone.Title!.text = p.DisplayName;
            desc!.text = p.Description + "\n" + L.F("PAN_COUNT", p.Bosses.Count);
            for (int i = 0; i < 4; i++)
            {
                if (names[i] != null)
                {
                    names[i]!.text = L.Get("BIND_" + Order[i].ToString().ToUpperInvariant());
                    names[i]!.color = rec != null && rec.Bound(Order[i]) ? NameDone : Color.white;
                }
                SetSprite(icons[i], Icons[i].Atlas, Icons[i].Sprite);
                SetSprite(seals[i], IconAtlas, SealSprite);
                SetSprite(glows[i], IconAtlas, GlowSprite);
            }
            Refresh();
            hint!.text = "";
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogError($"[Godhome] 万神殿面板填内容出错 / pantheon menu setup failed: {e}");
            Unbind();
            return 0;
        }
        int id = clone.Show(runner, input: true);
        if (id == 0)
        {
            Unbind();
        }
        return id;
    }

    private static void SetSprite(Image? img, string atlas, string name)
    {
        if (img == null)
        {
            return;
        }
        Sprite? s = SanctumArt.Get(atlas, name);
        img.sprite = s;
        img.enabled = s != null;
    }

    /// <summary>关面板（不回调选择），淡出完调 closed。</summary>
    internal static bool Hide(Action? closed)
    {
        onBegin = null;
        onCancel = null;
        return clone.Hide(closed);
    }

    internal static void ForceClose(string why)
    {
        onBegin = null;
        onCancel = null;
        clone.ForceClose(why);
    }

    /// <summary>每帧（面板开着时，LoomUI 调）：焦点、选中说明、丝的动画。</summary>
    internal static void Tick()
    {
        Selectable? sel = clone.Tick(binds[0]);
        if (clone.State != MenuClone.Phase.Open && clone.State != MenuClone.Phase.Opening)
        {
            return;
        }
        UpdateHint(sel);
        float dt = Time.unscaledDeltaTime;
        for (int i = 0; i < 4; i++)
        {
            Image? s = seals[i];
            if (s != null && s.rectTransform.localScale.x > 1.001f)
            {
                float k = Mathf.MoveTowards(s.rectTransform.localScale.x, 1f, dt * 2.5f);
                s.rectTransform.localScale = new Vector3(k, k, 1f);
            }
            Image? g = glows[i];
            if (g != null && g.enabled && g.gameObject.activeSelf)
            {
                Color c = g.color;
                c.a = 0.55f + 0.25f * Mathf.Sin(Time.unscaledTime * 3f + i * 0.6f);
                g.color = c;
            }
        }
    }

    private static void UpdateHint(Selectable? sel)
    {
        if (hint == null || def == null)
        {
            return;
        }
        BindingParams bp = PantheonCatalog.Bindings;
        string text = "";
        for (int i = 0; i < 4; i++)
        {
            if (binds[i] != null && sel == binds[i])
            {
                text = Order[i] switch
                {
                    Binding.Needle => L.F("BIND_DESC_NEEDLE", bp.NeedleCap),
                    Binding.Mask => L.F("BIND_DESC_MASK", bp.MaxMasks),
                    Binding.Tools => L.Get("BIND_DESC_TOOLS"),
                    _ => L.F("BIND_DESC_SILK", bp.MaxSilk),
                };
            }
        }
        if (begin != null && sel == begin)
        {
            string rec = record != null && record.Completed ? L.F("PAN_DONE", record.Clears, L.Time(record.BestTime)) : L.Get("PAN_NONE");
            text = chosen == Binding.None ? rec : rec + "　·　" + L.F("PAN_BOUND_N", Bindings.Describe(chosen));
        }
        if (hint.text != text)
        {
            hint.text = text;
        }
    }

    /// <summary>束缚按钮确认（照前作 BindingButton.OnSubmit）：套上 / 解开，小震；四个都套上时中震、一起变金。</summary>
    private static void Toggle(int i)
    {
        if (!clone.Open)
        {
            return;
        }
        chosen ^= Order[i];
        bool on = (chosen & Order[i]) != 0;
        if (seals[i] != null && on)
        {
            seals[i]!.rectTransform.localScale = new Vector3(1.45f, 1.45f, 1f);
        }
        MenuClone.Shake(false);
        bool all = chosen == Binding.All;
        if (all && !allBefore)
        {
            MenuClone.Shake(true);
            for (int k = 0; k < 4; k++)
            {
                if (seals[k] != null)
                {
                    seals[k]!.rectTransform.localScale = new Vector3(1.3f, 1.3f, 1f);
                }
            }
        }
        allBefore = all;
        Refresh();
        GodhomePlugin.Log.LogInfo($"[Godhome] 束缚 / binding {Order[i]} {(on ? "套上 / on" : "解开 / off")}：{Bindings.Describe(chosen)}");
    }

    private static void Refresh()
    {
        bool all = chosen == Binding.All;
        for (int i = 0; i < 4; i++)
        {
            bool on = (chosen & Order[i]) != 0;
            if (icons[i] != null)
            {
                icons[i]!.color = on ? (all ? Gold : BoundIcon) : Color.white;
            }
            if (seals[i] != null)
            {
                seals[i]!.gameObject.SetActive(on && seals[i]!.sprite != null);
                seals[i]!.color = all ? Gold : Color.white;
            }
            if (glows[i] != null)
            {
                glows[i]!.gameObject.SetActive(all && glows[i]!.sprite != null);
                glows[i]!.color = new Color(Gold.r, Gold.g, Gold.b, 0.7f);
            }
        }
    }

    private static void Begin()
    {
        if (!clone.Open)
        {
            return;
        }
        Action<Binding>? cb = onBegin;
        cb?.Invoke(chosen);
    }

    private static void Cancel()
    {
        if (!clone.Open)
        {
            return;
        }
        Action? cb = onCancel;
        cb?.Invoke();
    }

    // ───────────────────────────── 巡视用 ─────────────────────────────

    /// <summary>巡视用：照游戏的输入模块那样给按钮发确认（0..3 束缚、4 开始、5 返回）或取消。id 不是这次打开的就不发。</summary>
    internal static bool PressForTest(int i, int id, bool cancel = false)
    {
        Selectable? b = i >= 0 && i < 4 ? binds[i] : i == 4 ? begin : back;
        if (clone.State != MenuClone.Phase.Open || id != clone.Session || b == null || EventSystem.current == null)
        {
            return false;
        }
        var data = new BaseEventData(EventSystem.current);
        return cancel
            ? ExecuteEvents.Execute(b.gameObject, data, ExecuteEvents.cancelHandler)
            : ExecuteEvents.Execute(b.gameObject, data, ExecuteEvents.submitHandler);
    }

    /// <summary>巡视用：照方向键那样移动一次焦点。</summary>
    internal static bool MoveForTest(MoveDirection dir, int id)
    {
        EventSystem? es = EventSystem.current;
        GameObject? sel = es != null ? es.currentSelectedGameObject : null;
        if (clone.State != MenuClone.Phase.Open || id != clone.Session || sel == null)
        {
            return false;
        }
        return ExecuteEvents.Execute(sel, new AxisEventData(es) { moveDir = dir }, ExecuteEvents.moveHandler);
    }

    // ───────────────────────────── 图集、销毁 ─────────────────────────────

    /// <summary>图标是大厅借来的图集精灵：关面板、离开大厅前解开（见 ChallengeMenu.UnbindIcons）。</summary>
    private static void Unbind()
    {
        foreach (Image?[] arr in new[] { icons, seals, glows })
        {
            for (int i = 0; i < arr.Length; i++)
            {
                try
                {
                    if (arr[i] != null)
                    {
                        arr[i]!.sprite = null;
                        arr[i]!.enabled = false;
                    }
                }
                catch
                {
                    // 物体已经没了。
                }
            }
        }
    }

    internal static void ReleaseArt(string why)
    {
        ForceClose(why);
        Unbind();
    }

    internal static void Destroy(string why)
    {
        ForceClose(why);
        Unbind();
        clone.Destroy(why);
        for (int i = 0; i < 4; i++)
        {
            binds[i] = null;
            icons[i] = null;
            seals[i] = null;
            glows[i] = null;
            names[i] = null;
        }
        desc = null;
        hint = null;
        begin = null;
        back = null;
    }

    // ───────────────────────────── 搭界面 ─────────────────────────────

    private static bool Build(GameObject go, MenuScreen ms)
    {
        clone.OnFinish = Unbind;
        Transform? content = go.transform.Find("Content");
        Transform? normal = content != null ? content.Find("NormalButton") : null;
        Transform? bt = go.transform.Find("Controls/ApplyButton");
        MenuButton? bb = bt != null ? bt.GetComponent<MenuButton>() : null;
        Text? descTpl = normal != null && normal.Find("DescriptionText") is Transform dt ? dt.GetComponent<Text>() : null;
        if (normal == null || bb == null || descTpl == null || normal.GetComponent<MenuButton>() == null)
        {
            throw new InvalidOperationException($"界面结构和预期不一样 / unexpected layout: normal={normal != null} back={bb != null} desc={descTpl != null}");
        }
        int layer = go.layer;
        // 一排束缚按钮：放在自己的容器里（Content 有竖排的布局组件，会把它们排成一列）。
        var row = new GameObject("Bindings", typeof(RectTransform));
        row.layer = layer;
        var rr = (RectTransform)row.transform;
        rr.SetParent(go.transform, false);
        rr.anchorMin = rr.anchorMax = new Vector2(0.5f, 0.5f);
        rr.pivot = new Vector2(0.5f, 0.5f);
        rr.anchoredPosition = new Vector2(0f, 40f);
        rr.sizeDelta = new Vector2(1600f, 420f);
        for (int i = 0; i < 4; i++)
        {
            binds[i] = MakeBinding(normal.gameObject, row.transform, i, layer);
        }
        // “开始”：复制底下的“返回”按钮，放在它上面。
        GameObject bg = Object.Instantiate(bb.gameObject, bb.transform.parent, false);
        bg.name = "BeginButton";
        var bgr = (RectTransform)bg.transform;
        bgr.anchoredPosition = bb.GetComponent<RectTransform>().anchoredPosition + new Vector2(0f, 190f);
        MenuClone.Unlocalize(bg.transform);
        Text? btxt = bg.transform.Find("Text") is Transform btt ? btt.GetComponent<Text>() : null;
        if (btxt != null)
        {
            btxt.text = L.Get("PAN_BEGIN");
        }
        begin = bg.GetComponent<MenuButton>();
        back = bb;
        MenuClone.Hook(begin, Begin, Cancel);
        MenuClone.Hook(back, Cancel, Cancel);
        desc = MenuClone.CloneText(descTpl, go.transform, "Description", new Vector2(0f, 330f), new Vector2(1900f, 170f), 40, TextAnchor.UpperCenter);
        hint = MenuClone.CloneText(descTpl, go.transform, "Hint", new Vector2(0f, -235f), new Vector2(2000f, 80f), 34, TextAnchor.MiddleCenter);
        hint.color = new Color(0.86f, 0.86f, 0.88f, 1f);
        // 原来的三个大按钮不要了（模板已经复制过）。
        content!.gameObject.SetActive(false);
        // 左右在四个束缚之间循环，下到“开始”，再下到“返回”。
        for (int i = 0; i < 4; i++)
        {
            MenuClone.Link(binds[i]!, back, begin, binds[(i + 3) % 4], binds[(i + 1) % 4]);
        }
        MenuClone.Link(begin, binds[0], back);
        MenuClone.Link(back, begin, binds[0]);
        ms.defaultHighlight = binds[0];
        ms.backButton = back;
        clone.FirstSelect = () => binds[0];
        return true;
    }

    private static MenuButton MakeBinding(GameObject template, Transform parent, int i, int layer)
    {
        GameObject b = Object.Instantiate(template, parent, false);
        b.name = "Binding " + Order[i];
        b.SetActive(true);
        var r = (RectTransform)b.transform;
        r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
        r.pivot = new Vector2(0.5f, 0.5f);
        r.anchoredPosition = new Vector2(RowX[i], 0f);
        r.sizeDelta = new Vector2(320f, 360f);
        r.localScale = Vector3.one;
        foreach (Transform ch in b.transform)
        {
            if (ch.name.StartsWith("Game Start Burst", StringComparison.Ordinal) || ch.name == "DescriptionText")
            {
                Object.Destroy(ch.gameObject);
            }
        }
        if (b.transform.Find("Selector") is RectTransform sel)
        {
            sel.anchoredPosition = Vector2.zero;
            sel.sizeDelta = new Vector2(440f, 400f);
            sel.localScale = Vector3.one;
        }
        if (b.transform.Find("CursorLeft") is RectTransform cl)
        {
            cl.anchoredPosition = new Vector2(-18f, 20f);
        }
        if (b.transform.Find("CursorRight") is RectTransform cr)
        {
            cr.anchoredPosition = new Vector2(18f, 20f);
        }
        Image? icon = null;
        if (b.transform.Find("Image") is RectTransform ir)
        {
            ir.anchoredPosition = new Vector2(0f, 40f);
            ir.sizeDelta = new Vector2(200f, 200f);
            ir.localScale = Vector3.one;
            icon = ir.GetComponent<Image>();
            if (icon != null)
            {
                icon.preserveAspect = true;
                // 图集是紧密打包的：按矩形画会带出旁边精灵的碎片，按精灵自己的网格画（见 MenuClone.AddImage）。
                icon.useSpriteMesh = true;
                icon.sprite = null;
                icon.enabled = false;
            }
        }
        icons[i] = icon;
        // 全套上时图标后面的一圈金光（工具栏上锁的光），在图标后面。
        Image glow = MenuClone.AddImage(b.transform, "Bound Glow", new Vector2(0f, 40f), new Vector2(300f, 300f), layer);
        if (icon != null)
        {
            glow.transform.SetSiblingIndex(icon.transform.GetSiblingIndex());
        }
        glow.gameObject.SetActive(false);
        glows[i] = glow;
        // 套上时盖在图标上的一层丝（工具栏上锁时的丝）。
        Image seal = MenuClone.AddImage(b.transform, "Bound Silk", new Vector2(0f, 40f), new Vector2(230f, 250f), layer);
        seal.gameObject.SetActive(false);
        seals[i] = seal;
        Text? name = null;
        if (b.transform.Find("Text") is RectTransform tr)
        {
            tr.anchorMin = tr.anchorMax = new Vector2(0.5f, 0.5f);
            tr.pivot = new Vector2(0.5f, 0.5f);
            tr.anchoredPosition = new Vector2(0f, -125f);
            tr.sizeDelta = new Vector2(320f, 64f);
            name = tr.GetComponent<Text>();
            if (name != null)
            {
                name.fontSize = 42;
                name.alignment = TextAnchor.MiddleCenter;
                name.text = "";
            }
            if (tr.Find("FlashEffect") is RectTransform fr)
            {
                fr.anchoredPosition = new Vector2(0f, -2.5f);
            }
        }
        MenuClone.Unlocalize(b.transform);
        names[i] = name;
        MenuButton mb = b.GetComponent<MenuButton>();
        // 套上 / 解开以后按钮不失去焦点（前作的束缚按钮也是这样）：游戏的 MenuButton 只有 Activate 类型确认后不取消选中。
        mb.buttonType = MenuButton.MenuButtonType.Activate;
        int k = i;
        MenuClone.Hook(mb, () => Toggle(k), Cancel);
        return mb;
    }
}
