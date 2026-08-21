using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;

namespace InkContainer;

[StructLayout(LayoutKind.Sequential)]
struct GpuParams
{
    public float PointX, PointY, VelX, VelY;
    public float ColorR, ColorG, ColorB, Radius;
    public float TexelX, TexelY, VelTexelX, VelTexelY;
    public float SrcResX, SrcResY, LightX, LightY;
    public float Aspect, Density, Temp, Dt;
    public float Diss, Buoy, Grav, Swirl;
    public float Mix, Value, Extinction, Ambient;
    public float Incand, Dropoff, Exposure, AlphaMode;
    public float Thresh, BloomAmt, DirX, DirY;
    public float Mode, ShadowSteps, FlipY, Pad;
}

sealed class Rt : IDisposable
{
    public ID3D11Texture2D Tex = null!;
    public ID3D11RenderTargetView Rtv = null!;
    public ID3D11ShaderResourceView Srv = null!;
    public int W, H;
    public void Dispose()
    {
        Srv?.Dispose();
        Rtv?.Dispose();
        Tex?.Dispose();
    }
}

sealed class Ping : IDisposable
{
    public Rt A = null!, B = null!;
    public Rt Read => A;
    public Rt Write => B;
    public void Swap() => (A, B) = (B, A);
    public void Dispose() { A?.Dispose(); B?.Dispose(); }
}

sealed class Gpu : IDisposable
{
    ID3D11Device _dev = null!;
    ID3D11DeviceContext _ctx = null!;
    IDXGISwapChain1 _swap = null!;
    ID3D11Texture2D? _bb;
    ID3D11RenderTargetView? _bbRtv;
    ID3D11VertexShader _vs = null!;
    readonly Dictionary<string, ID3D11PixelShader> _ps = [];
    ID3D11SamplerState _samp = null!;
    ID3D11Buffer _cb = null!;
    ID3D11RasterizerState _rs = null!;
    ID3D11BlendState _opaque = null!;
    ID3D11DepthStencilState _noDepth = null!;
    Ping _vel = null!, _dye = null!, _temp = null!, _pressure = null!;
    Rt _div = null!, _curl = null!, _hat = null!, _hathat = null!, _beauty = null!, _bloom0 = null!, _bloom1 = null!;
    int _vw, _vh;
    IntPtr _hwnd;

    public int VelW { get; private set; }
    public int VelH { get; private set; }
    public int DyeW { get; private set; }
    public int DyeH { get; private set; }

    static void Log(string m)
    {
        try { File.AppendAllText(System.IO.Path.Combine(AppPaths.Root, "gpu.log"), DateTime.Now.ToString("o") + " " + m + "\n"); }
        catch { }
    }

    public void Init(IntPtr hwnd, int w, int h, string quality)
    {
        Log($"Init hwnd={hwnd} {w}x{h} q={quality}");
        _hwnd = hwnd;
        var flags = DeviceCreationFlags.BgraSupport;
#if DEBUG
        flags |= DeviceCreationFlags.Debug;
#endif
        D3D11CreateDevice(null, DriverType.Hardware, flags,
            [FeatureLevel.Level_11_0], out _dev, out _, out _ctx).CheckError();

        using var dxgiDev = _dev.QueryInterface<IDXGIDevice1>();
        using var adapter = dxgiDev.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        _swap = factory.CreateSwapChainForHwnd(_dev, hwnd, new SwapChainDescription1
        {
            Width = (uint)Math.Max(1, w),
            Height = (uint)Math.Max(1, h),
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore
        });
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);

