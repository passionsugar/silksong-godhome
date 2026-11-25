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
/// [Godhome] 复制一份游戏主菜单的“模式选择”界面（UIManager.playModeMenuScreen），给万神殿的挑战面板和结算用。
/// 做法和雕像的挑战面板（ChallengeMenu）一样，见那边的说明：复制品挂在 UICanvas 下，去掉会开新游戏、改菜单状态的组件；
/// 淡入淡出照 UIManager.ShowMenu / HideMenu 的步骤和速度，由我们自己的协程做；背后自己一份暗幕；
/// 界面输入（acceptingInput、sendNavigationEvents、allowMouseInput）打开前记一份，关的时候只还原“还是我们写的值”的位。
/// 一个实例管一份复制品；同一时间只开一个（调用方保证）。
/// </summary>
internal sealed class MenuClone
{
    internal enum Phase
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

    private static readonly int ShowProp = Animator.StringToHash("show");
    private static readonly int HideProp = Animator.StringToHash("hide");
    private const float DimAlpha = 0.8f;

    private readonly string label;

    internal MenuClone(string label) => this.label = label;

    internal GameObject? Root { get; private set; }

    internal MenuScreen? Screen { get; private set; }

    internal CanvasGroup? Group { get; private set; }

    internal Text? Title { get; private set; }

    internal Phase State { get; private set; }

    internal int Session { get; private set; }

    internal bool Open => State is Phase.Opening or Phase.Open;

    internal bool Busy => State != Phase.Closed;

    /// <summary>界面输入打开后选中谁（null = 屏幕的默认按钮）。</summary>
    internal Func<Selectable?>? FirstSelect;

    /// <summary>撤掉 / 关完时调（解开借来的精灵、停自己的动画）。</summary>
    internal Action? OnFinish;

    private MonoBehaviour? host;
    private Coroutine? routine;
    private Action? onClosed;
    private bool useInput;
    private InputBits before;
    private InputBits ours;
    private Selectable? lastSel;

    internal MonoBehaviour? Host => host;

    /// <summary>
    /// 复制界面（已经有就直接用）。customize 在复制品上搭自己的东西，返回 false 或抛异常都算失败（复制品销毁）。
    /// </summary>
    internal bool Ensure(Func<GameObject, MenuScreen, bool> customize, out string note)
    {
        note = "";
        if (Root != null && Screen != null && Group != null)
        {
            return true;
        }
        UIManager ui = UIManager.instance;
        if (ui == null || ui.playModeMenuScreen == null)
        {
            note = "找不到游戏的“模式选择”界面 / play mode menu not found";
            GodhomePlugin.Log.LogWarning($"[Godhome] {label}: {note}");
            return false;
        }
        GameObject? go = null;
        try
        {
            MenuScreen tpl = ui.playModeMenuScreen;
            go = Object.Instantiate(tpl.gameObject, tpl.transform.parent, false);
            go.name = "Godhome " + label;
            go.SetActive(false);
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
            if (ms == null || cg == null || tt == null)
            {
                throw new InvalidOperationException($"界面结构和预期不一样 / unexpected layout: screen={ms != null} group={cg != null} title={tt != null}");
            }
            MakeDimmer(go, ui);
            if (!customize(go, ms))
            {
                throw new InvalidOperationException("搭界面失败 / customize failed");
            }
            Root = go;
            Screen = ms;
            Group = cg;
            Title = tt;
            note = $"{label}：复制游戏的“模式选择”界面 / cloned from the play mode menu";
            GodhomePlugin.Log.LogInfo($"[Godhome] {note}");
            return true;
        }
        catch (Exception e)
        {
            note = $"{label} 搭不出来 / build failed: {e.Message}";
            GodhomePlugin.Log.LogError($"[Godhome] {note}\n{e}");
            if (go != null)
            {
                Object.Destroy(go);
            }
            ClearRefs();
            return false;
        }
    }

