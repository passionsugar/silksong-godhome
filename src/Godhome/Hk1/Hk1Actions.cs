namespace HutongGames.PlayMaker.Actions;

/// <summary>
/// [Godhome] 前作 FSM 里的动作，丝之歌里没有这个类：前作神居每个场景加载时几十条“Could Not Create Action: ForceHeroFootstepSound”
/// （雕像、门的 npc_control 里“Move Hero Left/Right”）。PlayMaker 按全名在所有已加载的程序集里找动作类（ReflectionUtils.GetGlobalType），
/// 这里补一个同名的。丝之歌的 HeroController 没有强制脚步声，所以什么都不做，进状态就结束（和缺动作时一样）。
/// 字段和前作一样（类型、顺序），PlayMaker 按字段类型算的动作哈希对得上，不会报“动作已改变”。
/// </summary>
public class ForceHeroFootstepSound : FsmStateAction
{
    public FsmEnum forceFootstepSound = null!;

    public FsmBool endOnStateExit = null!;

    public override void OnEnter()
    {
        Finish();
    }
}
