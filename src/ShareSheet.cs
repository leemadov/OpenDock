using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using global::Windows.ApplicationModel.DataTransfer;
using global::Windows.Storage;

namespace OpenDock
{
    // Finder's Share button: Windows' own share sheet (Nearby sharing, Mail, Teams, WhatsApp...) for the selected
    // files, attached to the Finder window -- the same panel Explorer's Share opens.
    static class ShareSheet
    {
        [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDataTransferManagerInterop
        {
            IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
            void ShowShareUIForWindow(IntPtr appWindow);
        }
        static readonly Guid ManagerIid = new Guid("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");
        static readonly Dictionary<IntPtr, DataTransferManager> hooked = new Dictionary<IntPtr, DataTransferManager>();   // kept: only this holds them
        static readonly Dictionary<IntPtr, List<string>> pending = new Dictionary<IntPtr, List<string>>();

        public static bool Share(IntPtr window, List<string> paths)
        {
            if (window == IntPtr.Zero || paths == null || paths.Count == 0) return false;
            try
            {
                var interop = (IDataTransferManagerInterop)WindowsRuntimeMarshal.GetActivationFactory(typeof(DataTransferManager));
                var iid = ManagerIid;
                pending[window] = paths;
                if (!hooked.ContainsKey(window))
                {
                    var ptr = interop.GetForWindow(window, ref iid);
                    var manager = (DataTransferManager)Marshal.GetObjectForIUnknown(ptr);
                    Marshal.Release(ptr);
                    var w = window;
                    manager.DataRequested += (s, e) => Fill(w, e.Request);
                    hooked[window] = manager;
                }
                interop.ShowShareUIForWindow(window);
                return true;
            }
            catch (Exception ex) { App.Log(ex); return false; }
        }

        // (no await here: the build has no WinRT task helpers, so each file is fetched with a Completed callback)
        static void Fill(IntPtr window, DataRequest request)
        {
            List<string> paths;
            if (!pending.TryGetValue(window, out paths)) return;
            var deferral = request.GetDeferral();
            request.Data.Properties.Title = paths.Count == 1 ? System.IO.Path.GetFileName(paths[0].TrimEnd(System.IO.Path.DirectorySeparatorChar)) : paths.Count + " items";
            var items = new List<IStorageItem>();
            Action<int> next = null;
            next = i =>
            {
                try
                {
                    if (i >= paths.Count)
                    {
                        if (items.Count > 0) request.Data.SetStorageItems(items);
                        else request.FailWithDisplayText("These items can't be shared.");
                        deferral.Complete();
                        return;
                    }
                    if (System.IO.Directory.Exists(paths[i]))
                    {
                        var op = StorageFolder.GetFolderFromPathAsync(paths[i]);
                        op.Completed = (o, st) => { try { if (st == global::Windows.Foundation.AsyncStatus.Completed) items.Add(o.GetResults()); } catch (Exception ex) { App.Log(ex); } next(i + 1); };
                    }
                    else
                    {
                        var op = StorageFile.GetFileFromPathAsync(paths[i]);
                        op.Completed = (o, st) => { try { if (st == global::Windows.Foundation.AsyncStatus.Completed) items.Add(o.GetResults()); } catch (Exception ex) { App.Log(ex); } next(i + 1); };
                    }
                }
                catch (Exception ex) { App.Log(ex); next(i + 1); }
            };
            next(0);
        }
    }
}
