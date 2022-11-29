﻿using MailKit;
using MailKit.Security;
using MailKit.Net.Imap;
using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MailKitSimplified.Receiver.Abstractions;
using MailKitSimplified.Receiver.Extensions;

namespace MailKitSimplified.Receiver
{
    public sealed class MailFolderMonitor : IMailFolderMonitor
    {
        public MessageSummaryItems MessageFilter { get; set; } = MessageSummaryItems.None;
        public Func<IMessageSummary, Task> MessageArrivalMethod { private get; set; }
        public Func<IMessageSummary, Task> MessageDepartureMethod { private get; set; }

        private const int _maxRetries = 3;
        private const int _idleMinutesGmail = 9;
        private const int _idleMinutesImap = 29;
        private int _idleMinutes = _idleMinutesImap;
        private static readonly Task _completedTask = Task.CompletedTask;
        private readonly object _cacheLock = new object();
        private readonly IList<IMessageSummary> _messageCache = new List<IMessageSummary>();
        private readonly ConcurrentQueue<IMessageSummary> _arrivalQueue = new ConcurrentQueue<IMessageSummary>();
        private readonly ConcurrentQueue<IMessageSummary> _departureQueue = new ConcurrentQueue<IMessageSummary>();
        private CancellationTokenSource _arrival = new CancellationTokenSource();
        private IImapClient _imapClient;
        private IMailFolder _mailFolder;
        private bool _canIdle;

        private readonly ILogger _logger;
        private readonly IImapReceiver _imapReceiver;
        private readonly IMailFolderClient _mailFolderClient;

        public MailFolderMonitor(IImapReceiver imapReceiver, ILogger<MailFolderMonitor> logger = null)
        {
            _logger = logger ?? NullLogger<MailFolderMonitor>.Instance;
            _imapReceiver = imapReceiver ?? throw new ArgumentNullException(nameof(imapReceiver));
            _mailFolderClient = _imapReceiver.MailFolderClient;
            MessageArrivalMethod = (m) =>
            {
                _logger.LogInformation($"{_imapReceiver} message #{m.UniqueId} arrival processed.");
                return _completedTask;
            };
            MessageDepartureMethod = (m) =>
            {
                _logger.LogInformation($"{_imapReceiver} message #{m.UniqueId} departure processed.");
                return _completedTask;
            };
        }

