using System.Runtime.InteropServices;

namespace FamilyDnsLock.Service;

internal sealed class WindowsServiceDispatcher
{
    private const int ServiceWin32OwnProcess = 0x10;
    private const int ServiceStartPending = 0x2;
    private const int ServiceRunning = 0x4;
    private const int ServiceStopPending = 0x3;
    private const int ServiceStopped = 0x1;
    private const int ServiceAcceptStop = 0x1;
    private const int ControlStop = 0x1;
    private const int NoError = 0;

    private readonly string _serviceName;
    private readonly Func<CancellationToken, Task> _worker;
    private readonly ServiceMainCallback _serviceMainCallback;
    private readonly HandlerCallback _handlerCallback;
    private readonly CancellationTokenSource _cancellation = new();
    private IntPtr _statusHandle;

    private WindowsServiceDispatcher(string serviceName, Func<CancellationToken, Task> worker)
    {
        _serviceName = serviceName;
        _worker = worker;
        _serviceMainCallback = ServiceMain;
        _handlerCallback = HandleControl;
    }

    public static void Run(string serviceName, Func<CancellationToken, Task> worker)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The enforcement service runs only on Windows.");
        }

        var dispatcher = new WindowsServiceDispatcher(serviceName, worker);
        var table = new[]
        {
            new ServiceTableEntry { Name = serviceName, Main = dispatcher._serviceMainCallback },
            new ServiceTableEntry { Name = null, Main = null }
        };
        if (!StartServiceCtrlDispatcher(table))
        {
            throw new InvalidOperationException($"StartServiceCtrlDispatcher failed: {Marshal.GetLastWin32Error()}.");
        }
    }

    private void ServiceMain(int argumentCount, IntPtr arguments)
    {
        _statusHandle = RegisterServiceCtrlHandler(_serviceName, _handlerCallback);
        if (_statusHandle == IntPtr.Zero)
        {
            return;
        }

        SetStatus(ServiceStartPending, 0);
        SetStatus(ServiceRunning, ServiceAcceptStop);
        try
        {
            _worker(_cancellation.Token).GetAwaiter().GetResult();
            SetStatus(ServiceStopped, 0);
        }
        catch
        {
            SetStatus(ServiceStopped, 0, errorCode: 1);
        }
    }

    private int HandleControl(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        if (control == ControlStop)
        {
            SetStatus(ServiceStopPending, 0);
            _cancellation.Cancel();
            return NoError;
        }

        return NoError;
    }

    private void SetStatus(int currentState, int acceptedControls, int errorCode = 0)
    {
        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = currentState,
            ControlsAccepted = acceptedControls,
            Win32ExitCode = errorCode,
            ServiceSpecificExitCode = 0,
            CheckPoint = 0,
            WaitHint = currentState is ServiceStartPending or ServiceStopPending ? 10_000 : 0
        };
        SetServiceStatus(_statusHandle, ref status);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        public ServiceMainCallback? Main;
    }

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

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainCallback(int argumentCount, IntPtr arguments);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int HandlerCallback(int control, int eventType, IntPtr eventData, IntPtr context);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] serviceTable);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandler(string serviceName, HandlerCallback handler);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr serviceStatusHandle, ref ServiceStatus serviceStatus);
}