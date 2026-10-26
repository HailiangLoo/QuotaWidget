using System;
using System.IO;
using System.Media;

namespace QuotaWidget.App;

/// <summary>A short in-memory chime, independent of the selected Windows notification sound scheme.</summary>
public static class CapacitySound
{
    static readonly SoundPlayer Player = new(new MemoryStream(Wave()));
    public static bool Play()
    {
        try { Player.Load(); Player.Play(); return true; }
        catch { return false; }
    }

    public static byte[] Wave()
    {
        const int rate=22050, samples=rate*3/5;
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));writer.Write(36+samples*2);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));writer.Write(16);writer.Write((short)1);writer.Write((short)1);
        writer.Write(rate);writer.Write(rate*2);writer.Write((short)2);writer.Write((short)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));writer.Write(samples*2);
        for(var i=0;i<samples;i++)
        {
            var time=i/(double)rate;var t=time<.25?time:time-.35;
            var envelope=t<0||t>=.25?0:Math.Min(1,Math.Min(t/.015,(.25-t)/.05));
            writer.Write((short)(Math.Sin(2*Math.PI*(time<.25?880:660)*t)*envelope*5200));
        }
        return stream.ToArray();
    }
}
