using System;
using System.Collections;
using GlobalEnums;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Godhome;

/// <summary>
/// [Godhome] 雕像的挑战面板，用游戏自己的界面做（原版样式）。
///
/// 照前作神居的挑战面板（反编译里还留着 BossChallengeUI：Boss 名字、三个难度按钮、返回），
/// 拿丝之歌主菜单的“模式选择”界面（UIManager.playModeMenuScreen）复制一份：
/// 大标题和顶上的花饰、三个大按钮（选中时的光、左右花饰光标、闪光、名字和一行说明）、底下的“返回”。
/// 这个界面里本来就留着前作的神居按钮（GGButton），三个按钮正好对应三档难度。
/// 按钮的选中音、确认音、取消音都是游戏的 MenuSelectable 自己放的。
/// 复制品挂在 UIManager 的 UICanvas 下（和原界面同一个画布、同样的缩放），复制时去掉会开新游戏、改菜单状态的组件。
/// 层级按游戏里实际读到的结构（Debugging/UiDump，2026-11-21）：
///   PlaymodeMenuScreen/Title、TopFleur、Content/{NormalButton,SteelButton,GGButton}/{Text,DescriptionText,Image,Selector}、Controls/ApplyButton。
///
/// 淡入淡出照 UIManager.ShowMenu / HideMenu 的步骤和速度（MENU_FADE_SPEED、花饰的 show/hide、0.1 秒后打开界面输入、AfterFade 选默认按钮），
/// 但由我们自己的协程做（Codex 审查 v0.7.0 #1 #2）：不调 UIManager 的协程，不写它的 isFadingMenu / fadingMenuScreen，
/// 背后的暗幕也是自己的一份（照暂停菜单暗幕的图和 0.8 的不透明度），不和暂停菜单抢；随时能同步撤掉（ForceClose）。
/// 界面输入（acceptingInput、sendNavigationEvents、allowMouseInput）每次打开前记一份；关的时候只还原“还是我们写进去的那个值”的位，
/// 期间别处改过的不动。
/// </summary>
internal static class ChallengeMenu
{
    private enum Phase
    {
        Closed,
        Opening,
        Open,
        Closing,
    }

    private struct InputBits
    {
        public bool Accepting;
        public bool Nav;
        public bool Mouse;
    }

    private static readonly string[] TierButtons = { "NormalButton", "SteelButton", "GGButton" };

    // 难度图标：雕像底座下那三颗织者灯芯（weave/weaver_heat_lamp_core_anim0000），没打过是暗的，打过是金的（照前作用图标表示完成，不用文字）。
    private const string IconAtlas = "weave";
    private const string IconSprite = "weaver_heat_lamp_core_anim0000";
    private static readonly Color IconDim = new(0.42f, 0.42f, 0.47f, 1f);
    private static readonly Color IconDone = new(1f, 0.84f, 0.5f, 1f);
    private static readonly Color TitleBlack = new(0.82f, 0.72f, 0.96f, 1f);
    private static readonly int ShowProp = Animator.StringToHash("show");
    private static readonly int HideProp = Animator.StringToHash("hide");
    private const float DimAlpha = 0.8f;

    private static GameObject? root;
    private static MenuScreen? screen;
    private static CanvasGroup? group;
    private static Text? title;
    private static readonly MenuButton?[] tiers = new MenuButton?[3];
    private static readonly Text?[] names = new Text?[3];
    private static readonly Text?[] descs = new Text?[3];
    private static readonly Image?[] icons = new Image?[3];
    private static MenuButton? back;

    private static Phase phase;
    private static MonoBehaviour? host;
    private static Coroutine? routine;
    private static int session;
    private static Selectable? lastSel;
    private static Action<int>? onChoose;
    private static Action? onCancel;
    private static Action? onClosed;
    private static InputBits before;
    private static InputBits ours;

    /// <summary>面板开着（正在淡入或已经打开，可以选）。</summary>
    internal static bool Open => phase is Phase.Opening or Phase.Open;

    /// <summary>面板还没关完（包括正在淡出）。</summary>
    internal static bool Busy => phase != Phase.Closed;

