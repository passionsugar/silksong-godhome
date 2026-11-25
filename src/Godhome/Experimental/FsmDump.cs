using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Godhome;

/// <summary>
/// [Godhome] 调试：把 FSM 整个写成文字（状态、每个动作的字段、跳转、全局跳转、变量）。
/// 用来查前作场景里的 FSM 在丝之歌里是怎么接的（雕像的交互、门的挑战界面），不靠猜。
/// </summary>
internal static class FsmDump
{
    internal static void Write(PlayMakerFSM pm, StringBuilder sb)
    {
        string path = PathOf(pm.transform);
        sb.AppendLine($"## {path} | {pm.FsmName}");
        Fsm? fsm = pm.Fsm;
        if (fsm == null)
        {
            sb.AppendLine("  (没有 Fsm / no Fsm)");
            return;
        }
        string active;
        try
        {
            active = pm.ActiveStateName;
        }
        catch (Exception e)
        {
            active = "出错 / error " + e.GetType().Name;
        }
        sb.AppendLine($"  开始 / start: {fsm.StartState}  当前 / active: {active}  启用 / enabled: {pm.isActiveAndEnabled}");
        try
        {
            NamedVariable[] vars = fsm.Variables.GetAllNamedVariables();
            if (vars.Length > 0)
            {
                sb.AppendLine("  变量 / variables: " + string.Join("; ", vars.Select(v => $"{v.GetType().Name.Replace("Fsm", "")} {v.Name}={Value(v)}")));
            }
        }
        catch (Exception e)
        {
            sb.AppendLine("  变量出错 / variables error: " + e.Message);
        }
        try
        {
            foreach (FsmTransition t in fsm.GlobalTransitions)
            {
                sb.AppendLine($"  全局 / global: {t.EventName} → {t.ToState}");
            }
        }
        catch (Exception e)
        {
            sb.AppendLine("  全局跳转出错 / global transitions error: " + e.Message);
        }
        foreach (FsmState st in fsm.States)
        {
            sb.AppendLine($"  [{st.Name}]");
            FsmStateAction[] actions;
            try
            {
                actions = st.Actions;
            }
            catch (Exception e)
            {
                sb.AppendLine("    动作出错 / actions error: " + e.Message);
                actions = Array.Empty<FsmStateAction>();
            }
            foreach (FsmStateAction a in actions)
            {
                if (a == null)
                {
                    sb.AppendLine("    (空动作 / null action)");
                    continue;
                }
                sb.AppendLine($"    - {a.GetType().Name}{(a.Enabled ? "" : " [关 / off]")}({Fields(a)})");
            }
            foreach (FsmTransition t in st.Transitions)
            {
                sb.AppendLine($"    {t.EventName} → {t.ToState}");
            }
        }
    }

    private static string Fields(FsmStateAction a)
    {
        var parts = new List<string>();
        foreach (FieldInfo f in a.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (f.DeclaringType == typeof(FsmStateAction))
            {
                continue;
            }
            object? v;
            try
            {
                v = f.GetValue(a);
            }
            catch
            {
                continue;
            }
            if (v == null)
            {
                continue;
            }
            string s = Value(v);
            if (s.Length == 0)
            {
                continue;
            }
            parts.Add($"{f.Name}={s}");
        }
        return string.Join(", ", parts);
    }

    private static string Value(object? v, int depth = 0)
    {
        switch (v)
        {
            case null:
                return "null";
            case FsmEvent e:
                return "event:" + e.Name;
            case FsmOwnerDefault od:
                return od.OwnerOption == OwnerDefaultOption.UseOwner ? "owner" : Value(od.GameObject, depth);
            case FsmGameObject g when !g.UseVariable:
                return g.Value != null ? "go:" + g.Value.name : "go:null";
            case FsmObject o when !o.UseVariable:
                return o.Value != null ? $"obj:{o.Value.name}({o.Value.GetType().Name})" : "obj:null";
            case NamedVariable nv:
                if (nv.UseVariable)
                {
                    return "@" + nv.Name;
                }
                string s = nv.ToString();
                return s.Length > 120 ? s.Substring(0, 120) + "…" : s;
            case FsmEventTarget et:
                return $"target:{et.target}{(et.target == FsmEventTarget.EventTarget.GameObject ? "/" + Value(et.gameObject, depth) : "")}{(et.fsmName != null && !string.IsNullOrEmpty(et.fsmName.Value) ? "/fsm " + et.fsmName.Value : "")}";
            case FsmProperty p:
                return $"prop:{p.PropertyName}";
            case FunctionCall fc:
                return $"call:{fc.FunctionName}({fc.ParameterType})";
            case Object uo:
                return uo != null ? $"{uo.name}({uo.GetType().Name})" : "null";
            case string str:
                return str.Length > 120 ? str.Substring(0, 120) + "…" : str;
            case Array arr when depth < 1:
                return "[" + string.Join(" | ", arr.Cast<object?>().Take(8).Select(x => Value(x, depth + 1))) + (arr.Length > 8 ? $" …{arr.Length}" : "") + "]";
            case IEnumerable when v is not string:
                return "";
            default:
                return v.GetType().IsPrimitive || v.GetType().IsEnum ? v.ToString() ?? "" : "";
        }
    }

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
}
