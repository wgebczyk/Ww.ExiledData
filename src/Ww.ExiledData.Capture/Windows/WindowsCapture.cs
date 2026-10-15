using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Ww.ExiledData.Capture.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsCapture
{
    /// <summary>Builds a <see cref="CaptureService"/> wired to the real Windows hook, clipboard and timer.</summary>
    public static CaptureService Create(CaptureOptions options, ICaptureSink sink, ILoggerFactory? loggers = null) =>
        new(options, new WindowsInputSource(), new WindowsClipboard(), new HighResolutionSleeper(), sink,
            logger: loggers?.CreateLogger<CaptureService>());
}
