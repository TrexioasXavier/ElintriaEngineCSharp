using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Drawing;
using TextCopy;

// =============================================================================
//  InputField  — single-line editable text box
// =============================================================================
//  Focus:     click to focus; Enter / Escape / click-elsewhere to unfocus.
//  Cursor:    click positions cursor; Shift+Arrow extends selection.
//  Editing:   Backspace, Delete, Home, End, Left, Right.
//  Clipboard: Ctrl+A (select all), Ctrl+C, Ctrl+X, Ctrl+V.
//  Events:    OnTextChanged fires on every change; OnSubmit fires on Enter.
//  Visual:    blinking cursor, selection highlight, placeholder text.
// =============================================================================

public class InputField : Panel
{
    // ── Content ───────────────────────────────────────────────────────────
    private string _text = "";
    public string Text
    {
        get => _text;
        set
        {
            string v = value ?? "";
            if (_text == v) return;
            _text = v;
            _cursor = Math.Clamp(_cursor, 0, _text.Length);
            _selAnchor = -1;
            OnTextChanged?.Invoke(_text);
        }
    }

    public int MaxLength { get; set; } = 256;
    public string PlaceholderText { get; set; } = "";
    public BitmapFont Font { get; set; }

    // ── Colours ───────────────────────────────────────────────────────────
    public Color TextColor { get; set; } = Color.FromArgb(255, 215, 215, 220);
    public Color PlaceholderColor { get; set; } = Color.FromArgb(110, 160, 160, 170);
    public Color SelectionColor { get; set; } = Color.FromArgb(120, 51, 153, 255);
    public Color CursorColor { get; set; } = Color.FromArgb(255, 200, 200, 210);
    public Color BorderNormal { get; set; } = Color.FromArgb(100, 90, 90, 110);
    public Color BorderFocused { get; set; } = Color.FromArgb(255, 75, 130, 230);

    // ── Events ────────────────────────────────────────────────────────────
    public Action<string> OnTextChanged;
    public Action<string> OnSubmit;

    // ── Private state ─────────────────────────────────────────────────────
    private int _cursor = 0;
    private int _selAnchor = -1;   // -1 means no selection
    private float _blinkTimer = 0f;
    private bool _showCaret = true;
    private float _scrollX = 0f;   // horizontal text offset in pixels

    private const float PAD_X = 5f;

    // ── Convenience ───────────────────────────────────────────────────────
    private int SelStart => _selAnchor < 0 ? _cursor : Math.Min(_cursor, _selAnchor);
    private int SelEnd => _selAnchor < 0 ? _cursor : Math.Max(_cursor, _selAnchor);
    private bool HasSel => _selAnchor >= 0 && _selAnchor != _cursor;
    public bool IsFocused => base.IsFocused;

    // ── Update ────────────────────────────────────────────────────────────
    public override void Update(float dt)
    {
        base.Update(dt);
        if (!IsFocused) return;

        _blinkTimer += dt;
        if (_blinkTimer >= 0.53f)
        {
            _showCaret = !_showCaret;
            _blinkTimer = 0f;
        }
    }

    // ── Draw ──────────────────────────────────────────────────────────────
    public override void Draw()
    {
        if (!Visible) return;

        Vector2 abs = GetAbsolutePosition();
        float lh = Font?.LineH ?? 14f;

        // Background
        Color bg = BackgroundColor.A > 0
            ? BackgroundColor
            : Color.FromArgb(200, 20, 20, 28);
        UIRenderer.DrawRect(abs.X, abs.Y, Size.X, Size.Y, bg);

        float clipW = Size.X - PAD_X * 2f;
        float tx = abs.X + PAD_X;
        float ty = abs.Y + (Size.Y - lh) * 0.5f;

        if (Font != null)
        {
            // Selection highlight
            if (HasSel)
            {
                float x0 = tx + Font.MeasureText(_text, SelStart) - _scrollX;
                float x1 = tx + Font.MeasureText(_text, SelEnd) - _scrollX;
                x0 = Math.Max(x0, tx);
                x1 = Math.Min(x1, tx + clipW);
                if (x1 > x0)
                    UIRenderer.DrawRect(x0, ty, x1 - x0, lh, SelectionColor);
            }

            // Text or placeholder
            if (_text.Length > 0)
                Font.DrawText(_text, tx - _scrollX, ty, TextColor);
            else if (!IsFocused && !string.IsNullOrEmpty(PlaceholderText))
                Font.DrawText(PlaceholderText, tx, ty, PlaceholderColor);

            // Caret
            if (IsFocused && _showCaret)
            {
                float cx = tx + Font.MeasureText(_text, _cursor) - _scrollX;
                if (cx >= tx && cx <= tx + clipW + 2f)
                    UIRenderer.DrawRect(cx, ty, 1.5f, lh, CursorColor);
            }
        }

        // Border — blue when focused, dim when not
        UIRenderer.DrawRectOutline(abs.X, abs.Y, Size.X, Size.Y,
            IsFocused ? BorderFocused : BorderNormal);

        DrawChildren(abs);
    }

