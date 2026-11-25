using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace Godhome;

/// <summary>
/// [Godhome] 万神殿完成后的结算（照前作 BossDoorChallengeCompleteUI.Sequence）：
/// 中间一枚纹章（前作的“核心”，这里用丝之歌猎者纹章 Crest__0014_hunter_lvl4），四角四个束缚标记，底下用时 mm:ss。
/// 以前就有的标记一开始就亮着；这次新得的逐个闪出来（每个隔 0.5 秒），四个都用上时一起变金、镜头中震；
/// 然后纹章闪出（四种束缚全用 → 金色；没受伤 → 身后一圈光），用时淡入，最后等任意键，淡出。
/// 界面是游戏“模式选择”界面的复制品（MenuClone），只留标题和花饰，不开界面输入（自己读按键）。
/// </summary>
internal static class PantheonResultScreen
{
    private const string CrestAtlas = "crest";
    private const string CoreSprite = "Crest__0014_hunter_lvl4";
    private const string CoreGlowSprite = "Crest__0014_hunter_lvl4_equip_glow";
    private const string FlashAtlas = "core_glows";
    private const string FlashSprite = "white_light";

    private static readonly Vector2[] CapPos = { new(-430f, 210f), new(430f, 210f), new(-430f, -110f), new(430f, -110f) };
    private static readonly Color Gold = new(1f, 0.84f, 0.5f, 1f);
    private static readonly Color Silver = new(0.92f, 0.93f, 1f, 1f);

    private static readonly MenuClone clone = new("Pantheon Result Screen");
    private static readonly Image?[] caps = new Image?[4];
    private static readonly Image?[] capFlash = new Image?[4];
    private static Image? core;
    private static Image? coreGlow;
    private static Image? radiance;
    private static Image? coreFlash;
    private static Text? timer;
    private static Text? keyHint;
    private static CanvasGroup? timerGroup;
    private static Coroutine? sequence;
    private static Action? onDone;
    private static bool legacyInputOk = true;

    internal static bool Busy
    {
        get
        {
            if (clone.Busy && clone.Root == null)
            {
                // 复制品被别的东西销毁了：放结算的协程碰到它会抛异常停掉，状态却还停在“开着”，
                // LoomUI 会一直锁着操控和暂停（Codex 审查 0.8.0）。直接收尾，LoomUI 看到不忙了就会还原。
                clone.ForceClose("clone destroyed / 复制品没了");
            }
            return clone.Busy;
        }
    }

    /// <summary>序列放完、正在等按键（巡视用）。</summary>
    internal static bool Waiting { get; private set; }

    internal static string Note { get; private set; } = "";

    internal static string TimerText => timer != null ? timer.text : "";

    /// <summary>巡视用：在等按键时当作按了一下。</summary>
    internal static bool SkipForTest { get; set; }

    /// <summary>打开结算，放完、按键关掉后调 done。打不开返回 false（调用方退回简易结算）。</summary>
    internal static bool Show(MonoBehaviour runner, PantheonResult r, Action done)
    {
        if (clone.Busy || runner == null)
        {
            return false;
        }
        if (!clone.Ensure(Build, out string note))
        {
            Note = note;
            return false;
        }
        Note = note;
        try
        {
            clone.Title!.text = r.Def.DisplayName;
            bool wasAll = r.WasAllBindings;
            for (int i = 0; i < 4; i++)
            {
                SetSprite(caps[i], PantheonMenu.Icons[i].Atlas, PantheonMenu.Icons[i].Sprite);
                SetSprite(capFlash[i], FlashAtlas, FlashSprite);
                bool already = (r.WasBound & PantheonMenu.Order[i]) != 0;
                Show(caps[i], already);
                if (caps[i] != null)
                {
                    caps[i]!.color = wasAll ? Gold : Silver;
                }
                Show(capFlash[i], false);
            }
            SetSprite(core, CrestAtlas, CoreSprite);
            SetSprite(coreGlow, CrestAtlas, CoreGlowSprite);
            SetSprite(radiance, FlashAtlas, FlashSprite);
            SetSprite(coreFlash, FlashAtlas, FlashSprite);
            // 以前就完成过：纹章一开始就在（照前作，以前得过的全束缚、无伤也一开始就亮）。
            StyleCore(r.WasCompleted, r.WasAllBindings, r.WasNoHits);
            Show(coreFlash, false);
            int s = Mathf.RoundToInt(r.Seconds);
            timer!.text = $"{s / 60:00}:{s % 60:00}";
            timerGroup!.alpha = 0f;
            keyHint!.text = L.Get("PAN_RESULT_KEY");
            keyHint.color = new Color(1f, 1f, 1f, 0f);
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogError($"[Godhome] 结算填内容出错 / result screen setup failed: {e}");
            Unbind();
            return false;
        }
        int id = clone.Show(runner, input: false);
        if (id == 0)
        {
            Unbind();
            return false;
        }
        onDone = done;
        Waiting = false;
        SkipForTest = false;
        sequence = runner.StartCoroutine(Sequence(r, id));
        return true;
    }

