using System.IO;
using System.Runtime.InteropServices;
using QuotaWidget.App;

static class StartupProbe
{
    [DllImport("shell32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);

    public static void Run()
    {
        foreach(var (exe,root) in new[]{(@"C:\工具 space\QuotaWidget.exe",@"D:\历史 data"),(@"C:\QuotaWidget.exe",@"D:\"),(@"C:\QuotaWidget.exe",@"D:\history\"),(@"\\server\share\QuotaWidget.exe",@"\\server\share\data")})
        {
            var command=WindowsStartup.Command(exe,root);var pointer=CommandLineToArgvW(command,out var count);
            if(pointer==IntPtr.Zero)throw new Exception("Cannot parse startup command");
            try
            {
                var actual=Enumerable.Range(0,count).Select(i=>Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer,i*IntPtr.Size))).ToArray();
                if(!actual.SequenceEqual(new[]{Path.GetFullPath(exe),"--data-dir",Path.GetFullPath(root)}))
                    throw new Exception("Startup command changed executable/data directory or added hidden flags");
            }
            finally{LocalFree(pointer);}
        }
        bool rejected=false;
        try{WindowsStartup.Command(@"C:\"+new string('a',240)+".exe",@"D:\history");}catch(IOException){rejected=true;}
        if(!rejected)throw new Exception("Windows Run-key length limit ignored");
        Console.WriteLine("PASS: Windows parses startup commands with Unicode, spaces, trailing separators and UNC paths; same data directory, visible start and 260-character limit. No startup entry modified.");
    }
}
