using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Ww.ExiledData.App;

/// <summary>File logging (daily rolling, 14 days kept) with a level that can change at runtime.</summary>
public sealed class AppLogging : IDisposable
{
    private readonly LoggingLevelSwitch _level = new();
    private readonly Serilog.Core.Logger _serilog;

    public AppLogging(string directory, LogLevel level)
    {
        Directory.CreateDirectory(directory);
        SetLevel(level);
        _serilog = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(_level)
            .Enrich.WithProperty("Pid", Environment.ProcessId)
            .WriteTo.File(
                Path.Combine(directory, "ww-exileddata-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] ({ThreadId,3}) {SourceContext}: {Message:l}{NewLine}{Exception}")
            .Enrich.With(new ThreadIdEnricher())
            .CreateLogger();
        Factory = new SerilogLoggerFactory(_serilog);
    }

    public ILoggerFactory Factory { get; }

    public void SetLevel(LogLevel level) => _level.MinimumLevel = level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Information => LogEventLevel.Information,
        LogLevel.Warning => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        _ => LogEventLevel.Fatal,
    };

    public void Dispose()
    {
        Factory.Dispose();
        _serilog.Dispose();
    }

    private sealed class ThreadIdEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory) =>
            logEvent.AddPropertyIfAbsent(factory.CreateProperty("ThreadId", Environment.CurrentManagedThreadId));
    }
}