    private static void SetSprite(Image? img, string atlas, string name)
    {
        if (img != null)
        {
            img.sprite = SanctumArt.Get(atlas, name);
        }
    }

    private static void Show(Image? img, bool on)
    {
        if (img != null)
        {
            img.enabled = on && img.sprite != null;
        }
    }

    /// <summary>纹章的样子：完成 → 银白；全束缚 → 金色；无伤 → 身后一圈光（前作的四种核心）。</summary>
    private static void StyleCore(bool shown, bool allBindings, bool noHits)
    {
        Show(core, shown);
        Show(coreGlow, shown);
        Show(radiance, shown && noHits);
        if (core != null)
        {
            core.color = allBindings ? Gold : Silver;
        }
        if (coreGlow != null)
        {
            coreGlow.color = allBindings ? new Color(1f, 0.8f, 0.4f, 0.9f) : new Color(0.85f, 0.9f, 1f, 0.6f);
        }
        if (radiance != null)
        {
            radiance.color = new Color(1f, 0.86f, 0.55f, 0.55f);
        }
    }

    private static bool Alive(int id) => clone.Session == id && clone.Open;

    /// <summary>等 seconds 秒（真实时间）。被撤掉就返回 false。</summary>
    private static IEnumerator Wait(float seconds, int id)
    {
        float t = 0f;
        while (t < seconds && Alive(id))
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
    }

    private static IEnumerator Sequence(PantheonResult r, int id)
    {
        // 前作：appearAnimDelay（2 秒）里界面出现；这里界面淡入约 0.3 秒，再停一下。
        yield return Wait(1.0f, id);
        for (int i = 0; i < 4 && Alive(id); i++)
        {
            if ((r.Bindings & PantheonMenu.Order[i]) == 0)
            {
                continue;
            }
            // 照前作 BindingIcon.DoAppearAnim：闪光，0.2 秒后图标出现；每个之间 0.5 秒。
            clone.Host!.StartCoroutine(Flash(capFlash[i], 0.45f, id, 340f));
            MenuClone.Sound(1);
            yield return Wait(0.2f, id);
            Show(caps[i], Alive(id));
            yield return Wait(0.3f, id);
        }
        if (!Alive(id))
        {
            yield break;
        }
        bool allNow = r.Bindings == Binding.All;
        if (allNow)
        {
            // 照前作 DoAllAppearAnim：四个一起再闪一次，变成“全解锁”的样子（这里是金色）。
            for (int i = 0; i < 4; i++)
            {
                clone.Host!.StartCoroutine(Flash(capFlash[i], 0.45f, id, 380f));
            }
            MenuClone.Shake(true);
            MenuClone.Sound(1);
            yield return Wait(0.2f, id);
            for (int i = 0; i < 4; i++)
            {
                if (caps[i] != null)
                {
                    caps[i]!.color = Gold;
                }
            }
        }
        // completionCapAppearDelay 0.75 秒后纹章闪出。
        yield return Wait(0.75f, id);
        if (!Alive(id))
        {
            yield break;
        }
        clone.Host!.StartCoroutine(Flash(coreFlash, 0.6f, id, 820f));
        MenuClone.Sound(1);
        yield return Wait(0.2f, id);
        StyleCore(true, allNow || r.WasAllBindings, !r.Damaged || r.WasNoHits);
        // 用时：timerFadeDelay 1 秒后淡入（timerFadeTime 2 秒）。
        yield return Wait(1f, id);
        for (float t = 0f; t <= 1.5f && Alive(id); t += Time.unscaledDeltaTime)
        {
            timerGroup!.alpha = t / 1.5f;
            yield return null;
        }
        if (timerGroup != null)
        {
            timerGroup.alpha = 1f;
        }
        // endAnimDelay 后等任意键。
        yield return Wait(1f, id);
        if (!Alive(id))
        {
            yield break;
        }
        Waiting = true;
        if (keyHint != null)
        {
            keyHint.color = new Color(1f, 1f, 1f, 0.45f);
        }
        while (Alive(id) && !AnyKey())
        {
            yield return null;
        }
        Waiting = false;
        if (!Alive(id))
        {
            yield break;
        }
        sequence = null;
        Action? done = onDone;
        onDone = null;
        if (!clone.Hide(() => done?.Invoke()))
        {
            done?.Invoke();
        }
    }

    /// <summary>一团白光：放大、淡出（前作的 flashEffects）。</summary>
    private static IEnumerator Flash(Image? img, float seconds, int id, float size)
    {
        if (img == null || img.sprite == null)
        {
            yield break;
        }
        img.enabled = true;
        RectTransform r = img.rectTransform;
        for (float t = 0f; t < seconds && Alive(id) && img != null; t += Time.unscaledDeltaTime)
        {
            float k = t / seconds;
            float s = size * Mathf.Lerp(0.5f, 1.4f, k);
            r.sizeDelta = new Vector2(s, s);
            img.color = new Color(1f, 0.95f, 0.85f, 0.95f * (1f - k));
            yield return null;
        }
        if (img != null)
        {
            img.enabled = false;
        }
    }

