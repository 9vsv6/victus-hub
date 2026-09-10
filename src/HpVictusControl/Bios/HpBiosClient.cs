using System.Management;

namespace HpVictusControl.Bios;

/// <summary>
/// Thermal / performance profile as understood by the HP BIOS WMI interface.
/// Values match what HP's own Omen/Victus control software sends.
/// </summary>
public enum HpFanMode : byte {
    Balanced = 0x30,
    Performance = 0x31,
    Cool = 0x50,
}

public sealed class HpBiosException : Exception {
    public HpBiosException(string message) : base(message) { }
    public HpBiosException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Talks to the HP BIOS WMI provider (root\wmi, class hpqBIntM/hpqBDataIn) that HP's own
/// Omen/Victus control software uses to read sensors and adjust fan behavior. The interface
/// itself is not publicly documented by HP; the command layout here follows values observed
/// and documented by the community (e.g. the OmenMon project) reverse-engineering it.
/// </summary>
public sealed class HpBiosClient : IDisposable {

    private const string WmiNamespace = "root\\wmi";
    private const string MethodClassName = "hpqBIntM";
    private const string DataClassName = "hpqBDataIn";
    private const string DataFieldName = "hpqBData";
    private const string MethodNamePrefix = "hpqBIOSInt";
    private const string ReturnCodeFieldName = "rwReturnCode";

    // Shared secret the BIOS provider expects on every call ("SECU" in ASCII).
    private static readonly byte[] Signature = { 0x53, 0x45, 0x43, 0x55 };

    // Command identifier for most sensor/fan/performance commands.
    private const uint CmdDefault = 0x20008;

    private ManagementScope? _scope;
    private ManagementObject? _methodObject;
    private ManagementClass? _dataClass;

    public bool IsAvailable { get; private set; }

    /// <summary>
    /// Connects to the WMI provider. Safe to call even on non-HP hardware: sets
    /// <see cref="IsAvailable"/> to false instead of throwing when the interface isn't present.
    /// </summary>
    public void Connect() {
        try {
            _scope = new ManagementScope(WmiNamespace);
            _scope.Connect();

            _dataClass = new ManagementClass(_scope, new ManagementPath(DataClassName), null);

            using var searcher = new ManagementObjectSearcher(_scope, new ObjectQuery($"SELECT * FROM {MethodClassName}"));
            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results) {
                _methodObject = (ManagementObject)item;
                break;
            }

            IsAvailable = _methodObject != null;
        } catch {
            IsAvailable = false;
        }
    }

    public void Dispose() {
        _methodObject?.Dispose();
        _dataClass?.Dispose();
    }

    private void EnsureAvailable() {
        if (!IsAvailable || _methodObject == null || _dataClass == null)
            throw new HpBiosException("The HP BIOS WMI interface is not available on this device.");
    }

    /// <summary>
    /// Sends one command to the BIOS and returns its status code (0 = success).
    /// <paramref name="outSize"/> must be one of the sizes the provider exposes a method for: 0, 4, 128, 1024, 4096.
    /// </summary>
    private int Send(uint commandType, byte[] inData, byte outSize, out byte[] outData) {
        EnsureAvailable();

        outData = Array.Empty<byte>();

        try {
            using ManagementBaseObject inParams = _methodObject!.GetMethodParameters(MethodNamePrefix + outSize);
            using ManagementObject dataIn = _dataClass!.CreateInstance();

            dataIn["Sign"] = Signature;
            dataIn["Command"] = CmdDefault;
            dataIn["CommandType"] = commandType;
            dataIn["Size"] = (uint)inData.Length;
            if (inData.Length > 0)
                dataIn[DataFieldName] = inData;

            inParams["InData"] = dataIn;

            using ManagementBaseObject result = _methodObject.InvokeMethod(MethodNamePrefix + outSize, inParams, null);
            using var outObj = (ManagementBaseObject)result["OutData"];

            if (outSize != 0)
                outData = (byte[])outObj["Data"];

            return Convert.ToInt32(outObj[ReturnCodeFieldName]);
        } catch (HpBiosException) {
            throw;
        } catch (Exception ex) {
            throw new HpBiosException("Failed to invoke the HP BIOS WMI method.", ex);
        }
    }

    private static void Check(int returnCode) {
        if (returnCode == 0)
            return;

        throw new HpBiosException(returnCode switch {
            3 => "This command is not supported on this device (BIOS status 3).",
            5 => "Insufficient buffer size for this command (BIOS status 5).",
            _ => $"The HP BIOS reported an error (status {returnCode})."
        });
    }

    // ----- Thermal / fan queries -----------------------------------------------------------

    /// <summary>Number of fans reported by the BIOS (typically 1 or 2 on Victus laptops).</summary>
    public byte GetFanCount() {
        int rc = Send(0x10, new byte[4], 4, out byte[] outData);
        Check(rc);
        return outData[0];
    }

    /// <summary>Current fan levels. Unit is roughly hundreds of RPM (e.g. 45 ~= 4500 RPM); treat as approximate.</summary>
    public (byte Cpu, byte Gpu) GetFanLevels() {
        int rc = Send(0x2D, new byte[4], 128, out byte[] outData);
        Check(rc);
        return (outData[0], outData[1]);
    }

    /// <summary>
    /// Directly requests a fan level for each fan. This is a best-effort BIOS call: on some
    /// models the BIOS's own automatic control loop will override it again shortly after.
    /// </summary>
    public void SetFanLevels(byte cpuLevel, byte gpuLevel) {
        int rc = Send(0x2E, new byte[] { cpuLevel, gpuLevel, 0x00, 0x00 }, 0, out _);
        Check(rc);
    }

    /// <summary>Hands fan control back to the BIOS's automatic curve.</summary>
    public void ReleaseManualFanControl() => SetFanLevels(0, 0);

    public bool GetMaxFanSpeed() {
        int rc = Send(0x26, new byte[4], 4, out byte[] outData);
        Check(rc);
        return (outData[0] & 0x01) != 0;
    }

    public void SetMaxFanSpeed(bool enabled) {
        int rc = Send(0x27, new byte[] { (byte)(enabled ? 1 : 0), 0x00, 0x00, 0x00 }, 0, out _);
        Check(rc);
    }

    public void SetFanMode(HpFanMode mode) {
        int rc = Send(0x1A, new byte[] { 0xFF, (byte)mode, 0x00, 0x00 }, 0, out _);
        Check(rc);
    }

    /// <summary>Single BIOS-reported thermal sensor value, in degrees Celsius.</summary>
    public byte GetTemperature() {
        int rc = Send(0x23, new byte[] { 0x01, 0x00, 0x00, 0x00 }, 4, out byte[] outData);
        Check(rc);
        return outData[0];
    }
}
