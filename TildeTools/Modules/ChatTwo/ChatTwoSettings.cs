using System;

namespace TildeTools.Modules.ChatTwo;

[Serializable]
public sealed class ChatTwoSettings
{
    public const int MaxInputLimit = 32000;

    public const int MinInputLimit = 500;

    public int InputLimitBytes { get; set => field = Math.Clamp(value, MinInputLimit, MaxInputLimit); } = 8000;
}