    /// <summary>这次打开的编号（每次打开加一；巡视用来确认还是同一次）。</summary>
    internal static int Session => session;

    /// <summary>搭面板的说明（日志、巡视用）。</summary>
    internal static string Note { get; private set; } = "";

    /// <summary>当前选中的按钮：0..2 难度，3 返回，没选中面板里的按钮返回 -1（巡视核对用）。</summary>
    internal static int Selected
    {
        get
        {
            GameObject? sel = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (sel == null)
            {
                return -1;
            }
            for (int i = 0; i < 3; i++)
            {
                if (tiers[i] != null && sel == tiers[i]!.gameObject)
                {
                    return i;
                }
            }
            return back != null && sel == back.gameObject ? 3 : -1;
        }
    }

    /// <summary>标题现在显示的文字（巡视核对用）。</summary>
    internal static string TitleText => title != null ? title.text.TrimEnd('\n') : "";

    /// <summary>难度按钮上的名字（巡视核对用；游戏的 FixVerticalAlign 会在字后面补一个换行，去掉再比）。</summary>
    internal static string TierText(int i) => i >= 0 && i < 3 && names[i] != null ? names[i]!.text.TrimEnd('\n') : "";

    /// <summary>巡视用：直接选中第 i 个难度按钮（不出声）。</summary>
    internal static void Highlight(int i, int id)
    {
        if (phase == Phase.Open && id == session && i >= 0 && i < 3 && tiers[i] != null)
        {
            UIManager.HighlightSelectableNoSound(tiers[i]!);
        }
    }

    /// <summary>巡视用：照游戏的输入模块收到方向键那样，给当前选中的按钮发一次移动（走按钮自己的导航）。</summary>
    internal static bool MoveForTest(MoveDirection dir, int id)
    {
        EventSystem? es = EventSystem.current;
        GameObject? sel = es != null ? es.currentSelectedGameObject : null;
        if (phase != Phase.Open || id != session || sel == null)
        {
            return false;
        }
        var data = new AxisEventData(es) { moveDir = dir };
        return ExecuteEvents.Execute(sel, data, ExecuteEvents.moveHandler);
    }

    /// <summary>巡视用：照游戏的输入模块收到“确认”（0..2 难度，3 返回）或“取消”键那样发给按钮。id 不是这次打开的就不发。</summary>
    internal static bool PressForTest(int i, int id, bool cancel = false)
    {
        MenuButton? b = i >= 0 && i < 3 ? tiers[i] : back;
        if (phase != Phase.Open || id != session || b == null || EventSystem.current == null)
        {
            return false;
        }
        var data = new BaseEventData(EventSystem.current);
        return cancel
            ? ExecuteEvents.Execute(b.gameObject, data, ExecuteEvents.cancelHandler)
            : ExecuteEvents.Execute(b.gameObject, data, ExecuteEvents.submitHandler);
    }

