namespace Ww.ExiledData.Capture;

public enum ClipboardWaitStatus
{
    Changed,
    Timeout,
    /// <summary>The clipboard changed but stayed locked by its owner until the deadline.</summary>
    Unreadable,
}

public sealed record ClipboardWaitResult(ClipboardWaitStatus Status, string? Text, uint Sequence, TimeSpan Elapsed);

/// <summary>Polls the clipboard sequence number until a new write appears or the timeout passes.</summary>
public sealed class ClipboardWaiter(IClipboard clipboard, ISleeper sleeper, TimeProvider time)
{
    /// <param name="baseline">Sequence number at the keypress.</param>
    /// <param name="consumed">Sequence number of the previous capture, so one write is never taken twice.</param>
    public ClipboardWaitResult Wait(uint baseline, uint consumed, TimeSpan timeout, TimeSpan pollInterval)
    {
        var start = time.GetUtcNow();
        var sawChange = false;
        while (true)
        {
            var seq = clipboard.SequenceNumber;
            if (seq != baseline && seq != consumed)
            {
                sawChange = true;
                if (clipboard.TryGetText(out var text))
                    // Re-read: the owner may write several formats, each bumping the number.
                    return new ClipboardWaitResult(ClipboardWaitStatus.Changed, text, clipboard.SequenceNumber, time.GetUtcNow() - start);
            }

            var elapsed = time.GetUtcNow() - start;
            if (elapsed >= timeout)
                return new ClipboardWaitResult(sawChange ? ClipboardWaitStatus.Unreadable : ClipboardWaitStatus.Timeout, null, seq, elapsed);
            sleeper.Sleep(pollInterval);
        }
    }
}
