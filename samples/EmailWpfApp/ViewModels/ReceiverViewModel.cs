﻿using System.Linq;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using MailKitSimplified.Receiver.Services;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using MailKit.Search;
using MailKitSimplified.Receiver.Abstractions;
using EmailWpfApp.Models;

namespace EmailWpfApp.ViewModels
{
    class ReceiverViewModel : BaseViewModel // ObservableObject
    {
        #region Public Properties
        public ObservableCollection<string> ViewModelData { get; private set; } = new ObservableCollection<string>();

        public IAsyncRelayCommand UserCommand { get; set; }
        #endregion

        private int _count = 0;

        public ReceiverViewModel()
        {
            UserCommand = new AsyncRelayCommand(ReceiveMailAsync);
            StatusText = string.Empty;
        }

        private async Task ReceiveMailAsync()
        {
            using var imapReceiver = App.ServiceProvider?.GetService<IImapReceiver>();
            if (imapReceiver != null)
            {
                var messageSummaries = await imapReceiver.ReadMail
                    .Take(1).GetMessageSummariesAsync();
                var messageSummary = messageSummaries.Single();
                var email = messageSummaries.Select(m => Email.Write
                    .To(m.Envelope.To.ToString())).Single();
                ViewModelData.Add(messageSummary.ToString());
                StatusText = $"Email received: {messageSummary.UniqueId}.";
            }
            else
            {
                ViewModelData.Add($"Email #{++_count}");
                StatusText = $"Email #{_count} received!";
                if (!string.IsNullOrWhiteSpace(StatusText))
                {
                    logger.LogDebug("Result: {0}", StatusText);
                }
            }
        }
    }
}
