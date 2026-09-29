using System;
using System.Collections.Generic;
using System.Threading;

namespace BluetoothAutoLock
{
    internal static class ClassicBluetoothEvidence
    {
        // 离线配对设备通常报告 0、-127 或 -128；低于物理噪声下限的值不能作为在场证据。
        internal const short RssiPhysicalNoiseFloor = -100;

        internal static bool HasCredibleRssi(short? rssiDbm)
        {
            if (!rssiDbm.HasValue) return false;

            short rssi = rssiDbm.Value;
            return rssi != 0 && rssi > RssiPhysicalNoiseFloor;
        }
    }

    internal enum LockLifecyclePhase
    {
        Monitoring = 0,
        AwaitingSessionUnlock = 1,
        RearmPending = 2,
        AwaitingUserReturn = 3
    }

    internal sealed class LockLifecycleState
    {
        private int _phase = (int)LockLifecyclePhase.Monitoring;

        internal bool IsLockedUntilSessionUnlock
        {
            get { return ReadPhase() != LockLifecyclePhase.Monitoring; }
        }

        internal void MarkLockSucceeded()
        {
            Interlocked.Exchange(ref _phase, (int)LockLifecyclePhase.AwaitingSessionUnlock);
        }

        internal bool RequestRearmAfterSessionUnlock()
        {
            return Interlocked.CompareExchange(
                ref _phase,
                (int)LockLifecyclePhase.RearmPending,
                (int)LockLifecyclePhase.AwaitingSessionUnlock) ==
                (int)LockLifecyclePhase.AwaitingSessionUnlock;
        }

        internal bool ConsumeRearmRequest()
        {
            return Interlocked.CompareExchange(
                ref _phase,
                (int)LockLifecyclePhase.Monitoring,
                (int)LockLifecyclePhase.RearmPending) ==
                (int)LockLifecyclePhase.RearmPending;
        }

        internal void MarkAppLockSucceeded()
        {
            Interlocked.Exchange(ref _phase, (int)LockLifecyclePhase.AwaitingUserReturn);
        }

        internal bool RearmAfterUserReturn()
        {
            return Interlocked.CompareExchange(
                ref _phase,
                (int)LockLifecyclePhase.Monitoring,
                (int)LockLifecyclePhase.AwaitingUserReturn) ==
                (int)LockLifecyclePhase.AwaitingUserReturn;
        }

        private LockLifecyclePhase ReadPhase()
        {
            return (LockLifecyclePhase)Interlocked.CompareExchange(ref _phase, 0, 0);
        }
    }

    internal static class UserReturnPolicy
    {
        internal const int InjectedInputGraceSeconds = 2;

        internal static bool InputOccurredAfter(DateTime actionFinishedUtc, DateTime nowUtc, int idleSeconds)
        {
            if (idleSeconds < 0) return false;
            double sinceAction = (nowUtc - actionFinishedUtc).TotalSeconds;
            return (double)idleSeconds + InjectedInputGraceSeconds < sinceAction;
        }
    }

    internal sealed class TargetWindowCandidate
    {
        public IntPtr Handle;
        public bool Visible;
        public bool Iconic;
        public bool HasOwner;
        public bool ToolWindow;
        public bool Minimizable;
        public string Title;
        public string ClassName;
        public int Width;
        public int Height;
    }

    internal static class TargetWindowPolicy
    {
        internal const int MinimumMainWindowSize = 200;

        internal static TargetWindowCandidate ChooseMainWindow(IEnumerable<TargetWindowCandidate> candidates)
        {
            if (candidates == null) return null;

            TargetWindowCandidate bestVisible = null;
            TargetWindowCandidate bestHidden = null;
            foreach (TargetWindowCandidate candidate in candidates)
            {
                if (!IsAppMainWindowCandidate(candidate)) continue;
                if (candidate.Visible)
                {
                    if (bestVisible == null || Area(candidate) > Area(bestVisible)) bestVisible = candidate;
                }
                else if (bestHidden == null || Area(candidate) > Area(bestHidden))
                {
                    bestHidden = candidate;
                }
            }

            return bestVisible ?? bestHidden;
        }

        internal static bool IsAppMainWindowCandidate(TargetWindowCandidate candidate)
        {
            if (candidate == null || candidate.Handle == IntPtr.Zero) return false;
            if (candidate.HasOwner || candidate.ToolWindow || !candidate.Minimizable) return false;
            if (string.IsNullOrWhiteSpace(candidate.Title)) return false;
            if ((candidate.ClassName ?? "").IndexOf("TrayIcon", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (candidate.Iconic) return true;
            return candidate.Width >= MinimumMainWindowSize && candidate.Height >= MinimumMainWindowSize;
        }

        private static long Area(TargetWindowCandidate candidate)
        {
            if (candidate.Iconic) return 0;
            return (long)Math.Max(0, candidate.Width) * Math.Max(0, candidate.Height);
        }
    }
}