    /// <summary>
    /// 淡入。input = 打开界面输入（有按钮的面板）；结算画面不用（自己读按键）。返回这次打开的编号，失败返回 0。
    /// </summary>
    internal int Show(MonoBehaviour runner, bool input)
    {
        if (State != Phase.Closed)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] {label} 还没关完，不重开 / still closing, not reopening");
            return 0;
        }
        if (runner == null || Root == null || Group == null || Screen == null)
        {
            return 0;
        }
        UIManager ui = UIManager.instance;
        InputHandler? ih = InputHandler.Instance;
        EventSystem? es = EventSystem.current;
        if (input && (ih == null || es == null || ui == null || ui.inputModule == null))
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] {label}：游戏的界面输入不全 / UI input missing");
            return 0;
        }
        try
        {
            useInput = input;
            if (input)
            {
                before = new InputBits { Accepting = ih!.acceptingInput, Nav = es!.sendNavigationEvents, Mouse = ui.inputModule.allowMouseInput };
                ours = before;
                if (es.currentSelectedGameObject != null)
                {
                    es.SetSelectedGameObject(null);
                }
                SetInput(false);
            }
            State = Phase.Opening;
            Session++;
            host = runner;
            lastSel = null;
            Group.alpha = 0f;
            Group.interactable = false;
            Root.SetActive(true);
            Fleur(true);
            routine = runner.StartCoroutine(ShowRoutine(Session));
            return Session;
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogError($"[Godhome] {label} 打开失败 / failed to open: {e}");
            ForceClose("open failed / 打开失败");
            return 0;
        }
    }

    private IEnumerator ShowRoutine(int id)
    {
        float t0 = Time.unscaledTime;
        float failsafe = 0f;
        int frames = 0;
        bool faded = false;
        bool input = !useInput;
        float speed = UIManager.instance != null ? UIManager.instance.MENU_FADE_SPEED : 3.2f;
        while (!faded || !input)
        {
            if (id != Session || State != Phase.Opening)
            {
                yield break;
            }
            bool failed = false;
            try
            {
                if (!faded)
                {
                    Group!.alpha += Time.unscaledDeltaTime * speed;
                    failsafe += Time.unscaledDeltaTime;
                    if (Group.alpha >= 0.95f || failsafe >= 2f)
                    {
                        Group.alpha = 1f;
                        Group.interactable = true;
                        faded = true;
                    }
                }
                if (!input && frames >= 2 && Time.unscaledTime - t0 >= 0.1f)
                {
                    SetInput(true);
                    Selectable? first = FirstSelect?.Invoke();
                    if (first != null)
                    {
                        UIManager.HighlightSelectableNoSound(first);
                        lastSel = first;
                    }
                    else
                    {
                        Screen!.HighlightDefault();
                    }
                    input = true;
                }
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogError($"[Godhome] {label} 淡入出错 / fade-in failed: {e}");
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
        if (id == Session && State == Phase.Opening)
        {
            State = Phase.Open;
            routine = null;
        }
    }

    /// <summary>淡出关掉；关完、界面输入还原以后调 closed。没开着返回 false（不会调 closed）。</summary>
    internal bool Hide(Action? closed)
    {
        if (!Open)
        {
            return false;
        }
        StopRoutine();
        State = Phase.Closing;
        onClosed = closed;
        try
        {
            if (useInput)
            {
                SetInput(false);
            }
            Fleur(false);
            Group!.interactable = false;
            routine = host!.StartCoroutine(HideRoutine(Session));
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] {label} 淡出出错，直接关 / fade-out failed, closing at once: {e.Message}");
            Finish(true);
        }
        return true;
    }

    private IEnumerator HideRoutine(int id)
    {
        float failsafe = 0f;
        float speed = UIManager.instance != null ? UIManager.instance.MENU_FADE_SPEED : 3.2f;
        while (true)
        {
            if (id != Session || State != Phase.Closing)
            {
                yield break;
            }
            bool done;
            try
            {
                Group!.alpha -= Time.unscaledDeltaTime * speed;
                failsafe += Time.unscaledDeltaTime;
                done = Group.alpha <= 0.05f || failsafe >= 2f;
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] {label} 淡出出错 / fade-out failed: {e.Message}");
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

    /// <summary>马上撤掉（不调回调）。已经关了就什么都不做。</summary>
    internal void ForceClose(string why)
    {
        if (State == Phase.Closed)
        {
            return;
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] {label} 直接撤掉 / closed at once ({why})");
        StopRoutine();
        onClosed = null;
        Finish(false);
    }

    private void Finish(bool callback)
    {
        routine = null;
        try
        {
            EventSystem? es = EventSystem.current;
            GameObject? sel = es != null ? es.currentSelectedGameObject : null;
            if (sel != null && Root != null && sel.transform.IsChildOf(Root.transform))
            {
                es!.SetSelectedGameObject(null);
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] {label} 清焦点出错 / deselect failed: {e.Message}");
        }
        try
        {
            if (Group != null)
            {
                Group.alpha = 0f;
                Group.interactable = false;
            }
            if (Root != null)
            {
                Root.SetActive(false);
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] {label} 藏起出错 / hide failed: {e.Message}");
        }
        if (useInput)
        {
            RestoreInput();
        }
        useInput = false;
        try
        {
            OnFinish?.Invoke();
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] {label} 收尾出错 / finish hook failed: {e.Message}");
        }
        State = Phase.Closed;
        lastSel = null;
        Action? cb = callback ? onClosed : null;
        onClosed = null;
        try
        {
            cb?.Invoke();
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogError($"[Godhome] {label} 关闭回调出错 / close callback failed: {e}");
        }
    }

    private void StopRoutine()
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
            GodhomePlugin.Log.LogWarning($"[Godhome] {label} 停协程出错 / stopping the routine failed: {e.Message}");
        }
        routine = null;
    }

    private void SetInput(bool on)
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

    private void RestoreInput()
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
            GodhomePlugin.Log.LogWarning($"[Godhome] {label} 还原界面输入出错 / restoring UI input failed: {e.Message}");
        }
        ours = before;
    }

    private void Fleur(bool show)
    {
        Animator? a = Screen != null ? Screen.topFleur : null;
        if (a == null)
        {
            return;
        }
        a.ResetTrigger(show ? HideProp : ShowProp);
        a.SetTrigger(show ? ShowProp : HideProp);
    }

    /// <summary>每帧（开着时）：焦点丢了就选回上次选中的按钮（见 ChallengeMenu.Tick）。返回现在选中的按钮。</summary>
    internal Selectable? Tick(Selectable? fallback)
    {
        if (Open && Root == null)
        {
            ForceClose("menu object gone / 界面物体没了");
            return null;
        }
        if (State != Phase.Open || Root == null || !useInput)
        {
            return null;
        }
        EventSystem? es = EventSystem.current;
        if (es == null)
        {
            return null;
        }
        GameObject? sel = es.currentSelectedGameObject;
        if (sel != null && sel.transform.IsChildOf(Root.transform))
        {
            Selectable? s = sel.GetComponent<Selectable>();
            if (s != null)
            {
                lastSel = s;
            }
            return s;
        }
        Selectable? again = lastSel != null ? lastSel : fallback;
        if (again != null && again.isActiveAndEnabled)
        {
            UIManager.HighlightSelectableNoSound(again);
        }
        return again;
    }

    /// <summary>销毁复制品（先同步撤掉）。没有就什么都不做。</summary>
    internal void Destroy(string why)
    {
        ForceClose(why);
        if (Root == null)
        {
            ClearRefs();
            return;
        }
        Object.Destroy(Root);
        ClearRefs();
        GodhomePlugin.Log.LogInfo($"[Godhome] {label} 已销毁 / destroyed ({why})");
    }

    private void ClearRefs()
    {
        Root = null;
        Screen = null;
        Group = null;
        Title = null;
        lastSel = null;
    }

    // ───────────────────────────── 搭界面的小工具（复制品内部用） ─────────────────────────────

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

    /// <summary>去掉自动本地化（不然打开时会被换回原来的字）。</summary>
    internal static void Unlocalize(Transform? t)
    {
        if (t == null)
        {
            return;
        }
        foreach (AutoLocalizeTextUI c in t.GetComponentsInChildren<AutoLocalizeTextUI>(true))
        {
            Object.DestroyImmediate(c);
        }
    }

    /// <summary>按钮确认时调 submit；取消键调 cancel（游戏的 MenuSelectable 看到有取消触发器就自己放取消音）。</summary>
    internal static void Hook(MenuButton b, Action submit, Action cancel)
    {
        b.OnSubmitPressed = new UnityEvent();
        b.OnSubmitPressed.AddListener(() => submit());
        b.cancelAction = CancelAction.DoNothing;
        EventTrigger et = b.gameObject.GetComponent<EventTrigger>();
        if (et == null)
        {
            et = b.gameObject.AddComponent<EventTrigger>();
        }
        et.triggers.Clear();
        var entry = new EventTrigger.Entry { eventID = EventTriggerType.Cancel };
        entry.callback.AddListener(_ => cancel());
        et.triggers.Add(entry);
    }

    internal static void Link(Selectable s, Selectable? up, Selectable? down, Selectable? left = null, Selectable? right = null)
    {
        Navigation nav = s.navigation;
        nav.mode = Navigation.Mode.Explicit;
        nav.selectOnUp = up;
        nav.selectOnDown = down;
        nav.selectOnLeft = left;
        nav.selectOnRight = right;
        s.navigation = nav;
    }

    /// <summary>挂在复制品下的一张图（UI Image，锚在中心）。</summary>
    internal static Image AddImage(Transform parent, string name, Vector2 pos, Vector2 size, int layer)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.layer = layer;
        var r = (RectTransform)go.transform;
        r.SetParent(parent, false);
        r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
        r.pivot = new Vector2(0.5f, 0.5f);
        r.anchoredPosition = pos;
        r.sizeDelta = size;
        r.localScale = Vector3.one;
        Image img = go.GetComponent<Image>();
        img.preserveAspect = true;
        // 借来的图集是紧密打包的（Tight）：Image 默认按精灵的矩形画，会把矩形里旁边精灵的碎片也画出来
        // （第一次实机结算截图里，纹章右上角多了一块别的图）。按精灵自己的网格画就只画它本身。
        img.useSpriteMesh = true;
        img.raycastTarget = false;
        img.enabled = false;
        return img;
    }

    /// <summary>复制一份游戏的文字物体当新的文字（同样的字体、材质），锚在中心。</summary>
    internal static Text CloneText(Text template, Transform parent, string name, Vector2 pos, Vector2 size, int fontSize, TextAnchor align)
    {
        GameObject go = Object.Instantiate(template.gameObject, parent, false);
        go.name = name;
        foreach (Transform ch in go.transform)
        {
            Object.Destroy(ch.gameObject);
        }
        Unlocalize(go.transform);
        foreach (ContentSizeFitter f in go.GetComponents<ContentSizeFitter>())
        {
            Object.DestroyImmediate(f);
        }
        var r = (RectTransform)go.transform;
        r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
        r.pivot = new Vector2(0.5f, 0.5f);
        r.anchoredPosition = pos;
        r.sizeDelta = size;
        r.localScale = Vector3.one;
        Text t = go.GetComponent<Text>();
        t.fontSize = fontSize;
        t.alignment = align;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.lineSpacing = 1f;
        t.text = "";
        t.raycastTarget = false;
        CanvasGroup? cg = go.GetComponent<CanvasGroup>();
        if (cg != null)
        {
            cg.alpha = 1f;
        }
        go.SetActive(true);
        return t;
    }

    /// <summary>镜头震动（丝之歌的做法：GlobalSettings.Camera 里的震动配置交给主镜头的震动管理器）。出错只记日志。</summary>
    internal static void Shake(bool big)
    {
        try
        {
            CameraShakeProfile p = big ? global::GlobalSettings.Camera.AverageShake : global::GlobalSettings.Camera.EnemyKillShake;
            CameraManagerReference m = global::GlobalSettings.Camera.MainCameraShakeManager;
            if (p != null && m != null && ModeController.Instance != null)
            {
                m.DoShake(p, ModeController.Instance, doFreeze: false, vibrate: true, sendWorldForce: false);
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 镜头震动出错 / camera shake failed: {e.Message}");
        }
    }

    /// <summary>菜单音效（游戏的 MenuAudioController：选中、确认、取消）。</summary>
    internal static void Sound(int which)
    {
        try
        {
            MenuAudioController? a = UIManager.instance != null ? UIManager.instance.uiAudioPlayer : null;
            if (a == null)
            {
                return;
            }
            switch (which)
            {
                case 0:
                    a.PlaySelect();
                    break;
                case 1:
                    a.PlaySubmit();
                    break;
                default:
                    a.PlayCancel();
                    break;
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 菜单音效出错 / menu sound failed: {e.Message}");
        }
    }
}
