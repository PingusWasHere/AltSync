using System.Runtime.InteropServices;
namespace AltSync.Core;
public sealed record ResourceLoad(long AvailableMb,double CpuPercent);
public static class SystemResources
{
    private static ulong previousIdle,previousTotal;
    private static readonly object sync=new();
    public static ResourceLoad Sample()
    {
        var memory=new Memory{Length=(uint)Marshal.SizeOf<Memory>()};long available=4096;
        if(GlobalMemoryStatusEx(ref memory))available=(long)(memory.AvailablePhysical/1048576);
        double cpu=0;
        if(GetSystemTimes(out ulong idle,out ulong kernel,out ulong user))lock(sync)
        {ulong total=kernel+user;if(previousTotal>0&&total>previousTotal)cpu=Math.Clamp(100d*(1d-(double)(idle-previousIdle)/(total-previousTotal)),0,100);previousIdle=idle;previousTotal=total;}
        return new(available,Math.Round(cpu,1));
    }
    [StructLayout(LayoutKind.Sequential)] private struct Memory{public uint Length,Load;public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtended;}
    [DllImport("kernel32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GlobalMemoryStatusEx(ref Memory data);
    [DllImport("kernel32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetSystemTimes(out ulong idle,out ulong kernel,out ulong user);
}
