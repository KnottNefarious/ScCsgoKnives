using Engine;
using GameEntitySystem;
using TemplatesDatabase;
namespace Game;

/// <summary>Scheduled airdrops of this world (airdrop-ladder-hud-smoke-20261012): the switch, whole in-game days between two
/// drops and the accumulated game time the current wait counts from. Its own saved group, registered with the compatibility
/// capsule: the 1.4.0/1.5.0 readers own only "ScAirdropDirector" (and rewrite it with their own fields), so they keep this
/// group untouched and hand it back. A world without it takes the device defaults once and waits from its first load.</summary>
public sealed class SubsystemScAirdropRules : Subsystem {
    public const int Schema=1;
    public const string GroupName="ScAirdropRules";
    public ScAirdropRules Rules {get;private set;}=ScAirdropRules.Default;
    public bool FromDefaults {get;private set;}
    /// <summary>Game time the current wait counts from (the last delivered drop, the moment the switch was turned on, or the
    /// first load); NaN until the first clock reading of a world that never had one.</summary>
    double m_anchor=double.NaN;
    SubsystemGameInfo m_info;SubsystemTimeOfDay m_day;
    public override void Load(ValuesDictionary values){
        base.Load(values);
        m_info=Project.FindSubsystem<SubsystemGameInfo>(true);m_day=Project.FindSubsystem<SubsystemTimeOfDay>(true);
        if(values.GetValue("Schema",Schema)!=Schema)throw new InvalidOperationException("空投规则版本不受支持，请使用相应版本探员包。");
        if(values.GetValue("Initialized",false)){
            var read=new ScAirdropRules(values.GetValue("Enabled",true),values.GetValue("Days",ScAirdropRules.DefaultDays));
            Rules=read.Normalize();
            if(Rules!=read)Log.Warning($"[CS_AIRDROP] world rules out of range (days={read.Days}); using {Rules.Days}.");
            m_anchor=values.GetValue("Anchor",double.NaN);
            if(!double.IsFinite(m_anchor)||m_anchor<0)throw new InvalidOperationException("空投计时无效，拒绝重置。");
        }else{Rules=ScUiSettings.AirdropDefaults;FromDefaults=true;}
        ScAirdropRulesBridge.Read=p=>p?.FindSubsystem<SubsystemScAirdropRules>(false)?.Rules;
        ScAirdropRulesBridge.Write=(p,r)=>ScNet.IsAuthority&&p?.FindSubsystem<SubsystemScAirdropRules>(false) is {} s&&s.Set(r);
        ScAirdropRulesBridge.Progress=(p,r)=>p?.FindSubsystem<SubsystemScAirdropRules>(false)?.Describe(r)??"";
        Log.Information($"[CS_AIRDROP] world rules enabled={Rules.Enabled} days={Rules.Days} anchor={m_anchor:F1} source={(FromDefaults?"device-defaults":"world")}");
    }
    public override void Save(ValuesDictionary values){
        base.Save(values);values.SetValue("Schema",Schema);values.SetValue("Initialized",true);
        values.SetValue("Enabled",Rules.Enabled);values.SetValue("Days",Rules.Days);values.SetValue("Anchor",Anchor());
    }
    double Now=>m_info.TotalElapsedGameTime;
    double DaySeconds=>m_day.DayDuration;
    double Anchor(){if(double.IsNaN(m_anchor))m_anchor=Now;return m_anchor;}
    /// <summary>Turning the switch on starts a new wait; other changes keep it, so new days count from the last drop.</summary>
    public bool Set(ScAirdropRules rules){
        rules=rules.Normalize();
        if(rules.Enabled&&!Rules.Enabled)m_anchor=Now;
        Rules=rules;FromDefaults=false;
        Log.Information($"[CS_AIRDROP] world rules changed enabled={Rules.Enabled} days={Rules.Days} anchor={Anchor():F1}");return true;
    }
    public bool Due=>IsDue(Rules,Anchor(),Now,DaySeconds);
    public void Delivered(){m_anchor=Now;}
    /// <summary>The wait has passed: enabled, a valid day length, and whole <see cref="ScAirdropRules.Days"/> days since the anchor.</summary>
    public static bool IsDue(ScAirdropRules rules,double anchor,double now,double daySeconds)=>rules.Enabled&&WaitLeft(rules,anchor,now,daySeconds)<=0;
    /// <summary>Game seconds still to wait (infinite with an invalid clock); the same function the gate uses.</summary>
    public static double WaitLeft(ScAirdropRules rules,double anchor,double now,double daySeconds)=>
        !double.IsFinite(anchor)||!double.IsFinite(now)||!double.IsFinite(daySeconds)||daySeconds<=0?double.PositiveInfinity
        :Math.Max(0,anchor+rules.Normalize().Days*daySeconds-now);
    /// <summary>Progress line of the settings page for the rules being edited (display only).</summary>
    string Describe(ScAirdropRules edited){
        double day=DaySeconds;if(!(day>0))return "";
        var settings=m_info.WorldSettings;
        if(!edited.Enabled)return "已关闭；重新开启后从那一刻开始计时。";
        if(ScModes.Of(Project) is not null||settings.GameMode<GameMode.Survival||settings.EnvironmentBehaviorMode!=EnvironmentBehaviorMode.Living)
            return "当前世界模式不投放空投（只在生存类、生物活跃的世界投放）。";
        // A switch turned on by this edit starts its wait when saved.
        double anchor=Rules.Enabled?Anchor():Now,left=WaitLeft(edited,anchor,Now,day);
        return left>0?$"距下次空投约 {left/day:0.0} 个游戏日。":"已到时间，正在附近寻找投放地点（附近 32～56 格的露天地面，需容得下五名守卫）。";
    }
}
