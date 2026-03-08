using OpenTK.Mathematics;
using System.IO;

namespace Elintria.Editor
{
    // =========================================================================
    // DragDropPayload
    // =========================================================================
    public enum DragDropSource { ProjectPanel, External }

    public enum DragDropAssetType
    {
        Unknown,
        Script,       // .cs
        Texture,      // .png .jpg .bmp .tga .hdr
        Mesh,         // .obj .fbx .gltf .glb
        Material,     // .mat
        Shader,       // .glsl .vert .frag .hlsl
        Scene,        // .scene.json / .scene
        Audio,        // .wav .mp3 .ogg
        Font,         // .ttf .otf
        Prefab,       // .prefab
        Generic,      // anything else
    }

    public class DragDropPayload
    {
        public string FilePath { get; init; }
        public string FileName => Path.GetFileName(FilePath);
        public string FileStem => Path.GetFileNameWithoutExtension(FilePath);
        public DragDropAssetType AssetType { get; init; }
        public DragDropSource Source { get; init; }

        public static DragDropAssetType Classify(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".cs" => DragDropAssetType.Script,
                ".png" or ".jpg" or ".jpeg"
                    or ".bmp" or ".tga"
                    or ".hdr" or ".exr" => DragDropAssetType.Texture,
                ".obj" or ".fbx" or ".gltf"
                    or ".glb" or ".dae"
                    or ".3ds" or ".blend" => DragDropAssetType.Mesh,
                ".mat" => DragDropAssetType.Material,
                ".glsl" or ".vert" or ".frag"
                    or ".hlsl" or ".shader" => DragDropAssetType.Shader,
                ".scene" or ".scene.json" => DragDropAssetType.Scene,
                ".wav" or ".mp3" or ".ogg"
                    or ".flac" or ".aiff" => DragDropAssetType.Audio,
                ".ttf" or ".otf" => DragDropAssetType.Font,
                ".prefab" => DragDropAssetType.Prefab,
                _ => DragDropAssetType.Generic,
            };
        }
    }

    // =========================================================================
    // DragDropService
    // =========================================================================
    /// <summary>
    /// Global drag-and-drop state machine.
    ///
    /// Usage:
    ///   Start drag:  DragDropService.Begin(payload, startScreenPos)
    ///   Each frame:  DragDropService.UpdatePosition(mouseScreenPos)
    ///   On drop:     DragDropService.IsDragging → check, then End()
    ///   Draw ghost:  DragDropService.DrawGhost(font)
    /// </summary>
    public static class DragDropService
    {
        // ------------------------------------------------------------------
        // State
        // ------------------------------------------------------------------
        public static bool IsDragging { get; private set; }
        public static DragDropPayload Payload { get; private set; }
        public static Vector2 GhostPos { get; private set; }

        // Ghost starts after dragging >6px from the start point
        private static Vector2 _startPos;
        private static bool _committed;   // ghost actually shown (past threshold)

        // ------------------------------------------------------------------
        // Begin — call from a panel's HandleMouseDown or Update once drag detected
        // ------------------------------------------------------------------
        public static void Begin(DragDropPayload payload, Vector2 startScreenPos)
        {
            IsDragging = true;
            Payload = payload;
            _startPos = startScreenPos;
            GhostPos = startScreenPos;
            _committed = false;
        }

        // ------------------------------------------------------------------
        // UpdatePosition — call each frame while LMB is held
        // ------------------------------------------------------------------
        public static void UpdatePosition(Vector2 mousePos)
        {
            if (!IsDragging) return;
            GhostPos = mousePos;
            if (!_committed && (mousePos - _startPos).Length > 6f)
                _committed = true;
        }

        // ------------------------------------------------------------------
        // TryDrop — call from a drop target's HandleMouseUp.
        // Returns the payload if this is an active drag, else null.
        // Clears state so only the first accepting target wins.
        // ------------------------------------------------------------------
        public static DragDropPayload TryDrop()
        {
            if (!IsDragging) return null;
            var p = Payload;
            End();
            return p;
        }

        // ------------------------------------------------------------------
        // End — clears state (called by Editor on global MouseUp)
        // ------------------------------------------------------------------
        public static void End()
        {
            IsDragging = false;
            Payload = null;
            _committed = false;
        }

        // ------------------------------------------------------------------
        // ShowGhost — true once drag moved past threshold
        // ------------------------------------------------------------------
        public static bool ShowGhost => IsDragging && _committed;

        // ------------------------------------------------------------------
        // DrawGhost — call from Editor.OnRenderFrame after all panels drawn
        // ------------------------------------------------------------------
        public static void DrawGhost(BitmapFont font)
        {
            if (!ShowGhost || Payload == null) return;

            float x = GhostPos.X + 10f;
            float y = GhostPos.Y + 4f;
            float w = (font?.MeasureText(Payload.FileName) ?? 100f) + 20f;
            float h = 20f;

            // Shaded pill
            UIRenderer.DrawRect(x + 2, y + 2, w, h, System.Drawing.Color.FromArgb(80, 0, 0, 0));
            UIRenderer.DrawRect(x, y, w, h, System.Drawing.Color.FromArgb(220, 44, 44, 60));
            UIRenderer.DrawRectOutline(x, y, w, h,
                System.Drawing.Color.FromArgb(180, 90, 130, 220), 1f);

            // Colour-coded icon dot by type
            var dot = AssetColor(Payload.AssetType);
            UIRenderer.DrawRect(x + 4, y + 6, 8, 8, dot);

            font?.DrawText(Payload.FileName, x + 14, y + 3,
                System.Drawing.Color.FromArgb(230, 220, 220, 230));
        }

        private static System.Drawing.Color AssetColor(DragDropAssetType t) => t switch
        {
            DragDropAssetType.Script => System.Drawing.Color.FromArgb(255, 120, 220, 120),
            DragDropAssetType.Texture => System.Drawing.Color.FromArgb(255, 220, 160, 80),
            DragDropAssetType.Mesh => System.Drawing.Color.FromArgb(255, 100, 160, 240),
            DragDropAssetType.Material => System.Drawing.Color.FromArgb(255, 200, 100, 200),
            DragDropAssetType.Shader => System.Drawing.Color.FromArgb(255, 100, 220, 200),
            DragDropAssetType.Scene => System.Drawing.Color.FromArgb(255, 240, 200, 80),
            _ => System.Drawing.Color.FromArgb(255, 180, 180, 180),
        };
    }
}