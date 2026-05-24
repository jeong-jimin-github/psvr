using System;
using System.Collections.Generic;
using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace PSVRPlayer.Rendering;

/// <summary>
/// OpenGL 3.3 renderer for VR video playback.
///
/// Pipeline (mirrors PSVRFramework VRVideoPlayer engine.cpp):
///   Pass 1 – Render equirectangular video on a large sphere to two eye FBOs (960×1080 each).
///             Head-tracking quaternion rotates the view.  No lens distortion here.
///   Pass 2 – Full-screen barrel distortion post-process: each eye FBO → left/right half
///             of the 1920×1080 PSVR display (K1=0.22, K2=0.24 from PSVRFramework vrdevice.h).
///
/// Supported formats:
///   mono360  – full-sphere single image
///   sbs360   – side-by-side stereo (left=left eye, right=right eye)
///   tb360    – top/bottom stereo
///   mono180  – front hemisphere only
///   sbs180   – 180° side-by-side stereo
///   flat     – 2D screen 3 m ahead
/// </summary>
public sealed class SphereRenderer : IDisposable
{
    // ── GLSL shaders ─────────────────────────────────────────────────────

    private const string SphereVert = @"
#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUV;
out vec2 vUV;
uniform mat4 uVP;
void main() {
    vUV = aUV;
    gl_Position = uVP * vec4(aPos, 1.0);
}";

    private const string SphereFrag = @"
#version 330 core
in  vec2 vUV;
out vec4 fragColor;
uniform sampler2D uTex;
uniform vec2 uUVOffset;   // (0,0) mono | (0,0)/(0.5,0) SBS | (0,0.5)/(0,0) TB
uniform vec2 uUVScale;    // (1,1) mono | (0.5,1)      SBS | (1,0.5)       TB
void main() {
    fragColor = texture(uTex, uUVOffset + vUV * uUVScale);
}";

    // Barrel distortion post-process (matching PSVRFramework vrdevice.h fragment shader)
    private const string DistortVert = @"
#version 330 core
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aUV;
out vec2 vUV;
void main() {
    vUV = aUV;
    gl_Position = vec4(aPos, 0.0, 1.0);
}";

    // Per-eye barrel distortion.  K1=0.22, K2=0.24 (PSVRFramework vrdevice.h)
    // Newton-Raphson inverse: find r_render such that r_render*(1+k1*r2+k2*r4) == r_screen.
    private const string DistortFrag = @"
#version 330 core
in  vec2 vUV;
out vec4 fragColor;
uniform sampler2D uEye;
uniform float uK1;         // 0.22
uniform float uK2;         // 0.24
uniform vec2  uLensCenter; // (0.5, 0.5) in eye-texture coords

void main() {
    vec2 d = (vUV - uLensCenter);
    float r = length(d);
    if (r < 0.0001) { fragColor = texture(uEye, vUV); return; }

    // Newton-Raphson invert: r_undist s.t. r_undist*(1+k1*r²+k2*r⁴) == r
    float u = r;
    for (int i = 0; i < 12; i++) {
        float u2 = u * u, u4 = u2 * u2;
        float f  = u * (1.0 + uK1*u2 + uK2*u4) - r;
        float fp = 1.0 + 3.0*uK1*u2 + 5.0*uK2*u4;
        u -= f / fp;
    }

    vec2 sampleUV = uLensCenter + d * (u / r);
    if (any(bvec2(clamp(sampleUV, 0.0, 1.0) != sampleUV)))
        fragColor = vec4(0.0);
    else
        fragColor = texture(uEye, sampleUV);
}";

    // ── GL objects ────────────────────────────────────────────────────────

    private int _sphereProg, _distortProg;
    private int _sphereVao, _sphereVbo, _sphereEbo;
    private int _quadVao, _quadVbo;
    private int _videoTex;
    private int _leftFbo, _rightFbo, _leftTex, _rightTex;
    private int _fboWidth = 960, _fboHeight = 1080;

    // UV params per eye × format
    private Vector2 _uvOffsetL, _uvOffsetR, _uvScale;

    private bool _initialised;

    public string Format { get; private set; } = "mono360";

    // ── Initialise ───────────────────────────────────────────────────────

    public void Init(int fboW = 960, int fboH = 1080)
    {
        _fboWidth = fboW; _fboHeight = fboH;

        _sphereProg  = CompileProgram(SphereVert,  SphereFrag);
        _distortProg = CompileProgram(DistortVert, DistortFrag);

        BuildSphere("mono360");
        BuildFullscreenQuad();
        BuildFBOs();

        _videoTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _videoTex);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,     (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,     (int)TextureWrapMode.ClampToEdge);

