// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;
using System.Runtime.InteropServices;
using Valve.VR;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
namespace VRCoplay;
internal sealed class SteamVrCalibrationOverlay : IDisposable
{
    internal const int Resolution = 768;
    internal const int TextureWidth = Resolution * 2;
    private readonly CVROverlay _api;
    private readonly List<IDisposable> _resources = [];
    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private ID3D11Texture2D _color = null!, _depth = null!;
    private ID3D11RenderTargetView _target = null!;
    private ID3D11DepthStencilView _depthView = null!;
    private ID3D11VertexShader _vertexShader = null!;
    private ID3D11PixelShader _pixelShader = null!;
    private ID3D11InputLayout _layout = null!;
    private ID3D11Buffer _constants = null!;
    private ID3D11Buffer? _vertices, _indices;
    private ID3D11RasterizerState _raster = null!;
    private ID3D11BlendState _blend = null!;
    private ID3D11BlendState _depthOnly = null!;
    private ID3D11DepthStencilState _readDepth = null!;
    private ControllerMesh? _mesh;
    private readonly ID3D11Texture2D[] _textures = new ID3D11Texture2D[2];
    private readonly ulong[] _handles = new ulong[2];
    private bool _visible;
    private readonly bool[] _rendered = new bool[2];
    internal SteamVrSurface[] Surfaces { get; } = new SteamVrSurface[2];
    private float _radius;
    internal IReadOnlyList<ulong> Handles => _handles;
    private T Own<T>(T resource) where T : IDisposable { _resources.Add(resource); return resource; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex { internal Vector3 Position, Normal; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants { internal Matrix4x4 World, ViewProjection; internal Vector4 Color, Camera; }
    internal SteamVrCalibrationOverlay(CVRSystem system, CVROverlay api, string? key = null)
    {
        _api = api;
        try
        {
            int index=0; system.GetDXGIOutputInfo(ref index);
            using var factory=DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            factory.EnumAdapters1((uint)Math.Max(0,index),out var adapter).CheckError();
            using (adapter)
                D3D11.D3D11CreateDevice(adapter,DriverType.Unknown,DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_0],out _device,out _context).CheckError();
            Own(_device); Own(_context);
            _color=Own(_device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm,
                TextureWidth,Resolution,1,1,BindFlags.RenderTarget,ResourceUsage.Default,CpuAccessFlags.None,4)));
            _target=Own(_device.CreateRenderTargetView(_color));
            _depth=Own(_device.CreateTexture2D(new Texture2DDescription(Format.D32_Float,
                TextureWidth,Resolution,1,1,BindFlags.DepthStencil,ResourceUsage.Default,CpuAccessFlags.None,4)));
            _depthView=Own(_device.CreateDepthStencilView(_depth));
            var vs=Compiler.Compile(Shader,[],"VS","Controller calibration","vs_5_0",ShaderFlags.OptimizationLevel3);
            var ps=Compiler.Compile(Shader,[],"PS","Controller calibration","ps_5_0",ShaderFlags.OptimizationLevel3);
            _vertexShader=Own(_device.CreateVertexShader(vs.Span));
            _pixelShader=Own(_device.CreatePixelShader(ps.Span));
            _layout=Own(_device.CreateInputLayout([
                new InputElementDescription("POSITION",0,Format.R32G32B32_Float,0,0),
                new InputElementDescription("NORMAL",0,Format.R32G32B32_Float,12,0)],vs.Span));
            _constants=Own(_device.CreateBuffer((uint)Marshal.SizeOf<Constants>(),BindFlags.ConstantBuffer));
            _raster=Own(_device.CreateRasterizerState(new RasterizerDescription(CullMode.None,FillMode.Solid) { MultisampleEnable=true }));
            _blend=Own(_device.CreateBlendState(BlendDescription.AlphaBlend));
            var depthOnly=BlendDescription.Opaque; depthOnly.RenderTarget[0].RenderTargetWriteMask=ColorWriteEnable.None;
            _depthOnly=Own(_device.CreateBlendState(depthOnly));
            _readDepth=Own(_device.CreateDepthStencilState(new DepthStencilDescription(true,DepthWriteMask.Zero,ComparisonFunction.LessEqual)));
            for (int eye=0;eye<2;eye++)
            {
                _textures[eye]=Own(_device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm,
                    TextureWidth,Resolution,1,1,BindFlags.ShaderResource|BindFlags.RenderTarget,
                    ResourceUsage.Default,CpuAccessFlags.None,1,0,ResourceOptionFlags.Shared)));
                Check(api.CreateOverlay((key??"vrcoplay.controller-alignment."+Environment.ProcessId)+"."+eye,
                    "Controller alignment",ref _handles[eye]));
                Check(api.SetOverlayInputMethod(_handles[eye],VROverlayInputMethod.None));
                Check(api.SetOverlayFlag(_handles[eye],VROverlayFlags.HideLaserIntersection,true));
                Check(api.SetOverlayFlag(_handles[eye],VROverlayFlags.IsPremultiplied,true));
                Check(api.SetOverlayFlag(_handles[eye],VROverlayFlags.SideBySide_Parallel,true));
                Check(api.SetOverlayTexelAspect(_handles[eye],1));
            }
        }
        catch { Dispose(); throw; }
    }
    internal void Render(ControllerHologramScene scene, HmdMatrix34_t head, HmdMatrix34_t current, CVRSystem system)
    {
        if (!ReferenceEquals(_mesh,scene.Mesh)) Upload(scene.Mesh);
        var headToWorld=SteamVrEye.Matrix(head);
        var poses=new[] { current, SteamVrControllerModel.Matrix(scene.Target) };
        for (int model=0;model<2;model++)
        {
            var world=SteamVrEye.Matrix(poses[model]);
            var center=scene.Mesh.Center; center.Z=-center.Z;
            var surface=SteamVrSurface.Around(Vector3.Transform(center,world),_radius,headToWorld.Translation);
            Surfaces[model]=surface;
            _rendered[model]=surface.Width>0 && (model==1 || scene.Tracking);
            if (!_rendered[model]) { _api.HideOverlay(_handles[model]); continue; }
            _context.ClearRenderTargetView(_target,new Color4(0,0,0,0));
            for (int eye=0;eye<2;eye++)
            {
                var camera=SteamVrEye.ThroughSurface(SteamVrEye.Matrix(system.GetEyeToHeadTransform((EVREye)eye))*headToWorld,surface);
                var color=model==0 ? new Vector4(.91f,.96f,1,.9f) : scene.Matched ? new(.18f,.88f,.73f,.32f) : new(.20f,.60f,1,.22f);
                RenderEye(poses[model],color,camera,eye);
            }
            _context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
            _context.ResolveSubresource(_textures[model],0,_color,0,Format.R8G8B8A8_UNorm);
            _context.Flush();
            var transform=SteamVrEye.Native(surface.Transform);
            Check(_api.SetOverlayTransformAbsolute(_handles[model],ETrackingUniverseOrigin.TrackingUniverseStanding,ref transform));
            Check(_api.SetOverlayWidthInMeters(_handles[model],surface.Width));
            var texture=new Texture_t { handle=_textures[model].NativePointer,eType=ETextureType.DirectX,eColorSpace=EColorSpace.Gamma };
            Check(_api.SetOverlayTexture(_handles[model],ref texture));
            if (_visible) Check(_api.ShowOverlay(_handles[model]));
        }
    }
    private void Upload(ControllerMesh mesh)
    {
        _vertices?.Dispose(); _vertices=null; _indices?.Dispose(); _indices=null;
        var vertices=mesh.Vertices.Select(p=>new Vertex { Position=new(p.X,p.Y,-p.Z) }).ToArray();
        for (int i=0;i<mesh.Indices.Length;i+=3)
        {
            int a=mesh.Indices[i],b=mesh.Indices[i+1],c=mesh.Indices[i+2];
            var n=Vector3.Cross(vertices[b].Position-vertices[a].Position,vertices[c].Position-vertices[a].Position);
            vertices[a].Normal+=n; vertices[b].Normal+=n; vertices[c].Normal+=n;
        }
        for (int i=0;i<vertices.Length;i++) vertices[i].Normal=vertices[i].Normal.LengthSquared()>1e-12f?Vector3.Normalize(vertices[i].Normal):Vector3.UnitY;
        _vertices=_device.CreateBuffer<Vertex>(vertices,BindFlags.VertexBuffer);
        _indices=_device.CreateBuffer<int>(mesh.Indices,BindFlags.IndexBuffer);
        _mesh=mesh;
        _radius=mesh.Vertices.Max(p=>Vector3.Distance(p,mesh.Center));
    }
    private void RenderEye(HmdMatrix34_t pose,Vector4 color,SteamVrEye eye,int index)
    {
        _context.OMSetRenderTargets(_target,_depthView);
        _context.RSSetViewport(new Viewport(index*Resolution,0,Resolution,Resolution));
        _context.RSSetState(_raster);
        _context.OMSetBlendState(_blend);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(_layout);
        _context.IASetVertexBuffer(0,_vertices!,24);
        _context.IASetIndexBuffer(_indices!,Format.R32_UInt,0);
        _context.VSSetShader(_vertexShader); _context.PSSetShader(_pixelShader);
        _context.VSSetConstantBuffer(0,_constants); _context.PSSetConstantBuffer(0,_constants);
        _context.ClearDepthStencilView(_depthView,DepthStencilClearFlags.Depth,1,0);
        var constants=new Constants { World=SteamVrEye.Matrix(pose),ViewProjection=eye.ViewProjection,Color=color,
            Camera=new(eye.Camera.m3,eye.Camera.m7,eye.Camera.m11,0) };
        _context.UpdateSubresource(in constants,_constants);
        _context.OMSetDepthStencilState(null);
        _context.OMSetBlendState(_depthOnly);
        _context.DrawIndexed((uint)_mesh!.Indices.Length,0,0);
        _context.OMSetDepthStencilState(_readDepth);
        _context.OMSetBlendState(_blend);
        _context.DrawIndexed((uint)_mesh!.Indices.Length,0,0);
    }
    internal void Show() { for(int i=0;i<2;i++) if(_rendered[i]) Check(_api.ShowOverlay(_handles[i])); _visible=true; }
    internal void Hide() { foreach(var handle in _handles) if(handle!=0) _api.HideOverlay(handle); _visible=false; }
    internal byte[] ReadPixels(int model)
    {
        var desc=_textures[model].Description; desc.Usage=ResourceUsage.Staging; desc.BindFlags=BindFlags.None;
        desc.CPUAccessFlags=CpuAccessFlags.Read; desc.MiscFlags=ResourceOptionFlags.None;
        using var staging=_device.CreateTexture2D(desc); _context.CopyResource(staging,_textures[model]);
        var mapped=_context.Map(staging,0,MapMode.Read,Vortice.Direct3D11.MapFlags.None);
        var pixels=new byte[TextureWidth*Resolution*4];
        try { for(int row=0;row<Resolution;row++) Marshal.Copy(mapped.DataPointer+(int)(row*mapped.RowPitch),pixels,row*TextureWidth*4,TextureWidth*4); }
        finally { _context.Unmap(staging,0); }
        return pixels;
    }
    private static void Check(EVROverlayError error) { if(error!=EVROverlayError.None) throw new InvalidOperationException("SteamVR controller display: "+error); }
    public void Dispose()
    {
        for(int i=0;i<2;i++) if(_handles[i]!=0) { _api.HideOverlay(_handles[i]); _api.ClearOverlayTexture(_handles[i]); _api.DestroyOverlay(_handles[i]); _handles[i]=0; }
        _context?.ClearState(); _context?.Flush(); _vertices?.Dispose(); _indices?.Dispose();
        for(int i=_resources.Count-1;i>=0;i--) _resources[i].Dispose(); _resources.Clear();
    }
    private const string Shader = """
        cbuffer Frame : register(b0) {
            row_major float4x4 World;
            row_major float4x4 ViewProjection;
            float4 Color;
            float4 Camera;
        };
        struct Vertex { float3 position : POSITION; float3 normal : NORMAL; };
        struct Fragment { float4 position : SV_POSITION; float3 world : TEXCOORD0; float3 normal : TEXCOORD1; };
        Fragment VS(Vertex v) {
            Fragment o;
            float4 w=mul(float4(v.position,1),World);
            o.position=mul(w,ViewProjection); o.world=w.xyz;
            o.normal=mul(float4(v.normal,0),World).xyz;
            return o;
        }
        float4 PS(Fragment i) : SV_TARGET {
            float3 n=normalize(i.normal);
            float rim=pow(1-abs(dot(n,normalize(Camera.xyz-i.world))),2);
            float light=.55+.45*abs(dot(n,normalize(float3(-.4,.8,.6))));
            float alpha=Color.a<.5 ? Color.a+.58*rim : Color.a;
            float3 rgb=Color.rgb*(Color.a<.5 ? .78+.22*rim : light);
            return float4(rgb*alpha,alpha);
        }
        """;
}
