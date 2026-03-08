namespace ElintriaEngine.Rendering
{
    internal static class UIShaders
    {
        // ── Flat-colour + textured quad shader ────────────────────────────────
        public const string VertexSource = @"
#version 330 core
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aUV;
layout(location = 2) in vec4 aColor;

uniform mat4 uProjection;

out vec2 vUV;
out vec4 vColor;

void main()
{
    gl_Position = uProjection * vec4(aPos, 0.0, 1.0);
    vUV    = aUV;
    vColor = aColor;
}";

        public const string FragmentSource = @"
#version 330 core
in vec2 vUV;
in vec4 vColor;

uniform sampler2D uTexture;
uniform int       uMode;   // 0 = flat colour, 1 = font atlas, 2 = full texture

out vec4 FragColor;

void main()
{
    if (uMode == 1)
    {
        // SDF / alpha-only font glyph
        float alpha = texture(uTexture, vUV).r;
        FragColor = vec4(vColor.rgb, vColor.a * alpha);
    }
    else if (uMode == 2)
    {
        FragColor = texture(uTexture, vUV) * vColor;
    }
    else
    {
        FragColor = vColor;
    }
}";

        // ── Scissor / clip-rect shader (same batch, we use glScissor) ─────────
        // We rely on OpenGL's native scissor test rather than shader clipping.
    }
}