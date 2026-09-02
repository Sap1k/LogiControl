// SPDX-License-Identifier: GPL-3.0-or-later

using LogiControl.Broker;
using LogiControl.Hid;

if (args.FirstOrDefault()?.Equals("profile-runtime", StringComparison.OrdinalIgnoreCase) == true)
{
    return await RuntimeProfileCommand.RunAsync(args[1..]);
}

string? command = args.FirstOrDefault()?.ToLowerInvariant();
if (command is "list" or "devices" or "select" or "status" or "telemetry" or "settings" or "emergency-stop")
{
    return await BrokerCliCommand.RunAsync(args);
}

string[] serverArguments = command == "serve" ? args[1..] : args;

bool fakeHid = serverArguments.Contains("--fake-hid", StringComparer.OrdinalIgnoreCase);
bool profile = serverArguments.Contains("--profile", StringComparer.OrdinalIgnoreCase);
if (serverArguments.Any(argument => !argument.Equals("--fake-hid", StringComparison.OrdinalIgnoreCase) &&
    !argument.Equals("--profile", StringComparison.OrdinalIgnoreCase)))
{
    Console.Error.WriteLine(
        "Usage: LogiControl.Broker [serve] [--fake-hid] [--profile] | " +
        "list [--json] | devices | select <device-id|auto> | status | telemetry | " +
        "settings [options] | emergency-stop | " +
        "profile-runtime [--seconds N] [--runs N] [--stress]");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var clock = new QpcMonotonicClock();
var coordinator = new BrokerSessionCoordinator(clock);
IForceFeedbackOutputSink output = fakeHid
    ? new NullForceFeedbackOutputSink()
    : new SwitchableForceFeedbackOutputSink();
using var runtime = new EffectRuntime(coordinator, clock, output, profileEvents: profile);
runtime.Start();
BrokerDeviceManager? deviceManager = null;
if (!fakeHid)
{
    deviceManager = new BrokerDeviceManager(
        new WindowsHidDeviceEnumerator(),
        new WindowsHidNotificationSource(),
        new WindowsHidTransportFactory(),
        new WindowsHidCalibrationMonitor(),
        coordinator,
        runtime,
        (SwitchableForceFeedbackOutputSink)output,
        profileEvents: profile);
}

var server = fakeHid
    ? new SemanticPipeServer(coordinator, runtime, deviceReady: true)
    : new SemanticPipeServer(coordinator, runtime, deviceManager!);

Console.WriteLine($"LogiControl Broker listening on {BrokerConstants.PipeName}.");
Console.WriteLine(fakeHid
    ? "Fake-HID acceptance is enabled; no physical HID transport will be opened."
    : "No wheel is attached; effect starts remain disabled until broker device ownership is ready.");
if (profile)
{
    Console.WriteLine("Per-event EventSource profiling is enabled; aggregate histograms are always active.");
}

Task serverTask = server.RunAsync(cancellation.Token);
Task deviceTask = fakeHid
    ? Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token)
    : deviceManager!.RunAsync(cancellation.Token);
try
{
    Task completed = await Task.WhenAny(serverTask, deviceTask);
    await completed;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
}
finally
{
    cancellation.Cancel();
    try
    {
        await Task.WhenAll(serverTask, deviceTask);
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
    }
}

return 0;
