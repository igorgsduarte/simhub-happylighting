using GameReaderCommon;
using SimHub.Plugins;
using System.Globalization;
using System.Reflection;
using System.Linq;

namespace HappyLightingPlugin;

public sealed class TelemetryPipeline
{
    private readonly PluginManager _pluginManager;
    private readonly MethodInfo? _getPropertyValue;

    public TelemetryPipeline(PluginManager pluginManager)
    {
        _pluginManager = pluginManager;
        _getPropertyValue = pluginManager
            .GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m =>
                string.Equals(m.Name, "GetPropertyValue", StringComparison.Ordinal) &&
                m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType == typeof(string));
    }

    public TelemetrySnapshot Build(GameData data, PluginSettings settings)
    {
        var current = data.NewData;
        if (current is null)
            return new TelemetrySnapshot { GameRunning = data.GameRunning };

        var pitLane = current.IsInPitLane > 0 || current.IsInPit > 0 || ReadBool("DataCorePlugin.GameData.NewData.OnPitRoad");
        var pitLimiter = current.PitLimiterOn > 0 || ReadBool("DataCorePlugin.GameData.NewData.PitLim");

        var fuel = current.Fuel > 0 ? current.Fuel : current.FuelRaw;
        if (fuel <= 0)
            fuel = ReadDouble("DataCorePlugin.GameData.NewData.FuelLevel");

        var gameRunning = data.GameRunning || ReadBool("DataCorePlugin.GameRunning");
        var headlights = ReadBool("DataCorePlugin.GameData.NewData.dcHeadlights");
        var nightSession = ReadBool("DataCorePlugin.GameData.NewData.IsNight");

        var flag = NormalizeFlag(ResolveFlag(current));
        if (string.IsNullOrWhiteSpace(flag))
            flag = NormalizeFlag(ReadString("DataCorePlugin.GameData.NewData.Flag"));

        return new TelemetrySnapshot
        {
            Timestamp = DateTimeOffset.UtcNow,
            PitLane = pitLane,
            PitLimiter = pitLimiter,
            FuelLiters = fuel,
            LowFuel = fuel > 0 && fuel <= settings.LowFuelThresholdLiters,
            GameRunning = gameRunning,
            HeadlightsOn = headlights,
            NightSession = nightSession,
            MarshalFlag = flag
        };
    }

    private bool ReadBool(string key)
    {
        var raw = ReadValue(key);
        if (raw is null) return false;
        if (raw is bool b) return b;
        if (raw is IConvertible c)
        {
            try { return c.ToDouble(CultureInfo.InvariantCulture) > 0; } catch { }
        }

        return bool.TryParse(raw.ToString(), out var parsed) && parsed;
    }

    private double ReadDouble(string key)
    {
        var raw = ReadValue(key);
        if (raw is null) return 0;
        if (raw is double d) return d;
        if (raw is IConvertible c)
        {
            try { return c.ToDouble(CultureInfo.InvariantCulture); } catch { }
        }

        return double.TryParse(raw.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private string? ReadString(string key)
    {
        return ReadValue(key)?.ToString();
    }

    private object? ReadValue(string key)
    {
        if (_getPropertyValue is null) return null;
        try { return _getPropertyValue.Invoke(_pluginManager, new object[] { key }); } catch { return null; }
    }

    private static string? ResolveFlag(StatusDataBase data)
    {
        if (data.Flag_Black > 0) return "black";
        if (data.Flag_Checkered > 0) return "checkered";
        if (data.Flag_Yellow > 0) return "yellow";
        if (data.Flag_Blue > 0) return "blue";
        if (data.Flag_Green > 0) return "green";
        if (data.Flag_White > 0) return "white";
        return string.IsNullOrWhiteSpace(data.Flag_Name) ? null : data.Flag_Name;
    }

    private static string? NormalizeFlag(string? rawFlag)
    {
        if (string.IsNullOrWhiteSpace(rawFlag))
            return null;

        var normalized = rawFlag!.Trim().ToLowerInvariant();
        if (normalized.Contains("yellow")) return "yellow";
        if (normalized.Contains("green")) return "green";
        if (normalized.Contains("white")) return "white";
        if (normalized.Contains("black")) return "black";
        if (normalized.Contains("check")) return "checkered";
        if (normalized.Contains("blue")) return "blue";
        return normalized;
    }
}
