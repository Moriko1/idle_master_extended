using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace IdleMasterExtended.Tests
{
    internal static class AsyncDeadlineTests
    {
        public static async Task RunAllAsync()
        {
            await TimeoutObservesLateOperationFailure();
            await CancellationObservesLateOperationFailure();
            await PreCancellationObservesAlreadyStartedOperation();
            await FailedTimerFactoryObservesLateOperationFailure();
            await SuccessfulOperationObservesLateTimerFailure();
            await FailedTimerPreservesItsFailure();
            await CanceledTimerPreservesCancellation();
            await NonGenericPreCancellationObservesLateFailure();
        }

        private static async Task TimeoutObservesLateOperationFailure()
        {
            var pending = new TaskCompletionSource<int>();
            var observed = new List<Task>();
            var timedOut = false;
            try { await AsyncDeadline.WaitAsync(pending.Task, TimeSpan.FromSeconds(10), CancellationToken.None,
                (time, token) => Task.CompletedTask, observed.Add); }
            catch (TimeoutException) { timedOut = true; }
            Test.Assert(timedOut, "A stalled browser operation cannot hold the UI or session gate indefinitely");
            pending.SetException(new InvalidOperationException("Synthetic late browser failure"));
            Test.Assert(observed.Count == 1 && ReferenceEquals(observed[0], pending.Task), "Timeout observes a late browser task failure");
        }

        private static async Task CancellationObservesLateOperationFailure()
        {
            using (var cancel = new CancellationTokenSource())
            {
                var canceled = false;
                var pending = new TaskCompletionSource<int>();
                var observed = new List<Task>();
                try { await AsyncDeadline.WaitAsync(pending.Task, TimeSpan.FromSeconds(10), cancel.Token,
                    (time, token) => { cancel.Cancel(); return Task.CompletedTask; }, observed.Add); }
                catch (OperationCanceledException) { canceled = true; }
                Test.Assert(canceled, "Closing interrupts a browser wait instead of waiting for its timeout");
                pending.SetException(new InvalidOperationException("Synthetic canceled browser failure"));
                Test.Assert(observed.Count == 1 && ReferenceEquals(observed[0], pending.Task), "Cancellation observes a late browser failure");
            }
        }

        private static async Task PreCancellationObservesAlreadyStartedOperation()
        {
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                var pending = new TaskCompletionSource<int>();
                var observed = new List<Task>();
                var timerStarted = false;
                var canceled = false;
                try { await AsyncDeadline.WaitAsync(pending.Task, TimeSpan.FromSeconds(10), cancel.Token,
                    (time, token) => { timerStarted = true; return Task.CompletedTask; }, observed.Add); }
                catch (OperationCanceledException) { canceled = true; }
                Test.Assert(canceled && !timerStarted, "A pre-canceled caller never starts a timer");
                pending.SetException(new InvalidOperationException("Synthetic pre-canceled browser failure"));
                Test.Assert(observed.Count == 1 && ReferenceEquals(observed[0], pending.Task), "Pre-cancellation observes the already started browser task");
            }
        }

        private static async Task FailedTimerFactoryObservesLateOperationFailure()
        {
            var pending = new TaskCompletionSource<int>();
            var observed = new List<Task>();
            var expected = new InvalidOperationException("Synthetic timer factory failure");
            var failed = false;
            try { await AsyncDeadline.WaitAsync(pending.Task, TimeSpan.FromSeconds(10), CancellationToken.None,
                (time, token) => { throw expected; }, observed.Add); }
            catch (InvalidOperationException ex) { failed = ReferenceEquals(ex, expected); }
            Test.Assert(failed, "A timer factory failure preserves its error");
            pending.SetException(new InvalidOperationException("Synthetic late browser failure"));
            Test.Assert(observed.Count == 1 && ReferenceEquals(observed[0], pending.Task), "A failed timer factory still observes the browser operation");
        }

        private static async Task SuccessfulOperationObservesLateTimerFailure()
        {
            var timer = new TaskCompletionSource<bool>();
            var observed = new List<Task>();
            CancellationToken timerToken = default(CancellationToken);
            Test.Assert(await AsyncDeadline.WaitAsync(Task.FromResult(7), TimeSpan.FromSeconds(10), CancellationToken.None,
                (time, token) => { timerToken = token; return timer.Task; }, observed.Add) == 7, "Completed browser operations preserve their result");
            Test.Assert(timerToken.IsCancellationRequested, "Successful browser work cancels its unused deadline timer");
            timer.SetException(new InvalidOperationException("Synthetic late timer failure"));
            Test.Assert(observed.Count == 1 && ReferenceEquals(observed[0], timer.Task), "A late deadline timer failure is observed");
        }

        private static async Task FailedTimerPreservesItsFailure()
        {
            var pending = new TaskCompletionSource<int>();
            var timer = new TaskCompletionSource<bool>();
            var observed = new List<Task>();
            var expected = new InvalidOperationException("Synthetic timer task failure");
            timer.SetException(expected);
            var failed = false;
            try { await AsyncDeadline.WaitAsync(pending.Task, TimeSpan.FromSeconds(10), CancellationToken.None,
                (time, token) => timer.Task, observed.Add); }
            catch (InvalidOperationException ex) { failed = ReferenceEquals(ex, expected); }
            Test.Assert(failed && observed.Count == 1 && ReferenceEquals(observed[0], timer.Task), "A faulted timer is observed and not reported as ordinary expiry");
            pending.SetResult(1);
        }

        private static async Task CanceledTimerPreservesCancellation()
        {
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                var pending = new TaskCompletionSource<int>();
                var observed = new List<Task>();
                var canceled = false;
                try { await AsyncDeadline.WaitAsync(pending.Task, TimeSpan.FromSeconds(10), CancellationToken.None,
                    (time, token) => Task.FromCanceled<bool>(cancel.Token), observed.Add); }
                catch (OperationCanceledException ex) { canceled = ex.CancellationToken == cancel.Token; }
                Test.Assert(canceled, "A canceled timer is consumed without being misclassified as ordinary expiry");
                pending.SetException(new InvalidOperationException("Synthetic late browser failure after timer cancellation"));
                Test.Assert(observed.Count == 1 && ReferenceEquals(observed[0], pending.Task), "Timer cancellation still observes a late browser failure");
            }
        }

        private static async Task NonGenericPreCancellationObservesLateFailure()
        {
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                var pending = new TaskCompletionSource<bool>();
                var observed = new TaskCompletionSource<Task>();
                var canceled = false;
                try { await AsyncDeadline.WaitAsync((Task)pending.Task, TimeSpan.FromSeconds(10), cancel.Token,
                    (time, token) => Task.CompletedTask, task => observed.TrySetResult(task)); }
                catch (OperationCanceledException) { canceled = true; }
                pending.SetException(new InvalidOperationException("Synthetic non-generic browser failure"));
                var failure = await observed.Task;
                Test.Assert(canceled && failure.IsFaulted, "The non-generic browser wait observes a late failure after pre-cancellation");
            }
        }
    }
}
