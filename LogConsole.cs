using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Monofont;
using Lux.UI.Widgets;
using TerrariaModder.Core.Input;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.UI;
using Color4 = TerrariaModder.Core.UI.Color4;
using WidgetInput = TerrariaModder.Core.UI.Widgets.WidgetInput;

namespace Lumberjack
{
    /// <summary>
    /// In-game log console overlay with resizable columns and text wrapping.
    /// Toggle with tilde key (~).
    ///
    /// Layout: Column header bar with draggable dividers, then scrollable rows.
    /// Columns: [Timestamp] | [Mod] | [Message]
    /// Text wraps within each cell; rows grow to fit wrapped content.
    /// Drag column dividers to resize (like Excel).
    /// </summary>
    internal static class LogConsole
    {
        // ── Panel ──────────────────────────────────────────────────────
        private static DraggablePanel _panel;
        private static ILogger _log;
        private static bool _isOpen;

        // ── Buffer ─────────────────────────────────────────────────────
        private static readonly List<LogEntry> _entries = new List<LogEntry>();
        private static readonly object _bufferLock = new object();
        private const int MaxEntries = 1000;

        // ── Filters ────────────────────────────────────────────────────
        private static string _filterModId;
        private static readonly bool[] _levelFilters = { true, true, true, true };
        private static bool _showTimestamps;

        // ── Vertical scroll (pixel-based for variable row heights) ────
        private static int _scrollPixel;
        private static bool _autoScroll = true;
        private const int CharWidthClip = 10; // conservative char width for clipping (prevents overflow)

        // ── Runtime char width calibration ───────────────────────────
        // Measured once on first draw via UIRenderer.MeasureText for accuracy.
        // Falls back to 8 if MeasureText isn't ready yet.
        private static int _charWidthWrap = 0; // 0 = not yet measured
        private const int CharWidthWrapFallback = 8;
        private const int ScrollbarThickness = 8;

        // ── Scrollbar drag state ─────────────────────────────────────
        private static bool _draggingVScroll;

        // ── Resizable columns ────────────────────────────────────────
        private static int _colTimestampW = 88;
        private static int _colModW = 120;
        private const int ColMinWidth = 40;
        private const int ColPad = 4;  // right-side padding inside each column (gap between columns)
        private const int ColHeaderHeight = 20;
        private const int DividerHitWidth = 8;

        // ── Column divider drag state ────────────────────────────────
        private static bool _draggingColDivider;
        private static int _dragColIndex;
        private static int _dragStartMouseX;
        private static int _dragStartColW;

        // ── Row height cache ─────────────────────────────────────────
        private static int[] _rowHeightCache = new int[0];

        // ── Mod tracking ───────────────────────────────────────────────
        private static readonly List<string> _knownModIds = new List<string>();
        private static readonly HashSet<string> _knownModIdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Dropdown ───────────────────────────────────────────────────
        private static bool _dropdownOpen;
        private static int _dropdownBtnX, _dropdownBtnY, _dropdownBtnW;

        // ── Selection ──────────────────────────────────────────────────
        private static int _selectedLineStart = -1;
        private static int _selectedLineEnd = -1;

        // ── Toast notification ────────────────────────────────────────
        private static float _toastAlpha;
        private static DateTime _toastStartTime;
        private const float ToastDuration = 2.0f;
        private const float ToastFadeStart = 1.5f;

        // ── Cached filtered results ────────────────────────────────────
        private static readonly List<LogEntry> _filtered = new List<LogEntry>();
        private static bool _filterDirty = true;

        // ── Toolbar width cache (measured ONCE on first draw) ──────────
        private static bool _toolbarMeasured;
        private static int _debugBtnW, _infoBtnW, _warnBtnW, _errorBtnW;
        private static int _copyBtnW, _clearBtnW;
        private static string _cachedDropLabel;
        private static int _cachedDropLabelW;

        // ── Layout constants ───────────────────────────────────────────
        private const int LineHeight = 18;
        private const int ToolbarHeight = 30;
        private const int ToolbarPad = 6;
        private const int BtnPad = 24;
        private const int BtnHeight = 22;

        // ── Keyboard state for Ctrl+C detection ────────────────────────
        private static bool _prevCKey;
        private static MethodInfo _getKeyboardState;
        private static MethodInfo _isKeyDown;
        private static object _keyCValue;
        private static bool _keyReflectionFailed;

        /// <summary>Bridge: draws MonoFont text using TerrariaModder's Color4.</summary>
        private static void DrawMono(string text, int x, int y, Color4 c)
            => MonoFont.DrawText(text, x, y, c.R, c.G, c.B, c.A);

        // ────────────────────────────────────────────────────────────────
        //  LIFECYCLE
        // ────────────────────────────────────────────────────────────────

        public static void Initialize(ILogger log)
        {
            _log = log;

            _panel = new DraggablePanel("core.logconsole", "Lumberjack - The Log Console", 800, 300);
            _panel.ShowCloseButton = true;
            _panel.CloseOnEscape = true;
            _panel.ClipContent = true;
            _panel.ShowIcon = false;
            _panel.Resizable = true;
            _panel.UseMonoFont = true;
            _panel.MinWidth = 400;
            _panel.MinHeight = 200;
            _panel.OnClose = () => { _isOpen = false; };
            _panel.RegisterDrawCallback(Draw);

            KeybindManager.Register("lumberjack", "logconsole", "Log Console",
                "Toggle the in-game log console", "OemTilde", Toggle);

            // Log interception is set up via Harmony in Mod.cs (patches LogManager.AddRecentLog)
            InitKeyReflection();
            // MonoFont is initialized lazily in Draw() — too early here (Main not ready)

            _log.Info("[LogConsole] Initialized");
        }

