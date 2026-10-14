using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace DisplayLullaby.SessionRestore;

/// <summary>
/// Launches a packaged app through IApplicationActivationManager so arguments (e.g. a file path) reach it.
/// shell:AppsFolder cannot pass arguments.
/// </summary>
internal static partial class PackagedActivation
{
    private static readonly Guid ClsidApplicationActivationManager = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");
    private static readonly Guid IidApplicationActivationManager = new("2E941141-7F97-4756-BA1D-9DECDE894A3D");
    private const uint ClsctxLocalServer = 0x4;

    public static bool TryActivate(string appUserModelId, string arguments)
    {
        try
        {
            var clsid = ClsidApplicationActivationManager;
            var iid = IidApplicationActivationManager;
            if (CoCreateInstance(ref clsid, 0, ClsctxLocalServer, ref iid, out var pointer) != 0 || pointer == 0)
            {
                return false;
            }

            try
            {
                var manager = (IApplicationActivationManager)ComWrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
                return manager.ActivateApplication(appUserModelId, arguments, 0, out _) >= 0;
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static readonly StrategyBasedComWrappers ComWrappers = new();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);

    [GeneratedComInterface]
    [Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
    internal partial interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            int options,
            out uint processId);

        [PreserveSig]
        int ActivateForFile(nint appUserModelId, nint itemArray, nint verb, out uint processId);

        [PreserveSig]
        int ActivateForProtocol(nint appUserModelId, nint itemArray, out uint processId);
    }
}