        _initialised = true;
        SetFormat("mono360");
    }

    public void SetFormat(string fmt)
    {
        Format = fmt;
        BuildSphere(fmt);
        switch (fmt)
        {
            case "sbs360":
            case "sbs180":
                _uvOffsetL = new Vector2(0f,  0f);
                _uvOffsetR = new Vector2(0.5f, 0f);
                _uvScale   = new Vector2(0.5f, 1f);
                break;
            case "tb360":
                _uvOffsetL = new Vector2(0f, 0.5f);
                _uvOffsetR = new Vector2(0f, 0f);
                _uvScale   = new Vector2(1f, 0.5f);
                break;
            default: // mono360, mono180, flat
                _uvOffsetL = _uvOffsetR = Vector2.Zero;
                _uvScale   = Vector2.One;
                break;
        }
    }

    // ── Frame upload ──────────────────────────────────────────────────────

    public void UploadVideoFrame(byte[] rgba, int w, int h)
    {
        GL.BindTexture(TextureTarget.Texture2D, _videoTex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
            w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
    }

    // ── Render ────────────────────────────────────────────────────────────

    /// <summary>
    /// Full render call. <paramref name="headQuat"/> is the head orientation from Madgwick.
    /// outputWidth/Height = PSVR display size (1920×1080).
    /// </summary>
    public void Render(System.Numerics.Quaternion headQuat, int outputWidth, int outputHeight)
    {
        if (!_initialised) return;

        // View matrix: inverse of head orientation (we are stationary, world rotates)
        var invQ = System.Numerics.Quaternion.Conjugate(headQuat);
        var view = Matrix4.CreateFromQuaternion(new OpenTK.Mathematics.Quaternion(invQ.X, invQ.Y, invQ.Z, invQ.W));
        float aspect = _fboWidth / (float)_fboHeight;
        var proj = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(110f), aspect, 0.01f, 2000f);
        var vp = view * proj;

        // ── Pass 1: render sphere to each eye FBO ───────────────────────
        RenderEye(_leftFbo,  _leftTex,  vp, _uvOffsetL);
        RenderEye(_rightFbo, _rightTex, vp, _uvOffsetR);

        // ── Pass 2: barrel-distorted composite to screen ─────────────────
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.Viewport(0, 0, outputWidth, outputHeight);
        GL.Clear(ClearBufferMask.ColorBufferBit);

        GL.UseProgram(_distortProg);
        GL.Uniform1(GL.GetUniformLocation(_distortProg, "uK1"), 0.22f);
        GL.Uniform1(GL.GetUniformLocation(_distortProg, "uK2"), 0.24f);
        GL.Uniform2(GL.GetUniformLocation(_distortProg, "uLensCenter"), 0.5f, 0.5f);

        GL.BindVertexArray(_quadVao);

        // Left eye → left half
        GL.Viewport(0, 0, outputWidth / 2, outputHeight);
        BindEyeTexture(_distortProg, _leftTex);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);

        // Right eye → right half
        GL.Viewport(outputWidth / 2, 0, outputWidth / 2, outputHeight);
        BindEyeTexture(_distortProg, _rightTex);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
    }

    private void RenderEye(int fbo, int tex, Matrix4 vp, Vector2 uvOffset)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.Viewport(0, 0, _fboWidth, _fboHeight);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        GL.UseProgram(_sphereProg);
        GL.UniformMatrix4(GL.GetUniformLocation(_sphereProg, "uVP"), false, ref vp);
        GL.Uniform2(GL.GetUniformLocation(_sphereProg, "uUVOffset"), uvOffset.X, uvOffset.Y);
        GL.Uniform2(GL.GetUniformLocation(_sphereProg, "uUVScale"),  _uvScale.X, _uvScale.Y);

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _videoTex);
        GL.Uniform1(GL.GetUniformLocation(_sphereProg, "uTex"), 0);

        GL.BindVertexArray(_sphereVao);
        GL.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, 0);
    }

    private static void BindEyeTexture(int prog, int tex)
    {
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.Uniform1(GL.GetUniformLocation(prog, "uEye"), 0);
    }

    // ── Geometry builders ─────────────────────────────────────────────────

    private int _indexCount;

    private void BuildSphere(string fmt)
    {
        if (!_initialised && _sphereVao == 0)
        {
            _sphereVao = GL.GenVertexArray();
            _sphereVbo = GL.GenBuffer();
            _sphereEbo = GL.GenBuffer();
        }

        bool is180 = fmt.Contains("180");
        bool isFlat = fmt == "flat";

        List<float> verts = new();
        List<uint>  idxs  = new();

        if (isFlat)
        {
            // Flat 16:9 screen at Z=-3
            float w = 3.2f, h = 1.8f;
            verts.AddRange(new[] {
                -w/2f, -h/2f, -3f, 0f, 1f,
                 w/2f, -h/2f, -3f, 1f, 1f,
                 w/2f,  h/2f, -3f, 1f, 0f,
                -w/2f,  h/2f, -3f, 0f, 0f,
            });
            idxs.AddRange(new uint[] { 0, 1, 2, 2, 3, 0 });
        }
        else
        {
            const int latSegs = 40, lonSegs = 60;
            const float R = 500f;
            float phiLen = is180 ? MathF.PI : 2f * MathF.PI;
            float phiStart = is180 ? -MathF.PI / 2f : 0f;

            for (int lat = 0; lat <= latSegs; lat++)
            {
                float theta = lat * MathF.PI / latSegs;
                float sT = MathF.Sin(theta), cT = MathF.Cos(theta);

                for (int lon = 0; lon <= lonSegs; lon++)
                {
                    float phi = phiStart + lon * phiLen / lonSegs;
                    float sP = MathF.Sin(phi), cP = MathF.Cos(phi);

                    // Invert X so normals point inward (viewer inside sphere)
                    verts.Add(-R * sT * cP);
                    verts.Add( R * cT);
                    verts.Add( R * sT * sP);
                    verts.Add((float)lon / lonSegs);
                    verts.Add((float)lat / latSegs);
                }
            }

            for (int lat = 0; lat < latSegs; lat++)
            {
                for (int lon = 0; lon < lonSegs; lon++)
                {
                    uint a = (uint)(lat * (lonSegs + 1) + lon);
                    uint b = a + (uint)(lonSegs + 1);
                    idxs.AddRange(new[] { a, b, a + 1, b, b + 1, a + 1 });
                }
            }
        }

        var va = verts.ToArray();
        var ia = idxs.ToArray();
        _indexCount = ia.Length;

        GL.BindVertexArray(_sphereVao);

        GL.BindBuffer(BufferTarget.ArrayBuffer, _sphereVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, va.Length * sizeof(float), va, BufferUsageHint.StaticDraw);

        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _sphereEbo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, ia.Length * sizeof(uint), ia, BufferUsageHint.StaticDraw);

        int stride = 5 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
    }

    private void BuildFullscreenQuad()
    {
        float[] quad = {
            // pos      uv
            -1f, -1f,  0f, 0f,
             1f, -1f,  1f, 0f,
             1f,  1f,  1f, 1f,
            -1f, -1f,  0f, 0f,
             1f,  1f,  1f, 1f,
            -1f,  1f,  0f, 1f,
        };

        _quadVao = GL.GenVertexArray();
        _quadVbo = GL.GenBuffer();

        GL.BindVertexArray(_quadVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _quadVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, quad.Length * sizeof(float), quad, BufferUsageHint.StaticDraw);

        int stride = 4 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 2 * sizeof(float));
    }

    private void BuildFBOs()
    {
        void MakeFBO(out int fbo, out int tex)
        {
            fbo = GL.GenFramebuffer();
            tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                _fboWidth, _fboHeight, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, tex, 0);

            int depthRbo = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depthRbo);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, _fboWidth, _fboHeight);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer, depthRbo);
        }

        MakeFBO(out _leftFbo,  out _leftTex);
        MakeFBO(out _rightFbo, out _rightTex);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    // ── Shader compilation ────────────────────────────────────────────────

    private static int CompileProgram(string vert, string frag)
    {
        int vs = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vs, vert);
        GL.CompileShader(vs);
        GL.GetShader(vs, ShaderParameter.CompileStatus, out int vsOk);
        if (vsOk == 0) throw new Exception("VS: " + GL.GetShaderInfoLog(vs));

        int fs = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fs, frag);
        GL.CompileShader(fs);
        GL.GetShader(fs, ShaderParameter.CompileStatus, out int fsOk);
        if (fsOk == 0) throw new Exception("FS: " + GL.GetShaderInfoLog(fs));

        int prog = GL.CreateProgram();
        GL.AttachShader(prog, vs);
        GL.AttachShader(prog, fs);
        GL.LinkProgram(prog);
        GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int linkOk);
        if (linkOk == 0) throw new Exception("Link: " + GL.GetProgramInfoLog(prog));

        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
        return prog;
    }

    // ── Dispose ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_sphereVao != 0) { GL.DeleteVertexArray(_sphereVao); GL.DeleteBuffer(_sphereVbo); GL.DeleteBuffer(_sphereEbo); }
        if (_quadVao   != 0) { GL.DeleteVertexArray(_quadVao);   GL.DeleteBuffer(_quadVbo); }
        if (_leftFbo   != 0) { GL.DeleteFramebuffer(_leftFbo);   GL.DeleteTexture(_leftTex); }
        if (_rightFbo  != 0) { GL.DeleteFramebuffer(_rightFbo);  GL.DeleteTexture(_rightTex); }
        if (_videoTex  != 0)   GL.DeleteTexture(_videoTex);
        if (_sphereProg  != 0) GL.DeleteProgram(_sphereProg);
        if (_distortProg != 0) GL.DeleteProgram(_distortProg);
    }
}