        public static void Unload()
        {
            _panel?.UnregisterDrawCallback();
            _panel?.Close();

            lock (_bufferLock)
            {
                _entries.Clear();
                _knownModIds.Clear();
                _knownModIdSet.Clear();
                _filtered.Clear();
            }

            _isOpen = false;
            _log?.Info("[LogConsole] Unloaded");
        }

        // ────────────────────────────────────────────────────────────────
        //  KEYBOARD REFLECTION (Ctrl+C)
        // ────────────────────────────────────────────────────────────────

        private static void InitKeyReflection()
        {
            try
            {
                // Find Terraria assembly via string lookup (no compile-time reference)
                Assembly asm = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetType("Terraria.Main") != null) { asm = a; break; }
                }
                if (asm == null) { _keyReflectionFailed = true; return; }
                var keysType = asm.GetType("Microsoft.Xna.Framework.Input.Keys");
                var kbType = asm.GetType("Microsoft.Xna.Framework.Input.Keyboard");
                if (keysType == null || kbType == null) { _keyReflectionFailed = true; return; }

                _getKeyboardState = kbType.GetMethod("GetState",
                    BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (_getKeyboardState == null) { _keyReflectionFailed = true; return; }

                _keyCValue = Enum.Parse(keysType, "C");
            }
            catch { _keyReflectionFailed = true; }
        }

        private static bool IsKeyCDown()
        {
            if (_keyReflectionFailed || _getKeyboardState == null) return false;
            try
            {
                var state = _getKeyboardState.Invoke(null, null);
                if (_isKeyDown == null)
                    _isKeyDown = state.GetType().GetMethod("IsKeyDown");
                if (_isKeyDown == null) { _keyReflectionFailed = true; return false; }
                return (bool)_isKeyDown.Invoke(state, new[] { _keyCValue });
            }
            catch { _keyReflectionFailed = true; return false; }
        }

        // ────────────────────────────────────────────────────────────────
        //  TOOLBAR CACHE (measure once, zero cost after)
        // ────────────────────────────────────────────────────────────────

        private static void EnsureToolbarMeasured()
        {
            if (_toolbarMeasured) return;

            // Use MonoFont.MeasureText (pure math: text.Length * 8) for all widths.
            // This works even before the atlas is built since it's just a calculation.
            _debugBtnW = MonoFont.MeasureText("DEBUG") + BtnPad * 2;
            _infoBtnW  = MonoFont.MeasureText("INFO")  + BtnPad * 2;
            _warnBtnW  = MonoFont.MeasureText("WARN")  + BtnPad * 2;
            _errorBtnW = MonoFont.MeasureText("ERROR") + BtnPad * 2;
            _copyBtnW  = MonoFont.MeasureText("Copy All") + BtnPad * 2;
            _clearBtnW = MonoFont.MeasureText("Clear")    + BtnPad * 2;

            // MonoFont is always 8px/char, set calibration width to match
            _charWidthWrap = MonoFont.GlyphWidth;

            // Compute min panel width from toolbar contents so right-side buttons
            // never overlap with the Time checkbox. Layout:
            // [dropdown] [DEBUG] [INFO] [WARN] [ERROR] [Time]  ...gap...  [Copy All] [Clear]
            int defaultDropW = MonoFont.MeasureText("v ALL MODS") + BtnPad * 2;
            int cbW = 14 + 6 + MonoFont.MeasureText("Time") + 4; // box + gap + label + pad
            int leftSide = ToolbarPad + defaultDropW + ToolbarPad
                         + _debugBtnW + 3 + _infoBtnW + 3 + _warnBtnW + 3 + _errorBtnW + 3
                         + ToolbarPad + cbW;
            int rightSide = _copyBtnW + 3 + _clearBtnW + ToolbarPad;
            int minToolbar = leftSide + ToolbarPad * 2 + rightSide; // gap = 2×ToolbarPad
            _panel.MinWidth = Math.Max(_panel.MinWidth, minToolbar);

            _toolbarMeasured = true;
        }

        private static int GetDropLabelWidth(string label)
        {
            if (label == _cachedDropLabel) return _cachedDropLabelW;
            _cachedDropLabel = label;
            _cachedDropLabelW = MonoFont.MeasureText(label) + BtnPad * 2;
            return _cachedDropLabelW;
        }

        // ────────────────────────────────────────────────────────────────
        //  TOGGLE
        // ────────────────────────────────────────────────────────────────

