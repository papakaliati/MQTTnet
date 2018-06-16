﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet.Adapter;
using MQTTnet.Client;
using MQTTnet.Diagnostics;
using MQTTnet.Exceptions;
using MQTTnet.Internal;
using MQTTnet.Packets;
using MQTTnet.Protocol;

namespace MQTTnet.Server
{
    public class MqttClientSession : IDisposable
    {
        private readonly MqttPacketIdentifierProvider _packetIdentifierProvider = new MqttPacketIdentifierProvider();

        private readonly MqttRetainedMessagesManager _retainedMessagesManager;
        private readonly MqttClientKeepAliveMonitor _keepAliveMonitor;
        private readonly MqttClientPendingMessagesQueue _pendingMessagesQueue;
        private readonly MqttClientSubscriptionsManager _subscriptionsManager;
        private readonly MqttClientSessionsManager _sessionsManager;

        private readonly IMqttNetChildLogger _logger;
        private readonly IMqttServerOptions _options;

        private CancellationTokenSource _cancellationTokenSource;
        private MqttApplicationMessage _willMessage;
        private bool _wasCleanDisconnect;
        private IMqttChannelAdapter _adapter;

        public MqttClientSession(
            string clientId,
            IMqttServerOptions options,
            MqttClientSessionsManager sessionsManager,
            MqttRetainedMessagesManager retainedMessagesManager,
            IMqttNetChildLogger logger)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            _options = options ?? throw new ArgumentNullException(nameof(options));
            _sessionsManager = sessionsManager;
            _retainedMessagesManager = retainedMessagesManager ?? throw new ArgumentNullException(nameof(retainedMessagesManager));

            ClientId = clientId;

            _logger = logger.CreateChildLogger(nameof(MqttClientSession));

            _keepAliveMonitor = new MqttClientKeepAliveMonitor(clientId, () => Stop(MqttClientDisconnectType.NotClean), _logger);
            _subscriptionsManager = new MqttClientSubscriptionsManager(clientId, _options, sessionsManager.Server);
            _pendingMessagesQueue = new MqttClientPendingMessagesQueue(_options, this, _logger);
        }

        public string ClientId { get; }

        public void FillStatus(MqttClientSessionStatus status)
        {
            status.ClientId = ClientId;
            status.IsConnected = _adapter != null;
            status.Endpoint = _adapter?.Endpoint;
            status.ProtocolVersion = _adapter?.PacketSerializer?.ProtocolVersion;
            status.PendingApplicationMessagesCount = _pendingMessagesQueue.Count;
            status.LastPacketReceived = _keepAliveMonitor.LastPacketReceived;
            status.LastNonKeepAlivePacketReceived = _keepAliveMonitor.LastNonKeepAlivePacketReceived;
        }

        public void NewTopicAdded(string topic)
        {
            _subscriptionsManager.NewTopicAdded(topic);
        }

