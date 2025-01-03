﻿using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static UsbipdGui.Usbipd;

namespace UsbipdGui
{
    partial class Usbipd
    {
        public class UsbDevice
        {
            public enum ConnectionStates
            {
                None = 0b_0000,
                Connected = 0b_0001,
                Shared = 0b_0010,
                Attached = 0b_0100,
                Disconnected = None,
                DisconnectedPersisted = Shared,
                ConnectedNotShared = Connected,
                ConnectedShared = Connected | Shared,
                ConnectedAttached = Connected | Shared | Attached,
                Unknown = 0b_1111,
            }

            public UsbDevice(string? busId, string? clientIpAddr, string? description, string? vid, string? pid, bool isForced, string? persistedGuid, string? stubInstanceId)
            {
                BusId = busId;
                ClientIpAddr = clientIpAddr;
                Description = description;
                Vid = vid;
                Pid = pid;
                IsForced = isForced;
                PersistedGuid = persistedGuid;
                StubInstanceId = stubInstanceId;
                State = (!String.IsNullOrWhiteSpace(BusId) ? ConnectionStates.Connected : ConnectionStates.None)
                    | (!String.IsNullOrWhiteSpace(PersistedGuid) ? ConnectionStates.Shared : ConnectionStates.None)
                    | (!String.IsNullOrWhiteSpace(ClientIpAddr) ? ConnectionStates.Attached : ConnectionStates.None);
            }

            public UsbDevice(string? vid, string? pid)
            {
                Vid = vid;
                Pid = pid;
                State = ConnectionStates.Disconnected;
            }

            public UsbDevice(string? description, string? vid, string? pid)
            {
                Description = description;
                Vid = vid;
                Pid = pid;
                State = ConnectionStates.Disconnected;
            }

            public ConnectionStates State { get; init; }
            public string? BusId { get; init; }
            public string? ClientIpAddr { get; init; }
            public string? Description { get; init; }
            public string? Vid { get; init; }
            public string? Pid { get; init; }
            public bool IsForced { get; init; }
            public string? PersistedGuid { get; init; }
            public string? StubInstanceId { get; init; }

            public override string ToString()
            {
                return $"BusId:{BusId} Desc:{Description} State:{State} ClientIpAddr:{ClientIpAddr} VID/PID:{Vid}/{Pid} IsForced:{IsForced} PersistedGuid:{PersistedGuid} StubInstanceId:{StubInstanceId}";
            }
        }

        public readonly string Version;

        public static Usbipd? BuildUsbIpdCommnad()
        {
            if (!IsExistsUsbipdCommand())
            {
                return null;
            }
            string? version = ExecuteCommand("usbipd", "--version");
            if (version is null)
            {
                return null;
            }
            return new Usbipd(version);
        }

        private Usbipd(in string version) {
            Version = version;
        }

        public List<UsbDevice> GetUsbDevices()
        {
            List<UsbDevice> usbDevices = [];

            System.Text.Json.JsonDocument? json = GetUsbIpdListAsJson();
            if (json is null)
            {
                return usbDevices;
            }

            foreach (JsonElement device in json.RootElement.GetProperty("Devices").EnumerateArray())
            {
                (string vid, string pid) = ExtractUsbIds(device.GetProperty("InstanceId").GetString());
                usbDevices.Add(new UsbDevice(
                    device.GetProperty("BusId").GetString(),
                    device.GetProperty("ClientIPAddress").GetString(),
                    device.GetProperty("Description").GetString(),
                    vid, pid,
                    device.GetProperty("IsForced").GetBoolean(),
                    device.GetProperty("PersistedGuid").GetString(),
                    device.GetProperty("StubInstanceId").GetString()));
            }

            return usbDevices.OrderBy(dev => Single.Parse((dev.BusId ?? "0").Replace('-', '.'))).ToList();
        }

