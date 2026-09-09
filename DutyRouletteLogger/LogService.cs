namespace DutyRouletteLogger;

/// <summary>
/// Forwards the plugin's messages to Dalamud's native log (<see cref="DutyRouletteLoggerPlugin.Log"/>,
/// visible in /xllog). There is no file-based logging: every message goes only through here,
/// respecting the "Enable Logging" toggle and the "Minimum Log Level" configured in the
/// Settings window (see <see cref="LogLevel"/> for what each level includes).
///
/// Note: <see cref="LogDebug"/> maps to Dalamud's own <c>IPluginLog.Debug</c>, which Dalamud
/// may filter separately based on its own log level — this plugin's filter only controls
/// whether the call is made at all, not whether Dalamud ultimately renders it in /xllog.
/// </summary>
public class LogService
{
    private readonly DutyRouletteLoggerConfiguration configuration;

    public LogService(DutyRouletteLoggerConfiguration configuration)
    {
        this.configuration = configuration;
    }

    public void LogInfo(string message) => Write(LogLevel.Info, message);

    public void LogDebug(string message) => Write(LogLevel.Debug, message);

    public void LogWarning(string message) => Write(LogLevel.Warning, message);

    public void LogError(string message) => Write(LogLevel.Error, message);

    private void Write(LogLevel level, string message)
    {
        if (!configuration.IsLoggingEnabled)
        {
            return;
        }

        if (level < configuration.MinimumLogLevel)
        {
            return;
        }

        // "{Message}" is a Serilog template with a single placeholder: this prevents literal
        // braces inside the message (e.g. a stack trace or a file path) from being interpreted
        // as part of the template.
        switch (level)
        {
            case LogLevel.Debug:
                DutyRouletteLoggerPlugin.Log.Debug("{Message}", message);
                break;
            case LogLevel.Warning:
                DutyRouletteLoggerPlugin.Log.Warning("{Message}", message);
                break;
            case LogLevel.Error:
                DutyRouletteLoggerPlugin.Log.Error("{Message}", message);
                break;
            default:
                DutyRouletteLoggerPlugin.Log.Info("{Message}", message);
                break;
        }
    }
}
