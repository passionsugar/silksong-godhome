using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;

namespace Godhome;

/// <summary>
/// [Godhome] 前作神居场景的音频（玩家反馈：神居一进来没声音、声音时有时无；探针实测大厅输出 -50 ~ -100 dBFS，战斗场地 -10 ~ -20）。
/// 前作场景的场景管理器挂的音频快照（音乐、环境、环境效果、角色、阴影）属于前作的混音器（跟前作场景一起读进来的那一份），
/// 丝之歌套用它们只动了前作的混音器，丝之歌自己的混音器没人管（反编译 CustomSceneManager.Start、AudioManager）：
/// - 角色：换场景时丝之歌把自己的角色混音器切到“暂停”（静音），由场景管理器的角色快照、或者 GameManager 的默认恢复切回来；
///   场景管理器挂了角色快照，默认恢复就让给它（AudioManager.CustomSceneManagerSnapshotReady），它切的却是前作那一份，
///   于是在前作神居里大黄蜂、敌人、机关的声音一直是静音。
/// - 音乐：丝之歌的音乐混音器停在上一个场景的快照（织巢、不少战斗场景是 Silent），前作场景的音乐放出来也听不见；
///   AudioManager 记的当前音乐快照也成了前作的，之后丝之歌场景混合音乐标记时拿它去混丝之歌的混音器。
/// - 环境音：前作的环境音（AtmosCue）结构不同，不套用（Hk1SceneManagerAwakePatch），上一个场景（织巢、熔岩场地……）的环境音一直接着放。
/// 这里在场景管理器 Awake（Start 套用之前）把这些快照换成丝之歌混音器里同名的快照；没有同名的就不套（角色的交给 GameManager 默认恢复）。
/// 前作的快照照样切到前作混音器上（前作场景自己的声源还走前作混音器，原来 Start 里就是这么切的）。上一个场景留下的环境音停掉。
/// 只在前作神居的场景里改（Hk1SceneManagerAwakePatch 判过）。
/// </summary>
internal static class Hk1Audio
{
    private static readonly AccessTools.FieldRef<CustomSceneManager, AudioMixerSnapshot> MusicSnapshot =
        AccessTools.FieldRefAccess<CustomSceneManager, AudioMixerSnapshot>("musicSnapshot");

    private static readonly AccessTools.FieldRef<CustomSceneManager, MusicCue> SceneMusicCue =
        AccessTools.FieldRefAccess<CustomSceneManager, MusicCue>("musicCue");

    private static readonly AccessTools.FieldRef<AudioManager, AudioMixer> MusicMixer =
        AccessTools.FieldRefAccess<AudioManager, AudioMixer>("musicMixer");

    private static readonly AccessTools.FieldRef<AudioManager, AudioMixer> AtmosMixer =
        AccessTools.FieldRefAccess<AudioManager, AudioMixer>("atmosMixer");

    /// <summary>丝之歌自己的环境效果混音器（AudioManager、GameManager 上没有它的引用，丝之歌场景的场景管理器 Awake 时记下）。</summary>
    private static AudioMixer? ourEnviro;

    /// <summary>丝之歌场景的场景管理器 Awake：记下丝之歌的环境效果混音器（只读）。</summary>
    internal static void NoteOurs(CustomSceneManager sm)
    {
        AudioMixerSnapshot? s = sm.enviroSnapshot;
        if (s != null && s.audioMixer != null)
        {
            ourEnviro = s.audioMixer;
        }
    }

