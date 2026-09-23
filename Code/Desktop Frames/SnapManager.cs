using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Desktop_Frames
{
    public static class SnapManager
    {
        private const double SnapThreshold = 24;
        private const double MinGap = 8;

        private static bool _isSnapping = false;
        public static NonActivatingWindow ActiveDragWindow = null;

        // ── Snap-line overlay windows ─────────────────────────────────────
        private static Window _lineH; // horizontal guide
        private static Window _lineV; // vertical guide

        private static readonly SolidColorBrush LineBrush =
            new SolidColorBrush(Color.FromArgb(220, 0, 120, 212)); // Win11 blue

        private static Window GetLine(ref Window field, bool horizontal)
        {
            if (field != null) return field;
            var win = new Window
            {
                WindowStyle     = WindowStyle.None,
                AllowsTransparency = true,
                ShowInTaskbar   = false,
                Topmost         = true,
                IsHitTestVisible = false,
                Background      = LineBrush,
                Opacity         = 0
            };
            if (horizontal) { win.Height = 2; win.Width = 1; }
            else             { win.Width  = 2; win.Height = 1; }
            win.Show();
            field = win;
            return win;
        }

        private static void ShowLine(ref Window field, bool horizontal,
                                     double pos, double start, double length)
        {
            var w = GetLine(ref field, horizontal);
            if (horizontal) { w.Left = start; w.Top  = pos;   w.Width  = length; w.Height = 2; }
            else             { w.Top  = start; w.Left = pos;   w.Height = length; w.Width  = 2; }
            FadeTo(w, 1.0);
        }

        private static void HideLine(Window w) { if (w != null) FadeTo(w, 0.0); }

        private static void FadeTo(Window w, double target)
        {
            var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(120));
            w.BeginAnimation(Window.OpacityProperty, anim);
        }

        private static void HideAllLines()
        {
            HideLine(_lineH);
            HideLine(_lineV);
        }

        // ── Drag lifecycle ────────────────────────────────────────────────
        public static void StartDrag(NonActivatingWindow win) => ActiveDragWindow = win;

        public static void EndDrag(NonActivatingWindow win)
        {
            try
            {
                HideAllLines();
                if (ActiveDragWindow != win) return;

                string myId = GetFrameIdFromWindow(win);
                if (myId != null && FrameDataManager.DockingMap.TryGetValue(myId, out var parentIds))
                    FrameDataManager.UpdateDockedRelationships(myId, parentIds);
                else if (myId != null)
                    FrameDataManager.UpdateDockedRelationships(myId, null);
            }
            finally { ActiveDragWindow = null; }
        }

        public static void AddSnapping(NonActivatingWindow win, IDictionary<string, object> frameData)
        {
            string myId = frameData.ContainsKey("Id") ? frameData["Id"].ToString() : null;

            win.LocationChanged += (s, e) =>
            {
                if (_isSnapping || ActiveDragWindow != win) return;
                _isSnapping = true;
                try
                {
                    var all = System.Windows.Application.Current.Windows
                                    .OfType<NonActivatingWindow>().ToList();
                    var (newLeft, newTop, snapXPos, snapYPos, snapXRange, snapYRange) =
                        CalculateSnap(win, all);

                    // Update snap line overlays
                    if (snapYPos.HasValue)
                        ShowLine(ref _lineH, true, snapYPos.Value,
                                 snapXRange.start, snapXRange.length);
                    else
                        HideLine(_lineH);

                    if (snapXPos.HasValue)
                        ShowLine(ref _lineV, false, snapXPos.Value,
                                 snapYRange.start, snapYRange.length);
                    else
                        HideLine(_lineV);

                    // Apply position
                    if (Math.Abs(win.Left - newLeft) > 0.1 || Math.Abs(win.Top - newTop) > 0.1)
                    {
                        win.Left = newLeft;
                        win.Top  = newTop;
                        frameData["X"] = newLeft;
                        frameData["Y"] = newTop;
                        FrameDataManager.SaveFrameData();
                    }

                    // Track docking parents
                    if (myId != null)
                    {
                        var parents = all.Where(f =>
                        {
                            if (f == win) return false;
                            bool vMatch = Math.Abs(f.Top + f.Height + MinGap - newTop) < SnapThreshold;
                            double overlap = Math.Min(f.Left + f.Width, newLeft + win.Width)
                                           - Math.Max(f.Left, newLeft);
                            return vMatch && overlap > Math.Min(f.Width, win.Width) * 0.2;
                        }).ToList();

                        var pIds = parents.Select(p => GetFrameIdFromWindow(p))
                                         .Where(id => id != null).ToList();
                        if (pIds.Count > 0) FrameDataManager.DockingMap[myId] = pIds;
                        else                FrameDataManager.DockingMap.Remove(myId);
                    }
                }
                finally { _isSnapping = false; }
            };

            win.PreviewMouseLeftButtonUp += (s, e) =>
            {
                HideAllLines();
                if (myId != null && FrameDataManager.DockingMap.TryGetValue(myId, out var pIds))
                    FrameDataManager.UpdateDockedRelationships(myId, pIds);
                else if (myId != null)
                    FrameDataManager.UpdateDockedRelationships(myId, null);
            };
        }

        // ── Core snap calculation ─────────────────────────────────────────
        // Returns: (newLeft, newTop, snapX, snapY, xSpan, ySpan)
        private static (double, double, double?, double?,
                         (double start, double length),
                         (double start, double length))
            CalculateSnap(NonActivatingWindow cur, List<NonActivatingWindow> all)
        {
            if (!SettingsManager.IsSnapEnabled)
                return (cur.Left, cur.Top, null, null, (0, 0), (0, 0));

            double cL = cur.Left, cT = cur.Top;
            double cR = cL + cur.Width, cB = cT + cur.Height;

            double dX = double.MaxValue, dY = double.MaxValue;
            double? bestXLine = null, bestYLine = null;
            double xSpanStart = 0, xSpanLen = 2000, ySpanStart = 0, ySpanLen = 2000;

            foreach (var other in all)
            {
                if (other == cur) continue;
                double oL = other.Left, oT = other.Top;
                double oR = oL + other.Width, oB = oT + other.Height;

                // X snaps — record snap-line position and which edges overlap vertically
                void TryX(double cPos, double tPos, double lineX)
                {
                    double d = tPos - cPos;
                    if (Math.Abs(d) <= SnapThreshold && Math.Abs(d) < Math.Abs(dX))
                    {
                        dX = d;
                        bestXLine = lineX;
                        ySpanStart = Math.Min(cT, oT);
                        ySpanLen   = Math.Max(cB, oB) - ySpanStart;
                    }
                }
                TryX(cR, oL - MinGap, oL);
                TryX(cL, oR + MinGap, oR);
                TryX(cL, oL, oL);
                TryX(cR, oR, oR);

                // Y snaps
                void TryY(double cPos, double tPos, double lineY)
                {
                    double d = tPos - cPos;
                    if (Math.Abs(d) <= SnapThreshold && Math.Abs(d) < Math.Abs(dY))
                    {
                        dY = d;
                        bestYLine = lineY;
                        xSpanStart = Math.Min(cL, oL);
                        xSpanLen   = Math.Max(cR, oR) - xSpanStart;
                    }
                }
                TryY(cB, oT - MinGap, oT);
                TryY(cT, oB + MinGap, oB);
                TryY(cT, oT, oT);
                TryY(cB, oB, oB);
            }

            // Screen-edge snaps
            double dpi = GetDpiScale(cur);
            foreach (var screen in Screen.AllScreens)
            {
                double sL = screen.Bounds.Left / dpi, sT = screen.Bounds.Top / dpi;
                double sR = screen.Bounds.Right / dpi, sB = screen.Bounds.Bottom / dpi;
                CheckSnap(cL, sL, ref dX); CheckSnap(cR, sR, ref dX);
                CheckSnap(cT, sT, ref dY); CheckSnap(cB, sB, ref dY);
            }

            double finalX = dX < double.MaxValue ? cL + dX : cL;
            double finalY = dY < double.MaxValue ? cT + dY : cT;
            return (finalX, finalY, bestXLine, bestYLine,
                    (xSpanStart, xSpanLen), (ySpanStart, ySpanLen));
        }

        private static void CheckSnap(double cur, double tgt, ref double best)
        {
            double d = tgt - cur;
            if (Math.Abs(d) <= SnapThreshold && Math.Abs(d) < Math.Abs(best)) best = d;
        }

        // ── Cascade / docking ─────────────────────────────────────────────
        public static void CascadeStack(string parentId, double deltaY)
        {
            var childIds = FrameDataManager.DockingMap
                .Where(kvp => kvp.Value?.Contains(parentId) == true)
                .Select(kvp => kvp.Key).ToList();

            foreach (var cid in childIds)
            {
                var cwin = System.Windows.Application.Current.Windows
                    .OfType<NonActivatingWindow>()
                    .FirstOrDefault(w => GetFrameIdFromWindow(w) == cid);
                if (cwin == null) continue;

                if (FrameDataManager.DockingMap.TryGetValue(cid, out var pIds))
                {
                    var activeParents = System.Windows.Application.Current.Windows
                        .OfType<NonActivatingWindow>()
                        .Where(w => pIds.Contains(GetFrameIdFromWindow(w))).ToList();
                    if (activeParents.Count == 0) continue;

                    double target = activeParents.Max(p => p.Top + p.Height) + 10.0;
                    if (Math.Abs(cwin.Top - target) > 0.5)
                    {
                        double actual = target - cwin.Top;
                        cwin.Top = target;
                        CascadeStack(cid, actual);
                    }
                }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────
        public static string GetFrameIdFromWindow(NonActivatingWindow win) => win?.Tag?.ToString();

        private static double GetDpiScale(Visual v)
        {
            try
            {
                var src = PresentationSource.FromVisual(v);
                if (src?.CompositionTarget != null) return src.CompositionTarget.TransformToDevice.M11;
            }
            catch { }
            return 1.0;
        }

        // Legacy stubs kept so callers compile
        public static void ShowSnapPreview(NonActivatingWindow win, bool isSnapped) { }
        public static void AnimateSnapConfirmation(NonActivatingWindow win) { }
    }
}
