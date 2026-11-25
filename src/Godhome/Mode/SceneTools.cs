using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Godhome;

/// <summary>[Godhome] 按路径找场景物体、操作 FSM。路径是场景内的层级路径，例如 "Boss Scene/Intro Sequence"。</summary>
internal static class SceneTools
{
    internal static IEnumerable<Scene> LoadedScenes(ICollection<string>? names)
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene s = SceneManager.GetSceneAt(i);
            if (s.isLoaded && (names == null || names.Count == 0 || names.Contains(s.name)))
            {
                yield return s;
            }
        }
    }

    /// <summary>按层级路径找物体（未激活的也找得到）。同名根物体会逐个尝试。</summary>
    internal static GameObject? FindPath(string path, ICollection<string>? scenes)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        int slash = path.IndexOf('/');
        string rootName = slash < 0 ? path : path.Substring(0, slash);
        string rest = slash < 0 ? "" : path.Substring(slash + 1);
        foreach (Scene s in LoadedScenes(scenes))
        {
            foreach (GameObject root in s.GetRootGameObjects())
            {
                if (root.name != rootName)
                {
                    continue;
                }
                if (rest.Length == 0)
                {
                    return root;
                }
                Transform? t = root.transform.Find(rest);
                if (t != null)
                {
                    return t.gameObject;
                }
            }
        }
        return null;
    }

    /// <summary>按物体名找（含未激活的，名字去掉 "(Clone)"）。</summary>
    internal static List<GameObject> FindNamed(ICollection<string> names, ICollection<string>? scenes)
    {
        var list = new List<GameObject>();
        if (names.Count == 0)
        {
            return list;
        }
        foreach (Scene s in LoadedScenes(scenes))
        {
            foreach (GameObject root in s.GetRootGameObjects())
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (names.Contains(t.name.Replace("(Clone)", "").Trim()))
                    {
                        list.Add(t.gameObject);
                    }
                }
            }
        }
        return list;
    }

    internal static PlayMakerFSM? FindFsm(string path, string fsmName, ICollection<string>? scenes)
    {
        GameObject? go = FindPath(path, scenes);
        if (go == null)
        {
            return null;
        }
        return go.GetComponents<PlayMakerFSM>().FirstOrDefault(f => string.IsNullOrEmpty(fsmName) || f.FsmName == fsmName);
    }

    /// <summary>FSM 当前状态是不是 when 里的一个（多个状态用 | 分隔）。</summary>
    internal static bool InState(PlayMakerFSM fsm, string when) =>
        when.Split('|').Select(s => s.Trim()).Contains(fsm.ActiveStateName);

    internal static string PathOf(Transform t)
    {
        var parts = new List<string>();
        for (Transform? x = t; x != null; x = x.parent)
        {
            parts.Add(x.name);
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    /// <summary>物体的路径和激活情况；没激活时列出自己关掉的那几层（要打开的是最上面那层）。</summary>
    internal static string Describe(GameObject go)
    {
        string path = PathOf(go.transform);
        if (go.activeInHierarchy)
        {
            return $"{path} (active)";
        }
        var off = new List<string>();
        for (Transform? x = go.transform; x != null; x = x.parent)
        {
            if (!x.gameObject.activeSelf)
            {
                off.Add(PathOf(x));
            }
        }
        off.Reverse();
        return $"{path} (inactive; 关掉的层 / self-inactive levels: {string.Join(" | ", off)})";
    }

    /// <summary>路径找不到时说明断在哪一层：根物体不存在（列出现有根物体），或者某一层下面没有这个子物体。</summary>
    internal static string ExplainMissing(string path, ICollection<string>? scenes)
    {
        string[] parts = path.Split('/');
        var roots = LoadedScenes(scenes).SelectMany(s => s.GetRootGameObjects()).ToList();
        List<GameObject> matches = roots.Where(r => r.name == parts[0]).ToList();
        if (matches.Count == 0)
        {
            return $"没有根物体 / no root '{parts[0]}' (roots: {string.Join(", ", roots.Select(r => r.name).Distinct().Take(25))})";
        }
        string best = parts[0];
        foreach (GameObject root in matches)
        {
            Transform t = root.transform;
            int i = 1;
            for (; i < parts.Length; i++)
            {
                Transform? next = t.Find(parts[i]);
                if (next == null)
                {
                    break;
                }
                t = next;
            }
            string reached = string.Join("/", parts.Take(i));
            if (reached.Length > best.Length)
            {
                best = reached;
            }
            if (i < parts.Length)
            {
                var kids = new List<string>();
                foreach (Transform c in t)
                {
                    kids.Add(c.name);
                }
                return $"'{reached}' 下面没有 / has no child '{parts[i]}' (children: {string.Join(", ", kids.Take(20))})";
            }
        }
        return $"只找到 / only reached '{best}'";
    }

    /// <summary>执行数据表里的一步。返回是否成功。</summary>
    internal static bool RunStep(FsmStep step, ICollection<string> scenes)
    {
        try
        {
            if (step.Type == "register")
            {
                EventRegister.SendEvent(step.Event);
                GodhomePlugin.Log.LogInfo($"[Godhome]   步骤 / step: {step}");
                return true;
            }
            if (step.Type is "activate" or "deactivate")
            {
                GodhomePlugin.Log.LogInfo($"[Godhome]   步骤 / step: {step}");
                bool exists = FindPath(step.Path, scenes) != null;
                SetActive(new[] { step.Path }, step.Type == "activate", scenes);
                return exists;
            }
            GameObject? go = FindPath(step.Path, scenes);
            if (go == null)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome]   步骤失败，找不到物体 / step failed, object not found: {step} — {ExplainMissing(step.Path, scenes)}");
                return false;
            }
            PlayMakerFSM? fsm = go.GetComponents<PlayMakerFSM>().FirstOrDefault(f => string.IsNullOrEmpty(step.Fsm) || f.FsmName == step.Fsm);
            if (fsm == null)
            {
                string have = string.Join(", ", go.GetComponents<PlayMakerFSM>().Select(f => f.FsmName));
                GodhomePlugin.Log.LogWarning($"[Godhome]   步骤失败，物体上没有这个 FSM / step failed, no such FSM: {step} (has: {have})");
                return false;
            }
            if (!fsm.gameObject.activeInHierarchy)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome]   步骤跳过，物体未激活 / step skipped, object inactive: {step} — {Describe(fsm.gameObject)}");
                return false;
            }
            if (step.Type == "state")
            {
                fsm.SetState(step.State);
            }
            else
            {
                fsm.SendEvent(step.Event);
            }
            GodhomePlugin.Log.LogInfo($"[Godhome]   步骤 / step: {step} (now '{fsm.ActiveStateName}')");
            return true;
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome]   步骤出错 / step error: {step}: {e.Message}");
            return false;
        }
    }

    internal static int SetActive(IEnumerable<string> paths, bool active, ICollection<string> scenes)
    {
        int n = 0;
        foreach (string p in paths)
        {
            GameObject? go = FindPath(p, scenes);
            if (go == null)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome]   找不到物体 / object not found: {p} — {ExplainMissing(p, scenes)}");
                continue;
            }
            if (go.activeSelf != active)
            {
                go.SetActive(active);
                n++;
                GodhomePlugin.Log.LogInfo($"[Godhome]   {(active ? "激活" : "关闭")} / {(active ? "activated" : "deactivated")}: {p}");
            }
            if (active && !go.activeInHierarchy)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome]   激活了但上层没开 / activated but a parent is off: {Describe(go)}");
            }
        }
        return n;
    }
}
