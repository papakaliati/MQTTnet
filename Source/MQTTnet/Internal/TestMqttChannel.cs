﻿using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet.Channel;

namespace MQTTnet.Internal
{
    public class TestMqttChannel : IMqttChannel
    {
        private readonly MemoryStream _stream;

        public TestMqttChannel(MemoryStream stream)
        {
            _stream = stream;
        }

        public void Dispose()
        {
        }

        public string Endpoint { get; }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }

        public void Connect()
        {
        }

        public Task DisconnectAsync()
        {
            return Task.FromResult(0);
        }

        public void Disconnect()
        {
        }

        public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return _stream.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return _stream.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            return _stream.Read(buffer, offset, count);
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            _stream.Write(buffer, offset, count);
        }
    }
}