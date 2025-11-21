using Microsoft.VisualStudio.TestTools.UnitTesting;
using Infidex.Api;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Infidex.Tests;

[TestClass]
public class ProcessMonitorTests
{
    [TestMethod]
    public void ProcessMonitor_DefaultState_IsCorrect()
    {
        using var monitor = new ProcessMonitor();
        
        Assert.IsFalse(monitor.IsRunning);
        Assert.IsFalse(monitor.Succeeded);
        Assert.IsFalse(monitor.IsCancelled);
        Assert.IsFalse(monitor.DidTimeOut);
        Assert.IsFalse(monitor.IsCompleted);
        Assert.AreEqual(0, monitor.ProgressPercent);
        Assert.AreEqual(string.Empty, monitor.ErrorMessage);
        Assert.IsNull(monitor.Exception);
        Assert.AreEqual(-1, monitor.TimeoutSeconds);
        Assert.AreEqual(ThreadPriority.Normal, monitor.ThreadPriority);
    }
    
    [TestMethod]
    public void ProgressPercent_ClampsToBounds()
    {
        using var monitor = new ProcessMonitor();
        
        // Set below minimum
        monitor.ProgressPercent = -50;
        Assert.AreEqual(0, monitor.ProgressPercent);
        
        // Set above maximum
        monitor.ProgressPercent = 150;
        Assert.AreEqual(100, monitor.ProgressPercent);
        
        // Set within bounds
        monitor.ProgressPercent = 42;
        Assert.AreEqual(42, monitor.ProgressPercent);
    }
    
    [TestMethod]
    public void ProgressChanged_RaisesEvent()
    {
        using var monitor = new ProcessMonitor();
        int eventCallCount = 0;
        int lastProgress = -1;
        
        monitor.ProgressChanged += (progress) =>
        {
            eventCallCount++;
            lastProgress = progress;
        };
        
        monitor.ProgressPercent = 25;
        Assert.AreEqual(1, eventCallCount);
        Assert.AreEqual(25, lastProgress);
        
        monitor.ProgressPercent = 50;
        Assert.AreEqual(2, eventCallCount);
        Assert.AreEqual(50, lastProgress);
        
        // Setting same value doesn't trigger event
        monitor.ProgressPercent = 50;
        Assert.AreEqual(2, eventCallCount);
    }
    
    [TestMethod]
    public void ProgressChanged_HandlesExceptionInHandler()
    {
        using var monitor = new ProcessMonitor();
        bool handler1Called = false;
        bool handler2Called = false;
        
        monitor.ProgressChanged += (progress) =>
        {
            handler1Called = true;
            throw new InvalidOperationException("Test exception");
        };
        
        monitor.ProgressChanged += (progress) =>
        {
            handler2Called = true;
        };
        
        // Should not throw, both handlers should be called
        monitor.ProgressPercent = 50;
        
        Assert.IsTrue(handler1Called);
        Assert.IsTrue(handler2Called);
    }
    
    [TestMethod]
    public void MarkStarted_SetsCorrectState()
    {
        using var monitor = new ProcessMonitor();
        DateTime beforeStart = DateTime.Now;
        
        monitor.MarkStarted();
        
        Assert.IsTrue(monitor.IsRunning);
        Assert.IsTrue(monitor.StartTime >= beforeStart);
        Assert.IsTrue(monitor.StartTime <= DateTime.Now);
    }
    
    [TestMethod]
    public void MarkFinished_SetsCorrectState()
    {
        using var monitor = new ProcessMonitor();
        
        monitor.MarkStarted();
        monitor.Succeeded = true;
        monitor.MarkFinished();
        
        Assert.IsFalse(monitor.IsRunning);
        Assert.IsTrue(monitor.IsCompleted);
        Assert.AreEqual(100, monitor.ProgressPercent); // Should set to 100 if succeeded
    }
    
    [TestMethod]
    public void MarkFinished_DoesNotSet100PercentIfNotSucceeded()
    {
        using var monitor = new ProcessMonitor();
        
        monitor.MarkStarted();
        monitor.ProgressPercent = 50;
        monitor.Succeeded = false;
        monitor.MarkFinished();
        
        Assert.IsFalse(monitor.IsRunning);
        Assert.AreEqual(50, monitor.ProgressPercent); // Should stay at 50
    }
    
