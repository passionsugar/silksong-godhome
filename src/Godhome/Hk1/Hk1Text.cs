using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Godhome;

/// <summary>
/// [Godhome] 前作界面上的字：前作的 AutoLocalizeTextUI 读前作的语言表（表名 CP3 之类），丝之歌里没有，
/// 显示成“!!CP3/UI_BEGIN!!”。换成我们的双语文本，并关掉那个组件（不然重新激活时又会被它改回去）。
/// </summary>
internal static class Hk1Text
{
    /// <summary>前作的键 → 我们的键。没列出的按“HK1_ + 前作的键”找，再找不到就留着并记日志。</summary>
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        ["UI_BINDINGS_TITLE"] = "PAN_BINDINGS",
        ["UI_BEGIN"] = "PAN_BEGIN",
    };

    private static readonly HashSet<string> logged = new(StringComparer.Ordinal);

    /// <summary>设字，关掉同一物体上前作的自动本地化。</summary>
    internal static void Set(Text t, string text)
    {
        if (t == null)
        {
            return;
        }
        foreach (MonoBehaviour mb in t.GetComponents<MonoBehaviour>())
        {
            if (mb != null && mb.GetType().Name.StartsWith("AutoLocalize", StringComparison.Ordinal))
            {
                mb.enabled = false;
            }
        }
        t.text = text;
    }

    /// <summary>界面里所有“!!表/键!!”的字换成我们的。</summary>
    internal static void FixMissing(Component root)
    {
        foreach (Text t in root.GetComponentsInChildren<Text>(true))
        {
            string s = t.text ?? "";
            if (!s.StartsWith("!!", StringComparison.Ordinal) || !s.EndsWith("!!", StringComparison.Ordinal) || s.Length < 5)
            {
                continue;
            }
            string inner = s.Substring(2, s.Length - 4);
            int slash = inner.LastIndexOf('/');
            string key = slash >= 0 ? inner.Substring(slash + 1) : inner;
            string ours = Map.TryGetValue(key, out string? k) ? k : "HK1_" + key;
            string text = L.Get(ours);
            if (text == ours)
            {
                if (logged.Add(key))
                {
                    GodhomePlugin.Log.LogWarning($"[Godhome] 前作界面上没有对应的字 / no text for hk1 UI key: {inner}（{t.name}）");
                }
                continue;
            }
            Set(t, text);
        }
    }
}
