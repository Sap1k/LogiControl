// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Pipes;
using LogiControl.Protocol;

namespace LogiControl.Broker;

public sealed class SemanticPipeServer
{
    private readonly BrokerSessionCoordinator coordinator;
    private readonly EffectRuntime runtime;
    private readonly Func<bool> deviceReady;

    public SemanticPipeServer(BrokerSessionCoordinator coordinator, EffectRuntime runtime, bool deviceReady = false)
        : this(coordinator, runtime, () => deviceReady)
    {
    }

    public SemanticPipeServer(BrokerSessionCoordinator coordinator, EffectRuntime runtime, Func<bool> deviceReady)
    {
        this.coordinator = coordinator;
        this.runtime = runtime;
        this.deviceReady = deviceReady ?? throw new ArgumentNullException(nameof(deviceReady));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var connections = new HashSet<Task>();
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                BrokerConstants.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                IpcFrameCodec.MaximumFrameLength,
                IpcFrameCodec.MaximumFrameLength);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            Task connection = HandleConnectionAsync(pipe, cancellationToken);
            connections.Add(connection);
            connections.RemoveWhere(static task => task.IsCompleted);
        }

        await Task.WhenAll(connections).ConfigureAwait(false);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        var dispatcher = new BrokerRequestDispatcher(coordinator, runtime, deviceReady);
        try
        {
            while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                IpcFrame? request = await IpcFrameStream.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    break;
                }

                IpcFrame response = dispatcher.Dispatch(request.Value);
                await IpcFrameStream.WriteAsync(pipe, response.Header, response.Payload, cancellationToken).ConfigureAwait(false);
                if (request.Value.Header.MessageType == IpcMessageType.CloseSession)
                {
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or OperationCanceledException)
        {
        }
        finally
        {
            dispatcher.CloseAfterTransportLoss();
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }
}
