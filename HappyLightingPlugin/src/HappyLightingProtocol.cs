namespace HappyLightingPlugin;

public sealed class HappyLightingProtocol
{
    public byte[] BuildFrame(LightFrame frame)
    {
        // Isolated protocol encoder; hardware revisions may require changes here only.
        // Typical HappyLighting RGB command pattern variant.
        var onByte = frame.IsOn ? (byte)0x23 : (byte)0x24;
        return [0x7E, 0x07, onByte, frame.R, frame.G, frame.B, frame.Brightness, 0x00, 0xEF];
    }
}
