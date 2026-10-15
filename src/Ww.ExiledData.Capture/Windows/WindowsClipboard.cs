using System.Runtime.Versioning;
using static Ww.ExiledData.Capture.Windows.NativeMethods;

namespace Ww.ExiledData.Capture.Windows;

[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsClipboard : IClipboard
{
    public uint SequenceNumber => GetClipboardSequenceNumber();

    public bool TryGetText(out string? text)
    {
        text = null;
        if (!OpenClipboard(0))
            return false; // owner (the game) still has it open

        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == 0)
                return true;

            var data = (char*)GlobalLock(handle);
            if (data is null)
                return true;
            try
            {
                // Bounded by the allocation size in case the terminator is missing.
                var max = (int)Math.Min((ulong)GlobalSize(handle) / sizeof(char), int.MaxValue);
                var span = new ReadOnlySpan<char>(data, max);
                var length = span.IndexOf('\0');
                text = new string(length >= 0 ? span[..length] : span);
                return true;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }
}