        public static void Toggle()
        {
            if (_isOpen)
            {
                _panel.Close();
                _isOpen = false;
            }
            else
            {
                int sw = UIRenderer.ScreenWidth > 0 ? UIRenderer.ScreenWidth : 1920;
                int sh = UIRenderer.ScreenHeight > 0 ? UIRenderer.ScreenHeight : 1080;
                int pw = (int)(sw * 0.22f);
                int ph = (int)(sh * 0.30f);
                _panel.Width = pw;
                _panel.Height = ph;
                _panel.Open((sw - pw) / 2, sh - ph - 20);
                _isOpen = true;
                _autoScroll = true;
                _scrollPixel = 0;
                _filterDirty = true;
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  LOG EVENT HANDLER
        // ────────────────────────────────────────────────────────────────

        internal static void OnLogEntry(string modId, LogLevel level, string message)
        {
            lock (_bufferLock)
            {
                _entries.Add(new LogEntry
                {
                    ModId = modId ?? "unknown",
                    Level = level,
                    Message = message,
                    Timestamp = DateTime.Now
                });

                while (_entries.Count > MaxEntries)
                    _entries.RemoveAt(0);

                string id = modId ?? "unknown";
                if (_knownModIdSet.Add(id))
                    _knownModIds.Add(id);

                _filterDirty = true;
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  FILTERING
        // ────────────────────────────────────────────────────────────────

        private static List<LogEntry> GetFilteredEntries()
        {
            if (!_filterDirty) return _filtered;

            lock (_bufferLock)
            {
                _filtered.Clear();
                for (int i = 0; i < _entries.Count; i++)
                {
                    var e = _entries[i];

                    if (_filterModId != null &&
                        !string.Equals(e.ModId, _filterModId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    int lv = (int)e.Level;
                    if (lv >= 0 && lv < _levelFilters.Length && !_levelFilters[lv])
                        continue;

                    _filtered.Add(e);
                }
                _filterDirty = false;
            }
            return _filtered;
        }

        // ────────────────────────────────────────────────────────────────
        //  DRAW
        // ────────────────────────────────────────────────────────────────

        private static void Draw()
        {
            if (!_panel.BeginDraw()) return;

            if (!MonoFont.IsReady) MonoFont.Initialize(); // retry until GPU ready
            EnsureToolbarMeasured();

            int cx = _panel.ContentX;
            int cy = _panel.ContentY;
            int cw = _panel.ContentWidth;
            int ch = _panel.ContentHeight;

            DrawToolbar(cx, cy, cw);

            int logY = cy + ToolbarHeight + 2;
            int logH = ch - ToolbarHeight - 2;
            if (logH > 0)
                DrawLogArea(cx, logY, cw, logH);

            if (_dropdownOpen)
                DrawDropdown();

            HandleCopyShortcut();
            DrawToast();

            _panel.EndDraw();
        }

        // ────────────────────────────────────────────────────────────────
        //  TOOLBAR
        // ────────────────────────────────────────────────────────────────

        private static void DrawToolbar(int x, int y, int width)
        {
            UIRenderer.DrawRect(x, y, width, ToolbarHeight, UIColors.SectionBg);

            int bx = x + ToolbarPad;
            int by = y + (ToolbarHeight - BtnHeight) / 2;

            // Mod filter dropdown
            string modText = _filterModId ?? "ALL MODS";
            string dropLabel = "v " + modText;
            int dropW = GetDropLabelWidth(dropLabel);
            DrawButton(bx, by, dropW, dropLabel,
                _dropdownOpen ? UIColors.AccentText : UIColors.Text,
                _dropdownOpen ? UIColors.Accent : UIColors.Button,
                () => { _dropdownOpen = !_dropdownOpen; });
            _dropdownBtnX = bx;
            _dropdownBtnY = by;
            _dropdownBtnW = dropW;
            bx += dropW + ToolbarPad;

            // Level toggles
            int[] lw = { _debugBtnW, _infoBtnW, _warnBtnW, _errorBtnW };
            string[] ln = { "DEBUG", "INFO", "WARN", "ERROR" };
            Color4[] lc = { UIColors.TextDim, UIColors.Text, UIColors.Warning, UIColors.Error };

            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                bool on = _levelFilters[i];
                DrawButton(bx, by, lw[i], ln[i],
                    on ? lc[i] : UIColors.TextHint,
                    on ? UIColors.Button : UIColors.PanelBg,
                    () => { _levelFilters[idx] = !_levelFilters[idx]; _filterDirty = true;
                            _selectedLineStart = -1; _selectedLineEnd = -1; });
                bx += lw[i] + 3;
            }

            bx += ToolbarPad;

            // Timestamps checkbox (proper checkbox widget)
            int cbW = 14 + 6 + MonoFont.MeasureText("Time") + 4; // box + gap + label + pad
            if (Checkbox.DrawWithLabel(bx, by, cbW, BtnHeight, "Time", _showTimestamps, useMonoFont: true))
                _showTimestamps = !_showTimestamps;

            // Right-aligned: Copy All, Clear
            int clearX = x + width - ToolbarPad - _clearBtnW;
            int copyX = clearX - 3 - _copyBtnW;
            DrawButton(copyX, by, _copyBtnW, "Copy All", UIColors.Text, UIColors.Button, CopyAll);
            DrawButton(clearX, by, _clearBtnW, "Clear", UIColors.Text, UIColors.Button, ClearBuffer);
        }

        private static void DrawButton(int x, int y, int w, string label,
            Color4 textColor, Color4 bgColor, Action onClick)
        {
            bool hover = WidgetInput.IsMouseOver(x, y, w, BtnHeight);
            UIRenderer.DrawRect(x, y, w, BtnHeight, hover ? UIColors.ButtonHover : bgColor);
            UIRenderer.DrawRectOutline(x, y, w, BtnHeight, UIColors.Border, 1);

            if (MonoFont.IsReady)
                DrawMono(label, x + 6, y + 3, textColor);
            else
                UIRenderer.DrawText(label, x + 6, y + 3, textColor);

            if (hover && WidgetInput.MouseLeftClick)
            {
                onClick();
                WidgetInput.ConsumeClick();
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  LOG AREA (resizable columns + text wrapping + variable rows)
        // ────────────────────────────────────────────────────────────────

        private static void DrawLogArea(int x, int y, int width, int height)
        {
            UIRenderer.DrawRect(x, y, width, height, UIColors.PanelBg);

            var entries = GetFilteredEntries();
            int totalRows = entries.Count;

            // Column header is drawn AFTER rows (below) so it covers any text
            // that bleeds upward from partially-visible top rows.
            HandleColumnDividerDrag(x, y, width);

            int logY = y + ColHeaderHeight;
            int logH = height - ColHeaderHeight;
            if (logH <= 0) return;

            // ── Compute column geometry ──────────────────────────────────
            int tsW = _showTimestamps ? _colTimestampW : 0;
            int modW = _colModW;

            // First pass: compute row heights assuming no scrollbar
            int msgW = Math.Max(ColMinWidth, width - tsW - modW - 4);
            int totalContentH;
            ComputeRowHeights(entries, msgW, modW, out totalContentH);

            bool needsVScroll = totalContentH > logH;

            // Second pass: if scrollbar needed, reduce msg width and recompute
            if (needsVScroll)
            {
                msgW = Math.Max(ColMinWidth, width - tsW - modW - 4 - ScrollbarThickness);
                ComputeRowHeights(entries, msgW, modW, out totalContentH);
            }

            int contentW = needsVScroll ? width - ScrollbarThickness : width;
            int maxScrollPixel = Math.Max(0, totalContentH - logH);

            // ── Scrollbar drag ───────────────────────────────────────────
            if (needsVScroll)
                HandleVScrollbarDrag(x + width - ScrollbarThickness, logY,
                    ScrollbarThickness, logH, maxScrollPixel);

            if (!needsVScroll)
                _draggingVScroll = false;

            // ── Scroll wheel input ───────────────────────────────────────
            if (!_draggingVScroll && !_draggingColDivider &&
                WidgetInput.IsMouseOver(x, logY, width, logH))
            {
                int scroll = WidgetInput.ScrollWheel;
                if (scroll != 0)
                {
                    _scrollPixel -= scroll / 30 * LineHeight;
                    _scrollPixel = Math.Max(0, Math.Min(_scrollPixel, maxScrollPixel));
                    _autoScroll = (_scrollPixel >= maxScrollPixel);
                    WidgetInput.ConsumeScroll();
                }
            }

            if (_autoScroll) _scrollPixel = maxScrollPixel;
            _scrollPixel = Math.Max(0, Math.Min(_scrollPixel, maxScrollPixel));

            // ── Find first visible row ───────────────────────────────────
            int cumY = 0;
            int firstRow = totalRows; // default: no rows visible
            for (int i = 0; i < totalRows; i++)
            {
                if (cumY + _rowHeightCache[i] > _scrollPixel)
                {
                    firstRow = i;
                    break;
                }
                cumY += _rowHeightCache[i];
            }
            int offsetInFirstRow = _scrollPixel - cumY;

            // ── Selection ────────────────────────────────────────────────
            int selMin = _selectedLineStart >= 0 ? Math.Min(_selectedLineStart, _selectedLineEnd) : -1;
            int selMax = _selectedLineStart >= 0 ? Math.Max(_selectedLineStart, _selectedLineEnd) : -1;

            // ── Draw visible rows ────────────────────────────────────────
            int drawY = logY - offsetInFirstRow;
            for (int i = firstRow; i < totalRows; i++)
            {
                if (drawY >= logY + logH) break;
                int rowH = _rowHeightCache[i];

                // Only draw if at least partially visible
                if (drawY + rowH > logY)
                {
                    var entry = entries[i];

                    // Selection highlight
                    if (selMin >= 0 && i >= selMin && i <= selMax)
                    {
                        int visY = Math.Max(drawY, logY);
                        int visH = Math.Min(drawY + rowH, logY + logH) - visY;
                        if (visH > 0)
                            UIRenderer.DrawRect(x, visY, contentW, visH, UIColors.ItemActiveBg);
                    }

                    // Draw cells
                    int cellX = x + 2;
                    int cellTopY = Math.Max(drawY + 1, logY);

                    // Timestamp cell
                    if (_showTimestamps)
                    {
                        string ts = entry.Timestamp.ToString("[HH:mm:ss]");
                        if (MonoFont.IsReady)
                            DrawMono(ts, cellX, cellTopY, UIColors.TextHint);
                        else
                            DrawClippedText(ts, cellX, cellTopY, cellX, cellX + tsW - ColPad, UIColors.TextHint);
                        cellX += tsW;
                    }

                    // Mod cell (wrapped) — ColPad keeps text from touching next column
                    string modTag = "[" + entry.ModId + "]";
                    DrawWrappedCell(modTag, cellX, drawY + 1, modW - ColPad,
                        logY, logY + logH, UIColors.Accent);
                    cellX += modW;

                    // Message cell (wrapped) — ColPad keeps text from touching scrollbar
                    DrawWrappedCell(entry.Message, cellX, drawY + 1, msgW - ColPad,
                        logY, logY + logH, GetLevelColor(entry.Level));
                }

                drawY += rowH;
            }

            // ── Column header (drawn AFTER rows so it covers bleed) ─────
            DrawColumnHeaders(x, y, width);

            // ── Row clicks ───────────────────────────────────────────────
            if (!_dropdownOpen && !_draggingVScroll && !_draggingColDivider)
            {
                if (WidgetInput.MouseLeftClick && WidgetInput.IsMouseOver(x, logY, contentW, logH))
                {
                    int clickY = WidgetInput.MouseY;
                    int cy2 = logY - offsetInFirstRow;
                    int clickedRow = -1;
                    for (int i = firstRow; i < totalRows; i++)
                    {
                        int rh = _rowHeightCache[i];
                        if (clickY >= cy2 && clickY < cy2 + rh)
                        {
                            clickedRow = i;
                            break;
                        }
                        cy2 += rh;
                        if (cy2 >= logY + logH) break;
                    }

                    if (clickedRow >= 0)
                    {
                        if (WidgetInput.IsShiftHeld && _selectedLineStart >= 0)
                            _selectedLineEnd = clickedRow;
                        else { _selectedLineStart = clickedRow; _selectedLineEnd = clickedRow; }

                        // Auto-copy selection to clipboard
                        CopySelected();
                    }
                    else
                    {
                        _selectedLineStart = -1; _selectedLineEnd = -1;
                    }
                    WidgetInput.ConsumeClick();
                }
            }

            // ── Draw vertical scrollbar ──────────────────────────────────
            if (needsVScroll)
                DrawVScrollbar(x + width - ScrollbarThickness, logY,
                    ScrollbarThickness, logH,
                    _scrollPixel, maxScrollPixel, logH, totalContentH);
        }

        // ────────────────────────────────────────────────────────────────
        //  COLUMN HEADERS
        // ────────────────────────────────────────────────────────────────

        private static void DrawColumnHeaders(int x, int y, int width)
        {
            UIRenderer.DrawRect(x, y, width, ColHeaderHeight, UIColors.SectionBg);
            UIRenderer.DrawRect(x, y + ColHeaderHeight - 1, width, 1, UIColors.Border);

            int mx = WidgetInput.MouseX;
            int colX = x + 2;

            // Timestamp column header
            if (_showTimestamps)
            {
                DrawClippedText("Time", colX + 4, y + 3, colX, colX + _colTimestampW, UIColors.TextDim);
                colX += _colTimestampW;

                // Divider between Timestamp and Mod
                bool hoverDiv0 = !_draggingColDivider &&
                    Math.Abs(mx - colX) <= DividerHitWidth / 2 &&
                    WidgetInput.IsMouseOver(colX - DividerHitWidth / 2, y, DividerHitWidth, ColHeaderHeight);
                bool activeDiv0 = _draggingColDivider && _dragColIndex == 0;
                Color4 div0Color = (hoverDiv0 || activeDiv0) ? UIColors.Accent : UIColors.Border;
                UIRenderer.DrawRect(colX - 1, y, 2, ColHeaderHeight, div0Color);
            }

            // Mod column header
            DrawClippedText("Mod", colX + 4, y + 3, colX, colX + _colModW, UIColors.TextDim);
            colX += _colModW;

            // Divider between Mod and Message
            int modDivIdx = _showTimestamps ? 1 : 0;
            bool hoverDiv1 = !_draggingColDivider &&
                Math.Abs(mx - colX) <= DividerHitWidth / 2 &&
                WidgetInput.IsMouseOver(colX - DividerHitWidth / 2, y, DividerHitWidth, ColHeaderHeight);
            bool activeDiv1 = _draggingColDivider && _dragColIndex == modDivIdx;
            Color4 div1Color = (hoverDiv1 || activeDiv1) ? UIColors.Accent : UIColors.Border;
            UIRenderer.DrawRect(colX - 1, y, 2, ColHeaderHeight, div1Color);

            // Message column header
            DrawClippedText("Message", colX + 4, y + 3, colX, x + width, UIColors.TextDim);
        }

        // ────────────────────────────────────────────────────────────────
        //  COLUMN DIVIDER DRAG
        // ────────────────────────────────────────────────────────────────

        private static void HandleColumnDividerDrag(int areaX, int y, int width)
        {
            int mx = WidgetInput.MouseX;
            bool mouseDown = WidgetInput.MouseLeft;
            bool mouseClick = WidgetInput.MouseLeftClick;

            if (!mouseDown)
            {
                _draggingColDivider = false;
                return;
            }

            // Continue existing drag
            if (_draggingColDivider)
            {
                int delta = mx - _dragStartMouseX;
                int newW = Math.Max(ColMinWidth, _dragStartColW + delta);

                if (_showTimestamps && _dragColIndex == 0)
                    _colTimestampW = newW;
                else
                    _colModW = newW;
                return;
            }

            // Start new drag on click in header divider zone
            if (mouseClick)
            {
                int colX = areaX + 2;

                // Divider 0: between Timestamp and Mod (only when timestamps shown)
                if (_showTimestamps)
                {
                    colX += _colTimestampW;
                    if (Math.Abs(mx - colX) <= DividerHitWidth / 2 &&
                        WidgetInput.IsMouseOver(colX - DividerHitWidth / 2, y, DividerHitWidth, ColHeaderHeight))
                    {
                        _draggingColDivider = true;
                        _dragColIndex = 0;
                        _dragStartMouseX = mx;
                        _dragStartColW = _colTimestampW;
                        WidgetInput.ConsumeClick();
                        return;
                    }
                }
                else
                {
                    colX = areaX + 2;
                }

                // Divider 1 (or 0 if no timestamps): between Mod and Message
                int modDivX = areaX + 2 + (_showTimestamps ? _colTimestampW : 0) + _colModW;
                if (Math.Abs(mx - modDivX) <= DividerHitWidth / 2 &&
                    WidgetInput.IsMouseOver(modDivX - DividerHitWidth / 2, y, DividerHitWidth, ColHeaderHeight))
                {
                    _draggingColDivider = true;
                    _dragColIndex = _showTimestamps ? 1 : 0;
                    _dragStartMouseX = mx;
                    _dragStartColW = _colModW;
                    WidgetInput.ConsumeClick();
                }
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  ROW HEIGHT COMPUTATION
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// How many wrapped lines does this text need at the given column width?
        /// With MonoFont: exact (8px per char). Fallback: conservative estimate.
        /// </summary>
        private static int WrapLineCount(string text, int colWidth)
        {
            if (string.IsNullOrEmpty(text) || colWidth <= 0) return 1;

            int charsPerLine;
            if (MonoFont.IsReady)
            {
                charsPerLine = MonoFont.CharsPerWidth(colWidth);
            }
            else
            {
                int cw = _charWidthWrap > 0 ? _charWidthWrap : CharWidthWrapFallback;
                charsPerLine = Math.Max(1, colWidth / (cw + 1));
            }

            if (text.Length <= charsPerLine) return 1;
            return (text.Length + charsPerLine - 1) / charsPerLine;
        }

        /// <summary>
        /// Compute row heights for all filtered entries. Reuses _rowHeightCache.
        /// Row height = max wrapped lines across all columns × LineHeight.
        /// </summary>
        private static void ComputeRowHeights(List<LogEntry> entries, int msgColWidth, int modColWidth,
            out int totalHeight)
        {
            int count = entries.Count;
            if (_rowHeightCache.Length < count)
                _rowHeightCache = new int[Math.Max(count, 256)];

            totalHeight = 0;
            for (int i = 0; i < count; i++)
            {
                var e = entries[i];
                int msgLines = WrapLineCount(e.Message, msgColWidth - ColPad);
                string modTag = "[" + e.ModId + "]";
                int modLines = WrapLineCount(modTag, modColWidth - ColPad);
                int maxLines = Math.Max(msgLines, modLines);
                int h = maxLines * LineHeight;
                _rowHeightCache[i] = h;
                totalHeight += h;
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  TEXT DRAWING HELPERS
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Draw text wrapped within a cell. Text is character-wrapped to fit the
        /// column width, with each wrapped line drawn on a new row.
        /// Clipped vertically to [clipTop, clipBottom).
        /// </summary>
        private static void DrawWrappedCell(string text, int cellX, int cellTopY,
            int cellWidth, int clipTop, int clipBottom, Color4 color)
        {
            if (string.IsNullOrEmpty(text)) return;

            if (MonoFont.IsReady)
            {
                // MonoFont path: exact char widths, no MeasureText needed
                int charsPerLine = MonoFont.CharsPerWidth(cellWidth);
                int pos = 0;
                int lineIdx = 0;
                while (pos < text.Length)
                {
                    int drawY = cellTopY + lineIdx * LineHeight;
                    if (drawY >= clipBottom) break;

                    int len = Math.Min(charsPerLine, text.Length - pos);
                    if (drawY + LineHeight > clipTop)
                        DrawMono(text.Substring(pos, len), cellX, drawY, color);

                    pos += len;
                    lineIdx++;
                }
                return;
            }

            // Fallback: UIRenderer path with variable-width font safety
            int cw = _charWidthWrap > 0 ? _charWidthWrap : CharWidthWrapFallback;
            int fallbackCharsPerLine = Math.Max(1, cellWidth / cw);

            int fPos = 0;
            int fLine = 0;
            while (fPos < text.Length)
            {
                int drawY = cellTopY + fLine * LineHeight;
                if (drawY >= clipBottom) break;

                int remaining = text.Length - fPos;
                int len = Math.Min(fallbackCharsPerLine, remaining);
                string segment = text.Substring(fPos, len);

                if (len > 1)
                {
                    int measured = UIRenderer.MeasureText(segment);
                    if (measured > 0 && measured + 4 > cellWidth)
                    {
                        while (len > 1)
                        {
                            len--;
                            segment = text.Substring(fPos, len);
                            measured = UIRenderer.MeasureText(segment);
                            if (measured <= 0 || measured + 4 <= cellWidth)
                                break;
                        }
                    }
                }

                if (drawY + LineHeight > clipTop)
                    UIRenderer.DrawText(segment, cellX, drawY, color);

                fPos += len;
                fLine++;
            }
        }

        /// <summary>
        /// Draw text clipped to [leftEdge, rightEdge). Skips if fully off-screen.
        /// Truncates text that would extend past rightEdge (software clipping).
        /// </summary>
        private static void DrawClippedText(string text, int drawX, int drawY,
            int leftEdge, int rightEdge, Color4 color)
        {
            if (string.IsNullOrEmpty(text)) return;

            int charW = MonoFont.IsReady ? MonoFont.GlyphWidth : CharWidthClip;
            int estWidth = text.Length * charW;

            // Fully off-screen right
            if (drawX >= rightEdge) return;

            // Fully off-screen left
            if (drawX + estWidth <= leftEdge) return;

            // Clamp draw position to left edge
            int actualX = Math.Max(drawX, leftEdge);

            // Truncate text that extends past right edge
            int availableWidth = rightEdge - actualX;
            int maxChars = availableWidth / charW;

            if (maxChars <= 0) return;
            if (maxChars < text.Length)
                text = text.Substring(0, maxChars);

            if (MonoFont.IsReady)
                DrawMono(text, actualX, drawY, color);
            else
                UIRenderer.DrawText(text, actualX, drawY, color);
        }

        private static Color4 GetLevelColor(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return UIColors.TextDim;
                case LogLevel.Info:  return UIColors.Text;
                case LogLevel.Warn:  return UIColors.Warning;
                case LogLevel.Error: return UIColors.Error;
                default:             return UIColors.Text;
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  SCROLLBAR DRAG (vertical only)
        // ────────────────────────────────────────────────────────────────

        private static void HandleVScrollbarDrag(int sbX, int sbY, int sbW, int sbH,
            int maxScrollPixel)
        {
            bool mouseDown = WidgetInput.MouseLeft;
            bool mouseClick = WidgetInput.MouseLeftClick;
            int my = WidgetInput.MouseY;

            if (!mouseDown)
            {
                _draggingVScroll = false;
                return;
            }

            // Continue existing drag
            if (_draggingVScroll)
            {
                if (sbH > 0 && maxScrollPixel > 0)
                {
                    float ratio = (float)(my - sbY) / sbH;
                    ratio = Math.Max(0f, Math.Min(1f, ratio));
                    _scrollPixel = (int)(ratio * maxScrollPixel);
                    _autoScroll = (_scrollPixel >= maxScrollPixel);
                }
                return;
            }

            // Start new drag on click
            if (mouseClick && WidgetInput.IsMouseOver(sbX, sbY, sbW, sbH))
            {
                _draggingVScroll = true;
                WidgetInput.ConsumeClick();
                if (sbH > 0 && maxScrollPixel > 0)
                {
                    float ratio = (float)(my - sbY) / sbH;
                    ratio = Math.Max(0f, Math.Min(1f, ratio));
                    _scrollPixel = (int)(ratio * maxScrollPixel);
                    _autoScroll = (_scrollPixel >= maxScrollPixel);
                }
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  SCROLLBAR DRAWING
        // ────────────────────────────────────────────────────────────────

        private static void DrawVScrollbar(int x, int y, int w, int h,
            int scrollPixel, int maxScrollPixel, int viewportH, int totalContentH)
        {
            UIRenderer.DrawRect(x, y, w, h, UIColors.ScrollTrack);
            if (totalContentH <= 0 || totalContentH <= viewportH) return;

            int thumbH = Math.Max(20, (int)(h * ((float)viewportH / totalContentH)));
            int thumbY = maxScrollPixel > 0
                ? y + (int)((h - thumbH) * ((float)scrollPixel / maxScrollPixel))
                : y;
            UIRenderer.DrawRect(x + 1, thumbY, w - 2, thumbH, UIColors.ScrollThumb);
        }

        // ────────────────────────────────────────────────────────────────
        //  DROPDOWN
        // ────────────────────────────────────────────────────────────────

        private static void DrawDropdown()
        {
            List<string> modIds;
            lock (_bufferLock) { modIds = new List<string>(_knownModIds); }

            int dx = _dropdownBtnX;
            int dy = _dropdownBtnY + BtnHeight + 2;
            int itemH = 22;

            // Compute dropdown width from the widest mod name so text never overflows
            int maxItemW = MonoFont.MeasureText("ALL MODS") + 12; // 6px padding each side
            for (int i = 0; i < modIds.Count; i++)
                maxItemW = Math.Max(maxItemW, MonoFont.MeasureText(modIds[i]) + 12);
            int dw = Math.Max(_dropdownBtnW, Math.Max(150, maxItemW));

            int dh = (1 + modIds.Count) * itemH + 4;

            UIRenderer.DrawRect(dx, dy, dw, dh, UIColors.HeaderBg);
            UIRenderer.DrawRectOutline(dx, dy, dw, dh, UIColors.Border, 1);

            int iy = dy + 2;

            // ALL MODS
            {
                bool hover = WidgetInput.IsMouseOver(dx, iy, dw, itemH);
                if (hover) UIRenderer.DrawRect(dx + 1, iy, dw - 2, itemH, UIColors.ButtonHover);
                Color4 allColor = _filterModId == null ? UIColors.Accent : UIColors.Text;
                if (MonoFont.IsReady)
                    DrawMono("ALL MODS", dx + 6, iy + 3, allColor);
                else
                    UIRenderer.DrawText("ALL MODS", dx + 6, iy + 3, allColor);
                if (hover && WidgetInput.MouseLeftClick)
                {
                    _filterModId = null; _dropdownOpen = false; _filterDirty = true;
                    _cachedDropLabel = null;
                    _selectedLineStart = -1; _selectedLineEnd = -1;
                    WidgetInput.ConsumeClick();
                }
                iy += itemH;
            }

            for (int i = 0; i < modIds.Count; i++)
            {
                string mid = modIds[i];
                bool hover = WidgetInput.IsMouseOver(dx, iy, dw, itemH);
                if (hover) UIRenderer.DrawRect(dx + 1, iy, dw - 2, itemH, UIColors.ButtonHover);
                bool sel = string.Equals(_filterModId, mid, StringComparison.OrdinalIgnoreCase);
                Color4 itemColor = sel ? UIColors.Accent : UIColors.Text;
                if (MonoFont.IsReady)
                    DrawMono(mid, dx + 6, iy + 3, itemColor);
                else
                    UIRenderer.DrawText(mid, dx + 6, iy + 3, itemColor);
                if (hover && WidgetInput.MouseLeftClick)
                {
                    _filterModId = mid; _dropdownOpen = false; _filterDirty = true;
                    _cachedDropLabel = null;
                    _selectedLineStart = -1; _selectedLineEnd = -1;
                    WidgetInput.ConsumeClick();
                }
                iy += itemH;
            }

            if (WidgetInput.MouseLeftClick && !WidgetInput.IsMouseOver(dx, dy, dw, dh))
                _dropdownOpen = false;
        }

        // ────────────────────────────────────────────────────────────────
        //  TOAST NOTIFICATION
        // ────────────────────────────────────────────────────────────────

        private static void DrawToast()
        {
            if (_toastAlpha <= 0f) return;

            float elapsed = (float)(DateTime.Now - _toastStartTime).TotalSeconds;
            if (elapsed >= ToastDuration) { _toastAlpha = 0f; return; }

            byte alpha;
            if (elapsed > ToastFadeStart)
                alpha = (byte)(255 * (1f - (elapsed - ToastFadeStart) / (ToastDuration - ToastFadeStart)));
            else
                alpha = 200;

            string msg = "Copied to Clipboard";
            int tw = MonoFont.MeasureText(msg);
            int padH = 8;
            int padW = 16;
            int th = MonoFont.GlyphHeight + padH;
            int px = _panel.ContentX + (_panel.ContentWidth - tw - padW) / 2;
            int py = _panel.ContentY + _panel.ContentHeight - th - 6;

            UIRenderer.DrawRect(px, py, tw + padW, th, 0, 0, 0, (byte)(alpha * 0.7f));
            UIRenderer.DrawRectOutline(px, py, tw + padW, th, 60, 60, 60, alpha, 1);

            if (MonoFont.IsReady)
                MonoFont.DrawText(msg, px + padW / 2, py + padH / 2, 120, 220, 120, alpha);
            else
                UIRenderer.DrawText(msg, px + padW / 2, py + padH / 2, 120, 220, 120, alpha);
        }

        // ────────────────────────────────────────────────────────────────
        //  COPY / CLEAR
        // ────────────────────────────────────────────────────────────────

        private static void HandleCopyShortcut()
        {
            if (!_isOpen || _selectedLineStart < 0) return;
            try
            {
                if (WidgetInput.IsCtrlHeld)
                {
                    bool c = IsKeyCDown();
                    if (c && !_prevCKey) CopySelected();
                    _prevCKey = c;
                }
                else _prevCKey = false;
            }
            catch { _prevCKey = false; }
        }

        private static void CopySelected()
        {
            var entries = GetFilteredEntries();
            int lo = Math.Max(0, Math.Min(_selectedLineStart, _selectedLineEnd));
            int hi = Math.Min(entries.Count - 1, Math.Max(_selectedLineStart, _selectedLineEnd));
            if (lo > hi) return;

            var sb = new StringBuilder();
            for (int i = lo; i <= hi; i++)
            {
                var e = entries[i];
                if (_showTimestamps) sb.Append(e.Timestamp.ToString("[HH:mm:ss] "));
                sb.Append("[").Append(e.ModId).Append("] ").AppendLine(e.Message);
            }
            CopyToClipboard(sb.ToString());
            ShowToast();
        }

        private static void CopyAll()
        {
            var entries = GetFilteredEntries();
            if (entries.Count == 0) return;

            var sb = new StringBuilder();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (_showTimestamps) sb.Append(e.Timestamp.ToString("[HH:mm:ss] "));
                sb.Append("[").Append(e.ModId).Append("] ").AppendLine(e.Message);
            }
            CopyToClipboard(sb.ToString());
            ShowToast();
        }

        private static void ShowToast()
        {
            _toastAlpha = 1f;
            _toastStartTime = DateTime.Now;
        }

        private static void ClearBuffer()
        {
            lock (_bufferLock) { _entries.Clear(); _filtered.Clear(); _filterDirty = true; }
            _scrollPixel = 0; _autoScroll = true;
            _selectedLineStart = -1; _selectedLineEnd = -1;
        }

        // ── Win32 Clipboard P/Invoke (no System.Windows.Forms dependency) ──

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;

        private static void CopyToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                if (!OpenClipboard(IntPtr.Zero)) return;
                try
                {
                    EmptyClipboard();

                    // Allocate global memory for the Unicode string (including null terminator)
                    int byteCount = (text.Length + 1) * 2;
                    IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)byteCount);
                    if (hGlobal == IntPtr.Zero) return;

                    IntPtr locked = GlobalLock(hGlobal);
                    if (locked == IntPtr.Zero) return;
                    try
                    {
                        Marshal.Copy(text.ToCharArray(), 0, locked, text.Length);
                        // Write null terminator
                        Marshal.WriteInt16(locked, text.Length * 2, 0);
                    }
                    finally
                    {
                        GlobalUnlock(hGlobal);
                    }

                    SetClipboardData(CF_UNICODETEXT, hGlobal);
                    // Note: after SetClipboardData succeeds, the system owns hGlobal — do NOT free it
                }
                finally
                {
                    CloseClipboard();
                }
            }
            catch (Exception ex)
            {
                _log?.Debug($"[LogConsole] Clipboard failed: {ex.Message}");
            }
        }
    }
}
