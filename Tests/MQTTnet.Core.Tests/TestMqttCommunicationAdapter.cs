﻿using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet.Adapter;
using MQTTnet.Packets;
using MQTTnet.Serializer;

namespace MQTTnet.Core.Tests
{
    public class TestMqttCommunicationAdapter : IMqttChannelAdapter
    {
        private readonly BlockingCollection<MqttBasePacket> _incomingPackets = new BlockingCollection<MqttBasePacket>();

        public TestMqttCommunicationAdapter Partner { get; set; }

        public string Endpoint { get; }

        public IMqttPacketSerializer PacketSerializer { get; } = new MqttPacketSerializer();

        public event EventHandler ReadingPacketStarted;
        public event EventHandler ReadingPacketCompleted;

        public void Dispose()
        {
        }

        public Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }

        public Task DisconnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }

        public Task SendPacketAsync(MqttBasePacket packet, CancellationToken cancellationToken)
        {
            ThrowIfPartnerIsNull();

            Partner.EnqueuePacketInternal(packet);

            return Task.FromResult(0);
        }

        public async Task<MqttBasePacket> ReceivePacketAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            ThrowIfPartnerIsNull();

            if (timeout > TimeSpan.Zero)
            {
                using (var timeoutCts = new CancellationTokenSource(timeout))
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken))
                {
                    return await Task.Run(() =>
                    {
                        try
                        {
                            return _incomingPackets.Take(cts.Token);
                        }
                        catch
                        {
                            return null;
                        }
                    }, cts.Token);
                }
            }

            return await Task.Run(() =>
            {
                try
                {
                    return _incomingPackets.Take(cancellationToken);
                }
                catch
                {
                    return null;
                }
            }, cancellationToken);
        }

        private void EnqueuePacketInternal(MqttBasePacket packet)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));

            _incomingPackets.Add(packet);
        }

        private void ThrowIfPartnerIsNull()
        {
            if (Partner == null)
            {
                throw new InvalidOperationException("Partner is not set.");
            }
        }
<<<<<<< HEAD
=======

        public void SendPackets(TimeSpan timeout, IEnumerable<MqttBasePacket> packets)
        {
            throw new NotImplementedException();
        }

        public MqttBasePacket ReceivePacket(TimeSpan timeout)
        {
            throw new NotImplementedException();
        }

        public void Connect(TimeSpan timeout)
        {
            throw new NotImplementedException();
        }

        public void Disconnect(TimeSpan timeout)
        {
            throw new NotImplementedException();
        }
>>>>>>> parent of f035b1a... Fix sync code to accommodate the changes in interfaces from batch to single message arguments.
    }
}
