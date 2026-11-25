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

    internal static bool IsChinese
    {
        get
        {
            try
            {
                return Language.CurrentLanguage() == LanguageCode.ZH;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    /// <summary>秒数 → m:ss.f</summary>
    internal static string Time(float seconds)
    {
        if (seconds < 0)
        {
            return "--";
        }
        int m = (int)(seconds / 60);
        float s = seconds - m * 60;
        return $"{m}:{s:00.0}";
    }
}