    /// <summary>
    /// 打开面板，返回这次打开的编号（大于 0）。搭不出来、游戏的界面输入不全、上一次还没关完都返回 0，调用方退回别的做法。
    /// runner 跑淡入淡出的协程。
    /// </summary>
    internal static int Show(MonoBehaviour runner, BossDef boss, bool black, int[] clears, Action<int> choose, Action cancel)
    {
        if (phase != Phase.Closed)
        {
            GodhomePlugin.Log.LogWarning("[Godhome] 挑战面板还没关完，不重开 / challenge menu still closing, not reopening");
            return 0;
        }
        if (runner == null || !Ensure())
        {
            return 0;
        }
        UIManager ui = UIManager.instance;
        InputHandler? ih = InputHandler.Instance;
        EventSystem? es = EventSystem.current;
        // 碰输入、显示之前先确认游戏的界面输入都在（缺了面板就没法操作，Codex 审查 v0.7.0 #3）。
        if (ih == null || es == null || ui == null || ui.inputModule == null || group == null || screen == null)
        {
            Note = $"游戏的界面输入不全 / UI input missing: input={ih != null} eventSystem={es != null} module={(ui != null && ui.inputModule != null)}";
            GodhomePlugin.Log.LogWarning($"[Godhome] {Note}");
            return 0;
        }
        try
        {
            before = new InputBits { Accepting = ih.acceptingInput, Nav = es.sendNavigationEvents, Mouse = ui.inputModule.allowMouseInput };
            ours = before;
            phase = Phase.Opening;
            session++;
            host = runner;
            lastSel = tiers[0];
            onChoose = choose;
            onCancel = cancel;
            title!.text = boss.DisplayName;
            title.color = black ? TitleBlack : Color.white;
            Sprite? icon = SanctumArt.Get(IconAtlas, IconSprite);
            for (int i = 0; i < 3; i++)
            {
                if (names[i] != null)
                {
                    names[i]!.text = L.Get("TIER_" + i);
                }
                if (descs[i] != null)
                {
                    descs[i]!.text = L.Get("TIER_DESC_" + i);
                }
                if (icons[i] != null)
                {
                    icons[i]!.sprite = icon;
                    icons[i]!.color = i < clears.Length && clears[i] > 0 ? IconDone : IconDim;
                    icons[i]!.gameObject.SetActive(icon != null);
                }
            }
            if (es.currentSelectedGameObject != null)
            {
                es.SetSelectedGameObject(null);
            }
            // 和 ShowMenu 一样：淡入期间不收界面输入。
            SetInput(false);
            group.alpha = 0f;
            group.interactable = false;
            root!.SetActive(true);
            Fleur(true);
            routine = runner.StartCoroutine(ShowRoutine(session));
            return session;
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogError($"[Godhome] 挑战面板打开失败 / challenge menu failed to open: {e}");
            ForceClose("open failed / 打开失败");
            return 0;
        }
    }

    private static IEnumerator ShowRoutine(int id)
    {
        float t0 = Time.unscaledTime;
        float failsafe = 0f;
        int frames = 0;
        bool faded = false;
        bool input = false;
        float speed = UIManager.instance != null ? UIManager.instance.MENU_FADE_SPEED : 3.2f;
        while (!faded || !input)
        {
            if (id != session || phase != Phase.Opening)
            {
                yield break;
            }
            bool failed = false;
            try
            {
                if (!faded)
                {
                    // FadeInCanvasGroup：每帧加 unscaledDeltaTime × MENU_FADE_SPEED，到 0.95 算满，最多 2 秒；满了才能按。
                    group!.alpha += Time.unscaledDeltaTime * speed;
                    failsafe += Time.unscaledDeltaTime;
                    if (group.alpha >= 0.95f || failsafe >= 2f)
                    {
                        group.alpha = 1f;
                        group.interactable = true;
                        faded = true;
                    }
                }
                // ShowMenu：隔一帧、等 0.1 秒、再隔一帧才打开界面输入，然后选默认按钮（AfterFade）。
                if (!input && frames >= 2 && Time.unscaledTime - t0 >= 0.1f)
                {
                    SetInput(true);
                    screen!.HighlightDefault();
                    input = true;
                }
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogError($"[Godhome] 挑战面板淡入出错 / challenge menu fade-in failed: {e}");
                failed = true;
            }
            if (failed)
            {
                ForceClose("fade-in failed / 淡入出错");
                yield break;
            }
            frames++;
            yield return null;
        }
        if (id == session && phase == Phase.Opening)
        {
            phase = Phase.Open;
            routine = null;
        }
    }

    /// <summary>
    /// 关掉面板（不回调选择）：淡出完、界面输入还原以后才调 closed。面板没开着就返回 false（不会调 closed）。
    /// </summary>
    internal static bool Hide(Action? closed)
    {
        if (!Open)
        {
            return false;
        }
        StopRoutine();
        phase = Phase.Closing;
        onChoose = null;
        onCancel = null;
        onClosed = closed;
        try
        {
            // HideMenu：先停界面输入、花饰收起，再淡出。
            SetInput(false);
            Fleur(false);
            group!.interactable = false;
            routine = host!.StartCoroutine(HideRoutine(session));
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 挑战面板淡出出错，直接关 / challenge menu fade-out failed, closing at once: {e.Message}");
            Finish(true);
        }
        return true;
    }

