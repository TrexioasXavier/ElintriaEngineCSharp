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

        // Script binding: which GameObject's script component to call on click
        public string TargetScriptName { get; set; } = "";   // e.g. "PlayerUI"
        public string TargetMethodName { get; set; } = "";   // e.g. "OnStartButton"

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

    // ═══════════════════════════════════════════════════════════════════════════
    //  UIDocumentSerializer  –  JSON save/load for UIDocument
    // ═══════════════════════════════════════════════════════════════════════════
    public static class UIDocumentSerializer
    {
        private static readonly System.Text.Json.JsonSerializerOptions _opts = new()
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        // Custom JSON helpers that handle Color and polymorphism
        public static string ToJson(UIDocument doc)
        {
            var dto = new UIDocumentDto
            {
                DesignWidth = doc.DesignWidth,
                DesignHeight = doc.DesignHeight,
                Elements = doc.Elements.ConvertAll(ElementToDto),
            };
            return System.Text.Json.JsonSerializer.Serialize(dto, _opts);
        }

        public static UIDocument FromJson(string json)
        {
            var dto = System.Text.Json.JsonSerializer.Deserialize<UIDocumentDto>(json, _opts);
            if (dto == null) return new UIDocument();
            var doc = new UIDocument { DesignWidth = dto.DesignWidth, DesignHeight = dto.DesignHeight };
            foreach (var e in dto.Elements) { var el = DtoToElement(e); if (el != null) doc.Add(el); }
            return doc;
        }

        public static void SaveToFile(UIDocument doc, string path)
            => System.IO.File.WriteAllText(path, ToJson(doc));

        public static UIDocument? LoadFromFile(string path)
        {
            if (!System.IO.File.Exists(path)) return null;
            try { return FromJson(System.IO.File.ReadAllText(path)); }
            catch { return null; }
        }

        // ── DTO types ─────────────────────────────────────────────────────────
        private static UIElementDto ElementToDto(UIElement e)
        {
            var dto = new UIElementDto
            {
                Kind = e.ElementType.ToString(),
                Id = e.Id,
                Name = e.Name,
                X = e.X,
                Y = e.Y,
                W = e.Width,
                H = e.Height,
                Visible = e.Visible,
            };
            switch (e)
            {
                case UITextElement te:
                    dto.Text = te.Text;
                    dto.FontSize = te.FontSize;
                    dto.Color = ColorToHex(te.Color);
                    dto.Alignment = te.Alignment.ToString();
                    break;
                case UIButtonElement be:
                    dto.Text = be.Text;
                    dto.FontSize = be.FontSize;
                    dto.BgColor = ColorToHex(be.BackgroundColor);
                    dto.TextColor = ColorToHex(be.TextColor);
                    dto.HoverColor = ColorToHex(be.HoverColor);
                    dto.PressColor = ColorToHex(be.PressedColor);
                    dto.OnClick = be.OnClickEvent;
                    dto.ScriptName = be.TargetScriptName;
                    dto.MethodName = be.TargetMethodName;
                    break;
                case UITextFieldElement fe:
                    dto.Placeholder = fe.Placeholder;
                    dto.Text = fe.Text;
                    dto.FontSize = fe.FontSize;
                    dto.BgColor = ColorToHex(fe.BackgroundColor);
                    dto.TextColor = ColorToHex(fe.TextColor);
                    dto.BorderColor = ColorToHex(fe.BorderColor);
                    dto.FocusColor = ColorToHex(fe.FocusBorderColor);
                    break;
                case UIScrollbarElement se:
                    dto.Orientation = se.Orientation.ToString();
                    dto.MinVal = se.MinValue;
                    dto.MaxVal = se.MaxValue;
                    dto.Value = se.Value;
                    dto.ThumbSize = se.ThumbSize;
                    dto.TrackColor = ColorToHex(se.TrackColor);
                    dto.ThumbColor = ColorToHex(se.ThumbColor);
                    break;
            }
            return dto;
        }

        private static UIElement? DtoToElement(UIElementDto d)
        {
            UIElement? e = d.Kind switch
            {
                "Text" => new UITextElement(),
                "Button" => new UIButtonElement(),
                "TextField" => new UITextFieldElement(),
                "Scrollbar" => new UIScrollbarElement(),
                _ => null
            };
            if (e == null) return null;
            e.Id = d.Id; e.Name = d.Name;
            e.X = d.X; e.Y = d.Y; e.Width = d.W; e.Height = d.H; e.Visible = d.Visible;
            switch (e)
            {
                case UITextElement te:
                    te.Text = d.Text ?? te.Text;
                    te.FontSize = d.FontSize;
                    te.Color = HexToColor(d.Color, te.Color);
                    if (Enum.TryParse<UITextAlignment>(d.Alignment, out var ta)) te.Alignment = ta;
                    break;
                case UIButtonElement be:
                    be.Text = d.Text ?? be.Text;
                    be.FontSize = d.FontSize;
                    be.BackgroundColor = HexToColor(d.BgColor, be.BackgroundColor);
                    be.TextColor = HexToColor(d.TextColor, be.TextColor);
                    be.HoverColor = HexToColor(d.HoverColor, be.HoverColor);
                    be.PressedColor = HexToColor(d.PressColor, be.PressedColor);
                    be.OnClickEvent = d.OnClick ?? "";
                    be.TargetScriptName = d.ScriptName ?? "";
                    be.TargetMethodName = d.MethodName ?? "";
                    break;
                case UITextFieldElement fe:
                    fe.Placeholder = d.Placeholder ?? fe.Placeholder;
                    fe.Text = d.Text ?? fe.Text;
                    fe.FontSize = d.FontSize;
                    fe.BackgroundColor = HexToColor(d.BgColor, fe.BackgroundColor);
                    fe.TextColor = HexToColor(d.TextColor, fe.TextColor);
                    fe.BorderColor = HexToColor(d.BorderColor, fe.BorderColor);
                    fe.FocusBorderColor = HexToColor(d.FocusColor, fe.FocusBorderColor);
                    break;
                case UIScrollbarElement se:
                    if (Enum.TryParse<UIScrollbarOrientation>(d.Orientation, out var so)) se.Orientation = so;
                    se.MinValue = d.MinVal;
                    se.MaxValue = d.MaxVal;
                    se.Value = d.Value;
                    se.ThumbSize = d.ThumbSize;
                    se.TrackColor = HexToColor(d.TrackColor, se.TrackColor);
                    se.ThumbColor = HexToColor(d.ThumbColor, se.ThumbColor);
                    break;
            }
            return e;
        }

        private static string ColorToHex(System.Drawing.Color c)
            => $"{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}";

        private static System.Drawing.Color HexToColor(string? hex, System.Drawing.Color fallback)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length < 6) return fallback;
            try
            {
                int r = Convert.ToInt32(hex[0..2], 16);
                int g = Convert.ToInt32(hex[2..4], 16);
                int b = Convert.ToInt32(hex[4..6], 16);
                int a = hex.Length >= 8 ? Convert.ToInt32(hex[6..8], 16) : 255;
                return System.Drawing.Color.FromArgb(a, r, g, b);
            }
            catch { return fallback; }
        }
    }

    // ── DTO ───────────────────────────────────────────────────────────────────
    internal class UIDocumentDto
    {
        public int DesignWidth { get; set; } = 1280;
        public int DesignHeight { get; set; } = 720;
        public List<UIElementDto> Elements { get; set; } = new();
    }

    internal class UIElementDto
    {
        public string Kind { get; set; } = "";
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public float W { get; set; }
        public float H { get; set; }
        public bool Visible { get; set; } = true;
        // shared
        public string? Text { get; set; }
        public float FontSize { get; set; }
        public string? Color { get; set; }
        public string? Alignment { get; set; }
        // button
        public string? BgColor { get; set; }
        public string? TextColor { get; set; }
        public string? HoverColor { get; set; }
        public string? PressColor { get; set; }
        public string? OnClick { get; set; }
        public string? ScriptName { get; set; }
        public string? MethodName { get; set; }
        // textfield
        public string? Placeholder { get; set; }
        public string? BorderColor { get; set; }
        public string? FocusColor { get; set; }
        // scrollbar
        public string? Orientation { get; set; }
        public float MinVal { get; set; }
        public float MaxVal { get; set; } = 1f;
        public float Value { get; set; }
        public float ThumbSize { get; set; } = 0.2f;
        public string? TrackColor { get; set; }
        public string? ThumbColor { get; set; }
    }
}