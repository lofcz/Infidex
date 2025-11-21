using System;
using System.Threading;
using System.Threading.Tasks;

namespace Infidex.Api;

/// <summary>
/// Monitors long-running operations with progress reporting, cancellation support, and timeout handling.
/// Used for operations like Init, Load, Index, and LoadAllFilters.
/// </summary>
public class ProcessMonitor : IDisposable
{
    private readonly object _lock = new object();
    private readonly ManualResetEventSlim _completedEvent = new ManualResetEventSlim(false);
    private readonly ManualResetEventSlim _startedEvent = new ManualResetEventSlim(false);
    private CancellationTokenSource _cts = new CancellationTokenSource();
    private TaskCompletionSource<bool>? _asyncCompletionSource;
    private bool _disposed;
    private int _progressPercent;
    
    /// <summary>
    /// Event raised when progress percentage changes (0-100).
    /// </summary>
    public event Action<int>? ProgressChanged;
    
    /// <summary>
    /// Gets or sets the current progress percentage (0-100).
    /// Setting this value will trigger the ProgressChanged event.
    /// </summary>
    public int ProgressPercent
    {
        get => _progressPercent;
        internal set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (_progressPercent == clamped)
                return;
            
            _progressPercent = clamped;
            
            // Safely invoke all subscribers
            var handlers = ProgressChanged;
            if (handlers != null)
            {
                foreach (var handler in handlers.GetInvocationList())
                {
                    try
                    {
                        ((Action<int>)handler)(_progressPercent);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error in ProgressChanged handler: {ex.Message}");
                    }
                }
            }
        }
    }
    
    /// <summary>
    /// Gets whether the operation is currently running.
    /// </summary>
    public bool IsRunning { get; internal set; }
    
    /// <summary>
    /// Gets whether the operation completed successfully.
    /// </summary>
    public bool Succeeded { get; internal set; }
    
    /// <summary>
    /// Gets whether the operation was cancelled via cancellation token.
    /// </summary>
    public bool IsCancelled => CancellationToken.IsCancellationRequested && !Succeeded && !IsRunning && !DidTimeOut;
    
    /// <summary>
    /// Gets whether the operation timed out.
    /// </summary>
    public bool DidTimeOut { get; internal set; }
    
    /// <summary>
    /// Gets whether the operation is completed (regardless of success/failure).
    /// </summary>
    public bool IsCompleted => !IsRunning && _completedEvent.Wait(0);
    
    /// <summary>
    /// Gets the time when the operation started.
    /// </summary>
    public DateTime StartTime { get; internal set; } = DateTime.Now;
    
    /// <summary>
    /// Gets or sets the error message if the operation failed.
    /// </summary>
    public string ErrorMessage { get; internal set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the exception if the operation threw an exception.
    /// </summary>
    public Exception? Exception { get; internal set; }
    
    /// <summary>
    /// Gets or sets the timeout in seconds. -1 means no timeout.
    /// </summary>
    public int TimeoutSeconds { get; set; } = -1;
    
    /// <summary>
    /// Gets or sets the thread priority for the operation.
    /// </summary>
    public ThreadPriority ThreadPriority { get; set; } = ThreadPriority.Normal;
    
    /// <summary>
    /// Gets the cancellation token for this operation.
    /// </summary>
    internal CancellationToken CancellationToken => _cts.Token;
    
    /// <summary>
    /// Creates a new ProcessMonitor instance.
    /// </summary>
    public ProcessMonitor()
    {
    }
    
    /// <summary>
    /// Requests cancellation of the operation.
    /// </summary>
    public void Cancel()
    {
        ThrowIfDisposed();
        _cts.Cancel();
    }
    
    /// <summary>
    /// Blocks until the operation completes or times out.
    /// Returns true if completed within timeout, false if timed out.
    /// </summary>
    public bool WaitForCompletion()
    {
        ThrowIfDisposed();
        
        int millisecondsTimeout = TimeoutSeconds > 0 ? TimeoutSeconds * 1000 : -1;
        bool completed = _completedEvent.Wait(millisecondsTimeout);
        
        IsRunning = false;
        DidTimeOut = !completed;
        
        if (DidTimeOut)
        {
            ErrorMessage = "Operation timed out.";
        }
        
        return completed;
    }
    
    /// <summary>
    /// Asynchronously waits for the operation to complete.
    /// </summary>
    public Task<bool> WaitForCompletionAsync()
    {
        ThrowIfDisposed();
        
        _asyncCompletionSource = new TaskCompletionSource<bool>();
        
        if (!IsRunning)
        {
            _asyncCompletionSource.TrySetResult(true);
        }
        
        return _asyncCompletionSource.Task;
    }
    
    /// <summary>
    /// Waits for the operation to start (useful when operations are started on background threads).
    /// </summary>
    public void WaitForProcessStarted(int timeoutMilliseconds = -1)
    {
        ThrowIfDisposed();
        _startedEvent.Wait(timeoutMilliseconds);
    }
    
    /// <summary>
    /// Checks if the operation should be aborted due to cancellation or timeout.
    /// Used internally by long-running operations to check periodically.
    /// </summary>
    internal static bool ShouldAbort(ProcessMonitor monitor)
    {
        if (monitor.CancellationToken.IsCancellationRequested)
        {
            monitor.ErrorMessage = "Operation was cancelled.";
            monitor.Succeeded = false;
            return true;
        }
        
        if (monitor.TimeoutSeconds > 0 && 
            (DateTime.Now - monitor.StartTime).TotalSeconds > monitor.TimeoutSeconds)
        {
            monitor.ErrorMessage = "Operation timed out.";
            monitor.DidTimeOut = true;
            monitor.Succeeded = false;
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Marks the operation as started. Called internally by operations.
    /// </summary>
    internal void MarkStarted()
    {
        lock (_lock)
        {
            Reset();
            IsRunning = true;
            StartTime = DateTime.Now;
            _startedEvent.Set();
        }
    }
    
    /// <summary>
    /// Marks the operation as finished. Called internally by operations.
    /// </summary>
    internal void MarkFinished()
    {
        lock (_lock)
        {
            IsRunning = false;
            _completedEvent.Set();
            _asyncCompletionSource?.TrySetResult(true);
            
            if (Succeeded)
            {
                ProgressPercent = 100;
            }
        }
    }
    
    /// <summary>
    /// Resets the monitor state for reuse.
    /// </summary>
    internal void Reset()
    {
        lock (_lock)
        {
            _startedEvent.Reset();
            _completedEvent.Reset();
            
            var oldCts = _cts;
            _cts = new CancellationTokenSource();
            oldCts?.Dispose();
            
            ErrorMessage = string.Empty;
            Exception = null;
            ProgressPercent = 0;
            Succeeded = false;
            DidTimeOut = false;
            StartTime = DateTime.Now;
        }
    }
    
    /// <summary>
    /// Throws if the monitor is already running an operation.
    /// </summary>
    internal void ThrowIfOccupied()
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("ProcessMonitor is already monitoring an operation. Wait for completion or use a different monitor.");
        }
    }
    
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ProcessMonitor));
        }
    }
    
    /// <summary>
    /// Disposes resources used by the ProcessMonitor.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    
    /// <summary>
    /// Disposes resources.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _completedEvent?.Dispose();
                _startedEvent?.Dispose();
                _cts?.Dispose();
                ProgressChanged = null;
            }
            _disposed = true;
        }
    }
    
    /// <summary>
    /// Finalizer for ProcessMonitor.
    /// </summary>
    ~ProcessMonitor()
    {
        Dispose(false);
    }
}

