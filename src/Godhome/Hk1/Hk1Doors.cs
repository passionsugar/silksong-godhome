using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Godhome;

/// <summary>
/// [Godhome] 前作神居的五扇万神殿的门：前厅四扇（GG_Challenge_Door (1)…(4)）、屋顶一扇（GG_Final_Challenge_Door），
/// 接到本作的五个万神殿（data/hk1.json 的 doors）。门本身、门前的挑战界面（BossDoorChallengeUI：四个束缚按钮、锁链动画）、
/// 回来时的开门动画都用前作的；名字、说明、门徽、束缚图标换成本作的；开门的条件、完成情况读我们的记录（data/pantheons.json 的 requires）。
/// </summary>
internal static class Hk1Doors
{
    private static Dictionary<string, string> doors = new(StringComparer.Ordinal);

    /// <summary>门的物体 id → 万神殿 id（这次加载的场景里）。</summary>
    private static readonly Dictionary<int, PantheonDef> byDoor = new();

    /// <summary>门前选好束缚、前作的开门动画还在放：等 FSM 发起转场时改成我们的万神殿（FilterTransition）。</summary>
    internal static (PantheonDef Def, Binding Bindings, float Since)? Pending { get; private set; }

    internal static void Load(JObject? o)
    {
        doors = o?.Properties().ToDictionary(p => p.Name, p => p.Value.ToString(), StringComparer.Ordinal) ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>万神殿 id → （门在哪个场景、门的物体名）。</summary>
    internal static (string Scene, string Door)? DoorOf(string pantheonId)
    {
        foreach (KeyValuePair<string, string> kv in doors)
        {
            if (kv.Value == pantheonId)
            {
                return (kv.Key.StartsWith("GG_Final", StringComparison.Ordinal) ? Hk1Lobby.Roof : Hk1Lobby.Atrium, kv.Key);
            }
        }
        return null;
    }

    internal static PantheonDef? DefOf(BossSequenceDoor? door) => door != null && byDoor.TryGetValue(door.GetInstanceID(), out PantheonDef? p) ? p : null;

    /// <summary>屋顶那扇门（前作万神殿的第五扇）的物体名。</summary>
    internal const string FinalDoor = "GG_Final_Challenge_Door";

    /// <summary>调试（前作巡视期间）：前厅当第五扇门已经开了（照玩家一到四门都通关的样子），去屋顶的出口 top2 才在，走玩家上屋顶那条路。</summary>
    internal static bool DebugFinalOpen { get; set; }

    /// <summary>这扇门（按物体名）接的万神殿开了没有（没接上的门当没开）。</summary>
    internal static bool Unlocked(string doorName)
    {
        if (DebugFinalOpen && doorName == FinalDoor)
        {
            return true;
        }
        PantheonDef? p = doors.TryGetValue(doorName, out string? id) ? PantheonCatalog.Find(id) : null;
        return p != null && ModeController.PantheonUnlocked(p);
    }

    internal static void Forget()
    {
        byDoor.Clear();
        iconBox.Clear();
        Pending = null;
    }

    internal static void ClearPending() => Pending = null;

    /// <summary>前厅或屋顶刚加载完（门的 Start 还没跑）：认出门，接上万神殿；第四扇门前作关在笼子里，按我们的开门条件开笼。</summary>
    internal static void Prepare(Scene scene)
    {
        byDoor.Clear();
        // 上一个房间里打开过的挑战界面已经跟着场景卸掉了（Codex 审查：表只增不减）。
        iconBox.Clear();
        Pending = null;
        int n = 0;
        foreach (BossSequenceDoor d in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<BossSequenceDoor>(true)))
        {
            if (!doors.TryGetValue(d.name, out string? id))
            {
                continue;
            }
            PantheonDef? p = PantheonCatalog.Find(id);
            if (p == null)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] hk1.json：门 {d.name} 的万神殿不在表里 / unknown pantheon for door: {id}");
                continue;
            }
            byDoor[d.GetInstanceID()] = p;
            // 万神殿里死了照前作回到这扇门前：回程门那里放一个复活点。
            Hk1Lobby.AddDeathMarker(scene, ModeController.DoorSpot(p.Id), d.dreamReturnGate, d.transform);
            n++;
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 前作神居 / hk1 godhome: {scene.name} 的万神殿的门 / pantheon doors: {n}（"
            + string.Join("，", byDoor.Values.Select(p => $"{p.Id}{(ModeController.PantheonUnlocked(p) ? "" : " 关着 / locked")}")) + "）");
        // 第四扇门前作关在骨笼里（前三扇都完成才开，读前作的 PlayerData）：按我们的开门条件，开着的门不放笼子。
        // 加载回调里场景还在激活，当场关会报“正在激活”（实机日志），下一帧再关（这时画面还是黑的）；笼子自己的 Start 不跑（DoorCageStartPatch）。
        List<BossSequenceDoor> mapped = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<BossSequenceDoor>(true)).Where(d => DefOf(d) != null).ToList();
        foreach (BossDoorCage cage in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<BossDoorCage>(true)))
        {
            BossSequenceDoor? door = mapped.OrderBy(d => Vector2.Distance(d.transform.position, cage.transform.position)).FirstOrDefault();
            PantheonDef? p = DefOf(door);
            bool open = p == null || ModeController.PantheonUnlocked(p);
            GodhomePlugin.Log.LogInfo($"[Godhome] 前作的门笼 / hk1 door cage {cage.name}: {(door != null ? door.name : "-")}（{p?.Id ?? "-"}）{(open ? "开着，不放笼子 / open, no cage" : "锁着 / locked")}");
            if (open)
            {
                ModeController.Instance?.StartCoroutine(HideNextFrame(cage.gameObject));
            }
        }
    }

    private static IEnumerator HideNextFrame(GameObject go)
    {
        yield return null;
        if (go != null)
        {
            go.SetActive(false);
        }
    }

    internal static void OnArrive(string scene)
    {
        // 束缚图标、锁门提示上的万神殿图标、打完回到门前的结算（中间的纹章 crest、闪光 core_glows）要借本作的图集
        // （离开大厅进战场时统一还，见 ModeController.ReleaseSanctum）。
        // 门本身照前作的样子，不挂本作的徽记（你要求：门上的图标用前作的；前作五扇门一样，只有门顶的完成标记、束缚标记）。
        ModeController? host = ModeController.Instance;
        if (host != null && byDoor.Count > 0)
        {
            host.StartCoroutine(SanctumArt.Load(new[] { "inventory", "ui", "achievement_icons_hornet", "crest", "core_glows" }, () => Hk1Lobby.InHub));
        }
    }

    /// <summary>门的完成情况（照前作 BossSequenceDoor.Completion），从我们的记录来。</summary>
    internal static BossSequenceDoor.Completion CompletionOf(PantheonDef p)
    {
        GodhomeSaveData rec = GodhomePlugin.Instance.Records;
        rec.Pantheons.TryGetValue(p.Id, out PantheonRecord? r);
        bool open = ModeController.PantheonUnlocked(p);
        return new BossSequenceDoor.Completion
        {
            canUnlock = open,
            unlocked = open,
            completed = r != null && r.Completed,
            allBindings = r != null && r.AllBindings,
            noHits = r != null && r.NoHits,
            boundNail = r != null && r.BoundNeedle,
            boundShell = r != null && r.BoundMask,
            boundCharms = r != null && r.BoundTools,
            boundSoul = r != null && r.BoundSilk,
            viewedBossSceneCompletions = new List<string>(),
        };
    }

    /// <summary>门前的挑战界面选好了（BossDoorChallengeUI.Begin）：记下来，让前作的开门动画放完，FSM 发起转场时开始（ModeController.FilterTransition）。</summary>
    internal static void Begin(PantheonDef p, Binding b)
    {
        Pending = (p, b, Time.unscaledTime);
        GodhomePlugin.Log.LogInfo($"[Godhome] 前作的门：选好了 / hk1 door: chosen {p.Id}（束缚 / bindings {b}），等开门动画 / waiting for the door animation");
        ModeController.Instance?.StartCoroutine(PendingWatch(Pending.Value));
    }

    /// <summary>
    /// 前作的开门动画 8 秒内没发起转场（某一步在丝之歌里卡住了）：直接开始。只认这一次请求（时间戳一起比），
    /// 而且只在人还在前作神居、没死、没在转场里时才开始；否则作废（Codex 审查）。
    /// </summary>
    private static IEnumerator PendingWatch((PantheonDef Def, Binding Bindings, float Since) mine)
    {
        float t0 = Time.unscaledTime;
        while (Time.unscaledTime - t0 < 8f)
        {
            if (Pending == null || !Pending.Value.Equals(mine))
            {
                yield break;
            }
            yield return null;
        }
        if (Pending == null || !Pending.Value.Equals(mine))
        {
            yield break;
        }
        Pending = null;
        ModeController? mc = ModeController.Instance;
        GameManager gm = GameManager.UnsafeInstance;
        HeroController hero = HeroController.SilentInstance;
        if (mc == null || mc.State != LoomState.Lobby || !Hk1Lobby.InHub || gm == null || gm.IsInSceneTransition || hero == null || hero.cState.dead)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作的开门请求作废 / hk1 door request dropped: {mine.Def.Id}（state {mc?.State}，在转场 / in transition {gm != null && gm.IsInSceneTransition}）");
            yield break;
        }
        GodhomePlugin.Log.LogWarning($"[Godhome] 前作的开门动画没有发起转场，直接开始 / hk1 door animation did not start a transition; starting {mine.Def.Id} directly");
        Hk1Lobby.ReleaseHero();
        mc.StartPantheon(mine.Def, mine.Bindings);
    }

    // ───────────────────────────── 换图、换字 ─────────────────────────────

    /// <summary>前作四个束缚图标原来的大小（按钮的物体 id → 原图的原始尺寸）：本作的图按这个框缩放。</summary>
    private static readonly Dictionary<int, Vector2> iconBox = new();

    /// <summary>
    /// 本作的束缚图标放进前作图标的框里（保持比例）。前作的按钮每次选中、取消、全选都调 SetNativeSize，
    /// 本作几张图原始尺寸差很多（面具的图特别大），直接用就大小不一（BossDoorChallengeUIBindingButton 反编译核实）。
    /// </summary>
    internal static void FitIcon(BossDoorChallengeUIBindingButton? b)
    {
        Image? img = b != null ? b.iconImage : null;
        if (!Hk1Lobby.Active || b == null || img == null || img.sprite == null || !iconBox.TryGetValue(b.GetInstanceID(), out Vector2 box))
        {
            return;
        }
        Vector2 n = img.sprite.rect.size;
        if (n.x <= 0f || n.y <= 0f)
        {
            return;
        }
        float k = Mathf.Min(box.x / n.x, box.y / n.y);
        img.preserveAspect = true;
        img.rectTransform.sizeDelta = n * k;
    }

    /// <summary>挑战界面打开时（BossDoorChallengeUI.Setup 后缀）：标题、说明换成本作的；四个束缚的图标换成本作的织针、面具、工具、丝轴。</summary>
    internal static void DressChallengeUI(BossDoorChallengeUI ui, PantheonDef p)
    {
        // 前作标题分两行（小字“Pantheon of the”、大字“Master”）；本作的名字是一整句，小字一行写圣所的名字。
        Hk1Text.Set(ui.titleTextSuper, L.Get("LOOM_NAME"));
        Hk1Text.Set(ui.titleTextMain, p.DisplayName);
        Hk1Text.Set(ui.descriptionText, p.Description);
        BossDoorChallengeUIBindingButton[] buttons = { ui.boundNailButton, ui.boundHeartButton, ui.boundCharmsButton, ui.boundSoulButton };
        for (int i = 0; i < buttons.Length && i < PantheonMenu.Icons.Length; i++)
        {
            BossDoorChallengeUIBindingButton b = buttons[i];
            Sprite? icon = SanctumArt.Get(PantheonMenu.Icons[i].Atlas, PantheonMenu.Icons[i].Sprite);
            if (b == null || icon == null || b.iconImage == null)
            {
                continue;
            }
            // 第一次换之前记下前作原图的原始尺寸（换过以后 defaultSprite 已经是本作的了）。
            if (!iconBox.ContainsKey(b.GetInstanceID()) && DefaultSprite(b) != null && DefaultSprite(b) != icon)
            {
                Sprite cur = b.iconImage.sprite;
                b.iconImage.sprite = DefaultSprite(b);
                b.iconImage.SetNativeSize();
                iconBox[b.GetInstanceID()] = b.iconImage.rectTransform.sizeDelta;
                b.iconImage.sprite = cur;
                GodhomePlugin.Log.LogInfo($"[Godhome] 前作束缚图标的框 / hk1 binding icon box {i}: {iconBox[b.GetInstanceID()]}，本作的图 / ours {icon.name} {icon.rect.size}");
            }
            DefaultSprite(b) = icon;
            b.selectedSprite = icon;
            b.allSelectedSprite = icon;
            if (!b.Selected)
            {
                b.iconImage.sprite = icon;
            }
            FitIcon(b);
        }
        // 前作按钮上的说明文字（AutoLocalizeTextUI 读前作的语言表，丝之歌里没有这些键）：按顺序换成本作束缚的名字。
        for (int i = 0; i < buttons.Length && i < PantheonMenu.Order.Length; i++)
        {
            if (buttons[i] == null)
            {
                continue;
            }
            foreach (Text t in buttons[i].GetComponentsInChildren<Text>(true))
            {
                Hk1Text.Set(t, L.Get("BIND_" + PantheonMenu.Order[i].ToString().ToUpperInvariant()));
            }
        }
        Hk1Text.FixMissing(ui);
    }

    private static readonly AccessTools.FieldRef<BossDoorChallengeUIBindingButton, Sprite> DefaultSprite =
        AccessTools.FieldRefAccess<BossDoorChallengeUIBindingButton, Sprite>("defaultSprite");
}