        public async Task<bool> RunAsync(MqttConnectPacket connectPacket, IMqttChannelAdapter adapter)
        {
            if (connectPacket == null) throw new ArgumentNullException(nameof(connectPacket));
            if (adapter == null) throw new ArgumentNullException(nameof(adapter));

            try
            {
                _adapter = adapter;
                adapter.ReadingPacketStarted += OnAdapterReadingPacketStarted;
                adapter.ReadingPacketCompleted += OnAdapterReadingPacketCompleted;

                _cancellationTokenSource = new CancellationTokenSource();
                _wasCleanDisconnect = false;
                _willMessage = connectPacket.WillMessage;

                _pendingMessagesQueue.Start(adapter, _cancellationTokenSource.Token);
                _keepAliveMonitor.Start(connectPacket.KeepAlivePeriod, _cancellationTokenSource.Token);

                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    var packet = await adapter.ReceivePacketAsync(TimeSpan.Zero, _cancellationTokenSource.Token).ConfigureAwait(false);
                    if (packet != null)
                    {
                        _keepAliveMonitor.PacketReceived(packet);
                        await ProcessReceivedPacketAsync(adapter, packet, _cancellationTokenSource.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                if (exception is MqttCommunicationException)
                {
                    if (exception is MqttCommunicationClosedGracefullyException)
                    {
                        _logger.Verbose("Client '{0}': Connection closed gracefully.", ClientId); ;
                    }
                    else
                    {
                        _logger.Warning(exception, "Client '{0}': Communication exception while receiving client packets.", ClientId);
                    }
                }
                else
                {
                    _logger.Error(exception, "Client '{0}': Unhandled exception while receiving client packets.", ClientId);
                }
                
                Stop(MqttClientDisconnectType.NotClean);
            }
            finally
            {
                if (_adapter != null)
                {
                    _adapter.ReadingPacketStarted -= OnAdapterReadingPacketStarted;
                    _adapter.ReadingPacketCompleted -= OnAdapterReadingPacketCompleted;
                }
                
                _adapter = null;

                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }

            return _wasCleanDisconnect;
        }

        public bool Run(MqttConnectPacket connectPacket, IMqttChannelAdapter adapter)
        {
            if (connectPacket == null) throw new ArgumentNullException(nameof(connectPacket));
            if (adapter == null) throw new ArgumentNullException(nameof(adapter));

            try
            {
                _adapter = adapter;
                adapter.ReadingPacketStarted += OnAdapterReadingPacketStarted;
                adapter.ReadingPacketCompleted += OnAdapterReadingPacketCompleted;

                _cancellationTokenSource = new CancellationTokenSource();
                _wasCleanDisconnect = false;
                _willMessage = connectPacket.WillMessage;

                _pendingMessagesQueue.IsSync = true;
                _pendingMessagesQueue.Start(adapter, _cancellationTokenSource.Token);
                _keepAliveMonitor.Start(connectPacket.KeepAlivePeriod, _cancellationTokenSource.Token);

                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    var packet = adapter.ReceivePacket(TimeSpan.Zero);
                    if (packet != null)
                    {
                        _keepAliveMonitor.PacketReceived(packet);
                        ProcessReceivedPacket(adapter, packet);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                if (exception is MqttCommunicationException)
                {
                    if (exception is MqttCommunicationClosedGracefullyException)
                    {
                        _logger.Verbose("Client '{0}': Connection closed gracefully.", ClientId); ;
                    }
                    else
                    {
                        _logger.Warning(exception, "Client '{0}': Communication exception while receiving client packets.", ClientId);
                    }
                }
                else
                {
                    _logger.Error(exception, "Client '{0}': Unhandled exception while receiving client packets.", ClientId);
                }

                Stop(MqttClientDisconnectType.NotClean);
            }
            finally
            {
                if (_adapter != null)
                {
                    _adapter.ReadingPacketStarted -= OnAdapterReadingPacketStarted;
                    _adapter.ReadingPacketCompleted -= OnAdapterReadingPacketCompleted;
                }

                _adapter = null;

                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }

            return _wasCleanDisconnect;
        }

        public void Stop(MqttClientDisconnectType type)
        {
            try
            {
                var cts = _cancellationTokenSource;
                if (cts == null || cts.IsCancellationRequested)
                {
                    return;
                }

                _wasCleanDisconnect = type == MqttClientDisconnectType.Clean;

                _cancellationTokenSource?.Cancel(false);

                if (_willMessage != null && !_wasCleanDisconnect)
                {
                    _sessionsManager.StartDispatchApplicationMessage(this, _willMessage);
                }

                _willMessage = null;

                ////_pendingMessagesQueue.WaitForCompletion();
                ////_keepAliveMonitor.WaitForCompletion();
            }
            finally
            {
                _logger.Info("Client '{0}': Session stopped.", ClientId);
            }
        }

        public void EnqueueApplicationMessage(MqttClientSession senderClientSession, MqttApplicationMessage applicationMessage)
        {
            if (applicationMessage == null) throw new ArgumentNullException(nameof(applicationMessage));

            var checkSubscriptionsResult = _subscriptionsManager.CheckSubscriptions(applicationMessage);
            if (!checkSubscriptionsResult.IsSubscribed)
            {
                return;
            }

            var publishPacket = applicationMessage.ToPublishPacket();
            publishPacket.QualityOfServiceLevel = checkSubscriptionsResult.QualityOfServiceLevel;

            if (publishPacket.QualityOfServiceLevel > 0)
            {
                publishPacket.PacketIdentifier = _packetIdentifierProvider.GetNewPacketIdentifier();
            }

            if (_options.ClientMessageQueueInterceptor != null)
            {
                var context = new MqttClientMessageQueueInterceptorContext(
                    senderClientSession?.ClientId,
                    ClientId,
                    publishPacket.ToApplicationMessage());
               
                _options.ClientMessageQueueInterceptor?.Invoke(context);

                if (!context.AcceptEnqueue || context.ApplicationMessage == null)
                {
                    return;
                }
            }
            
            _pendingMessagesQueue.Enqueue(publishPacket);
        }

        public Task SubscribeAsync(IList<TopicFilter> topicFilters)
        {
            if (topicFilters == null) throw new ArgumentNullException(nameof(topicFilters));

            _subscriptionsManager.Subscribe(new MqttSubscribePacket
            {
                TopicFilters = topicFilters
            });

            EnqueueSubscribedRetainedMessages(topicFilters);
            return Task.FromResult(0);
        }

        public void Subscribe(IList<TopicFilter> topicFilters)
        {
            if (topicFilters == null) throw new ArgumentNullException(nameof(topicFilters));

            _subscriptionsManager.Subscribe(new MqttSubscribePacket
            {
                TopicFilters = topicFilters
            });

            EnqueueSubscribedRetainedMessages(topicFilters);
        }

        public Task UnsubscribeAsync(IList<string> topicFilters)
        {
            if (topicFilters == null) throw new ArgumentNullException(nameof(topicFilters));

            _subscriptionsManager.Unsubscribe(new MqttUnsubscribePacket
            {
                TopicFilters = topicFilters
            });

            return Task.FromResult(0);
        }

        public void Unsubscribe(IList<string> topicFilters)
        {
            if (topicFilters == null) throw new ArgumentNullException(nameof(topicFilters));

            _subscriptionsManager.Unsubscribe(new MqttUnsubscribePacket
            {
                TopicFilters = topicFilters
            });
        }

        public void ClearPendingApplicationMessages()
        {
            _pendingMessagesQueue.Clear();
        }

        public void Dispose()
        {
            _pendingMessagesQueue?.Dispose();

            _cancellationTokenSource?.Dispose();
        }

        private Task ProcessReceivedPacketAsync(IMqttChannelAdapter adapter, MqttBasePacket packet, CancellationToken cancellationToken)
        {
            if (packet is MqttPublishPacket publishPacket)
            {
                return HandleIncomingPublishPacketAsync(adapter, publishPacket, cancellationToken);
            }

            if (packet is MqttPingReqPacket)
            {
                return adapter.SendPacketAsync(_options.DefaultCommunicationTimeout, new MqttPingRespPacket(), cancellationToken);
            }

            if (packet is MqttPubRelPacket pubRelPacket)
            {
                return HandleIncomingPubRelPacketAsync(adapter, pubRelPacket, cancellationToken);
            }

            if (packet is MqttPubRecPacket pubRecPacket)
            {
                var responsePacket = new MqttPubRelPacket
                {
                    PacketIdentifier = pubRecPacket.PacketIdentifier
                };

                return adapter.SendPacketAsync(_options.DefaultCommunicationTimeout, responsePacket, cancellationToken);
            }

            if (packet is MqttPubAckPacket || packet is MqttPubCompPacket)
            {
                // Discard message.
                return Task.FromResult(0);
            }

            if (packet is MqttSubscribePacket subscribePacket)
            {
                return HandleIncomingSubscribePacketAsync(adapter, subscribePacket, cancellationToken);
            }

            if (packet is MqttUnsubscribePacket unsubscribePacket)
            {
                return HandleIncomingUnsubscribePacketAsync(adapter, unsubscribePacket, cancellationToken);
            }

            if (packet is MqttDisconnectPacket)
            {
                Stop(MqttClientDisconnectType.Clean);
                return Task.FromResult(0);
            }

            if (packet is MqttConnectPacket)
            {
                Stop(MqttClientDisconnectType.NotClean);
                return Task.FromResult(0);
            }

            _logger.Warning(null, "Client '{0}': Received not supported packet ({1}). Closing connection.", ClientId, packet);
            Stop(MqttClientDisconnectType.NotClean);
            return Task.FromResult(0);
        }

        private void ProcessReceivedPacket(IMqttChannelAdapter adapter, MqttBasePacket packet)
        {
            if (packet is MqttPublishPacket publishPacket)
            {
                HandleIncomingPublishPacket(adapter, publishPacket);
            }

            else if (packet is MqttPingReqPacket)
            {
                adapter.SendPacket(_options.DefaultCommunicationTimeout, new MqttPingRespPacket());
            }

            else if (packet is MqttPubRelPacket pubRelPacket)
            {
                HandleIncomingPubRelPacket(adapter, pubRelPacket);
            }

            else if (packet is MqttPubRecPacket pubRecPacket)
            {
                var responsePacket = new MqttPubRelPacket
                {
                    PacketIdentifier = pubRecPacket.PacketIdentifier
                };

                adapter.SendPacket(_options.DefaultCommunicationTimeout, responsePacket);
            }

            else if (packet is MqttPubAckPacket || packet is MqttPubCompPacket)
            {
                // Discard message.
            }

            else if (packet is MqttSubscribePacket subscribePacket)
            {
                HandleIncomingSubscribePacket(adapter, subscribePacket);
            }

            else if (packet is MqttUnsubscribePacket unsubscribePacket)
            {
                HandleIncomingUnsubscribePacket(adapter, unsubscribePacket);
            }

            else if (packet is MqttDisconnectPacket)
            {
                Stop(MqttClientDisconnectType.Clean);
            }

            else if (packet is MqttConnectPacket)
            {
                Stop(MqttClientDisconnectType.NotClean);
            }

            else
            {
                _logger.Warning(null, "Client '{0}': Received not supported packet ({1}). Closing connection.", ClientId, packet);
                Stop(MqttClientDisconnectType.NotClean);
            }
        }

        private void EnqueueSubscribedRetainedMessages(ICollection<TopicFilter> topicFilters)
        {
            var retainedMessages = _retainedMessagesManager.GetSubscribedMessages(topicFilters);
            foreach (var applicationMessage in retainedMessages)
            {
                EnqueueApplicationMessage(null, applicationMessage);
            }
        }

        private async Task HandleIncomingSubscribePacketAsync(IMqttChannelAdapter adapter, MqttSubscribePacket subscribePacket, CancellationToken cancellationToken)
        {
            var subscribeResult = _subscriptionsManager.Subscribe(subscribePacket);
            await adapter.SendPacketAsync(_options.DefaultCommunicationTimeout, subscribeResult.ResponsePacket, cancellationToken).ConfigureAwait(false);

            if (subscribeResult.CloseConnection)
            {
                Stop(MqttClientDisconnectType.NotClean);
                return;
            }

            EnqueueSubscribedRetainedMessages(subscribePacket.TopicFilters);
        }

        private void HandleIncomingSubscribePacket(IMqttChannelAdapter adapter, MqttSubscribePacket subscribePacket)
        {
            var subscribeResult = _subscriptionsManager.Subscribe(subscribePacket);
            adapter.SendPacket(_options.DefaultCommunicationTimeout, subscribeResult.ResponsePacket);

            if (subscribeResult.CloseConnection)
            {
                Stop(MqttClientDisconnectType.NotClean);
                return;
            }

            EnqueueSubscribedRetainedMessages(subscribePacket.TopicFilters);
        }

        private Task HandleIncomingUnsubscribePacketAsync(IMqttChannelAdapter adapter, MqttUnsubscribePacket unsubscribePacket, CancellationToken cancellationToken)
        {
            var unsubscribeResult = _subscriptionsManager.Unsubscribe(unsubscribePacket);
            return adapter.SendPacketAsync(_options.DefaultCommunicationTimeout, unsubscribeResult, cancellationToken);
        }

        private void HandleIncomingUnsubscribePacket(IMqttChannelAdapter adapter, MqttUnsubscribePacket unsubscribePacket)
        {
            var unsubscribeResult = _subscriptionsManager.Unsubscribe(unsubscribePacket);
            adapter.SendPacket(_options.DefaultCommunicationTimeout, unsubscribeResult);
        }

        private Task HandleIncomingPublishPacketAsync(IMqttChannelAdapter adapter, MqttPublishPacket publishPacket, CancellationToken cancellationToken)
        {
            var applicationMessage = publishPacket.ToApplicationMessage();

            switch (applicationMessage.QualityOfServiceLevel)
            {
                case MqttQualityOfServiceLevel.AtMostOnce:
                    {
                        _sessionsManager.StartDispatchApplicationMessage(this, applicationMessage);
                        return Task.FromResult(0);
                    }
                case MqttQualityOfServiceLevel.AtLeastOnce:
                    {
                        return HandleIncomingPublishPacketWithQoS1(adapter, applicationMessage, publishPacket, cancellationToken);
                    }
                case MqttQualityOfServiceLevel.ExactlyOnce:
                    {
                        return HandleIncomingPublishPacketWithQoS2(adapter, applicationMessage, publishPacket, cancellationToken);
                    }
                default:
                    {
                        throw new MqttCommunicationException("Received a not supported QoS level.");
                    }
            }
        }

        private void HandleIncomingPublishPacket(IMqttChannelAdapter adapter, MqttPublishPacket publishPacket)
        {
            var applicationMessage = publishPacket.ToApplicationMessage();

            switch (applicationMessage.QualityOfServiceLevel)
            {
                case MqttQualityOfServiceLevel.AtMostOnce:
                    {
                        _sessionsManager.DispatchApplicationMessage(this, applicationMessage);
                    }
                    break;
                case MqttQualityOfServiceLevel.AtLeastOnce:
                    {
                        HandleIncomingPublishPacketWithQoS1Sync(adapter, applicationMessage, publishPacket);
                    }
                    break;
                case MqttQualityOfServiceLevel.ExactlyOnce:
                    {
                        HandleIncomingPublishPacketWithQoS2Sync(adapter, applicationMessage, publishPacket);
                    }
                    break;
                default:
                    {
                        throw new MqttCommunicationException("Received a not supported QoS level.");
                    }
            }
        }

        private Task HandleIncomingPublishPacketWithQoS1(IMqttChannelAdapter adapter, MqttApplicationMessage applicationMessage, MqttPublishPacket publishPacket, CancellationToken cancellationToken)
        {
            _sessionsManager.StartDispatchApplicationMessage(this, applicationMessage);

            var response = new MqttPubAckPacket { PacketIdentifier = publishPacket.PacketIdentifier };
            return adapter.SendPacketAsync(_options.DefaultCommunicationTimeout, response, cancellationToken);
        }

        private void HandleIncomingPublishPacketWithQoS1Sync(IMqttChannelAdapter adapter, MqttApplicationMessage applicationMessage, MqttPublishPacket publishPacket)
        {
            _sessionsManager.DispatchApplicationMessage(this, applicationMessage);

            var response = new MqttPubAckPacket { PacketIdentifier = publishPacket.PacketIdentifier };
            adapter.SendPacket(_options.DefaultCommunicationTimeout, response);
        }

        private Task HandleIncomingPublishPacketWithQoS2(IMqttChannelAdapter adapter, MqttApplicationMessage applicationMessage, MqttPublishPacket publishPacket, CancellationToken cancellationToken)
        {
            // QoS 2 is implement as method "B" (4.3.3 QoS 2: Exactly once delivery)
            _sessionsManager.StartDispatchApplicationMessage(this, applicationMessage);

            var response = new MqttPubRecPacket { PacketIdentifier = publishPacket.PacketIdentifier };
            return adapter.SendPacketAsync(_options.DefaultCommunicationTimeout, response, cancellationToken);
        }

        private void HandleIncomingPublishPacketWithQoS2Sync(IMqttChannelAdapter adapter, MqttApplicationMessage applicationMessage, MqttPublishPacket publishPacket)
        {
            // QoS 2 is implement as method "B" (4.3.3 QoS 2: Exactly once delivery)
            _sessionsManager.DispatchApplicationMessage(this, applicationMessage);

            var response = new MqttPubRecPacket { PacketIdentifier = publishPacket.PacketIdentifier };
            adapter.SendPacket(_options.DefaultCommunicationTimeout, response);
        }

        private Task HandleIncomingPubRelPacketAsync(IMqttChannelAdapter adapter, MqttPubRelPacket pubRelPacket, CancellationToken cancellationToken)
        {
            var response = new MqttPubCompPacket { PacketIdentifier = pubRelPacket.PacketIdentifier };
            return adapter.SendPacketAsync(_options.DefaultCommunicationTimeout, response, cancellationToken);
        }

        private void HandleIncomingPubRelPacket(IMqttChannelAdapter adapter, MqttPubRelPacket pubRelPacket)
        {
            var response = new MqttPubCompPacket { PacketIdentifier = pubRelPacket.PacketIdentifier };
            adapter.SendPacket(_options.DefaultCommunicationTimeout, response);
        }

        private void OnAdapterReadingPacketCompleted(object sender, EventArgs e)
        {
            _keepAliveMonitor?.Pause();
        }

        private void OnAdapterReadingPacketStarted(object sender, EventArgs e)
        {
            _keepAliveMonitor?.Resume();
        }
    }
}