        Log("device ok, compiling shaders");
        CompileShaders();
        Log("shaders ok");
        _samp = _dev.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxLOD = float.MaxValue
        });
        _cb = _dev.CreateBuffer(new BufferDescription((uint)((Marshal.SizeOf<GpuParams>() + 15) & ~15), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _rs = _dev.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { DepthClipEnable = true });
        _opaque = _dev.CreateBlendState(BlendDescription.Opaque);
        _noDepth = _dev.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = false,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.Always
        });

        _vw = Math.Max(1, w);
        _vh = Math.Max(1, h);
        CreateBackbuffer();
        CreateSim(quality);
    }

    void CompileShaders()
    {
        var src = LoadHlsl();
        ReadOnlyMemory<byte> Compile(string entry, string profile)
        {
            try { return Compiler.Compile(src, entry, "fluid.hlsl", profile); }
            catch (Exception ex) { throw new InvalidOperationException($"HLSL {entry}: {ex.Message}", ex); }
        }
        _vs = _dev.CreateVertexShader(Compile("VSMain", "vs_5_0").Span);
        foreach (var e in new[] { "PSSplat", "PSAdvect", "PSMac", "PSBuoyancy", "PSCurl", "PSVort", "PSDivergence", "PSJacobi", "PSGradient", "PSDiffuse", "PSFade", "PSClear", "PSDisplay", "PSExtract", "PSBlur", "PSComposite" })
            _ps[e] = _dev.CreatePixelShader(Compile(e, "ps_5_0").Span);
    }

    static string LoadHlsl()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream("fluid.hlsl")
            ?? throw new InvalidOperationException("missing fluid.hlsl");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    Rt MakeRt(int w, int h, Format fmt)
    {
        var td = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = fmt,
            SampleDescription = new SampleDescription(1, 0),
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            Usage = ResourceUsage.Default
        };
        var tex = _dev.CreateTexture2D(td);
        return new Rt
        {
            Tex = tex,
            Rtv = _dev.CreateRenderTargetView(tex),
            Srv = _dev.CreateShaderResourceView(tex),
            W = w,
            H = h
        };
    }

    Ping MakePing(int w, int h, Format fmt) => new() { A = MakeRt(w, h, fmt), B = MakeRt(w, h, fmt) };

    public void CreateSim(string quality)
    {
        DisposeSim();
        var shortSide = Sim.SimShort(quality);
        var aspect = _vw / (float)_vh;
        int sw, sh;
        if (aspect >= 1) { sh = shortSide; sw = Math.Max(32, (int)MathF.Round(shortSide * aspect / 8) * 8); }
        else { sw = shortSide; sh = Math.Max(32, (int)MathF.Round(shortSide / aspect / 8) * 8); }
        var dw = Math.Max(32, (int)MathF.Round(sw * 2 / 8f) * 8);
        var dh = Math.Max(32, (int)MathF.Round(sh * 2 / 8f) * 8);
        VelW = sw; VelH = sh; DyeW = dw; DyeH = dh;
        _vel = MakePing(sw, sh, Format.R16G16_Float);
        _dye = MakePing(dw, dh, Format.R16G16B16A16_Float);
        _temp = MakePing(sw, sh, Format.R16_Float);
        _pressure = MakePing(sw, sh, Format.R16_Float);
        _div = MakeRt(sw, sh, Format.R16_Float);
        _curl = MakeRt(sw, sh, Format.R16_Float);
        _hat = MakeRt(dw, dh, Format.R16G16B16A16_Float);
        _hathat = MakeRt(dw, dh, Format.R16G16B16A16_Float);
        _beauty = MakeRt(_vw, _vh, Format.R16G16B16A16_Float);
        _bloom0 = MakeRt(Math.Max(8, _vw / 2), Math.Max(8, _vh / 2), Format.R16G16B16A16_Float);
        _bloom1 = MakeRt(Math.Max(8, _vw / 2), Math.Max(8, _vh / 2), Format.R16G16B16A16_Float);
        ClearSim();
    }

    public void ClearSim()
    {
        var z = new Color4(0, 0, 0, 0);
        foreach (var rt in new[] { _vel.A, _vel.B, _dye.A, _dye.B, _temp.A, _temp.B, _pressure.A, _pressure.B, _div, _curl, _hat, _hathat })
            _ctx.ClearRenderTargetView(rt.Rtv, z);
    }

    void CreateBackbuffer()
    {
        _bbRtv?.Dispose();
        _bb?.Dispose();
        _bb = _swap.GetBuffer<ID3D11Texture2D>(0);
        _bbRtv = _dev.CreateRenderTargetView(_bb);
    }

    public void Resize(int w, int h, string quality)
    {
        if (_dev == null || w < 8 || h < 8) return;
        if (w == _vw && h == _vh) return;
        _ctx.OMSetRenderTargets((ID3D11RenderTargetView[])[]);
        _bbRtv?.Dispose(); _bbRtv = null;
        _bb?.Dispose(); _bb = null;
        _swap.ResizeBuffers(2, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
        _vw = w; _vh = h;
        CreateBackbuffer();
        CreateSim(quality);
    }

    public void Frame(Sim sim, float dt, bool vsync)
    {
        if (_bbRtv == null) return;
        var dr = sim.Drive();
        if (!sim.Paused)
        {
            foreach (var s in sim.Splats)
                DoSplat(s);
            sim.Splats.Clear();
            Step(sim, dt, dr);
        }
        Display(sim);
        _swap.Present(vsync ? 1u : 0u, PresentFlags.None);
    }

    void DoSplat(Splat s)
    {
        var aspect = _vw / (float)_vh;
        var p = BaseParams();
        p.PointX = s.X; p.PointY = s.Y;
        p.VelX = s.Dx; p.VelY = s.Dy;
        p.ColorR = s.Color.X; p.ColorG = s.Color.Y; p.ColorB = s.Color.Z;
        p.Radius = s.Radius;
        p.Aspect = aspect;
        p.Density = s.Density;
        p.Temp = s.Temp;
        p.Mode = 0;
        Blit("PSSplat", _vel.Write, p, _vel.Read);
        _vel.Swap();
        p.Mode = 1;
        Blit("PSSplat", _dye.Write, p, _dye.Read);
        _dye.Swap();
        p.Mode = 2;
        p.Radius = s.Radius * 1.15f;
        Blit("PSSplat", _temp.Write, p, _temp.Read);
        _temp.Swap();
    }

    void Step(Sim sim, float dt, float dr)
    {
        var vtex = new Vector2(1f / VelW, 1f / VelH);
        var dtex = new Vector2(1f / DyeW, 1f / DyeH);
        var p = BaseParams();
        p.Dt = dt;
        p.TexelX = vtex.X; p.TexelY = vtex.Y;
        p.VelTexelX = vtex.X; p.VelTexelY = vtex.Y;

        if (sim.Buoyancy > 0.001f || sim.Gravity > 0.001f)
        {
            p.Buoy = sim.Buoyancy; p.Grav = sim.Gravity;
            Blit("PSBuoyancy", _vel.Write, p, _vel.Read, _dye.Read, _temp.Read);
            _vel.Swap();
        }
        if (sim.Swirl > 0.01f)
        {
            Blit("PSCurl", _curl, p, _vel.Read);
            p.Swirl = sim.Swirl * (0.12f + 0.88f * dr);
            Blit("PSVort", _vel.Write, p, _vel.Read, _curl);
            _vel.Swap();
        }
        if (sim.Viscosity > 0.001f)
        {
            p.Mix = Math.Min(0.95f, sim.Viscosity);
            Blit("PSDiffuse", _vel.Write, p, _vel.Read);
            _vel.Swap();
        }
        Blit("PSDivergence", _div, p, _vel.Read);
        p.Value = 0.8f;
        Blit("PSFade", _pressure.Write, p, _pressure.Read);
        _pressure.Swap();
        var iters = Math.Max(4, sim.PressureIters);
        for (var i = 0; i < iters; i++)
        {
            Blit("PSJacobi", _pressure.Write, p, _pressure.Read, _div);
            _pressure.Swap();
        }
        Blit("PSGradient", _vel.Write, p, _vel.Read, _pressure.Read);
        _vel.Swap();

        p.Diss = Dissipate(sim.VelDiss + (1 - sim.Energy) * 0.32f, dt);
        p.SrcResX = VelW; p.SrcResY = VelH;
        p.TexelX = vtex.X; p.TexelY = vtex.Y;
        Blit("PSAdvect", _vel.Write, p, _vel.Read, _vel.Read);
        _vel.Swap();

        p.Diss = 1;
        p.SrcResX = DyeW; p.SrcResY = DyeH;
        p.TexelX = dtex.X; p.TexelY = dtex.Y;
        p.Dt = dt;
        Blit("PSAdvect", _hat, p, _vel.Read, _dye.Read);
        p.Dt = -dt;
        Blit("PSAdvect", _hathat, p, _vel.Read, _hat);
        p.Dt = dt;
        p.Diss = Dissipate(sim.DensDiss, dt);
        Blit("PSMac", _dye.Write, p, _dye.Read, _hat, _hathat);
        _dye.Swap();

        p.Diss = Dissipate(sim.TempDiss, dt);
        p.SrcResX = VelW; p.SrcResY = VelH;
        p.TexelX = vtex.X; p.TexelY = vtex.Y;
        Blit("PSAdvect", _temp.Write, p, _vel.Read, _temp.Read);
        _temp.Swap();
    }

    static float Dissipate(float value, float dt) => MathF.Exp(-Math.Max(0, value) * dt * 2.5f);

    void Display(Sim sim)
    {
        var p = BaseParams();
        p.TexelX = 1f / DyeW; p.TexelY = 1f / DyeH;
        p.LightX = 0.55f; p.LightY = 0.85f;
        p.Extinction = sim.Shadow;
        p.Ambient = sim.Ambient;
        p.Incand = sim.Incandescence;
        p.Dropoff = sim.Dropoff;
        p.Exposure = 1.35f;
        p.Mode = sim.Viz switch
        {
            Viz.Raw => 1,
            Viz.Velocity => 2,
            Viz.Pressure => 3,
            Viz.Temperature => 6,
            _ => 0
        };
        p.ShadowSteps = sim.Quality == "low" ? 8 : sim.Quality == "ultra" ? 20 : 16;
        Blit("PSDisplay", _beauty, p, _dye.Read, _temp.Read, _pressure.Read);

        if (sim.Bloom && sim.BloomAmt > 0.01f && sim.Viz == Viz.Dye)
        {
            p.Thresh = 0.72f;
            Blit("PSExtract", _bloom0, p, _beauty);
            p.TexelX = 1f / _bloom0.W; p.TexelY = 1f / _bloom0.H;
            p.DirX = 1; p.DirY = 0;
            Blit("PSBlur", _bloom1, p, _bloom0);
            p.DirX = 0; p.DirY = 1;
            Blit("PSBlur", _bloom0, p, _bloom1);
            p.BloomAmt = sim.BloomAmt;
            BlitToBack("PSComposite", p, _beauty, _bloom0);
        }
        else
        {
            BlitToBack("PSFade", new GpuParams { Value = 1 }, _beauty);
        }
    }

    GpuParams BaseParams() => new() { LightX = 0.55f, LightY = 0.85f };

    void Upload(in GpuParams p)
    {
        var mapped = _ctx.Map(_cb, 0, MapMode.WriteDiscard);
        Marshal.StructureToPtr(p, mapped.DataPointer, false);
        _ctx.Unmap(_cb, 0);
    }

    void Blit(string ps, Rt dest, in GpuParams p, params Rt[] srvs)
    {
        Upload(p);
        _ctx.OMSetRenderTargets(dest.Rtv);
        _ctx.RSSetViewport(new Viewport(0, 0, dest.W, dest.H));
        _ctx.RSSetState(_rs);
        _ctx.OMSetBlendState(_opaque);
        _ctx.OMSetDepthStencilState(_noDepth);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_vs);
        _ctx.PSSetShader(_ps[ps]);
        _ctx.PSSetSampler(0, _samp);
        _ctx.VSSetConstantBuffer(0, _cb);
        _ctx.PSSetConstantBuffer(0, _cb);
        BindSrvs(srvs);
        _ctx.Draw(3, 0);
        UnbindSrvs();
        _ctx.OMSetRenderTargets((ID3D11RenderTargetView[])[]);
    }

    void BlitToBack(string ps, in GpuParams p, params Rt[] srvs)
    {
        Upload(p);
        _ctx.OMSetRenderTargets(_bbRtv!);
        _ctx.RSSetViewport(new Viewport(0, 0, _vw, _vh));
        _ctx.RSSetState(_rs);
        _ctx.OMSetBlendState(_opaque);
        _ctx.OMSetDepthStencilState(_noDepth);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_vs);
        _ctx.PSSetShader(_ps[ps]);
        _ctx.PSSetSampler(0, _samp);
        _ctx.VSSetConstantBuffer(0, _cb);
        _ctx.PSSetConstantBuffer(0, _cb);
        BindSrvs(srvs);
        _ctx.Draw(3, 0);
        UnbindSrvs();
        _ctx.OMSetRenderTargets((ID3D11RenderTargetView[])[]);
    }

    void BindSrvs(Rt[] srvs)
    {
        var views = new ID3D11ShaderResourceView[3];
        for (var i = 0; i < srvs.Length && i < 3; i++)
            views[i] = srvs[i].Srv;
        _ctx.PSSetShaderResources(0, views);
    }

    void UnbindSrvs()
    {
        _ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[3]);
    }

    void DisposeSim()
    {
        _vel?.Dispose(); _dye?.Dispose(); _temp?.Dispose(); _pressure?.Dispose();
        _div?.Dispose(); _curl?.Dispose(); _hat?.Dispose(); _hathat?.Dispose();
        _beauty?.Dispose(); _bloom0?.Dispose(); _bloom1?.Dispose();
    }

    public void Dispose()
    {
        DisposeSim();
        foreach (var p in _ps.Values) p.Dispose();
        _ps.Clear();
        _vs?.Dispose();
        _samp?.Dispose();
        _cb?.Dispose();
        _rs?.Dispose();
        _opaque?.Dispose();
        _noDepth?.Dispose();
        _bbRtv?.Dispose();
        _bb?.Dispose();
        _swap?.Dispose();
        _ctx?.Dispose();
        _dev?.Dispose();
    }
}
