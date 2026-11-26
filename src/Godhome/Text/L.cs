using System;
using TeamCherry.Localization;

namespace Godhome;

/// <summary>[Godhome] 文本。键值在 languages/zh.json、en.json，由 I18N 载入到 Mods.local.silksong.godhome 表。</summary>
internal static class L
{
    private static readonly string Sheet = "Mods." + GodhomePlugin.Id;

    internal static string Get(string key)
    {
        try
        {
            if (Language.Has(key, Sheet))
            {
                return Language.Get(key, Sheet);
            }
        }
        catch (Exception)
        {
            // 语言系统还没初始化（例如启动早期），直接回退到键名。
        }
        return key;
    }

    internal static string F(string key, params object[] args)
    {
        try
        {
            return string.Format(Get(key), args);
        }
        catch (FormatException)
        {
            return Get(key);
        }
    }

    /// <summary>游戏语言是中文（简体 ZH，以及枚举里别的 ZH_* 地区写法）。</summary>
    internal static bool IsChinese
    {
        get
        {
            try
            {
                LanguageCode code = Language.CurrentLanguage();
                return code == LanguageCode.ZH || code.ToString().StartsWith("ZH_", StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// 秒数 → m:ss.f。先把总时间舍入到十分之一秒再拆分钟和秒（Codex 审计 0.10.3 F08：以前先截分钟再舍入秒，59.95 秒显示成 0:60.0）。
    /// 负数、非有限值（NaN、无穷）显示 --。
    /// </summary>
    internal static string Time(float seconds)
    {
        if (seconds < 0 || float.IsNaN(seconds) || float.IsInfinity(seconds))
        {
            return "--";
        }
        long tenths = (long)Math.Round(seconds * 10.0, MidpointRounding.AwayFromZero);
        long m = tenths / 600;
        long rest = tenths % 600;
        return $"{m}:{rest / 10:00}.{rest % 10}";
    }
}
