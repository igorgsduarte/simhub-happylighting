namespace HappyLightingPlugin;

internal static class Compatibility
{
    public static int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    public static string ToHexString(byte[] value)
    {
        return BitConverter.ToString(value).Replace("-", string.Empty);
    }
}
