using System;
using System.Threading;
using System.Threading.Tasks;

namespace IdleMasterExtended
{
    // WebView2 operations have no cancellation overload. Stop waiting without treating
    // a stuck browser operation as expired login, and observe any late task failure.
    internal static class AsyncDeadline
    {
        public static async Task<T> WaitAsync<T>(Task<T> operation, TimeSpan timeout, CancellationToken token,
            Func<TimeSpan, CancellationToken, Task> delay = null, Action<Task> faultObserved = null)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            // The caller has already started this uncancellable operation. Even cancellation
            // before entry or a failing timer factory must observe its eventual exception.
            ObserveFaults(operation, faultObserved);
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            token.ThrowIfCancellationRequested();
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                try
                {
                    var timer = (delay ?? ((duration, cancellation) => Task.Delay(duration, cancellation)))(timeout, deadline.Token);
                    if (timer == null) throw new InvalidOperationException("The deadline timer was not created.");
                    ObserveFaults(timer, faultObserved);
                    var completed = await Task.WhenAny(operation, timer);
                    token.ThrowIfCancellationRequested();
                    if (completed != operation)
                    {
                        // Consume a canceled/faulted timer too; only successful expiry is a timeout.
                        await timer;
                        throw new TimeoutException("The browser operation took too long.");
                    }
                    var result = await operation;
                    token.ThrowIfCancellationRequested();
                    return result;
                }
                finally
                {
                    deadline.Cancel();
                }
            }
        }
        public static Task WaitAsync(Task operation, TimeSpan timeout, CancellationToken token,
            Func<TimeSpan, CancellationToken, Task> delay = null, Action<Task> faultObserved = null)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            return WaitAsync(CompleteAsync(operation), timeout, token, delay, faultObserved);
        }
        private static async Task<bool> CompleteAsync(Task operation) { await operation.ConfigureAwait(false); return true; }

        private static void ObserveFaults(Task task, Action<Task> faultObserved)
        {
            _ = task.ContinueWith(completed =>
            {
                var ignored = completed.Exception;
                try { faultObserved?.Invoke(completed); }
                catch { /* The optional diagnostic/test observer cannot create another fault. */ }
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
