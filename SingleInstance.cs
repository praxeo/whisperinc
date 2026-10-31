using System;
using System.Threading;

namespace WhisperInk
{
    /// <summary>
    /// Keeps WhisperInk to one copy per Windows session. Two copies both answer
    /// Ctrl+Space, so a take is recorded and pasted twice, and the bar isn't in
    /// the taskbar, so a pinned icon gives no sign the app is already running:
    /// a click there used to start a second copy.
    ///
    /// The first copy holds a named mutex for its whole life. A later copy
    /// "knocks" on a named event, which makes the first show its bar, and exits.
    /// Builds older than this one have no guard and can still run beside it.
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        /// <summary>"Local\": per Windows session, so another signed-in user keeps their own copy.</summary>
        public const string AppName = @"Local\WhisperInk";

        private readonly Mutex _running;
        private readonly EventWaitHandle _knock;
        private RegisteredWaitHandle? _listening;
        private int _disposed;

        private SingleInstance(Mutex running, EventWaitHandle knock)
        {
            _running = running;
            _knock = knock;
        }

        /// <summary>
        /// The guard if this is the first copy, or null if another copy is
        /// running, which has then been told to show itself. <paramref name="wait"/>
        /// covers a copy on its way out: a restart can start the new copy before
        /// the old one has gone, and refusing to start then would leave no app.
        /// </summary>
        public static SingleInstance? Claim(string name, TimeSpan wait)
        {
            var running = new Mutex(false, name + "-running");
            var knock = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-knock");
            bool claimed;
            try
            {
                claimed = running.WaitOne(0);
                if (!claimed)
                {
                    knock.Set();
                    claimed = running.WaitOne(wait);
                }
            }
            catch (AbandonedMutexException)
            {
                // Its last owner was killed while holding it (a deploy's
                // Stop-Process, a crash). The wait still succeeded: it's ours.
                claimed = true;
            }

            if (!claimed)
            {
                running.Dispose();
                knock.Dispose();
                return null;
            }
            knock.Reset();   // our own knock, if the copy we knocked on was leaving
            return new SingleInstance(running, knock);
        }

        /// <summary>
        /// Calls <paramref name="handler"/> on a thread-pool thread each time a
        /// later copy knocks, including one that knocked before this was called.
        /// </summary>
        public void WhenAnotherCopyStarts(Action handler)
        {
            _listening = ThreadPool.RegisterWaitForSingleObject(
                _knock, (_, _) => handler(), null, Timeout.Infinite, executeOnlyOnce: false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _listening?.Unregister(null);
            // A mutex can only be released by the thread that took it. From any
            // other thread this throws, and process exit releases it anyway.
            try { _running.ReleaseMutex(); } catch (ApplicationException) { }
            _running.Dispose();
            _knock.Dispose();
        }
    }
}
