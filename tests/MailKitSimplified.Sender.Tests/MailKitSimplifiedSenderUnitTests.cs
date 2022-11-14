global using Xunit;
global using Moq;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MailKit;
using MailKit.Net.Smtp;
using MailKitSimplified.Sender.Abstractions;
using MailKitSimplified.Sender.Services;
using MailKitSimplified.Sender.Models;

namespace MailKitSimplified.Sender.Tests
{
    public class MailKitSimplifiedSenderUnitTests
    {
        private static readonly string _logFilePath = @"C:\Temp\EmailClientSmtp.log";
        private const string _attachment1Path = @"C:\Temp\attachment1.txt";
        private const string _attachment2Path = @"C:\Temp\attachment2.pdf";
        private static readonly Task _completedTask = Task.CompletedTask;

        private readonly IFileSystem _fileSystem;
        private readonly IAttachmentHandler _attachmentHandler;
        private readonly ISmtpSender _emailSender;
        private readonly IEmailWriter _emailWriter;
        private readonly ILoggerFactory _loggerFactory;

        public MailKitSimplifiedSenderUnitTests()
        {
            _fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { _logFilePath, new MockFileData(string.Empty) },
                { _attachment1Path, new MockFileData("ABC") },
                { _attachment2Path, new MockFileData("123") }
            });
            _loggerFactory = LoggerFactory.Create(_ => _.SetMinimumLevel(LogLevel.Trace).AddDebug().AddConsole());
            _attachmentHandler = new AttachmentHandler(_loggerFactory.CreateLogger<AttachmentHandler>(), _fileSystem);
            var smtpClientMock = new Mock<ISmtpClient>();
            smtpClientMock.Setup(_ => _.SendAsync(It.IsAny<MimeMessage>(), It.IsAny<CancellationToken>(), It.IsAny<ITransferProgress>()))
                .ReturnsAsync("Mail accepted").Verifiable();
            //var mailKitProtocolLogger = new MailKitProtocolLogger(null, _fileSystem, _loggerFactory.CreateLogger<MailKitProtocolLogger>());
            var senderOptions = new EmailSenderOptions("localhost");
            var options = Options.Create(senderOptions);
            _emailSender = new SmtpSender(options, _loggerFactory.CreateLogger<SmtpSender>(), smtpClientMock.Object);
            _emailWriter = new EmailWriter(_emailSender, _loggerFactory.CreateLogger<EmailWriter>(), _fileSystem);
        }

        [Theory]
        [InlineData("localhost")]
        [InlineData("smtp.google.com")]
        [InlineData("smtp.sendgrid.com")]
        [InlineData("smtp.mail.yahoo.com")]
        [InlineData("outlook.office365.com")]
        [InlineData("smtp.freesmtpservers.com")]
        public void WriteEmail_WithEmailSender_VerifyCreatedAsync(string smtpHost)
        {
            using var smtpSender = SmtpSender.Create(smtpHost);
            var email = smtpSender.WriteEmail
                .From("My Name", "me@example.com")
                .To("Your Name", "you@example.com")
                .Cc("friend1@example.com")
                .Bcc("friend2@example.com")
                .Subject("Hey You")
                .BodyHtml("Hello World");
            Assert.NotNull(email);
        }

        [Fact]
        public async Task SendAsync_WithAttachment_VerifySentAsync()
        {
            await _emailWriter
                .From("from@localhost")
                .To("to@localhost")
                .Subject("Hi")
                .BodyHtml("~")
                .Attach(_attachment1Path, _attachment2Path)
                .SendAsync();
        }

        [Fact]
        public async Task TrySendAsync_WithAttachment_VerifySentAsync()
        {
            var isSent = await _emailWriter
                .From("from@localhost")
                .To("to@localhost")
                .Subject("Hi")
                .BodyHtml("~")
                .TryAttach(_attachment1Path, _attachment2Path)
                .TrySendAsync();
            Assert.True(isSent);
        }

        private static async Task<Stream> GetTestStream(int capacity = 1)
        {
            var randomBytes = new byte[capacity];
            new Random().NextBytes(randomBytes);
            var streamStub = new MemoryStream(capacity);
            await streamStub.WriteAsync(randomBytes);
            streamStub.Position = 0;
            return streamStub;
        }

        [Theory]
        [InlineData("attachment1.txt|attachment2.pdf")]
        public async Task LoadFilePathAsync_WithAnyAttachmentName_VerifyAttached(string filePaths)
        {
            // Arrange
            var fileSystem = new MockFileSystem();
            foreach (var filePath in filePaths.Split('|'))
                fileSystem.AddFile(filePath, new MockFileData("~"));
            IAttachmentHandler attachmentHandler = new AttachmentHandler(null, fileSystem);
            // Act
            var attachments = await attachmentHandler.LoadFilePathAsync(filePaths);
            // Assert
            Assert.NotNull(attachments);
            Assert.True(attachments.Any());
        }

        [Theory]
        [InlineData("./attachment1.txt", "./attachment2.pdf")]
        public async Task LoadFilePathsAsync_WithAnyAttachmentName_VerifyAttached(params string[] filePaths)
        {
            // Arrange
            var fileSystem = new MockFileSystem();
            foreach (var filePath in filePaths)
                fileSystem.AddFile(filePath, new MockFileData("~"));
            IAttachmentHandler attachmentHandler = new AttachmentHandler(null, fileSystem);
            // Act
            var attachments = await attachmentHandler.LoadFilePathsAsync(filePaths);
            // Assert
            Assert.NotNull(attachments);
            Assert.True(attachments.Any());
        }

        [Theory]
        [InlineData(_attachment1Path)]
        [InlineData(_attachment2Path)]
        public async Task GetFileStreamAsync_WithIFileHandler_VerifyStreamContainsData(string filePath)
        {
            var stream = await _attachmentHandler.GetFileStreamAsync(filePath);
            Assert.NotNull(stream);
            Assert.True(stream.Length > 0);
        }

        [Theory]
        [InlineData("testFileName.txt")]
        public async Task GetMimePart_WithAnyFileName_VerifyMimePartFileName(string fileName)
        {
            var stream = await GetTestStream();
            var attachment = AttachmentHandler.GetMimePart(stream, fileName);
            Assert.NotNull(attachment);
            Assert.Equal(fileName, attachment.FileName);
        }

        [Theory]
        [InlineData(_attachment1Path, _attachment2Path)]
        public async Task ConvertToMimeMessageAsync_WithAnyAttachmentName_VerifyAttached(params string[] filePaths)
        {
            // Arrange
            var mimeMessage = new MimeMessage();
            // Act
            mimeMessage = await _attachmentHandler.AddAttachmentsAsync(mimeMessage, filePaths, CancellationToken.None).ConfigureAwait(false);
            // Assert
            Assert.NotNull(mimeMessage);
            Assert.True(mimeMessage.Attachments.Any());
        }

        [Fact]
        public void SendAsync_WithEmailWriter_VerifySent()
        {
            // Arrange
            var emailSenderMock = new Mock<ISmtpSender>();
            emailSenderMock
                .Setup(sender => sender.SendAsync(It.IsAny<MimeMessage>(), It.IsAny<CancellationToken>()))
                .Returns(_completedTask);
            var email = new EmailWriter(emailSenderMock.Object)
                .From("from@localhost").To("to@localhost");
            // Act
            var result = email.SendAsync(It.IsAny<CancellationToken>());
            // Assert
            Assert.Equal(_completedTask, result);
            emailSenderMock.Verify(sender => sender.SendAsync(It.IsAny<MimeMessage>(), It.IsAny<CancellationToken>()), Times.Once());
        }

        [Fact]
        public async void TrySendAsync_WithEmailWriter_VerifySent()
        {
            // Arrange
            var emailSenderMock = new Mock<ISmtpSender>();
            emailSenderMock
                .Setup(sender => sender.TrySendAsync(It.IsAny<MimeMessage>(), It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(true));
            var email = new EmailWriter(emailSenderMock.Object)
                .From("MyName@localhost").To("your.name@localhost");
            // Act
            var result = await email.TrySendAsync(It.IsAny<CancellationToken>());
            // Assert
            Assert.True(result);
            emailSenderMock.Verify(sender => sender.TrySendAsync(It.IsAny<MimeMessage>(), It.IsAny<CancellationToken>()), Times.Once());
        }
    }
}