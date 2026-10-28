using System;
using System.Runtime.InteropServices;
using NLog;

namespace XboxGamingBarHelper.Labs
{
    /// <summary>
    /// Software unplug/replug of the Legion controller's USB composite: a USB port power-cycle
    /// of the VID_17EF&amp;PID_61EB composite via IOCTL_USB_HUB_CYCLE_PORT. LegionButtonMonitor
    /// escalates to it when the composite latches a bad USB state and plain reconnects keep
    /// failing (only a real port power-cycle or a reboot clears that).
    ///
    /// Why the port-cycle and not Disable/pnputil restart: the composite contains a HID keyboard
    /// child (a boot/system device) that vetoes any PnP disable/remove, so those all defer to a
    /// reboot. IOCTL_USB_HUB_CYCLE_PORT tells the parent hub to power-cycle the physical port —
    /// a hardware surprise-remove + re-arrival, exactly like pulling and reinserting the
    /// connector — which bypasses that veto.
    ///
    /// (This used to also back the opt-in Labs "Fix Task View Bug", which ran it once per boot;
    /// that option was removed.)
    /// </summary>
    internal static class LegionUsbPortCycler
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private const string LegionCompositePrefix = @"USB\VID_17EF&PID_61EB\";

        // ---------------------------------------------------------------------------------------
        // The actual work: find the Legion composite -> its parent hub + port -> IOCTL cycle port.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Simulate a physical unplug/replug of the Legion controller composite via a USB port
        /// power-cycle. Returns true on a successful IOCTL. Safe no-op (returns false) if the
        /// controller isn't present or the OS refuses the cycle.
        /// </summary>
        public static bool CyclePort()
        {
            try
            {
                string compositeId = FindLegionComposite();
                if (compositeId == null)
                {
                    Logger.Info("PortCycle: Legion composite (USB\\VID_17EF&PID_61EB\\...) not present — nothing to cycle.");
                    return false;
                }

                if (CM_Locate_DevNodeW(out uint devInst, compositeId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS)
                { Logger.Warn("PortCycle: CM_Locate_DevNode failed."); return false; }

                if (CM_Get_Parent(out uint hubInst, devInst, 0) != CR_SUCCESS)
                { Logger.Warn("PortCycle: CM_Get_Parent failed."); return false; }

                string hubId = GetDeviceId(hubInst);
                if (!GetPortNumber(devInst, out uint port))
                { Logger.Warn("PortCycle: could not read the port number (DEVPKEY_Device_Address)."); return false; }

                string hubPath = GetHubInterfacePath(hubId);
                if (hubPath == null)
                { Logger.Warn("PortCycle: could not resolve the parent hub's device interface path."); return false; }

                Logger.Info($"PortCycle: cycling port {port} on hub '{hubId}' (composite '{compositeId}').");

                IntPtr h = CreateFileW(hubPath, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == IntPtr.Zero || h == new IntPtr(-1))
                { Logger.Warn($"PortCycle: CreateFile(hub) failed, err={Marshal.GetLastWin32Error()}."); return false; }

                try
                {
                    var p = new USB_CYCLE_PORT_PARAMS { ConnectionIndex = port, StatusReturned = 0 };
                    uint size = (uint)Marshal.SizeOf(typeof(USB_CYCLE_PORT_PARAMS));
                    bool ok = DeviceIoControl(h, IOCTL_USB_HUB_CYCLE_PORT, ref p, size, ref p, size, out uint _, IntPtr.Zero);
                    if (!ok)
                    {
                        Logger.Warn($"PortCycle: IOCTL_USB_HUB_CYCLE_PORT failed, err={Marshal.GetLastWin32Error()} (StatusReturned={p.StatusReturned}).");
                        return false;
                    }
                    Logger.Info($"PortCycle: port cycled OK (StatusReturned={p.StatusReturned}). Controllers re-enumerating.");
                    return true;
                }
                finally { CloseHandle(h); }
            }
            catch (Exception ex)
            {
                Logger.Error($"PortCycle: CyclePort threw: {ex.Message}");
                return false;
            }
        }

        private static string FindLegionComposite()
        {
            foreach (string id in GetPresentDeviceIds())
            {
                if (id.StartsWith(LegionCompositePrefix, StringComparison.OrdinalIgnoreCase)
                    && id.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) < 0
                    && id.IndexOf("&COL", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return id;
                }
            }
            return null;
        }

        private static string[] GetPresentDeviceIds()
        {
            if (CM_Get_Device_ID_List_SizeW(out uint len, null, CM_GETIDLIST_FILTER_PRESENT) != CR_SUCCESS || len == 0)
                return Array.Empty<string>();
            char[] buf = new char[len];
            if (CM_Get_Device_ID_ListW(null, buf, len, CM_GETIDLIST_FILTER_PRESENT) != CR_SUCCESS)
                return Array.Empty<string>();
            return new string(buf).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string GetDeviceId(uint devInst)
        {
            if (CM_Get_Device_ID_Size(out uint len, devInst, 0) != CR_SUCCESS) return null;
            char[] buf = new char[len + 1];
            if (CM_Get_Device_IDW(devInst, buf, (uint)buf.Length, 0) != CR_SUCCESS) return null;
            return new string(buf).TrimEnd('\0');
        }

        private static bool GetPortNumber(uint devInst, out uint port)
        {
            port = 0;
            uint size = 4;
            byte[] buf = new byte[4];
            if (CM_Get_DevNode_PropertyW(devInst, ref DEVPKEY_Device_Address, out _, buf, ref size, 0) != CR_SUCCESS)
                return false;
            port = BitConverter.ToUInt32(buf, 0);
            return true;
        }

        private static string GetHubInterfacePath(string hubInstanceId)
        {
            if (hubInstanceId == null) return null;
            if (CM_Get_Device_Interface_List_SizeW(out uint len, ref GUID_DEVINTERFACE_USB_HUB, hubInstanceId, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS || len == 0)
                return null;
            char[] buf = new char[len];
            if (CM_Get_Device_Interface_ListW(ref GUID_DEVINTERFACE_USB_HUB, hubInstanceId, buf, len, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS)
                return null;
            string[] parts = new string(buf).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : null;
        }

        // ---- P/Invoke: CfgMgr32 ----
        private const uint CR_SUCCESS = 0;
        private const uint CM_LOCATE_DEVNODE_NORMAL = 0;
        private const uint CM_GETIDLIST_FILTER_PRESENT = 0x00000100;
        private const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

        // DEVPKEY_Device_Address = {a45c254e-df1c-4efd-8020-67d146a850e0}, 30 → USB port number on the hub.
        private static DEVPROPKEY DEVPKEY_Device_Address = new DEVPROPKEY
        { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 30 };

        // GUID_DEVINTERFACE_USB_HUB
        private static Guid GUID_DEVINTERFACE_USB_HUB = new Guid("f18a0e88-c30c-11d0-8815-00a0c906bed8");

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_ID_List_SizeW(out uint len, string filter, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_ID_ListW(string filter, char[] buffer, uint bufferLen, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_Parent(out uint parentInst, uint devInst, uint flags);
        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_Device_ID_Size(out uint len, uint devInst, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_IDW(uint devInst, char[] buffer, uint bufferLen, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint propType, byte[] buffer, ref uint size, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid guid, string deviceId, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_Interface_ListW(ref Guid guid, string deviceId, char[] buffer, uint bufferLen, uint flags);

        // ---- P/Invoke: kernel32 ----
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
        private const uint OPEN_EXISTING = 3;
        private const uint IOCTL_USB_HUB_CYCLE_PORT = 0x220444;

        [StructLayout(LayoutKind.Sequential)]
        private struct USB_CYCLE_PORT_PARAMS { public uint ConnectionIndex; public uint StatusReturned; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr h, uint code, ref USB_CYCLE_PORT_PARAMS inBuf, uint inSize, ref USB_CYCLE_PORT_PARAMS outBuf, uint outSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);
    }
}
