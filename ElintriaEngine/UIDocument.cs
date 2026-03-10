using System;
using System.Collections.Generic;
using System.Drawing;

namespace ElintriaEngine.Core
{
    public enum UIElementType { Text, Button, TextField, Scrollbar }
    public enum UITextAlignment { Left, Center, Right }
    public enum UIScrollbarOrientation { Horizontal, Vertical }

    // ═══════════════════════════════════════════════════════════════════════════
    //  UIElement  –  base for every GUI element
    // ═══════════════════════════════════════════════════════════════════════════
    public abstract class UIElement
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Name { get; set; } = "Element";
        public float X { get; set; } = 100f;
        public float Y { get; set; } = 100f;
        public float Width { get; set; } = 120f;
        public float Height { get; set; } = 30f;
        public bool Visible { get; set; } = true;

        public RectangleF Rect => new(X, Y, Width, Height);
        public abstract UIElementType ElementType { get; }
        public abstract UIElement Clone();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Text
    // ─────────────────────────────────────────────────────────────────────────
    public class UITextElement : UIElement
    {
        public string Text { get; set; } = "New Text";
        public float FontSize { get; set; } = 14f;
        public Color Color { get; set; } = Color.White;
        public UITextAlignment Alignment { get; set; } = UITextAlignment.Left;

        public UITextElement() { Width = 140; Height = 26; Name = "Text"; }
        public override UIElementType ElementType => UIElementType.Text;
        public override UIElement Clone() => (UITextElement)MemberwiseClone();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Button
    // ─────────────────────────────────────────────────────────────────────────
    public class UIButtonElement : UIElement
    {
        public string Text { get; set; } = "Button";
        public Color BackgroundColor { get; set; } = Color.FromArgb(255, 60, 100, 200);
        public Color TextColor { get; set; } = Color.White;
        public Color HoverColor { get; set; } = Color.FromArgb(255, 85, 130, 240);
        public Color PressedColor { get; set; } = Color.FromArgb(255, 40, 70, 150);
        public float FontSize { get; set; } = 13f;
        public string OnClickEvent { get; set; } = "";

        public UIButtonElement() { Width = 120; Height = 34; Name = "Button"; }
        public override UIElementType ElementType => UIElementType.Button;
        public override UIElement Clone() => (UIButtonElement)MemberwiseClone();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  TextField
    // ─────────────────────────────────────────────────────────────────────────
    public class UITextFieldElement : UIElement
    {
        public string Placeholder { get; set; } = "Enter text...";
        public string Text { get; set; } = "";
        public Color BackgroundColor { get; set; } = Color.FromArgb(255, 28, 28, 28);
        public Color TextColor { get; set; } = Color.FromArgb(255, 210, 210, 210);
        public Color BorderColor { get; set; } = Color.FromArgb(255, 80, 80, 80);
        public Color FocusBorderColor { get; set; } = Color.FromArgb(255, 80, 140, 230);
        public int MaxLength { get; set; } = 100;
        public float FontSize { get; set; } = 12f;

        public UITextFieldElement() { Width = 180; Height = 28; Name = "TextField"; }
        public override UIElementType ElementType => UIElementType.TextField;
        public override UIElement Clone() => (UITextFieldElement)MemberwiseClone();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Scrollbar
    // ─────────────────────────────────────────────────────────────────────────
    public class UIScrollbarElement : UIElement
    {
        public UIScrollbarOrientation Orientation { get; set; } = UIScrollbarOrientation.Horizontal;
        public float MinValue { get; set; } = 0f;
        public float MaxValue { get; set; } = 1f;
        public float Value { get; set; } = 0.5f;
        public float ThumbSize { get; set; } = 0.2f;   // fraction of track
        public Color TrackColor { get; set; } = Color.FromArgb(255, 35, 35, 35);
        public Color ThumbColor { get; set; } = Color.FromArgb(255, 90, 90, 90);
        public Color ThumbHover { get; set; } = Color.FromArgb(255, 120, 120, 120);

        public UIScrollbarElement() { Width = 200; Height = 16; Name = "Scrollbar"; }
        public override UIElementType ElementType => UIElementType.Scrollbar;
        public override UIElement Clone() => (UIScrollbarElement)MemberwiseClone();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  UIDocument  –  the full set of GUI elements for a scene
    // ═══════════════════════════════════════════════════════════════════════════
    public class UIDocument
    {
        public List<UIElement> Elements { get; } = new();
        public int DesignWidth { get; set; } = 1280;
        public int DesignHeight { get; set; } = 720;

        public UIElement? FindById(string id) => Elements.Find(e => e.Id == id);

        public void Add(UIElement e) => Elements.Add(e);

        public void Remove(UIElement e) => Elements.Remove(e);

        public void BringForward(UIElement e)
        {
            int i = Elements.IndexOf(e);
            if (i < Elements.Count - 1) { Elements.RemoveAt(i); Elements.Insert(i + 1, e); }
        }
        public void SendBackward(UIElement e)
        {
            int i = Elements.IndexOf(e);
            if (i > 0) { Elements.RemoveAt(i); Elements.Insert(i - 1, e); }
        }
    }
}