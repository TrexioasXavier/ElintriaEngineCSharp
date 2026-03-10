using System;
using System.Drawing;
using ElintriaEngine.Core;
using ElintriaEngine.UI.Panels;

namespace ElintriaEngine.Rendering
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  UIDocumentRenderer
    //
    //  Renders a UIDocument on top of the game using IEditorRenderer.
    //  Called from EditorLayout.Render2D (editor preview) and from the built
    //  game's render loop.
    //
    //  Coordinates: design-space (e.g. 1280x720) mapped to the given viewport.
    // ═══════════════════════════════════════════════════════════════════════════
    public static class UIDocumentRenderer
    {
        /// <summary>
        /// Draws all visible UI elements scaled from design space into
        /// <paramref name="viewport"/>. Pass isEditor=true to also draw
        /// selection outlines and handles.
        /// </summary>
        public static void Render(IEditorRenderer r, UIDocument doc,
            RectangleF viewport, UIElement? selected = null)
        {
            if (doc.Elements.Count == 0) return;

            float scaleX = viewport.Width / doc.DesignWidth;
            float scaleY = viewport.Height / doc.DesignHeight;

            r.PushClip(viewport);

            foreach (var elem in doc.Elements)
            {
                if (!elem.Visible) continue;

                // Map design → screen
                var sr = new RectangleF(
                    viewport.X + elem.X * scaleX,
                    viewport.Y + elem.Y * scaleY,
                    elem.Width * scaleX,
                    elem.Height * scaleY);

                switch (elem)
                {
                    case UITextElement te: DrawText(r, te, sr, scaleX, scaleY); break;
                    case UIButtonElement be: DrawButton(r, be, sr, scaleX, scaleY); break;
                    case UITextFieldElement fe: DrawTextField(r, fe, sr, scaleX, scaleY); break;
                    case UIScrollbarElement se: DrawScrollbar(r, se, sr); break;
                }
            }

            r.PopClip();
        }

        // ── Element drawers ───────────────────────────────────────────────────

        private static void DrawText(IEditorRenderer r, UITextElement e,
            RectangleF sr, float sx, float sy)
        {
            float fs = e.FontSize * sy;
            var pos = e.Alignment switch
            {
                UITextAlignment.Center => new PointF(sr.X + sr.Width / 2f - e.Text.Length * fs * 0.3f, sr.Y + sr.Height / 2f - fs / 2f),
                UITextAlignment.Right => new PointF(sr.Right - e.Text.Length * fs * 0.6f, sr.Y + sr.Height / 2f - fs / 2f),
                _ => new PointF(sr.X + 2f, sr.Y + sr.Height / 2f - fs / 2f),
            };
            r.DrawText(e.Text, pos, e.Color, fs);
        }

        private static void DrawButton(IEditorRenderer r, UIButtonElement e,
            RectangleF sr, float sx, float sy)
        {
            r.FillRect(sr, e.BackgroundColor);
            r.DrawRect(sr, DarkenColor(e.BackgroundColor, 0.6f));

            float fs = e.FontSize * sy;
            float tw = e.Text.Length * fs * 0.6f;
            float tx = sr.X + (sr.Width - tw) / 2f;
            float ty = sr.Y + (sr.Height - fs) / 2f;
            r.DrawText(e.Text, new PointF(tx, ty), e.TextColor, fs);
        }

        private static void DrawTextField(IEditorRenderer r, UITextFieldElement e,
            RectangleF sr, float sx, float sy)
        {
            r.FillRect(sr, e.BackgroundColor);
            r.DrawRect(sr, e.BorderColor);

            float fs = e.FontSize * sy;
            float pad = 4f * sx;
            bool empty = string.IsNullOrEmpty(e.Text);
            string disp = empty ? e.Placeholder : e.Text;
            Color col = empty
                ? Color.FromArgb(140, e.TextColor.R, e.TextColor.G, e.TextColor.B)
                : e.TextColor;
            r.DrawText(disp, new PointF(sr.X + pad, sr.Y + (sr.Height - fs) / 2f), col, fs);
        }

        private static void DrawScrollbar(IEditorRenderer r, UIScrollbarElement e, RectangleF sr)
        {
            r.FillRect(sr, e.TrackColor);
            r.DrawRect(sr, DarkenColor(e.TrackColor, 0.5f));

            float range = e.MaxValue - e.MinValue;
            float t = range > 0 ? (e.Value - e.MinValue) / range : 0f;

            RectangleF thumb;
            if (e.Orientation == UIScrollbarOrientation.Horizontal)
            {
                float tw = sr.Width * e.ThumbSize;
                float tx = sr.X + (sr.Width - tw) * t;
                thumb = new RectangleF(tx, sr.Y + 1, tw, sr.Height - 2);
            }
            else
            {
                float th = sr.Height * e.ThumbSize;
                float ty = sr.Y + (sr.Height - th) * t;
                thumb = new RectangleF(sr.X + 1, ty, sr.Width - 2, th);
            }

            r.FillRect(thumb, e.ThumbColor);
            r.DrawRect(thumb, DarkenColor(e.ThumbColor, 0.7f));
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        private static Color DarkenColor(Color c, float factor)
            => Color.FromArgb(c.A,
                (int)(c.R * factor),
                (int)(c.G * factor),
                (int)(c.B * factor));
    }
}