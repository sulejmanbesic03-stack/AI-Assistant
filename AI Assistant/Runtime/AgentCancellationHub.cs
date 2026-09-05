using System;
using System.Threading;

namespace AI_Assistant.Runtime
{
    /// <summary>
    /// Process-local cancellation signal for the single foreground Cowork task.
    /// The desktop UI runs one task at a time, so provider calls and controlled
    /// host processes can share this token without leaking cancellation into the
    /// next task.
    /// </summary>
    internal static class AgentCancellationHub
    {
        private static readonly object Gate = new object();
        private static CancellationTokenSource current = new CancellationTokenSource();

        public static CancellationToken Token
        {
            get
            {
                lock (Gate)
                {
                    return current.Token;
                }
            }
        }

        public static bool IsCancellationRequested
        {
            get
            {
                lock (Gate)
                {
                    return current.IsCancellationRequested;
                }
            }
        }

        public static void BeginTask()
        {
            lock (Gate)
            {
                CancellationTokenSource previous = current;
                current = new CancellationTokenSource();
                previous.Dispose();
            }
        }

        public static void CancelCurrent()
        {
            lock (Gate)
            {
                if (!current.IsCancellationRequested)
                {
                    current.Cancel();
                }
            }
        }
    }
}