        public async Task IdleAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var tasks = new Task[]
                {
                    IdleStartAsync(cancellationToken).ContinueWith(t =>
                        _logger.LogError(t.Exception?.InnerException ?? t.Exception,
                            "Idle client failed."), TaskContinuationOptions.OnlyOnFaulted),
                    ProcessArrivalQueueAsync(MessageArrivalMethod, cancellationToken).ContinueWith(t =>
                        _logger.LogError(t.Exception?.InnerException ?? t.Exception,
                            "Arrival queue processing failed."), TaskContinuationOptions.OnlyOnFaulted),
                    ProcessDepartureQueueAsync(MessageDepartureMethod, cancellationToken).ContinueWith(t =>
                        _logger.LogError(t.Exception?.InnerException ?? t.Exception,
                            "Departure queue processing failed."), TaskContinuationOptions.OnlyOnFaulted)
                };
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogTrace($"{_imapReceiver} email monitoring cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{_imapReceiver} email monitoring failed.");
            }
        }

        private async Task IdleStartAsync(CancellationToken cancellationToken = default)
        {
            if (MessageArrivalMethod != null && MessageDepartureMethod != null)
            {
                try
                {
                    _imapClient = await _imapReceiver.ConnectAuthenticatedImapClientAsync(cancellationToken).ConfigureAwait(false);
                    _canIdle = _imapClient.Capabilities.HasFlag(ImapCapabilities.Idle);
                    _mailFolder = await _mailFolderClient.ConnectAsync(false, cancellationToken).ConfigureAwait(false);
                    _logger.LogDebug($"{_imapReceiver} ({_mailFolder.Count}) idle monitor started.");

                    _mailFolder.CountChanged += OnCountChanged;
                    _mailFolder.MessageExpunged += OnMessageExpunged;

                    if (_mailFolder.Count > 0)
                        await ProcessMessagesArrivedAsync(cancellationToken).ConfigureAwait(false);
                    await WaitForNewMessagesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Initial fetch or idle task in mail folder monitor was cancelled.");
                }
                catch (AuthenticationException ex)
                {
                    _logger.LogWarning(ex, "Stopping mail folder monitor service.");
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogError(ex, "IMAP client not available.");
                }
                finally
                {
                    if (_mailFolder != null)
                    {
                        _mailFolder.MessageExpunged -= OnMessageExpunged;
                        _mailFolder.CountChanged -= OnCountChanged;
                    }

                    await _mailFolderClient.DisposeAsync().ConfigureAwait(false);
                    await _imapReceiver.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);

                    _arrival?.Dispose();
                    _messageCache?.Clear();
#if NET6_0_OR_GREATER
                    _arrivalQueue?.Clear();
                    _departureQueue?.Clear();
#endif
                }
            }
        }
        
        private async ValueTask ReconnectAsync(CancellationToken cancellationToken = default)
        {
            _ = await _imapReceiver.ConnectAuthenticatedImapClientAsync(cancellationToken).ConfigureAwait(false);
            _ = await _mailFolderClient.ConnectAsync(false, cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask WaitForNewMessagesAsync(CancellationToken cancellationToken = default)
        {
            int retryCount = 0;
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var idleTime = TimeSpan.FromMinutes(_idleMinutes);
            do
            {
                try
                {
                    if (_canIdle)
                    {
                        using (var done = new CancellationTokenSource(idleTime))
                        using (var arrival = CancellationTokenSource.CreateLinkedTokenSource(_arrival.Token, done.Token))
                            await _imapClient.IdleAsync(arrival.Token, cancel.Token).ConfigureAwait(false);
                    }
                    else // simulate IMAP idle
                    {
                        await Task.Delay(TimeSpan.FromMinutes(1), cancel.Token).ConfigureAwait(false);
                        await _imapClient.NoOpAsync(cancel.Token).ConfigureAwait(false);
                    }
                    if (_arrival.IsCancellationRequested && !cancel.Token.IsCancellationRequested)
                        await ProcessMessagesArrivedAsync(cancel.Token).ConfigureAwait(false);
                    retryCount = 0;
                }
                catch (OperationCanceledException) // includes TaskCanceledException
                {
                    _logger.LogTrace($"{_imapReceiver} mail folder idle wait task cancelled.");
                    cancel.Cancel(false);
                }
                catch (ImapProtocolException ex)
                {
                    if (ex.Message.StartsWith("Idle timeout"))
                        _logger.LogInformation($"{ex.Message} Trying again.");
                    else
                        _logger.LogInformation(ex, "IMAP protocol exception, checking connection.");
                    await ReconnectAsync(cancel.Token).ConfigureAwait(false);
                    if (_idleMinutes > _idleMinutesGmail)
                        _idleMinutes = _idleMinutesGmail;
                    else if (_idleMinutes == _idleMinutesGmail)
                        _idleMinutes = 1;
                }
                catch (ImapCommandException ex)
                {
                    _logger.LogInformation(ex, "IMAP command exception, rechecking server connection.");
                    if (!cancel.Token.IsCancellationRequested)
                        await ReconnectAsync(cancel.Token).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    _logger.LogInformation("IMAP I/O exception, reconnecting.");
                    await ReconnectAsync(cancel.Token).ConfigureAwait(false);
                }
                catch (SocketException ex)
                {
                    _logger.LogInformation(ex, "IMAP socket exception.");
                    if (!cancel.Token.IsCancellationRequested)
                        await ReconnectAsync(cancel.Token).ConfigureAwait(false);
                }
                catch (ServiceNotConnectedException)
                {
                    _logger.LogInformation("IMAP service not connected, reconnecting.");
                    await ReconnectAsync(cancel.Token).ConfigureAwait(false);
                }
                catch (ServiceNotAuthenticatedException)
                {
                    _logger.LogInformation("IMAP service not authenticated, authenticating.");
                    await ReconnectAsync(cancel.Token).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogError(ex, "IMAP client is being accessed by multiple threads.");
                    cancel.Cancel(false);
                }
                finally
                {
                    retryCount++;
                }
            } while (!cancel.IsCancellationRequested && retryCount < _maxRetries);
            cancel.Dispose();
        }

        private async ValueTask<int> ProcessMessagesArrivedAsync(CancellationToken cancellationToken = default)
        {
            int startIndex = _messageCache.Count;
            _logger.LogTrace($"{_imapReceiver} ({_mailFolder.Count}) Fetching new message arrivals, starting from {startIndex}.");
            if (startIndex > _mailFolder.Count)
                startIndex = _mailFolder.Count;
            var filter = MessageFilter | MessageSummaryItems.UniqueId;
            var fetched = await _mailFolder.FetchAsync(startIndex, -1, filter, cancellationToken).ConfigureAwait(false);
            if (_arrival.IsCancellationRequested)
                _arrival = new CancellationTokenSource();
            var newMail = _messageCache.TryAddUniqueRange(fetched);
            newMail.ActionEach((mail) => _arrivalQueue.Enqueue(mail), cancellationToken);
            return newMail.Count;
        }

        private async Task ProcessArrivalQueueAsync(Func<IMessageSummary, Task> messageArrivalMethod, CancellationToken cancellationToken = default)
        {
            IMessageSummary messageSummary = null;
            try
            {
                if (messageArrivalMethod != null)
                {
                    do
                    {
                        if (_arrivalQueue.TryDequeue(out messageSummary))
                            await messageArrivalMethod(messageSummary).ConfigureAwait(false);
                        else if (_arrivalQueue.IsEmpty)
                            await Task.Delay(100).ConfigureAwait(false);
                    }
                    while (!cancellationToken.IsCancellationRequested);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogTrace("Arrival queue cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error occurred processing arrival queue item #{messageSummary}.");
                if (messageSummary != null)
                    _arrivalQueue.Enqueue(messageSummary);
            }
        }

        private async Task ProcessDepartureQueueAsync(Func<IMessageSummary, Task> messageDepartureMethod, CancellationToken cancellationToken = default)
        {
            IMessageSummary messageSummary = null;
            try
            {
                if (messageDepartureMethod != null)
                {
                    do
                    {
                        if (_departureQueue.TryDequeue(out messageSummary))
                            await messageDepartureMethod(messageSummary).ConfigureAwait(false);
                        else if (_departureQueue.IsEmpty)
                            await Task.Delay(100).ConfigureAwait(false);
                    }
                    while (!cancellationToken.IsCancellationRequested);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogTrace("Departure queue cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error occurred processing departure queue item #{messageSummary}.");
                if (messageSummary != null)
                    _departureQueue.Enqueue(messageSummary);
            }
        }

        /// <summary>
        /// Keep the message cache in sync with the <see cref="ImapFolder">mail folder</see> by adding items.
        /// </summary>
        private void OnCountChanged(object sender, EventArgs e)
        {
            using (_logger.BeginScope("OnCountChanged"))
            {
                if (sender is ImapFolder folder)
                {
                    int previousCount = _messageCache.Count;
                    int presentCount = folder.Count;
                    int changeCount = presentCount - previousCount;
                    if (changeCount > 0)
                    {
                        _arrival?.Cancel(true);
                        _logger.LogTrace($"{_imapReceiver} message count increased by {changeCount} ({previousCount} to {presentCount}).");
                    }
                    else if (changeCount < 0)
                    {
                        _logger.LogTrace($"{_imapReceiver} message count decreased by {changeCount} ({previousCount} to {presentCount}).");
                    }
                }
                else
                {
                    _logger.LogWarning($"{_imapReceiver} message count changed ({_messageCache.Count} to {_mailFolder.Count}), folder unknown.");
                }
            }
        }

        /// <summary>
        /// Keep the message cache in sync with the <see cref="ImapFolder">mail folder</see> by removing items.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Collection index is invalid.</exception>
        private void OnMessageExpunged(object sender, MessageEventArgs e)
        {
            using (_logger.BeginScope("OnMessageExpunged"))
            {
                int index = e.Index;
                var cachedCount = _messageCache.Count;
                if (index < cachedCount)
                {
                    IMessageSummary messageSummary;
                    lock (_cacheLock)
                    {
                        messageSummary = _messageCache[index];
                        _messageCache.RemoveAt(index);
                    }
                    _logger.LogTrace($"{_imapReceiver}[{index}] (count={cachedCount}) expunged, item #{messageSummary.UniqueId}.");
                }
                else
                    _logger.LogWarning($"{_imapReceiver}[{index}] (count={cachedCount}) was out of range.");
            }
        }

        public override string ToString() => _imapReceiver.ToString();
    }
}
