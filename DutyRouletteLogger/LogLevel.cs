namespace DutyRouletteLogger;

/// <summary>
/// Verbosity levels for the plugin's own log filter (the "Minimum Log Level" setting).
/// The numeric order controls what gets included when a level is selected: a level always
/// includes everything listed after it below.
///
/// - <see cref="Warning"/> (default): only warnings and errors. The quietest setting.
/// - <see cref="Debug"/>: the important moves the plugin makes (e.g. entering/leaving an
///   instance), plus warnings and errors.
/// - <see cref="Info"/>: the plugin's less important, more chatty activity (e.g. queue-type
///   detection internals), plus Debug, warnings, and errors. The noisiest setting.
///
/// <see cref="Error"/> always gets logged regardless of the configured minimum — as long as
/// logging itself isn't fully disabled — since it sits above every other level here.
/// </summary>
public enum LogLevel
{
    Info = 0,
    Debug = 1,
    Warning = 2,
    Error = 3,
}