        public bool Bind(ref UsbDevice device)
        {
            Debug.WriteLine($"bind -f -b {device.BusId}");
            if (ExecuteCommand("usbipd", $"bind -f -b {device.BusId}") is null)
            {
                return false;
            }

            List<UsbDevice> updatedDevices = GetUsbDevices();
            if (updatedDevices.Count == 0)
            {
                return false;
            }
            foreach (UsbDevice udev in updatedDevices)
            {
                if (device.BusId == udev.BusId)
                {
                    if ((udev.State & UsbDevice.ConnectionStates.Shared) == UsbDevice.ConnectionStates.Shared)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public bool Unbind(ref UsbDevice device)
        {
            if (ExecuteCommand("usbipd", $"unbind -b {device.BusId}") is null)
            {
                return false;
            }

            List<UsbDevice> updatedDevices = GetUsbDevices();
            if (updatedDevices.Count == 0)
            {
                return false;
            }
            foreach (UsbDevice udev in updatedDevices)
            {
                if (device.BusId == udev.BusId)
                {
                    if ((udev.State & UsbDevice.ConnectionStates.Shared) == UsbDevice.ConnectionStates.None)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public bool AttachToWSL(ref UsbDevice device)
        {
            if (ExecuteCommand("usbipd", $"attach --wsl -b {device.BusId}") is null)
            {
                return false;
            }
            List<UsbDevice> updatedDevices = GetUsbDevices();
            if (updatedDevices.Count == 0)
            {
                return false;
            }
            foreach (UsbDevice udev in updatedDevices)
            {
                if (device.BusId == udev.BusId)
                {
                    if ((udev.State & UsbDevice.ConnectionStates.Attached) == UsbDevice.ConnectionStates.Attached)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public bool DetachFromWSL(ref UsbDevice device)
        {
            if (ExecuteCommand("usbipd", $"detach -b {device.BusId}") is null)
            {
                return false;
            }
            List<UsbDevice> updatedDevices = GetUsbDevices();
            if (updatedDevices.Count == 0)
            {
                return false;
            }
            foreach (UsbDevice udev in updatedDevices)
            {
                if (device.BusId == udev.BusId)
                {
                    if ((udev.State & UsbDevice.ConnectionStates.Shared) == UsbDevice.ConnectionStates.Shared)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static string? ExecuteCommand(string command, string args)
        {
            try
            {
                using Process proc = new();
                proc.StartInfo.FileName = command;
                proc.StartInfo.Arguments = args;
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.CreateNoWindow = true;
                proc.StartInfo.RedirectStandardOutput = true;
                proc.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                proc.StartInfo.RedirectStandardError = true;
                proc.Start();
                string result = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                return result;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsExistsUsbipdCommand()
        {
            return !string.IsNullOrWhiteSpace(ExecuteCommand("where", "usbipd"));
        }

        private System.Text.Json.JsonDocument? GetUsbIpdListAsJson()
        {
            string? jsonString = ExecuteCommand("usbipd", "state");
            if (string.IsNullOrWhiteSpace(jsonString))
            {
                return null;
            }

            try
            {
                System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(jsonString);
                return json;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Regex target examples:
        // - "InstanceId: USB\VID_8087 & PID_0025\7 & 2E104BF0 & 0 & 2"
        // - "InstanceId: USB\VID_0403 & PID_6001\A901O7VP"
        [GeneratedRegex(@"USB\\VID_(\w{4})&PID_(\w{4})")]
        private static partial Regex InstanceIdRegex();

        private static (string vid, string pid) ExtractUsbIds(string? instanceIdString)
        {
            if (string.IsNullOrWhiteSpace(instanceIdString))
            {
                return ("-", "-");
            }
            Regex regex = InstanceIdRegex();

            Match match = regex.Match(instanceIdString);
            if (!match.Success)
            {
                return ("-", "-");
            }
            return (match.Groups[1].Value, match.Groups[2].Value);
        }

    }

    class UsbIdEqualityComparer : IEqualityComparer<UsbDevice>
    {
        public bool Equals(UsbDevice? a, UsbDevice? b)
        {
            if (a is null || b is null)
            {
                return false;
            }
            return (a.Vid == b.Vid) && (a.Pid == b.Pid);
        }

        public int GetHashCode(UsbDevice dev) => (dev.Vid?.GetHashCode() ?? 0) ^ (dev.Pid?.GetHashCode() ?? 0);
    }
}
