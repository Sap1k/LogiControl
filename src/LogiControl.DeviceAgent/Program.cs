// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using LogiControl.DeviceAgent;
using LogiControl.Hid;

return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] arguments)
{
    string command = arguments.FirstOrDefault()?.ToLowerInvariant() ?? "help";
    try
    {
        return command switch
        {
            "list" => await ListAsync(arguments.Contains("--json", StringComparer.OrdinalIgnoreCase)).ConfigureAwait(false),
            "run" => await RunAgentAsync(arguments.Contains("--observe-only", StringComparer.OrdinalIgnoreCase)).ConfigureAwait(false),
            "status" => await BrokerCommandAsync(emergencyStop: false).ConfigureAwait(false),
            "emergency-stop" => await BrokerCommandAsync(emergencyStop: true).ConfigureAwait(false),
            _ => PrintUsage(),
        };
    }
    catch (OperationCanceledException)
    {
        return 0;
    }
    catch (Exception exception)
    {
        StructuredLog.Error("fatal", exception);
        return 1;
    }
}

static async Task<int> ListAsync(bool json)
{
    var enumerator = new WindowsHidDeviceEnumerator();
    IReadOnlyList<HidDeviceSnapshot> devices = await enumerator.EnumerateAsync().ConfigureAwait(false);
    HidDeviceSnapshot[] logitech = devices.Where(device => device.VendorId == 0x046D).ToArray();
    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(logitech, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }));
    }
    else
    {
        foreach (HidDeviceSnapshot device in logitech)
        {
            Console.WriteLine(
                $"{device.VendorId:X4}:{device.ProductId:X4} rev {device.VersionNumber:X4} " +
                $"usage {device.UsagePage:X4}:{device.Usage:X4} " +
                $"reports in={device.InputReportByteLength} out={device.OutputReportByteLength} feature={device.FeatureReportByteLength}");
            Console.WriteLine($"  {device.InstanceId}");
            Console.WriteLine($"  container={device.ContainerId} location={string.Join(" | ", device.EffectiveLocationPaths)}");
            Console.WriteLine($"  driver={device.DriverProvider} {device.DriverInfPath} service={device.DriverService}");
            Console.WriteLine($"  path={device.DevicePath}");
        }
    }

    return logitech.Length > 0 ? 0 : 2;
}

static async Task<int> RunAgentAsync(bool observeOnly)
{
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    await using var broker = new LegacyBrokerControlClient();
    var manager = new WheelSessionManager(
        new WindowsHidDeviceEnumerator(),
        new WindowsHidNotificationSource(),
        new WindowsHidTransportFactory(),
        new WindowsHidCalibrationMonitor(),
        broker,
        observeOnly);
    StructuredLog.Write("agent-start", new { ObserveOnly = observeOnly });
    await manager.RunAsync(cancellation.Token).ConfigureAwait(false);
    return 0;
}

static async Task<int> BrokerCommandAsync(bool emergencyStop)
{
    await using var broker = new LegacyBrokerControlClient();
    LegacyBrokerStatus status = emergencyStop
        ? await broker.EmergencyStopAsync().ConfigureAwait(false)
        : await broker.GetStatusAsync().ConfigureAwait(false);
    Console.WriteLine(JsonSerializer.Serialize(status, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    }));
    return 0;
}

static int PrintUsage()
{
    Console.Error.WriteLine(
        "usage: LogiControl.DeviceAgent <list [--json] | run [--observe-only] | status | emergency-stop>");
    return 64;
}