    /// <summary>前作场景的场景管理器 Awake：换快照、停掉上一个场景的环境音。返回一行说明（记日志）。</summary>
    internal static string Adopt(CustomSceneManager sm)
    {
        GameManager gm = GameManager.instance;
        AudioManager? am = gm != null ? gm.AudioManager : null;
        AudioMixer? music = am != null ? MusicMixer(am) : null;
        AudioMixer? atmos = am != null ? AtmosMixer(am) : null;
        AudioMixer? actors = gm != null && gm.actorSnapshotUnpaused != null ? gm.actorSnapshotUnpaused.audioMixer : null;
        var notes = new List<string>();

        AudioMixerSnapshot? Swap(AudioMixerSnapshot? hk1, AudioMixer? ours, string role)
        {
            if (hk1 == null)
            {
                return null;
            }
            if (ours != null && hk1.audioMixer == ours)
            {
                return hk1;
            }
            AudioMixerSnapshot? s = null;
            if (ours != null)
            {
                try
                {
                    s = ours.FindSnapshot(hk1.name);
                }
                catch (Exception)
                {
                    s = null;
                }
            }
            try
            {
                hk1.TransitionTo(0f);
            }
            catch (Exception e)
            {
                notes.Add($"{role} 前作快照切换出错 / hk1 snapshot failed: {e.Message}");
            }
            notes.Add($"{role} {hk1.name} → {(s != null ? $"{s.name}@{ours!.name}" : "-")}");
            return s;
        }

        MusicSnapshot(sm) = Swap(MusicSnapshot(sm), music, "音乐 / music")!;
        sm.atmosSnapshot = Swap(sm.atmosSnapshot, atmos, "环境 / atmos")!;
        AudioMixerSnapshot? hk1Enviro = sm.enviroSnapshot;
        sm.enviroSnapshot = Swap(hk1Enviro, ourEnviro != null ? ourEnviro : OtherMixer(hk1Enviro), "环境效果 / enviro")!;
        sm.actorSnapshot = Swap(sm.actorSnapshot, actors, "角色 / actor")!;
        sm.shadeSnapshot = Swap(sm.shadeSnapshot, null, "阴影 / shade")!;

        // 前作的音乐配置（MusicCue）和丝之歌的字段一样，照常交给 AudioManager 放（丝之歌的音乐声源、丝之歌的音乐混音器）。
        // 只是“替代版本”结构不同（前作按 PlayerData 的布尔名选，丝之歌按 PlayerDataTest）：读进来的条件不可信，清掉。
        MusicCue? cue = SceneMusicCue(sm);
        if (cue != null)
        {
            Traverse alts = Traverse.Create(cue).Field("alternatives");
            if (alts.GetValue() is Array a && a.Length > 0)
            {
                alts.SetValue(Array.CreateInstance(a.GetType().GetElementType()!, 0));
                notes.Add($"音乐替代版本清掉 / music alternatives cleared ({a.Length})");
            }
            notes.Add($"音乐 / music cue {cue.name}（{Clips(cue)}）");
        }
        else
        {
            notes.Add("没有音乐配置 / no music cue");
        }

        if (am != null && am.CurrentAtmosCue != null)
        {
            string was = am.CurrentAtmosCue.name;
            am.StopAndClearAtmos();
            notes.Add($"停掉上一个场景的环境音 / stopped the leftover atmos {was}");
        }
        return string.Join("，", notes);
    }

    /// <summary>和前作那份同名、但不是它的混音器（丝之歌自己的；找不到返回 null）。</summary>
    private static AudioMixer? OtherMixer(AudioMixerSnapshot? hk1)
    {
        if (hk1 == null || hk1.audioMixer == null)
        {
            return null;
        }
        AudioMixer theirs = hk1.audioMixer;
        return Resources.FindObjectsOfTypeAll<AudioMixer>().FirstOrDefault(m => m != null && m != theirs && m.name == theirs.name);
    }

    private static string Clips(MusicCue cue)
    {
        try
        {
            var names = new List<string>();
            foreach (MusicChannels ch in Enum.GetValues(typeof(MusicChannels)))
            {
                MusicCue.MusicChannelInfo? info = cue.GetChannelInfo(ch);
                if (info != null && info.IsEnabled)
                {
                    names.Add($"{ch} {info.Clip.name}");
                }
            }
            return names.Count > 0 ? string.Join(", ", names) : "没有音轨 / no clips";
        }
        catch (Exception e)
        {
            return "读不了音轨 / clips unreadable: " + e.Message;
        }
    }
}
