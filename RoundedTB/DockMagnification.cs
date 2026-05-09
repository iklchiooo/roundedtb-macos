using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RoundedTB
{
    /// <summary>
    /// macOS Dock-style magnification hover effect for the Windows taskbar.
    ///
    /// Approach: Instead of resizing individual icon child-windows (which Windows 11
    /// doesn't expose cleanly), we resize the AppList window itself and shift its
    /// position so that the "pill" expands upward from the taskbar edge — exactly
    /// like macOS Dock magnification.  The icons inside scale because MSTaskSwWClass /
    /// MSTaskListWClass fills its parent automatically.
    ///
    /// The effect works in three stages:
    ///   1. Cursor enters the AppList RECT → begin expanding
    ///   2. Cursor stays inside → maintain expanded state, peak scale near cursor
    ///   3. Cursor leaves → smoothly shrink back to original size
    ///
    /// All Win32 calls reuse RoundedTB's existing LocalPInvoke helpers.
    ///
    /// Threading model
    /// ───────────────
    /// _worker (BackgroundWorker) runs Tick() at ~60 fps on a pool thread.
    /// Background.DoWork runs on a second pool thread and writes mw.taskbarDetails.
    /// All fixes are documented inline with "FIX N" markers.
    /// </summary>
    public class DockMagnification
    {
        // ── tuneable parameters ────────────────────────────────────────────
        public double MaxScale        { get; set; } = 1.7;   // 1.7 = 70 % taller at peak
        public double SmoothingFactor { get; set; } = 0.16;  // lerp per tick (0–1)
        public bool   Enabled         { get; set; } = false;

        // ── internal state ─────────────────────────────────────────────────
        private readonly MainWindow _mw;
        private BackgroundWorker    _worker;
        private bool                _running;

        // Per-taskbar animation state
        private class MagState
        {
            public double CurrentScale = 1.0;
            public double TargetScale  = 1.0;
            // Original size of the AppList window (captured once, restored on exit)
            public int BaseWidth;
            public int BaseHeight;
            public int BaseLeft;
            public int BaseTop;
            public bool BaseRecorded;
        }

        private readonly Dictionary<IntPtr, MagState> _states
            = new Dictionary<IntPtr, MagState>();

        // ── constructor ───────────────────────────────────────────────────
        public DockMagnification(MainWindow mainWindow)
        {
            _mw = mainWindow;
        }

        // ── public API ────────────────────────────────────────────────────
        public void Start()
        {
            if (_running) return;

            // FIX B: If a previous worker is still winding down (CancellationPending
            // set but not yet checked), wait for it to finish before creating a new
            // one. RunWorkerAsync on a busy BackgroundWorker throws
            // InvalidOperationException. Matches the spin-wait pattern used in
            // MainWindow.xaml.cs for the main taskbarThread.
            if (_worker != null && _worker.IsBusy)
            {
                _worker.CancelAsync();
                while (_worker.IsBusy)
                    System.Threading.Thread.Sleep(16);
            }

            _running = true;
            _worker  = new BackgroundWorker { WorkerSupportsCancellation = true };
            _worker.DoWork += WorkerLoop;
            _worker.RunWorkerAsync();
            Debug.WriteLine("[DockMag] Started");
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;

            // FIX A: Cancel and WAIT for the worker to exit before calling
            // RestoreAll(). Without the wait, Tick() can still be mid-MoveWindow
            // while RestoreAll() issues its own MoveWindow on the same hwnd,
            // leaving the AppList at an unexpected size or position.
            if (_worker != null && _worker.IsBusy)
            {
                _worker.CancelAsync();
                while (_worker.IsBusy)
                    System.Threading.Thread.Sleep(16);
            }

            RestoreAll();
            Debug.WriteLine("[DockMag] Stopped");
        }

        // ── background loop ───────────────────────────────────────────────
        private void WorkerLoop(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker worker = sender as BackgroundWorker;

            while (!worker.CancellationPending)
            {
                try
                {
                    if (Enabled)
                        Tick();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DockMag] Tick error: {ex.Message}");
                }

                System.Threading.Thread.Sleep(16); // ~60 fps
            }
        }

        // ── one animation frame ───────────────────────────────────────────
        private void Tick()
        {
            // FIX 1: Snapshot the list reference once at the top of each tick.
            // Background.DoWork writes _mw.taskbarDetails from a separate BGW thread.
            // A single reference read is atomic on both 32-bit and 64-bit CLR, so
            // the rest of the tick operates on a stable snapshot.
            List<Types.Taskbar> taskbars = _mw.taskbarDetails;
            if (taskbars == null || taskbars.Count == 0) return;

            LocalPInvoke.GetCursorPos(out LocalPInvoke.POINT cursor);

            foreach (Types.Taskbar tb in taskbars)
            {
                // FIX 3: AppListHwnd == Zero means Background hasn't populated the
                // list yet (happens on the first Apply tick). Invalidate so the
                // baseline is re-recorded once valid handles arrive (~100 ms later).
                if (tb.AppListHwnd == IntPtr.Zero)
                {
                    InvalidateBaselines();
                    return;
                }

                // ── record baseline once ──────────────────────────────────
                if (!_states.TryGetValue(tb.AppListHwnd, out MagState state))
                {
                    state = new MagState();
                    _states[tb.AppListHwnd] = state;
                }

                // Read baseline directly from the live hwnd, never from
                // taskbarDetails.AppListRect — Background.DoWork overwrites that
                // field with GetWindowRect results which reflect the scaled size
                // while DockMag is active (FIX 2 prevention).
                LocalPInvoke.GetWindowRect(tb.AppListHwnd, out LocalPInvoke.RECT appRect);

                if (!state.BaseRecorded && appRect.Right - appRect.Left > 0)
                {
                    state.BaseWidth    = appRect.Right  - appRect.Left;
                    state.BaseHeight   = appRect.Bottom - appRect.Top;
                    state.BaseLeft     = appRect.Left;
                    state.BaseTop      = appRect.Top;
                    state.BaseRecorded = true;
                }

                if (!state.BaseRecorded) continue;

                // ── determine target scale from cursor position ────────────
                // CS0206: TaskbarRect is a property; must copy to local for ref-pass.
                LocalPInvoke.RECT taskbarRect = tb.TaskbarRect;
                bool cursorOverTaskbar = LocalPInvoke.PtInRect(ref taskbarRect, cursor);

                if (cursorOverTaskbar)
                {
                    // Distance from cursor to centre of AppList window
                    double appCentreX = state.BaseLeft + state.BaseWidth  / 2.0;
                    double appCentreY = state.BaseTop  + state.BaseHeight / 2.0;
                    double dx   = cursor.x - appCentreX;
                    double dy   = cursor.y - appCentreY;
                    double dist = Math.Sqrt(dx * dx + dy * dy);

                    // Generous influence radius — mimics macOS Dock falloff
                    double radius = Math.Max(state.BaseHeight, state.BaseWidth) * 1.5;
                    double t      = Math.Max(0.0, 1.0 - dist / radius);
                    double smooth = SmoothStep(t);

                    state.TargetScale = 1.0 + (MaxScale - 1.0) * smooth;
                }
                else
                {
                    state.TargetScale = 1.0;
                }

                // ── lerp toward target ────────────────────────────────────
                double prev = state.CurrentScale;
                state.CurrentScale += (state.TargetScale - state.CurrentScale) * SmoothingFactor;

                // Snap to exactly 1.0 when very close to avoid perpetual micro-moves
                if (Math.Abs(state.CurrentScale - 1.0) < 0.002 && state.TargetScale == 1.0)
                    state.CurrentScale = 1.0;

                if (Math.Abs(state.CurrentScale - prev) < 0.001) continue; // nothing changed

                // ── apply ─────────────────────────────────────────────────
                ApplyScale(tb, state);
            }
        }

        // ── resize & reposition the AppList window ────────────────────────
        private void ApplyScale(Types.Taskbar tb, MagState state)
        {
            double s = state.CurrentScale;

            int newW = (int)Math.Round(state.BaseWidth  * s);
            int newH = (int)Math.Round(state.BaseHeight * s);

            // Centre horizontally around the base position
            int newX = (int)Math.Round(state.BaseLeft + (state.BaseWidth  - newW) / 2.0);

            // Anchor growth direction to the taskbar edge
            // SystemParameters.PrimaryScreenHeight is backed by GetSystemMetrics —
            // safe to call from a background thread (no WPF dispatcher required).
            int screenH  = (int)System.Windows.SystemParameters.PrimaryScreenHeight;
            int tbTop    = tb.TaskbarRect.Top;
            int tbBottom = tb.TaskbarRect.Bottom;

            int newY;
            if (tbBottom >= screenH - 2)
            {
                // Taskbar at bottom — grow upward (bottom edge stays fixed)
                newY = (int)Math.Round((double)(state.BaseTop + (state.BaseHeight - newH)));
            }
            else if (tbTop <= 2)
            {
                // Taskbar at top — grow downward (top edge stays fixed)
                newY = state.BaseTop;
            }
            else
            {
                // Left / right taskbar — keep vertically centred
                newY = (int)Math.Round(state.BaseTop + (state.BaseHeight - newH) / 2.0);
            }

            LocalPInvoke.MoveWindow(tb.AppListHwnd, newX, newY, newW, newH, true);
        }

        // ── restore all windows to original size ──────────────────────────
        // Only called from Stop(), which already waits for the worker to exit
        // (FIX A), so there is no concurrent Tick() racing here.
        private void RestoreAll()
        {
            // FIX 1 (same pattern as Tick): snapshot reference before iterating
            List<Types.Taskbar> taskbars = _mw.taskbarDetails;
            if (taskbars == null) return;

            foreach (Types.Taskbar tb in taskbars)
            {
                if (tb.AppListHwnd == IntPtr.Zero) continue;
                if (!_states.TryGetValue(tb.AppListHwnd, out MagState state)) continue;
                if (!state.BaseRecorded) continue;

                LocalPInvoke.MoveWindow(
                    tb.AppListHwnd,
                    state.BaseLeft, state.BaseTop,
                    state.BaseWidth, state.BaseHeight,
                    true);

                state.CurrentScale = 1.0;
                state.TargetScale  = 1.0;
            }

            // FIX 2: Clear baselines after restore. Forces the next hover cycle to
            // re-measure from the live hwnd, preventing drift where Background.DoWork
            // had written the scaled AppListRect back into taskbarDetails before
            // RestoreAll() ran.
            InvalidateBaselines();
        }

        // ── called when taskbar list is regenerated ───────────────────────
        public void InvalidateBaselines()
        {
            _states.Clear();
        }

        // ── math helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Smoothstep: 3t²−2t³  (t ∈ [0,1] → [0,1])
        /// Gives the silky-smooth macOS acceleration curve.
        /// </summary>
        private static double SmoothStep(double t)
            => t * t * (3 - 2 * t);
    }
}