    /// <summary>任意键（照前作：手柄任意键或者键盘任意键；旧输入系统不可用时看游戏的确认、取消、跳、攻击）。</summary>
    private static bool AnyKey()
    {
        if (SkipForTest)
        {
            SkipForTest = false;
            return true;
        }
        InputHandler? ih = InputHandler.Instance;
        if (ih != null)
        {
            try
            {
                if (ih.gameController != null && ih.gameController.AnyButtonWasPressed)
                {
                    return true;
                }
            }
            catch
            {
                // 没有手柄。
            }
            HeroActions? a = ih.inputActions;
            if (a != null && (a.MenuSubmit.WasPressed || a.MenuCancel.WasPressed || a.Jump.WasPressed || a.Attack.WasPressed))
            {
                return true;
            }
        }
        if (legacyInputOk)
        {
            try
            {
                return Input.anyKeyDown;
            }
            catch (InvalidOperationException)
            {
                legacyInputOk = false;
            }
        }
        return false;
    }

    /// <summary>马上撤掉（不调 done）。</summary>
    internal static void ForceClose(string why)
    {
        onDone = null;
        Waiting = false;
        StopSequence();
        clone.ForceClose(why);
    }

    private static void StopSequence()
    {
        try
        {
            if (sequence != null && clone.Host != null)
            {
                clone.Host.StopCoroutine(sequence);
            }
        }
        catch
        {
            // 协程宿主已经没了。
        }
        sequence = null;
    }

    private static void Unbind()
    {
        foreach (Image? img in new[] { core, coreGlow, radiance, coreFlash, caps[0], caps[1], caps[2], caps[3], capFlash[0], capFlash[1], capFlash[2], capFlash[3] })
        {
            try
            {
                if (img != null)
                {
                    img.sprite = null;
                    img.enabled = false;
                }
            }
            catch
            {
                // 物体已经没了。
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
            caps[i] = null;
            capFlash[i] = null;
        }
        core = coreGlow = radiance = coreFlash = null;
        timer = keyHint = null;
        timerGroup = null;
    }

    private static bool Build(GameObject go, MenuScreen ms)
    {
        clone.OnFinish = Unbind;
        Transform? content = go.transform.Find("Content");
        Transform? controls = go.transform.Find("Controls");
        Text? title = go.transform.Find("Title") is Transform tt ? tt.GetComponent<Text>() : null;
        Transform? normal = content != null ? content.Find("NormalButton") : null;
        Text? descTpl = normal != null && normal.Find("DescriptionText") is Transform dt ? dt.GetComponent<Text>() : null;
        if (content == null || title == null || descTpl == null)
        {
            throw new InvalidOperationException($"界面结构和预期不一样 / unexpected layout: content={content != null} title={title != null} desc={descTpl != null}");
        }
        int layer = go.layer;
        Transform root = go.transform;
        Vector2 c = new(0f, 40f);
        radiance = MenuClone.AddImage(root, "Core Radiance", c, new Vector2(820f, 820f), layer);
        coreGlow = MenuClone.AddImage(root, "Core Glow", c, new Vector2(400f, 550f), layer);
        core = MenuClone.AddImage(root, "Core", c, new Vector2(320f, 440f), layer);
        coreFlash = MenuClone.AddImage(root, "Core Flash", c, new Vector2(820f, 820f), layer);
        for (int i = 0; i < 4; i++)
        {
            capFlash[i] = MenuClone.AddImage(root, "Cap Flash " + i, CapPos[i], new Vector2(340f, 340f), layer);
            caps[i] = MenuClone.AddImage(root, "Cap " + PantheonMenu.Order[i], CapPos[i], new Vector2(170f, 170f), layer);
        }
        var tg = new GameObject("Timer", typeof(RectTransform), typeof(CanvasGroup));
        tg.layer = layer;
        var tgr = (RectTransform)tg.transform;
        tgr.SetParent(root, false);
        tgr.anchorMin = tgr.anchorMax = new Vector2(0.5f, 0.5f);
        tgr.anchoredPosition = Vector2.zero;
        tgr.sizeDelta = new Vector2(1000f, 1000f);
        timerGroup = tg.GetComponent<CanvasGroup>();
        timerGroup.interactable = false;
        timerGroup.blocksRaycasts = false;
        timer = MenuClone.CloneText(title, tg.transform, "Timer Text", new Vector2(0f, -340f), new Vector2(900f, 120f), 72, TextAnchor.MiddleCenter);
        keyHint = MenuClone.CloneText(descTpl, root, "Key Hint", new Vector2(0f, -520f), new Vector2(1400f, 70f), 32, TextAnchor.MiddleCenter);
        content.gameObject.SetActive(false);
        if (controls != null)
        {
            controls.gameObject.SetActive(false);
        }
        ms.defaultHighlight = null;
        ms.backButton = null;
        return true;
    }
}
