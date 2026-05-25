using Microsoft.Extensions.Logging;

namespace HappyLightingPlugin;

public interface ITelemetrySource
{
    bool TryGetBool(string key, out bool value);
    bool TryGetDouble(string key, out double value);
    bool TryGetString(string key, out string? value);
}

public sealed class TelemetryReader(ILogger logger)
{
    public TelemetrySnapshot Read(ITelemetrySource source, PluginSettings settings)
    {
        source.TryGetBool("DataCorePlugin.GameData.NewData.PitLim", out var pitLimiter);
        source.TryGetBool("DataCorePlugin.GameRunning", out var gameRunning);
        source.TryGetBool("DataCorePlugin.GameData.NewData.OnPitRoad", out var pitRoad);
        source.TryGetBool("DataCorePlugin.GameData.NewData.dcHeadlights", out var headlights);
        source.TryGetBool("DataCorePlugin.GameData.NewData.IsNight", out var isNight);
        source.TryGetDouble("DataCorePlugin.GameData.NewData.FuelLevel", out var fuel);
        source.TryGetString("DataCorePlugin.GameData.NewData.Flag", out var flag);

        var snapshot = new TelemetrySnapshot
        {
            GameRunning = gameRunning,
            PitLane = pitRoad,
            PitLimiter = pitLimiter,
            HeadlightsOn = headlights,
            NightSession = isNight,
            FuelLiters = fuel,
            LowFuel = fuel <= settings.LowFuelThresholdLiters,
            MarshalFlag = flag
        };

        if (settings.EnableDetailedTelemetryLogs)
        {
            logger.LogInformation("Telemetry snapshot pit={PitLane} lim={PitLimiter} head={Headlights} night={Night} fuel={Fuel} flag={Flag}",
                snapshot.PitLane, snapshot.PitLimiter, snapshot.HeadlightsOn, snapshot.NightSession, snapshot.FuelLiters, snapshot.MarshalFlag);
        }

        return snapshot;
    }
}
