using System;
using System.Runtime.InteropServices;
using System.Threading;

// ============================================================================
// ScmHost — minimal Service Control Manager binding, hand-rolled P/Invoke so
// the project keeps its zero-NuGet rule (same precedent as the ICS COM code).
// StartServiceCtrlDispatcher connects the process to the SCM; ServiceMain
// reports status and blocks until a STOP/SHUTDOWN/PRESHUTDOWN control arrives.
// ============================================================================
internal static class ScmHost
{
    private const int OwnProcess = 0x10;
    private const int Stopped = 1, StartPending = 2, StopPending = 3, Running = 4;
    private const int AcceptStop = 0x1, AcceptShutdown = 0x4, AcceptPreshutdown = 0x100;
    private const int ControlStop = 0x1, ControlInterrogate = 0x4, ControlShutdown = 0x5, ControlPreshutdown = 0xF;
    private const int ErrorCallNotImplemented = 120;
    public const int ErrorFailedServiceControllerConnect = 1063;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        public string? ServiceName;
        public ServiceMainFunction? ServiceProc;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainFunction(int argc, IntPtr argv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int HandlerFunctionEx(int control, int eventType, IntPtr eventData, IntPtr context);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool StartServiceCtrlDispatcherW(ServiceTableEntry[] table);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerExW(string serviceName, HandlerFunctionEx handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr statusHandle, ref ServiceStatus status);

    // Delegates must stay alive for the lifetime of the process; the SCM holds
    // raw function pointers to them.
    private static ServiceMainFunction? _main;
    private static HandlerFunctionEx? _handler;
    private static IntPtr _statusHandle;
    private static ServiceStatus _status;
    private static ManualResetEvent? _stopEvent;
    private static Action? _onStart;
    private static Action? _onStop;
    private static string _serviceName = "";
    private static int _checkpoint;

    // Blocks until the service stops. Returns the process exit code.
    public static int Run(string serviceName, Action onStart, Action onStop)
    {
        _serviceName = serviceName;
        _onStart = onStart;
        _onStop = onStop;
        _stopEvent = new ManualResetEvent(false);
        _main = ServiceMain;
        _handler = HandlerEx;
        var table = new[]
        {
            new ServiceTableEntry { ServiceName = serviceName, ServiceProc = _main },
            new ServiceTableEntry { ServiceName = null, ServiceProc = null }, // table terminator
        };
        if (!StartServiceCtrlDispatcherW(table))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorFailedServiceControllerConnect)
                Log.Error("--service must be launched by the Windows Service Control Manager, not from a terminal. Run 'Anything install' first.");
            else
                Log.Error($"StartServiceCtrlDispatcher failed (Win32 {error}).");
            return 1;
        }
        return 0;
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _statusHandle = RegisterServiceCtrlHandlerExW(_serviceName, _handler!, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
        {
            Log.Error($"RegisterServiceCtrlHandlerEx failed (Win32 {Marshal.GetLastWin32Error()}).");
            return;
        }
        _status.ServiceType = OwnProcess;
        SetStatus(StartPending, 0, waitHintMs: 30000);
        try
        {
            _onStart!();
            SetStatus(Running, AcceptStop | AcceptShutdown | AcceptPreshutdown);
        }
        catch (Exception ex)
        {
            Log.Error("Service initialization failed: " + ex);
            SetStatus(Stopped, 0, exitCode: 1);
            return;
        }
        _stopEvent!.WaitOne();
        SetStatus(StopPending, 0, waitHintMs: 90000);
        try { _onStop!(); }
        catch (Exception ex) { Log.Error("Service shutdown error: " + ex); }
        SetStatus(Stopped, 0);
    }

    // Runs on an SCM control thread, not the ServiceMain thread.
    private static int HandlerEx(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case ControlInterrogate:
                SetStatus(_status.CurrentState, _status.ControlsAccepted);
                return 0;
            case ControlStop:
            case ControlShutdown:
            case ControlPreshutdown:
                SetStatus(StopPending, 0, waitHintMs: 90000);
                _stopEvent!.Set();
                return 0;
            default:
                return ErrorCallNotImplemented;
        }
    }

    private static void SetStatus(int state, int accept, int waitHintMs = 0, int exitCode = 0)
    {
        _status.CurrentState = state;
        _status.ControlsAccepted = accept;
        _status.Win32ExitCode = exitCode;
        _status.CheckPoint = state is StartPending or StopPending ? ++_checkpoint : 0;
        _status.WaitHint = waitHintMs;
        SetServiceStatus(_statusHandle, ref _status);
    }
}