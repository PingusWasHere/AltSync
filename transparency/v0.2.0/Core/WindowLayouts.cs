using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AltSync.Core;
public static class WindowLayouts
{
    public static WindowPlacement Clamp(WindowPlacement position,int x,int y,int width,int height)
    {
        int w=Math.Clamp(position.Width,320,Math.Max(320,width)),h=Math.Clamp(position.Height,240,Math.Max(240,height));
        return position with{X=Math.Clamp(position.X,x,x+Math.Max(0,width-w)),Y=Math.Clamp(position.Y,y,y+Math.Max(0,height-h)),Width=w,Height=h};
    }
    public static WindowPlacement[] Arrange(SessionMonitor monitor,int columns,nint ownerWindow)
    {
        if(columns is <1 or >4)throw new ArgumentException("Choose between one and four columns.");
        var sessions=monitor.Snapshot().Where(s=>s.AccountId is not null).ToArray();if(sessions.Length==0)return [];
        var work=WorkArea(ownerWindow);columns=Math.Min(columns,sessions.Length);int rows=(sessions.Length+columns-1)/columns;
        int width=Math.Max(320,(work.Right-work.Left)/columns),height=Math.Max(240,(work.Bottom-work.Top)/rows);var result=new List<WindowPlacement>();
        for(int i=0;i<sessions.Length;i++)
        {
            using var process=monitor.Owned(sessions[i].Pid);if(process.MainWindowHandle==0)continue;
            var position=Clamp(new(sessions[i].AccountId!.Value,work.Left+i%columns*width,work.Top+i/columns*height,width,height),work.Left,work.Top,work.Right-work.Left,work.Bottom-work.Top);
            Apply(process.MainWindowHandle,position);result.Add(position);
        }
        return result.ToArray();
    }
    public static WindowPlacement[] Capture(SessionMonitor monitor)
    {
        var result=new List<WindowPlacement>();
        foreach(var session in monitor.Snapshot().Where(s=>s.AccountId is not null))
        {
            using var process=monitor.Owned(session.Pid);if(process.MainWindowHandle==0||!GetWindowRect(process.MainWindowHandle,out var rect))continue;
            if(rect.Right-rect.Left>=320&&rect.Bottom-rect.Top>=240)result.Add(new(session.AccountId!.Value,rect.Left,rect.Top,rect.Right-rect.Left,rect.Bottom-rect.Top));
        }
        return result.ToArray();
    }
    public static void Focus(SessionMonitor monitor,int pid)
    {using var process=monitor.Owned(pid);if(process.MainWindowHandle==0)throw new InvalidOperationException("This Roblox window is not ready yet.");ShowWindow(process.MainWindowHandle,9);SetForegroundWindow(process.MainWindowHandle);}
    public static async Task Restore(SessionMonitor monitor,int pid,WindowPlacement position,CancellationToken token)
    {
        for(int i=0;i<40;i++)
        {
            token.ThrowIfCancellationRequested();using var process=monitor.Owned(pid);process.Refresh();
            if(process.MainWindowHandle!=0){var area=WorkArea(process.MainWindowHandle);Apply(process.MainWindowHandle,Clamp(position,area.Left,area.Top,area.Right-area.Left,area.Bottom-area.Top));return;}
            await Task.Delay(250,token);
        }
    }
    private static Rect WorkArea(nint window)
    {var info=new MonitorInfo{Size=(uint)Marshal.SizeOf<MonitorInfo>()};if(!GetMonitorInfo(MonitorFromWindow(window,2),ref info))throw new InvalidOperationException("Windows could not read the display area.");return info.Work;}
    private static void Apply(nint window,WindowPlacement p)
    {ShowWindow(window,9);if(!SetWindowPos(window,0,p.X,p.Y,p.Width,p.Height,0x0004|0x0010|0x4000))throw new InvalidOperationException("Windows could not arrange this Roblox window.");}
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct MonitorInfo{public uint Size;public Rect Monitor,Work;public uint Flags;}
    [DllImport("user32.dll")]private static extern nint MonitorFromWindow(nint window,uint flags);
    [DllImport("user32.dll",EntryPoint="GetMonitorInfoW")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetMonitorInfo(nint monitor,ref MonitorInfo info);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetWindowRect(nint window,out Rect rect);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetWindowPos(nint window,nint after,int x,int y,int w,int h,uint flags);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool ShowWindow(nint window,int command);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetForegroundWindow(nint window);
}
