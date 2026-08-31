// SPDX-License-Identifier: GPL-3.0-or-later

using LogiControl.Protocol;

namespace LogiControl.Broker.Tests;

public sealed class BrokerControlClientTests
{
    [Fact]
    public async Task ControlClientRoundTripsStatusSettingsTelemetryAndEmergencyStop()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var clock = new QpcMonotonicClock();
        var coordinator = new BrokerSessionCoordinator(clock);
        using var runtime = new EffectRuntime(coordinator, clock, new NullForceFeedbackOutputSink());
        runtime.Start();
        var server = new SemanticPipeServer(coordinator, runtime, deviceReady: true);
        Task serverTask = server.RunAsync(cancellation.Token);
        try
        {
            await using var client = new BrokerControlClient();
            await client.ConnectAsync(TestContext.Current.CancellationToken);
            BrokerControlStatus status = await client.QueryStatusAsync(TestContext.Current.CancellationToken);
            Assert.True(status.DeviceReady);
            Assert.False(status.HasActiveEffects);
            Assert.Equal(1, status.SessionCount);

            RuntimeSettings settings = await client.QueryRuntimeSettingsAsync(TestContext.Current.CancellationToken);
            RuntimeSettings updated = settings with { RangeDegrees = 540, IdleAutocenter = 1_000 };
            await client.SetRuntimeSettingsAsync(updated, TestContext.Current.CancellationToken);
            Assert.Equal(updated, await client.QueryRuntimeSettingsAsync(TestContext.Current.CancellationToken));

            BrokerTelemetryStatus telemetry = await client.QueryTelemetryAsync(TestContext.Current.CancellationToken);
            Assert.True(telemetry.Commands > 0);
            await client.EmergencyStopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