    private static IEnumerator HideRoutine(int id)
    {
        float failsafe = 0f;
        float speed = UIManager.instance != null ? UIManager.instance.MENU_FADE_SPEED : 3.2f;
        while (true)
        {
            if (id != session || phase != Phase.Closing)
            {
                yield break;
            }
            bool done;
            try
            {
                // FadeOutCanvasGroup：每帧减 unscaledDeltaTime × MENU_FADE_SPEED，到 0.05 算完，最多 2 秒。
                group!.alpha -= Time.unscaledDeltaTime * speed;
                failsafe += Time.unscaledDeltaTime;
                done = group.alpha <= 0.05f || failsafe >= 2f;
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 挑战面板淡出出错 / challenge menu fade-out failed: {e.Message}");
                done = true;
            }
            if (done)
            {
                break;
            }
            yield return null;
        }
        Finish(true);
    }

    /// <summary>
    /// 马上撤掉面板（死亡、转场、读档、回标题、模式重置、出错）：停掉淡入淡出，藏起面板，
    /// 只还原还是我们写进去的界面输入位，只清掉还选在面板里的焦点，不调任何回调。已经关了就什么都不做。
    /// </summary>
    internal static void ForceClose(string why)
    {
        if (phase == Phase.Closed)
        {
            return;
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 挑战面板直接撤掉 / challenge menu closed at once ({why})");
        StopRoutine();
        onClosed = null;
        Finish(false);
    }

    /// <summary>收尾（正常关和直接撤掉都走这里）。</summary>
    private static void Finish(bool callback)
    {
        routine = null;
        try
        {
            EventSystem? es = EventSystem.current;
            GameObject? sel = es != null ? es.currentSelectedGameObject : null;
            if (sel != null && root != null && sel.transform.IsChildOf(root.transform))
            {
                es!.SetSelectedGameObject(null);
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 挑战面板清焦点出错 / challenge menu deselect failed: {e.Message}");
        }
        try
        {
            if (group != null)
            {
                group.alpha = 0f;
                group.interactable = false;
            }
            if (root != null)
            {
                root.SetActive(false);
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 挑战面板藏起出错 / challenge menu hide failed: {e.Message}");
        }
        RestoreInput();
        UnbindIcons();
        phase = Phase.Closed;
        onChoose = null;
        onCancel = null;
        lastSel = null;
        Action? cb = callback ? onClosed : null;
        onClosed = null;
        try
        {
            cb?.Invoke();
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogError($"[Godhome] 挑战面板关闭回调出错 / challenge menu close callback failed: {e}");
        }
    }

    private static void StopRoutine()
    {
        try
        {
            if (routine != null && host != null)
            {
                host.StopCoroutine(routine);
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 停挑战面板协程出错 / stopping the challenge menu routine failed: {e.Message}");
        }
        routine = null;
    }

    /// <summary>和 InputHandler.StartUIInput / StopUIInput 一样写三个界面输入位，并记下我们写的值。</summary>
    private static void SetInput(bool on)
    {
        InputHandler? ih = InputHandler.Instance;
        EventSystem? es = EventSystem.current;
        UIManager? ui = UIManager.instance;
        if (ih != null)
        {
            if (on)
            {
                ih.StartAcceptingInput();
            }
            else
            {
                ih.StopAcceptingInput();
            }
            ours.Accepting = on;
        }
        if (es != null)
        {
            es.sendNavigationEvents = on;
            ours.Nav = on;
        }
        if (ui != null && ui.inputModule != null)
        {
            ui.inputModule.allowMouseInput = on;
            ours.Mouse = on;
        }
    }

    /// <summary>把打开前的界面输入还回去：只还“现在还是我们写的那个值”的位（期间游戏或别的界面改过的不动）。</summary>
    private static void RestoreInput()
    {
        try
        {
            InputHandler? ih = InputHandler.Instance;
            if (ih != null && ih.acceptingInput == ours.Accepting && ours.Accepting != before.Accepting)
            {
                if (before.Accepting)
                {
                    ih.StartAcceptingInput();
                }
                else
                {
                    ih.StopAcceptingInput();
                }
            }
            EventSystem? es = EventSystem.current;
            if (es != null && es.sendNavigationEvents == ours.Nav)
            {
                es.sendNavigationEvents = before.Nav;
            }
            UIManager? ui = UIManager.instance;
            if (ui != null && ui.inputModule != null && ui.inputModule.allowMouseInput == ours.Mouse)
            {
                ui.inputModule.allowMouseInput = before.Mouse;
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 还原界面输入出错 / restoring UI input failed: {e.Message}");
        }
        ours = before;
    }

    private static void Fleur(bool show)
    {
        Animator? a = screen != null ? screen.topFleur : null;
        if (a == null)
        {
            return;
        }
        a.ResetTrigger(show ? HideProp : ShowProp);
        a.SetTrigger(show ? ShowProp : HideProp);
    }

    /// <summary>
    /// 每帧（面板开着时，LoomUI 调）：鼠标点了空白处、焦点丢了，就选回上次选中的按钮，手柄和键盘能接着用
    /// （游戏的输入模块这时会去选 firstSelectedGameObject，那不是我们的按钮，Codex 审查 v0.7.0 低 #2）。
    /// </summary>
    internal static void Tick()
    {
        if (Open && root == null)
        {
            // 复制的界面被别处销毁了（比如 UIManager 换了一个）：收尾，LoomUI 会退回简易面板。
            ForceClose("menu object gone / 界面物体没了");
            return;
        }
        if (phase != Phase.Open || root == null)
        {
            return;
        }
        EventSystem? es = EventSystem.current;
        if (es == null)
        {
            return;
        }
        GameObject? sel = es.currentSelectedGameObject;
        if (sel != null && sel.transform.IsChildOf(root.transform))
        {
            Selectable? s = sel.GetComponent<Selectable>();
            if (s != null)
            {
                lastSel = s;
            }
            return;
        }
        Selectable? again = lastSel != null ? lastSel : tiers[0];
        if (again != null && again.isActiveAndEnabled)
        {
            UIManager.HighlightSelectableNoSound(again);
        }
    }

    private static void Choose(int tier)
    {
        if (!Open)
        {
            return;
        }
        Action<int>? cb = onChoose;
        cb?.Invoke(tier);
    }

    private static void Cancel()
    {
        if (!Open)
        {
            return;
        }
        Action? cb = onCancel;
        cb?.Invoke();
    }

    /// <summary>图标用的是大厅借来的图集精灵：关面板、离开大厅前解开，免得界面上留着已经销毁的精灵（Codex 审查 v0.7.0 #4）。</summary>
    private static void UnbindIcons()
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (icons[i] != null)
                {
                    icons[i]!.sprite = null;
                    icons[i]!.gameObject.SetActive(false);
                }
            }
            catch
            {
                // 图标物体已经没了：不用管。
            }
        }
    }

    /// <summary>大厅的图集要还了：面板开着就撤掉，图标解开。</summary>
    internal static void ReleaseArt(string why)
    {
        ForceClose(why);
        UnbindIcons();
    }

    // ───────────────────────────── 复制游戏的“模式选择”界面 ─────────────────────────────

    private static bool Ensure()
    {
        if (root != null && screen != null && group != null)
        {
            return true;
        }
        UIManager ui = UIManager.instance;
        if (ui == null || ui.playModeMenuScreen == null)
        {
            Note = "找不到游戏的“模式选择”界面 / play mode menu not found";
            GodhomePlugin.Log.LogWarning($"[Godhome] {Note}");
            return false;
        }
        GameObject? go = null;
        try
        {
            MenuScreen tpl = ui.playModeMenuScreen;
            go = Object.Instantiate(tpl.gameObject, tpl.transform.parent, false);
            go.name = "Godhome Challenge Screen";
            go.SetActive(false);
            // 会开新游戏（StartGameEventTrigger）、按条件藏按钮和改菜单状态（MenuButtonList）、取消时跳回存档界面（EventTrigger）的组件都去掉。
            foreach (MenuButtonList c in go.GetComponentsInChildren<MenuButtonList>(true))
            {
                Object.DestroyImmediate(c);
            }
            foreach (StartGameEventTrigger c in go.GetComponentsInChildren<StartGameEventTrigger>(true))
            {
                Object.DestroyImmediate(c);
            }
            foreach (EventTrigger c in go.GetComponentsInChildren<EventTrigger>(true))
            {
                Object.DestroyImmediate(c);
            }
            MenuScreen ms = go.GetComponent<MenuScreen>();
            CanvasGroup cg = go.GetComponent<CanvasGroup>();
            Transform? t = go.transform.Find("Title");
            Text? tt = t != null ? t.GetComponent<Text>() : null;
            Unlocalize(t);
            Transform? content = go.transform.Find("Content");
            var found = new MenuButton?[3];
            for (int i = 0; i < 3; i++)
            {
                Transform? b = content != null ? content.Find(TierButtons[i]) : null;
                found[i] = b != null ? b.GetComponent<MenuButton>() : null;
                tiers[i] = found[i];
                names[i] = null;
                descs[i] = null;
                icons[i] = null;
                if (b == null || found[i] == null)
                {
                    continue;
                }
                b.gameObject.SetActive(true);
                Transform? nt = b.Find("Text");
                Transform? dt = b.Find("DescriptionText");
                Transform? it = b.Find("Image");
                Unlocalize(nt);
                Unlocalize(dt);
                names[i] = nt != null ? nt.GetComponent<Text>() : null;
                descs[i] = dt != null ? dt.GetComponent<Text>() : null;
                icons[i] = it != null ? it.GetComponent<Image>() : null;
                if (icons[i] != null)
                {
                    // 原来那张大图（540×217，在按钮中间偏左）在游戏里只在主菜单加载，这里换成小图标，放在名字左边、名字和说明之间的高度。
                    RectTransform ir = icons[i]!.rectTransform;
                    icons[i]!.preserveAspect = true;
                    icons[i]!.useSpriteMesh = true;
                    icons[i]!.sprite = null;
                    icons[i]!.gameObject.SetActive(false);
                    ir.localScale = Vector3.one;
                    ir.sizeDelta = new Vector2(110f, 110f);
                    float left = -137.5f;
                    float y = -13f;
                    if (b is RectTransform brt && nt is RectTransform nrt)
                    {
                        float textW = brt.sizeDelta.x * (nrt.anchorMax.x - nrt.anchorMin.x) + nrt.sizeDelta.x;
                        left = nrt.anchoredPosition.x - textW / 2f;
                        y = dt is RectTransform drt ? (nrt.anchoredPosition.y + drt.anchoredPosition.y) / 2f : nrt.anchoredPosition.y;
                    }
                    ir.anchoredPosition = new Vector2(left - 80f, y);
                }
                // 开新游戏时的光效（Game Start Burst，带镜头震动）不要。
                foreach (Transform ch in b)
                {
                    if (ch.name.StartsWith("Game Start Burst", StringComparison.Ordinal))
                    {
                        ch.gameObject.SetActive(false);
                    }
                }
                int tier = i;
                Hook(found[i]!, () => Choose(tier));
            }
            Transform? bt = go.transform.Find("Controls/ApplyButton");
            MenuButton? bb = bt != null ? bt.GetComponent<MenuButton>() : null;
            if (bb != null)
            {
                Hook(bb, Cancel);
            }
            if (ms == null || cg == null || tt == null || bb == null || Array.Exists(found, x => x == null))
            {
                throw new InvalidOperationException($"界面结构和预期不一样 / unexpected layout: screen={ms != null} group={cg != null} title={tt != null} back={bb != null} tiers={string.Join(",", Array.ConvertAll(found, x => x != null))}");
            }
            // 上下键在三个难度和“返回”之间循环。
            Link(found[0]!, bb, found[1]!);
            Link(found[1]!, found[0]!, found[2]!);
            Link(found[2]!, found[1]!, bb);
            Link(bb, found[2]!, found[0]!);
            ms.defaultHighlight = found[0];
            ms.backButton = bb;
            MakeDimmer(go, ui);
            root = go;
            screen = ms;
            group = cg;
            title = tt;
            back = bb;
            Note = "挑战面板：复制游戏的“模式选择”界面 / challenge menu cloned from the play mode menu";
            GodhomePlugin.Log.LogInfo($"[Godhome] {Note}");
            return true;
        }
        catch (Exception e)
        {
            Note = "挑战面板搭不出来 / challenge menu build failed: " + e.Message;
            GodhomePlugin.Log.LogError($"[Godhome] {Note}\n{e}");
            if (go != null)
            {
                Object.Destroy(go);
            }
            ClearRefs();
            return false;
        }
    }

    /// <summary>
    /// 面板背后的暗幕：照暂停菜单的暗幕（UICanvas/ModalDimmer 的图、颜色，不透明度 0.8），但用自己的一份，
    /// 放在面板最底下，跟着面板一起淡入淡出（不和暂停菜单共用，Codex 审查 v0.7.0 #2）。
    /// </summary>
    private static void MakeDimmer(GameObject screenGo, UIManager ui)
    {
        var d = new GameObject("Godhome Dimmer", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(CanvasGroup));
        d.layer = screenGo.layer;
        var r = (RectTransform)d.transform;
        r.SetParent(screenGo.transform, false);
        r.SetAsFirstSibling();
        r.anchorMin = Vector2.zero;
        r.anchorMax = Vector2.one;
        r.pivot = new Vector2(0.5f, 0.5f);
        r.anchoredPosition = Vector2.zero;
        r.localScale = Vector3.one;
        // 面板铺满画布；再大一圈，宽屏和缩放下也盖得住。
        r.sizeDelta = new Vector2(1200f, 1200f);
        Image img = d.GetComponent<Image>();
        Image? src = ui.modalDimmer != null ? ui.modalDimmer.GetComponent<Image>() : null;
        if (src != null)
        {
            img.sprite = src.sprite;
            img.type = src.type;
            img.color = src.color;
            if (src.material != null && src.material != src.defaultMaterial)
            {
                img.material = src.material;
            }
        }
        else
        {
            img.color = Color.black;
        }
        img.raycastTarget = true;
        CanvasGroup cg = d.GetComponent<CanvasGroup>();
        cg.alpha = DimAlpha;
        cg.interactable = false;
        cg.blocksRaycasts = true;
    }

    /// <summary>去掉自动本地化（不然打开时会被换回原来的“经典”“钢铁之魂”等字）。</summary>
    private static void Unlocalize(Transform? t)
    {
        if (t == null)
        {
            return;
        }
        foreach (AutoLocalizeTextUI c in t.GetComponents<AutoLocalizeTextUI>())
        {
            Object.DestroyImmediate(c);
        }
    }

    /// <summary>按钮确认时调我们的回调；取消键（返回）也调我们的取消（游戏的 MenuSelectable 看到有取消触发器就自己放取消音）。</summary>
    private static void Hook(MenuButton b, Action submit)
    {
        b.OnSubmitPressed = new UnityEvent();
        b.OnSubmitPressed.AddListener(() => submit());
        b.cancelAction = CancelAction.DoNothing;
        EventTrigger et = b.gameObject.AddComponent<EventTrigger>();
        var entry = new EventTrigger.Entry { eventID = EventTriggerType.Cancel };
        entry.callback.AddListener(_ => Cancel());
        et.triggers.Add(entry);
    }

    private static void Link(Selectable s, Selectable up, Selectable down)
    {
        Navigation nav = s.navigation;
        nav.mode = Navigation.Mode.Explicit;
        nav.selectOnUp = up;
        nav.selectOnDown = down;
        nav.selectOnLeft = null;
        nav.selectOnRight = null;
        s.navigation = nav;
    }

    private static void ClearRefs()
    {
        root = null;
        screen = null;
        group = null;
        title = null;
        back = null;
        lastSel = null;
        for (int i = 0; i < 3; i++)
        {
            tiers[i] = null;
            names[i] = null;
            descs[i] = null;
            icons[i] = null;
        }
    }

    /// <summary>回标题、模式重置时销毁复制出来的界面（先同步撤掉，再销毁；下次打开再复制一份）。没有就什么都不做。</summary>
    internal static void Destroy(string why)
    {
        ForceClose(why);
        if (root == null)
        {
            ClearRefs();
            return;
        }
        UnbindIcons();
        Object.Destroy(root);
        ClearRefs();
        GodhomePlugin.Log.LogInfo($"[Godhome] 挑战面板已销毁 / challenge menu destroyed ({why})");
    }
}