    [TestMethod]
    public void Cancel_RequestsCancellation()
    {
        using var monitor = new ProcessMonitor();
        
        Assert.IsFalse(monitor.CancellationToken.IsCancellationRequested);
        
        monitor.Cancel();
        
        Assert.IsTrue(monitor.CancellationToken.IsCancellationRequested);
    }
    
    [TestMethod]
    public void WaitForCompletion_WaitsForMarkFinished()
    {
        using var monitor = new ProcessMonitor();
        monitor.MarkStarted();
        
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            monitor.MarkFinished();
        });
        
        bool completed = monitor.WaitForCompletion();
        
        Assert.IsTrue(completed);
        Assert.IsFalse(monitor.IsRunning);
    }
    
    [TestMethod]
    public void WaitForCompletion_TimesOut()
    {
        using var monitor = new ProcessMonitor();
        monitor.TimeoutSeconds = 1;
        monitor.MarkStarted();
        
        // Never call MarkFinished, should timeout
        bool completed = monitor.WaitForCompletion();
        
        Assert.IsFalse(completed);
        Assert.IsTrue(monitor.DidTimeOut);
        Assert.IsTrue(monitor.ErrorMessage.Contains("timed out"));
    }
    
    [TestMethod]
    public async Task WaitForCompletionAsync_WaitsForMarkFinished()
    {
        using var monitor = new ProcessMonitor();
        monitor.MarkStarted();
        
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            monitor.MarkFinished();
        });
        
        bool completed = await monitor.WaitForCompletionAsync();
        
        Assert.IsTrue(completed);
        Assert.IsFalse(monitor.IsRunning);
    }
    
    [TestMethod]
    public async Task WaitForCompletionAsync_CompletesImmediatelyIfNotRunning()
    {
        using var monitor = new ProcessMonitor();
        
        bool completed = await monitor.WaitForCompletionAsync();
        
        Assert.IsTrue(completed);
    }
    
    [TestMethod]
    public void WaitForProcessStarted_WaitsForMarkStarted()
    {
        using var monitor = new ProcessMonitor();
        
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            monitor.MarkStarted();
        });
        
        monitor.WaitForProcessStarted(2000);
        
        Assert.IsTrue(monitor.IsRunning);
    }
    
    [TestMethod]
    public void Reset_ClearsState()
    {
        using var monitor = new ProcessMonitor();
        
        // Set various states
        monitor.MarkStarted();
        monitor.ProgressPercent = 50;
        monitor.ErrorMessage = "Test error";
        monitor.Exception = new InvalidOperationException("Test");
        monitor.Succeeded = true;
        monitor.DidTimeOut = true;
        monitor.Cancel();
        
        // Note: IsRunning is true at this point
        Assert.IsTrue(monitor.IsRunning);
        
        // Reset
        monitor.Reset();
        
        // Verify all cleared (Note: Reset doesn't change IsRunning, only MarkFinished does)
        // IsRunning is not cleared by Reset, only by MarkFinished
        Assert.AreEqual(0, monitor.ProgressPercent);
        Assert.AreEqual(string.Empty, monitor.ErrorMessage);
        Assert.IsNull(monitor.Exception);
        Assert.IsFalse(monitor.Succeeded);
        Assert.IsFalse(monitor.DidTimeOut);
        Assert.IsFalse(monitor.CancellationToken.IsCancellationRequested); // New token
    }
    
    [TestMethod]
    public void ShouldAbort_ReturnsFalseByDefault()
    {
        using var monitor = new ProcessMonitor();
        monitor.MarkStarted();
        
        bool shouldAbort = ProcessMonitor.ShouldAbort(monitor);
        
        Assert.IsFalse(shouldAbort);
    }
    
    [TestMethod]
    public void ShouldAbort_ReturnsTrueWhenCancelled()
    {
        using var monitor = new ProcessMonitor();
        monitor.MarkStarted();
        monitor.Cancel();
        
        bool shouldAbort = ProcessMonitor.ShouldAbort(monitor);
        
        Assert.IsTrue(shouldAbort);
        Assert.IsFalse(monitor.Succeeded);
        Assert.IsTrue(monitor.ErrorMessage.Contains("cancelled"));
    }
    
    [TestMethod]
    public void ShouldAbort_ReturnsTrueWhenTimedOut()
    {
        using var monitor = new ProcessMonitor();
        monitor.TimeoutSeconds = 1; // 1 second timeout
        monitor.MarkStarted();
        
        // Artificially set start time to past
        var field = typeof(ProcessMonitor).GetProperty("StartTime");
        field!.SetValue(monitor, DateTime.Now.AddSeconds(-2));
        
        bool shouldAbort = ProcessMonitor.ShouldAbort(monitor);
        
        Assert.IsTrue(shouldAbort);
        Assert.IsTrue(monitor.DidTimeOut);
        Assert.IsFalse(monitor.Succeeded);
        Assert.IsTrue(monitor.ErrorMessage.Contains("timed out"));
    }
    
    [TestMethod]
    [ExpectedException(typeof(InvalidOperationException))]
    public void ThrowIfOccupied_ThrowsWhenRunning()
    {
        using var monitor = new ProcessMonitor();
        monitor.MarkStarted();
        
        monitor.ThrowIfOccupied();
    }
    
    [TestMethod]
    public void ThrowIfOccupied_DoesNotThrowWhenNotRunning()
    {
        using var monitor = new ProcessMonitor();
        
        // Should not throw
        monitor.ThrowIfOccupied();
    }
    
    [TestMethod]
    public void IsCancelled_ReturnsTrueWhenCancelledAndNotRunning()
    {
        using var monitor = new ProcessMonitor();
        
        monitor.MarkStarted();
        monitor.Cancel();
        monitor.Succeeded = false;
        monitor.MarkFinished();
        
        Assert.IsTrue(monitor.IsCancelled);
    }
    
    [TestMethod]
    public void IsCancelled_ReturnsFalseWhenSucceeded()
    {
        using var monitor = new ProcessMonitor();
        
        monitor.MarkStarted();
        monitor.Cancel();
        monitor.Succeeded = true;
        monitor.MarkFinished();
        
        Assert.IsFalse(monitor.IsCancelled);
    }
    
    [TestMethod]
    public void IsCancelled_ReturnsFalseWhenTimedOut()
    {
        using var monitor = new ProcessMonitor();
        
        monitor.MarkStarted();
        monitor.Cancel();
        monitor.DidTimeOut = true;
        monitor.MarkFinished();
        
        Assert.IsFalse(monitor.IsCancelled);
    }
    
    [TestMethod]
    [ExpectedException(typeof(ObjectDisposedException))]
    public void Cancel_ThrowsAfterDispose()
    {
        var monitor = new ProcessMonitor();
        monitor.Dispose();
        
        monitor.Cancel();
    }
    
    [TestMethod]
    [ExpectedException(typeof(ObjectDisposedException))]
    public void WaitForCompletion_ThrowsAfterDispose()
    {
        var monitor = new ProcessMonitor();
        monitor.Dispose();
        
        monitor.WaitForCompletion();
    }
    
    [TestMethod]
    public void SimulateIndexingOperation_WithProgressReporting()
    {
        using var monitor = new ProcessMonitor();
        int progressUpdateCount = 0;
        
        monitor.ProgressChanged += (progress) =>
        {
            progressUpdateCount++;
        };
        
        // Simulate indexing operation
        Task.Run(() =>
        {
            monitor.MarkStarted();
            
            for (int i = 0; i <= 100; i += 10)
            {
                if (ProcessMonitor.ShouldAbort(monitor))
                    break;
                
                monitor.ProgressPercent = i;
                Thread.Sleep(10);
            }
            
            monitor.Succeeded = true;
            monitor.MarkFinished();
        });
        
        bool completed = monitor.WaitForCompletion();
        
        Assert.IsTrue(completed);
        Assert.IsTrue(monitor.Succeeded);
        Assert.AreEqual(100, monitor.ProgressPercent);
        Assert.IsTrue(progressUpdateCount > 0);
    }
    
    [TestMethod]
    public void SimulateIndexingOperation_WithCancellation()
    {
        using var monitor = new ProcessMonitor();
        
        // Simulate indexing operation
        _ = Task.Run(() =>
        {
            monitor.MarkStarted();
            
            for (int i = 0; i <= 100; i += 10)
            {
                if (ProcessMonitor.ShouldAbort(monitor))
                {
                    monitor.MarkFinished();
                    return;
                }
                
                monitor.ProgressPercent = i;
                Thread.Sleep(50);
            }
            
            monitor.Succeeded = true;
            monitor.MarkFinished();
        });
        
        // Wait a bit then cancel
        Thread.Sleep(100);
        monitor.Cancel();
        
        bool completed = monitor.WaitForCompletion();
        
        Assert.IsTrue(completed);
        Assert.IsFalse(monitor.Succeeded);
        Assert.IsTrue(monitor.IsCancelled);
        Assert.IsTrue(monitor.ProgressPercent < 100);
    }
}

