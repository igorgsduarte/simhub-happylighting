using GameReaderCommon;
using SimHub.Plugins;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace HappyLightingPlugin;

public sealed class TelemetryPipeline
{
    private const string OnPitRoadKey = "DataCorePlugin.GameData.NewData.OnPitRoad";
    private const string PitLimiterKey = "DataCorePlugin.GameData.NewData.PitLim";
    private const string FuelLevelKey = "DataCorePlugin.GameData.NewData.FuelLevel";
    private const string HeadlightsKey = "DataCorePlugin.GameData.NewData.dcHeadlights";
    private const string IsNightKey = "DataCorePlugin.GameData.NewData.IsNight";
    private const string FlagKey = "DataCorePlugin.GameData.NewData.Flag";
    private const string GameRunningKey = "DataCorePlugin.GameRunning";

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

        var cache = new Dictionary<string, object?>(StringComparer.Ordinal);
        var pitLane = current.IsInPitLane > 0 ||
            current.IsInPit > 0 ||
            ReadBool(OnPitRoadKey, cache) ||
            ReadDouble("GameData.IsInPitSince", cache) > 0;

        var pitLimiter = current.PitLimiterOn > 0 ||
            ReadBool(PitLimiterKey, cache) ||
            ReadBool("GameData.PitLimiterOn", cache);

        var fuel = current.Fuel > 0 ? current.Fuel : current.FuelRaw;
        if (fuel <= 0)
            fuel = ReadDouble(FuelLevelKey, cache);

        var gameRunning = data.GameRunning || ReadBool(GameRunningKey, cache) || ReadBool("GameRunning", cache);
        var headlights = ReadBool(HeadlightsKey, cache);
        var nightSession = ReadBool(IsNightKey, cache);
        if (!nightSession)
        {
            var dayNightMode = ReadString("GameData.DayNightMode", cache);
            nightSession = string.Equals(dayNightMode, "Night", StringComparison.OrdinalIgnoreCase);
        }

        var flag = NormalizeFlag(ResolveFlag(current));
        if (string.IsNullOrWhiteSpace(flag))
            flag = NormalizeFlag(ResolveFlagFromGameDataKeys(cache));
        if (string.IsNullOrWhiteSpace(flag))
            flag = NormalizeFlag(ReadString(FlagKey, cache));

        // When key telemetry channels are active, force running state to avoid white idle lingering.
        if (!gameRunning && (pitLane || pitLimiter || !string.IsNullOrWhiteSpace(flag) || fuel > 0))
            gameRunning = true;

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

    private bool ReadBool(string key, Dictionary<string, object?> cache)
    {
        var raw = ReadValue(key, cache);
        if (raw is null) return false;
        if (raw is bool b) return b;
        if (raw is IConvertible c)
        {
            try { return c.ToDouble(CultureInfo.InvariantCulture) > 0; } catch { }
        }

        return bool.TryParse(raw.ToString(), out var parsed) && parsed;
    }

    private double ReadDouble(string key, Dictionary<string, object?> cache)
    {
        var raw = ReadValue(key, cache);
        if (raw is null) return 0;
        if (raw is double d) return d;
        if (raw is IConvertible c)
        {
            try { return c.ToDouble(CultureInfo.InvariantCulture); } catch { }
        }

        return double.TryParse(raw.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private string? ReadString(string key, Dictionary<string, object?> cache)
    {
        return ReadValue(key, cache)?.ToString();
    }

    private object? ReadValue(string key, Dictionary<string, object?> cache)
    {
        if (cache.TryGetValue(key, out var cached))
            return cached;

        object? value = null;
        if (_getPropertyValue is not null)
        {
            try { value = _getPropertyValue.Invoke(_pluginManager, new object[] { key }); } catch { }
        }

        cache[key] = value;
        return value;
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

    private string? ResolveFlagFromGameDataKeys(Dictionary<string, object?> cache)
    {
        if (ReadString("GameRawData.Telemetry.SessionFlagsDetails.Isblack", cache) == "True") return "black";
        if (ReadString("GameRawData.Telemetry.SessionFlagsDetails.Isblue", cache) == "True") return "blue";
        if (ReadString("GameRawData.Telemetry.SessionFlagsDetails.checkered", cache) == "True") return "checkered";
        if (ReadString("GameRawData.Telemetry.SessionFlagsDetails.Isgreen", cache) == "True") return "green";
        if (ReadString("GameRawData.Telemetry.SessionFlagsDetails.Isyellow", cache) == "True") return "yellow";
        if (ReadString("GameRawData.Telemetry.SessionFlagsDetails.Iswhite", cache) == "True") return "white";
        return ReadString("GameData.Flag_Name", cache);
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
