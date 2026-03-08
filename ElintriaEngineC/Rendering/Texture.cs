using OpenTK.Graphics.OpenGL4;
using System.Drawing;
using System.Drawing.Imaging;

namespace Elintria.Engine.Rendering
{
    public enum TextureWrap { Repeat, ClampToEdge, MirroredRepeat }
    public enum TextureFilter { Nearest, Linear, Trilinear }

    /// <summary>
    /// Wraps an OpenGL 2D texture. Supports loading from file, from a
    /// System.Drawing Bitmap, or creating solid-colour/blank textures.
    ///
    /// Usage:
    ///   var tex = Texture.Load("data/textures/brick.png");
    ///   var tex = Texture.CreateSolidColor(Color.White, 1, 1);
    ///   tex.Bind(unit);    // TextureUnit.Texture0, Texture1 …
    ///   tex.Dispose();
    /// </summary>
    public class Texture : System.IDisposable
    {
        // ------------------------------------------------------------------
        // Properties
        // ------------------------------------------------------------------
        public int Handle { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public string Name { get; set; }

        public TextureWrap WrapS { get; private set; }
        public TextureWrap WrapT { get; private set; }
        public TextureFilter Filter { get; private set; }

        // ------------------------------------------------------------------
        // Private constructor — use factories
        // ------------------------------------------------------------------
        private Texture() { }

        // ------------------------------------------------------------------
        // Factories
        // ------------------------------------------------------------------

        /// <summary>Load from PNG/JPG/BMP/etc. via System.Drawing.</summary>
        public static Texture Load(string path,
                                   TextureWrap wrap = TextureWrap.Repeat,
                                   TextureFilter filter = TextureFilter.Trilinear)
        {
            if (!System.IO.File.Exists(path))
                throw new System.IO.FileNotFoundException($"Texture not found: {path}");

            using var bmp = new Bitmap(path);
            return FromBitmap(bmp, System.IO.Path.GetFileName(path), wrap, filter);
        }

        /// <summary>Create from an existing System.Drawing.Bitmap.</summary>
        public static Texture FromBitmap(Bitmap bmp, string name = "Texture",
                                         TextureWrap wrap = TextureWrap.Repeat,
                                         TextureFilter filter = TextureFilter.Trilinear)
        {
            var tex = new Texture
            {
                Handle = GL.GenTexture(),
                Width = bmp.Width,
                Height = bmp.Height,
                Name = name,
                WrapS = wrap,
                WrapT = wrap,
                Filter = filter
            };

            GL.BindTexture(TextureTarget.Texture2D, tex.Handle);

            GL.TexParameter(TextureTarget.Texture2D,
                TextureParameterName.TextureWrapS, (int)ToGL(wrap));
            GL.TexParameter(TextureTarget.Texture2D,
                TextureParameterName.TextureWrapT, (int)ToGL(wrap));
            GL.TexParameter(TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter, (int)ToGLMin(filter));
            GL.TexParameter(TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter, (int)ToGLMag(filter));

            // Flip Y so (0,0) = bottom-left to match OpenGL convention
            bmp.RotateFlip(RotateFlipType.RotateNoneFlipY);

            var data = bmp.LockBits(
                new Rectangle(0, 0, bmp.Width, bmp.Height),
                ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            GL.TexImage2D(TextureTarget.Texture2D, 0,
                PixelInternalFormat.Rgba,
                bmp.Width, bmp.Height, 0,
                OpenTK.Graphics.OpenGL4.PixelFormat.Bgra,
                PixelType.UnsignedByte,
                data.Scan0);

            bmp.UnlockBits(data);

            if (filter == TextureFilter.Trilinear)
                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

            GL.BindTexture(TextureTarget.Texture2D, 0);
            return tex;
        }

        /// <summary>Create a 1×1 solid-colour texture (useful as a default/white).</summary>
        public static Texture CreateSolidColor(Color color,
                                               TextureWrap wrap = TextureWrap.Repeat,
                                               TextureFilter filter = TextureFilter.Nearest)
        {
            using var bmp = new Bitmap(1, 1);
            bmp.SetPixel(0, 0, color);
            return FromBitmap(bmp, $"SolidColor_{color.Name}", wrap, filter);
        }

        /// <summary>Create an empty (black/transparent) texture of given size.</summary>
        public static Texture CreateBlank(int width, int height,
                                          TextureWrap wrap = TextureWrap.Repeat,
                                          TextureFilter filter = TextureFilter.Linear)
        {
            using var bmp = new Bitmap(width, height);
            return FromBitmap(bmp, "Blank", wrap, filter);
        }

        // ------------------------------------------------------------------
        // Bind / Unbind
        // ------------------------------------------------------------------
        public void Bind(TextureUnit unit = TextureUnit.Texture0)
        {
            GL.ActiveTexture(unit);
            GL.BindTexture(TextureTarget.Texture2D, Handle);
        }

        public static void Unbind(TextureUnit unit = TextureUnit.Texture0)
        {
            GL.ActiveTexture(unit);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        // ------------------------------------------------------------------
        // IDisposable
        // ------------------------------------------------------------------
        public void Dispose()
        {
            if (Handle != 0) { GL.DeleteTexture(Handle); Handle = 0; }
        }

        // ------------------------------------------------------------------
        // GL enum helpers
        // ------------------------------------------------------------------
        private static TextureWrapMode ToGL(TextureWrap w) => w switch
        {
            TextureWrap.ClampToEdge => TextureWrapMode.ClampToEdge,
            TextureWrap.MirroredRepeat => TextureWrapMode.MirroredRepeat,
            _ => TextureWrapMode.Repeat
        };

        private static TextureMinFilter ToGLMin(TextureFilter f) => f switch
        {
            TextureFilter.Nearest => TextureMinFilter.Nearest,
            TextureFilter.Trilinear => TextureMinFilter.LinearMipmapLinear,
            _ => TextureMinFilter.Linear
        };

        private static TextureMagFilter ToGLMag(TextureFilter f) =>
            f == TextureFilter.Nearest ? TextureMagFilter.Nearest : TextureMagFilter.Linear;
    }
}