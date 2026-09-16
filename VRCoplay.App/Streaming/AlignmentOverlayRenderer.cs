// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using static VRCoplay.OverlayRenderer;
namespace VRCoplay;
internal sealed class AlignmentOverlayRenderer(int width,int height)
{
    private Task<Sprite[]>? _art;
    private bool _reported, _shown;
    private int _captures;
    private PointerCalibrationRenderer.TextTransition? _title, _instruction, _progress;
    private readonly int _textWidth=Even(Math.Min(width*.76,height*1.45));
    internal event Action<Exception>? Failed;
    internal bool Render(OverlayCanvas output,(int Hint,float Progress) state,
        ControllerHologramScene? scene=null,bool animate=false,double elapsed=1d/60)
    {
        if(state.Hint is <1 or >10) { _captures=0; _shown=false; return false; }
        _art??=Task.Run(()=> {
            var bytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets","Overlays","Runtime","alignment-copy.rgba"));
            if(bytes.Length!=1024*1664*4) throw new InvalidDataException("Controller alignment artwork is missing. Reinstall VRCoplay.");
            var bitmap=new Bitmap(1024,1664,bytes);
            return Enumerable.Range(0,26).Select(i=>Sprite.Scale(bitmap.Crop(0,i*64,1024,64),_textWidth,Even(_textWidth/16d))).ToArray();
        });
        if(!_art.IsCompleted) return false;
        if(_art.IsFaulted)
        {
            if(!_reported) { _reported=true; Failed?.Invoke(_art.Exception!.GetBaseException()); }
            return false;
        }
        var art=_art.Result;
        _title??=new(Enumerable.Range(0,10).Select(i=>art[i*2]).ToArray(),fadeThrough:true);
        _instruction??=new(Enumerable.Range(0,10).Select(i=>art[i*2+1]).ToArray(),fadeThrough:true);
        _progress??=new(art[22..26]);
        output.Dim(163 / 255f);
        if(scene is not null) _captures=scene.Captures;
        bool complete=state.Hint==10,matching=state.Hint is 1 or 2 or 3 or 4;
        bool snap=!animate||!_shown; _shown=true;
        double dt=Math.Clamp(elapsed,0,.05);
        _title.Update(state.Hint-1,dt,snap);
        _instruction.Update(state.Hint-1,dt,snap);
        _progress.Update(matching?Math.Clamp(_captures,0,3):-1,dt,snap);
        Draw(output,_progress.Frame,-44);
        Draw(output,_title.Frame,0);
        Draw(output,_instruction.Frame,48);
        if(matching) HoldBar(output,state.Hint==3?Math.Clamp(state.Progress,0,1):0);
        if(!complete) { Draw(output,art[20],130); Draw(output,art[21],160); }
        return true;
    }
    private void Draw(OverlayCanvas output, Sprite sprite, double offset)
    {
        int x=Even((width-_textWidth)/2d),y=Even(height/2d+offset*_textWidth/1024d-sprite.Height/2d);
        if(y+sprite.Height>height || x+_textWidth>width) return;
        sprite.Draw(output,x,y,1);
    }
    private void HoldBar(OverlayCanvas output, float progress)
    {
        double unit=_textWidth/1024d;
        int w=Even(160*unit),h=Math.Max(2,Even(3*unit)),x=Even((width-w)/2d),y=Even(height/2d+86*unit);
        output.Fill(x,y,w,h,new Vortice.Mathematics.Color4(.14f,.16f,.24f,1));
        output.Fill(x,y,w*progress,h,new Vortice.Mathematics.Color4(.42f,.69f,.99f,1));
    }
    private static int Even(double value)=>Math.Max(0,(int)Math.Round(value/2)*2);
}
