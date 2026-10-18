namespace InputMonitor.Core;

/// <summary>
/// Coarse key groups stored instead of the raw virtual-key code while privacy mode is on.
/// The bucket keeps the key heatmap meaningful without letting typed text be reconstructed.
/// </summary>
public static class KeyBuckets
{
    public const string Letter = "字母";
    public const string Digit = "数字";
    public const string Symbol = "符号";
    public const string Whitespace = "空格与换行";
    public const string Editing = "编辑键";
    public const string Navigation = "导航键";
    public const string Modifier = "修饰键";
    public const string Function = "功能键";
    public const string Other = "其他按键";

    /// <summary>Maps a Windows virtual-key code to its display bucket.</summary>
    public static string For(long? keyCode) => keyCode switch
    {
        >= 0x41 and <= 0x5A => Letter,
        (>= 0x30 and <= 0x39) or (>= 0x60 and <= 0x69) => Digit,
        0x20 or 0x0D or 0x09 => Whitespace,
        0x08 or 0x2E or 0x2D => Editing,
        >= 0x21 and <= 0x28 => Navigation,
        0x10 or 0x11 or 0x12 or 0x14 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5) => Modifier,
        >= 0x70 and <= 0x87 => Function,
        (>= 0xBA and <= 0xC0) or (>= 0xDB and <= 0xDF) or (>= 0x6A and <= 0x6F) => Symbol,
        _ => Other
    };
}
