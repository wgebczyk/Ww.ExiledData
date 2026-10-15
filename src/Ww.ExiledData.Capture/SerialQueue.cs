using System.Collections.Concurrent;

namespace Ww.ExiledData.Capture;

/// <summary>Runs work items one at a time, in order, on a dedicated background thread.</summary>
internal sealed class SerialQueue : IDisposable
{
    private readonly BlockingCollection<(Action Work, TaskCompletionSource Done)> _items = [];
    private readonly Thread _thread;
    private int _disposed;

    public SerialQueue(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>Queues work; the task completes (or faults) once it has run. Canceled after disposal.</summary>
    public Task Post(Action work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _items.Add((work, done));
        }
        catch (InvalidOperationException)
        {
            done.SetCanceled();
        }
        return done.Task;
    }

    private void Run()
    {
        foreach (var (work, done) in _items.GetConsumingEnumerable())
        {
            try
            {
                work();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        _items.CompleteAdding();
        if (Thread.CurrentThread != _thread)
            _thread.Join();
        _items.Dispose();
    }
}
