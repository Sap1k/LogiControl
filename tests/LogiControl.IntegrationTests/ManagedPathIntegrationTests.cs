// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LogiControl.Broker;
using LogiControl.Hid;
using LogiControl.Protocol;

namespace LogiControl.IntegrationTests;

public sealed class ManagedPathIntegrationTests
{
    [Fact]
    public async Task ManagedLifecycleReadinessDrivesSemanticPlaybackAndRemovalInvalidatesHandles()
    {
        HidDeviceSnapshot native = Snapshot();
        var enumerator = new MutableEnumerator { Devices = [native] };
        var transport = new CapturingTransport(native);
        var clock = new QpcMonotonicClock();
        var coordinator = new BrokerSessionCoordinator(clock);
        using var output = new SwitchableForceFeedbackOutputSink();
        using var runtime = new EffectRuntime(coordinator, clock, output);
        runtime.Start();
        var manager = new BrokerDeviceManager(
            enumerator,
            new SilentNotifications(),
            new SingleTransportFactory(transport),
            new UnusedCalibrationMonitor(),
            coordinator,
            runtime,
            output,
            new BrokerDeviceManagerOptions(
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1)));
        var dispatcher = new BrokerRequestDispatcher(coordinator, runtime, () => manager.IsDeviceReady);
        try
        {
            await manager.ScanOnceAsync(TestContext.Current.CancellationToken);
            Assert.True(manager.IsDeviceReady);
            IpcFrame hello = dispatcher.Dispatch(Request(IpcMessageType.Hello, 1, 0, []));
            Assert.Equal(BrokerResult.Ok, Result(hello));
            byte[] binding = { 4, 0, (byte)'D', (byte)'F', (byte)'G', (byte)'T' };
            Assert.Equal(BrokerResult.Ok, Result(dispatcher.Dispatch(
                Request(IpcMessageType.BindDevice, 2, hello.Header.SessionId, binding))));

            var definition = new ConstantEffectDefinition(
                new EffectCommon(EffectCommon.InfiniteDuration, 0, 0, 10_000, 10_000, null), 2_000);
            byte[] encoded = new byte[EffectDefinitionCodec.GetEncodedLength(definition)];
            Assert.True(EffectDefinitionCodec.TryWrite(encoded, definition, out _));
            var upsertPayload = new byte[8 + encoded.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(upsertPayload.AsSpan(4), (ushort)EffectUpdateMask.All);
            encoded.CopyTo(upsertPayload, 8);
            IpcFrame upsert = dispatcher.Dispatch(
                Request(IpcMessageType.UpsertEffect, 3, hello.Header.SessionId, upsertPayload));
            Assert.Equal(BrokerResult.Ok, Result(upsert));
            uint handle = BinaryPrimitives.ReadUInt32LittleEndian(upsert.Payload.AsSpan(4));
            var start = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(start, handle);
            BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(8), 2);
            Assert.Equal(BrokerResult.Ok, Result(dispatcher.Dispatch(
                Request(IpcMessageType.StartEffect, 4, hello.Header.SessionId, start))));
            Assert.True(SpinWait.SpinUntil(
                () => transport.WriteReports.Any(static report => report[2] == 0x08 && report[3] > 0x80),
                TimeSpan.FromSeconds(2)));

            enumerator.Devices = [];
            await manager.ScanOnceAsync(TestContext.Current.CancellationToken);
            Assert.False(manager.IsDeviceReady);
            var handlePayload = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(handlePayload, handle);
            Assert.Equal(BrokerResult.NotFound, Result(dispatcher.Dispatch(
                Request(IpcMessageType.QueryEffect, 5, hello.Header.SessionId, handlePayload))));
            Assert.Equal(BrokerResult.DeviceNotReady, Result(dispatcher.Dispatch(
                Request(IpcMessageType.StartEffect, 6, hello.Header.SessionId, start))));
            Assert.Equal((byte)0xF3, transport.WriteReports.Last()[1]);
        }
        finally
        {
            dispatcher.CloseAfterTransportLoss();
        }
    }

    private static IpcFrame Request(IpcMessageType type, ulong request, ulong session, byte[] payload) =>
        new(new IpcFrameHeader(1, 0, type, IpcFrameFlags.None, (uint)payload.Length, request, session), payload);

    private static BrokerResult Result(IpcFrame response) =>
        (BrokerResult)BinaryPrimitives.ReadInt32LittleEndian(response.Payload);

    private static HidDeviceSnapshot Snapshot() => new(
        "fake-native",
        "HID\\VID_046D&PID_C29A\\integration",
        0x046D,
        0xC29A,
        0x1326,
        1,
        4,
        9,
        8,
        132,
        Guid.NewGuid(),
        ["PCIROOT(0)#USBROOT(0)#USB(2)"]);

    private sealed class MutableEnumerator : IHidDeviceEnumerator
    {
        public IReadOnlyList<HidDeviceSnapshot> Devices { get; set; } = [];

        public ValueTask<IReadOnlyList<HidDeviceSnapshot>> EnumerateAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Devices);
        }
    }

    private sealed class SilentNotifications : IHidNotificationSource
    {
        public async IAsyncEnumerable<HidDeviceChange> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class UnusedCalibrationMonitor : IHidCalibrationMonitor
    {
        public ValueTask<SteeringCalibrationObservation> WaitForCompletionAsync(
            HidDeviceSnapshot device,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("An already-native device must not be calibration-gated again.");
    }

    private sealed class SingleTransportFactory(CapturingTransport transport) : IHidTransportFactory
    {
        private int opens;

        public IHidTransport OpenForOutput(HidDeviceSnapshot device)
        {
            Assert.Equal(1, Interlocked.Increment(ref opens));
            Assert.Equal(transport.Device, device);
            return transport;
        }
    }

    private sealed class CapturingTransport(HidDeviceSnapshot device) : IHidTransport
    {
        public HidDeviceSnapshot Device { get; } = device;
        public ConcurrentQueue<byte[]> WriteReports { get; } = new();

        public ValueTask SetOutputReportAsync(
            ReadOnlyMemory<byte> report,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Native output must not use HidD_SetOutputReport.");

        public ValueTask WriteOutputReportAsync(
            ReadOnlyMemory<byte> report,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteReports.Enqueue(report.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
