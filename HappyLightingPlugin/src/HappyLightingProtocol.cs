namespace HappyLightingPlugin;

public sealed class HappyLightingProtocol
{
    public byte[] BuildFrame(LightFrame frame)
    {
        if (!frame.IsOn)
            return [0x7E, 0x07, 0x05, 0x03, 0x00, 0x00, 0x00, 0x10, 0xEF];

        return BuildColorCommand(frame);
    }

    public IReadOnlyList<byte[]> BuildFrameVariants(LightFrame frame)
    {
        if (!frame.IsOn)
        {
            return
            [
                [0x7E, 0x07, 0x05, 0x03, 0x00, 0x00, 0x00, 0x10, 0xEF],
            ];
        }

        var brightness = ToBrightnessPercent(frame.Brightness);
        return
        [
            [0x7E, 0x04, 0x04, 0x01, 0xFF, 0xFF, 0xFF, 0x00, 0xEF],
            BuildBrightnessCommand(brightness),
            BuildColorCommand(frame),
        ];
    }

    private static byte[] BuildBrightnessCommand(byte brightnessPercent)
    {
        return [0x7E, 0x00, 0x01, brightnessPercent, 0x00, 0x00, 0x00, 0x00, 0xEF];
    }

    private static byte[] BuildColorCommand(LightFrame frame)
    {
        return [0x7E, 0x07, 0x05, 0x03, frame.R, frame.G, frame.B, 0x10, 0xEF];
    }

    private static byte ToBrightnessPercent(byte brightness)
    {
        return (byte)Compatibility.Clamp((int)Math.Round(brightness * 100.0 / 255.0), 0, 100);
    }
}
