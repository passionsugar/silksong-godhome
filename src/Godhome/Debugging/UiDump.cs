using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace Godhome;

/// <summary>
/// [Godhome] 调试：把游戏自己的菜单界面（UIManager 下的几个 MenuScreen）的层级、组件、文字、字体、图、动画写进文件，
/// 用来照原版样式做挑战面板（不猜结构）。只读，不改任何东西。
/// </summary>
internal static class UiDump
{
    internal static string Write(string path)
    {
        var sb = new StringBuilder();
        try
        {
            UIManager ui = UIManager.instance;
            sb.AppendLine($"Screen {Screen.width}x{Screen.height}");
            foreach (Camera c in Camera.allCameras.OrderBy(c => c.depth))
            {
                sb.AppendLine($"camera {c.name} depth {c.depth} clear {c.clearFlags} mask 0x{c.cullingMask:X} ortho {c.orthographic} size {c.orthographicSize:0.##} rt {(c.targetTexture != null ? c.targetTexture.name : "-")} enabled {c.enabled}");
            }
            if (ui == null)
            {
                sb.AppendLine("no UIManager");
            }
            else
            {
                Canvas cv = ui.UICanvas;
                if (cv != null)
                {
                    CanvasScaler? sc = cv.GetComponent<CanvasScaler>();
                    sb.AppendLine($"UICanvas {cv.name} mode {cv.renderMode} cam {(cv.worldCamera != null ? cv.worldCamera.name : "-")} plane {cv.planeDistance} order {cv.sortingOrder} layer {LayerMask.LayerToName(cv.gameObject.layer)} scale {cv.transform.localScale}"
                        + (sc != null ? $" scaler {sc.uiScaleMode} ref {sc.referenceResolution} match {sc.matchWidthOrHeight}" : ""));
                    sb.AppendLine("UICanvas children:");
                    foreach (Transform ch in cv.transform)
                    {
                        sb.AppendLine($"  {ch.name} [{(ch.gameObject.activeSelf ? "on" : "off")}] {string.Join(",", ch.GetComponents<Component>().Select(x => x == null ? "null" : x.GetType().Name))}");
                    }
                }
                if (ui.GenericMessageCanvas != null)
                {
                    sb.AppendLine($"GenericMessageCanvas {ui.GenericMessageCanvas.name} mode {ui.GenericMessageCanvas.renderMode}");
                }
                foreach ((string name, MenuScreen? m) in new (string, MenuScreen?)[]
                {
                    ("quitGamePrompt", ui.quitGamePrompt),
                    ("returnMainMenuPrompt", ui.returnMainMenuPrompt),
                    ("pauseMenuScreen", ui.pauseMenuScreen),
                    ("playModeMenuScreen", ui.playModeMenuScreen),
                    ("extrasMenuScreen", ui.extrasMenuScreen),
                })
                {
                    sb.AppendLine();
                    sb.AppendLine($"######## {name}");
                    if (m == null)
                    {
                        sb.AppendLine("null");
                        continue;
                    }
                    sb.AppendLine($"path {PathOf(m.transform)}");
                    sb.AppendLine($"MenuScreen topFleur={N(m.topFleur)} bottomFleur={N(m.bottomFleur)} back={N(m.backButton)} default={N(m.defaultHighlight)} highlight={m.HighlightBehaviour}");
                    Node(m.transform, 0, 9, sb);
                }
            }
        }
        catch (Exception e)
        {
            sb.AppendLine("ERROR " + e);
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static string N(Component? c) => c != null ? c.name : "-";

    private static string PathOf(Transform t)
    {
        string p = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            p = t.name + "/" + p;
        }
        return p;
    }

    private static void Node(Transform t, int depth, int max, StringBuilder sb)
    {
        string pad = new(' ', depth * 2);
        string rect = "";
        if (t is RectTransform rt)
        {
            rect = $" a({rt.anchorMin.x:0.##},{rt.anchorMin.y:0.##})-({rt.anchorMax.x:0.##},{rt.anchorMax.y:0.##}) piv({rt.pivot.x:0.##},{rt.pivot.y:0.##}) pos({rt.anchoredPosition.x:0.#},{rt.anchoredPosition.y:0.#}) size({rt.sizeDelta.x:0.#},{rt.sizeDelta.y:0.#})";
        }
        if (t.localScale != Vector3.one)
        {
            rect += $" s({t.localScale.x:0.##},{t.localScale.y:0.##})";
        }
        sb.AppendLine($"{pad}{t.name} [{(t.gameObject.activeSelf ? "on" : "off")}]{rect}");
        foreach (Component c in t.GetComponents<Component>())
        {
            if (c == null || c is Transform)
            {
                continue;
            }
            sb.AppendLine($"{pad}  · {Describe(c)}");
        }
        if (depth >= max)
        {
            if (t.childCount > 0)
            {
                sb.AppendLine($"{pad}  … {t.childCount} children");
            }
            return;
        }
        foreach (Transform ch in t)
        {
            Node(ch, depth + 1, max, sb);
        }
    }

    private static string Describe(Component c)
    {
        string type = c.GetType().Name;
        try
        {
            switch (c)
            {
                case Text tx:
                    return $"Text '{Clip(tx.text)}' font={(tx.font != null ? tx.font.name : "-")} size={tx.fontSize} style={tx.fontStyle} color={tx.color} align={tx.alignment} line={tx.lineSpacing} best={tx.resizeTextForBestFit} mat={(tx.material != null ? tx.material.name : "-")}";
                case Image im:
                    return $"Image sprite={(im.sprite != null ? im.sprite.name : "-")} color={im.color} type={im.type} aspect={im.preserveAspect} ray={im.raycastTarget} mat={(im.material != null ? im.material.name : "-")}";
                case CanvasGroup cg:
                    return $"CanvasGroup alpha={cg.alpha} interactable={cg.interactable} blocks={cg.blocksRaycasts}";
                case Animator an:
                    return $"Animator ctrl={(an.runtimeAnimatorController != null ? an.runtimeAnimatorController.name : "-")} params={(an.isActiveAndEnabled ? string.Join(",", an.parameters.Select(p => p.name)) : "?")}";
                case MenuButton mb:
                    return $"MenuButton type={mb.buttonType} flash={N(mb.flashEffect)} cancel={mb.cancelAction} L={N(mb.leftCursor)} R={N(mb.rightCursor)} hl={N(mb.selectHighlight)} desc={N(mb.descriptionText)} nav={Nav(mb)} submit=[{Persistent(mb.OnSubmitPressed)}]";
                case MenuSelectable ms:
                    return $"{type} cancel={ms.cancelAction} L={N(ms.leftCursor)} R={N(ms.rightCursor)} hl={N(ms.selectHighlight)} desc={N(ms.descriptionText)} nav={Nav(ms)}";
                case Selectable sel:
                    return $"{type} nav={Nav(sel)}";
                case Canvas cv:
                    return $"Canvas mode={cv.renderMode} override={cv.overrideSorting} order={cv.sortingOrder}";
                case SpriteRenderer sr:
                    return $"SpriteRenderer sprite={(sr.sprite != null ? sr.sprite.name : "-")} color={sr.color}";
            }
            // 别的组件：TMP（TMProOld）等用反射读 text / font。
            PropertyInfo? textProp = c.GetType().GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
            if (textProp != null && textProp.PropertyType == typeof(string))
            {
                object? font = c.GetType().GetProperty("font", BindingFlags.Public | BindingFlags.Instance)?.GetValue(c);
                object? size = c.GetType().GetProperty("fontSize", BindingFlags.Public | BindingFlags.Instance)?.GetValue(c);
                return $"{type} '{Clip(textProp.GetValue(c) as string ?? "")}' font={(font as UnityEngine.Object)?.name ?? "-"} size={size}";
            }
            if (type == "AutoLocalizeTextUI")
            {
                object? ls = c.GetType().GetProperty("Text")?.GetValue(c);
                return $"AutoLocalizeTextUI {ls}";
            }
        }
        catch (Exception e)
        {
            return $"{type} (读取出错 / read failed: {e.Message})";
        }
        return type;
    }

    private static string Nav(Selectable s)
    {
        Navigation n = s.navigation;
        return $"{n.mode} up={N(n.selectOnUp)} down={N(n.selectOnDown)}";
    }

    private static string Persistent(UnityEngine.Events.UnityEventBase ev)
    {
        int n = ev.GetPersistentEventCount();
        var parts = new string[n];
        for (int i = 0; i < n; i++)
        {
            UnityEngine.Object? target = ev.GetPersistentTarget(i);
            parts[i] = $"{(target != null ? target.GetType().Name + ":" + target.name : "-")}.{ev.GetPersistentMethodName(i)}";
        }
        return string.Join("; ", parts);
    }

    private static string Clip(string s)
    {
        s = s.Replace("\n", "\\n");
        return s.Length > 60 ? s.Substring(0, 60) + "…" : s;
    }
}