    // ── Input — mouse ────────────────────────────────────────────────────
    protected override bool OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.Button != MouseButton.Left) return false;
        Panel.SetFocus(this);
        _cursor = HitTestCursor(GetMousePosition().X);
        _selAnchor = -1;
        ResetCaret();
        return true;
    }

    // ── Input — keyboard ─────────────────────────────────────────────────
    protected override bool OnKeyDown(KeyboardKeyEventArgs e)
    {
        if (!IsFocused) return false;

        bool ctrl = e.Modifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.Modifiers.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            // ── Navigation ────────────────────────────────────────
            case Keys.Left:
                if (shift) StartSel(); else DropSel();
                if (_cursor > 0) _cursor--;
                break;

            case Keys.Right:
                if (shift) StartSel(); else DropSel();
                if (_cursor < _text.Length) _cursor++;
                break;

            case Keys.Home:
                if (shift) StartSel(); else DropSel();
                _cursor = 0;
                break;

            case Keys.End:
                if (shift) StartSel(); else DropSel();
                _cursor = _text.Length;
                break;

            // ── Delete ────────────────────────────────────────────
            case Keys.Backspace:
                if (HasSel) DeleteSel();
                else if (_cursor > 0) { _text = _text.Remove(_cursor - 1, 1); _cursor--; Notify(); }
                break;

            case Keys.Delete:
                if (HasSel) DeleteSel();
                else if (_cursor < _text.Length) { _text = _text.Remove(_cursor, 1); Notify(); }
                break;

            // ── Confirm / Cancel ──────────────────────────────────
            case Keys.Enter:
            case Keys.KeyPadEnter:
                Panel.ClearFocus();
                OnSubmit?.Invoke(_text);
                break;

            case Keys.Escape:
                Panel.ClearFocus();
                break;

            // ── Clipboard ─────────────────────────────────────────
            case Keys.A when ctrl:
                _selAnchor = 0;
                _cursor = _text.Length;
                break;

            case Keys.C when ctrl:
                if (HasSel) OSClipSet(_text.Substring(SelStart, SelEnd - SelStart));
                break;

            case Keys.X when ctrl:
                if (HasSel) { OSClipSet(_text.Substring(SelStart, SelEnd - SelStart)); DeleteSel(); }
                break;

            case Keys.V when ctrl:
                string paste = OSClipGet();
                if (!string.IsNullOrEmpty(paste))
                {
                    if (HasSel) DeleteSel();
                    foreach (char c in paste) PutChar(c);
                }
                break;

            default:
                return false;
        }

        ScrollToCursor();
        ResetCaret();
        return true;
    }

    protected override bool OnTextInput(TextInputEventArgs e)
    {
        if (!IsFocused) return false;
        if (HasSel) DeleteSel();
        foreach (char c in e.AsString) PutChar(c);
        ScrollToCursor();
        ResetCaret();
        return true;
    }

    // ── Focus callbacks ──────────────────────────────────────────────────
    protected override void OnGotFocus() { ResetCaret(); }
    protected override void OnLostFocus() { _selAnchor = -1; _showCaret = false; }

    // ── Helpers ───────────────────────────────────────────────────────────
    private void PutChar(char c)
    {
        if (char.IsControl(c) || _text.Length >= MaxLength) return;
        _text = _text.Insert(_cursor, c.ToString());
        _cursor++;
        Notify();
    }

    private void DeleteSel()
    {
        if (!HasSel) return;
        _text = _text.Remove(SelStart, SelEnd - SelStart);
        _cursor = SelStart;
        DropSel();
        Notify();
    }

    private void Notify() => OnTextChanged?.Invoke(_text);
    private void ResetCaret() { _showCaret = true; _blinkTimer = 0f; }
    private void StartSel() { if (_selAnchor < 0) _selAnchor = _cursor; }
    private void DropSel() { _selAnchor = -1; }

    private int HitTestCursor(float screenX)
    {
        if (Font == null) return _text.Length;
        float local = screenX - (GetAbsolutePosition().X + PAD_X) + _scrollX;
        float adv = 0f;
        for (int i = 0; i < _text.Length; i++)
        {
            float gw = Font.MeasureGlyphAdvance(_text[i]);
            if (local < adv + gw * 0.5f) return i;
            adv += gw;
        }
        return _text.Length;
    }

    private void ScrollToCursor()
    {
        if (Font == null) return;
        float clipW = Size.X - PAD_X * 2f;
        float offset = Font.MeasureText(_text, _cursor);
        if (offset - _scrollX < 0f) _scrollX = offset;
        else if (offset - _scrollX > clipW) _scrollX = offset - clipW;
    }

    // ── OS Clipboard ─────────────────────────────────────────────────────
    private static void OSClipSet(string text)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var t = new System.Threading.Thread(
                    () => ClipboardService.SetText(text));
                t.SetApartmentState(System.Threading.ApartmentState.STA);
                t.Start(); t.Join();
            }
        }
        catch { }
    }

    private static string OSClipGet()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                string result = "";
                var t = new System.Threading.Thread(
                    () => result = ClipboardService.GetText());
                t.SetApartmentState(System.Threading.ApartmentState.STA);
                t.Start(); t.Join();
                return result;
            }
        }
        catch { }
        return "";
    }
}