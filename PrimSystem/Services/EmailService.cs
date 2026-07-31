using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace PrimSystem.Services
{
    public class EmailService
    {
        private readonly IConfiguration _config;

        public EmailService(IConfiguration config)
        {
            _config = config;
        }

        public async Task SendAsync(string toEmail, string subject, string htmlBody)
        {
            var smtp = _config.GetSection("Smtp");
            var host = smtp["Host"] ?? "smtp.office365.com";
            var port = int.Parse(smtp["Port"] ?? "587");
            var username = smtp["Username"] ?? "";
            var password = smtp["Password"] ?? "";
            var fromName = smtp["FromName"] ?? "Prim Sistemi";
            var fromAddress = smtp["FromAddress"] ?? username;

            if (string.IsNullOrEmpty(username))
                throw new InvalidOperationException("SMTP yapılandırması eksik: appsettings.json'da Smtp:Username tanımlayın");

            var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress(fromName, fromAddress));
            msg.To.Add(MailboxAddress.Parse(toEmail));
            msg.Subject = subject;
            msg.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

            try
            {
                using var client = new SmtpClient();
                // 587 → STARTTLS, 465 → implicit SSL, diğer → otomatik
                var secureOption = port switch
                {
                    465 => SecureSocketOptions.SslOnConnect,
                    587 => SecureSocketOptions.StartTls,
                    _   => SecureSocketOptions.Auto
                };
                await client.ConnectAsync(host, port, secureOption);
                await client.AuthenticateAsync(username, password);
                await client.SendAsync(msg);
                await client.DisconnectAsync(true);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"E-posta gönderilemedi: {ex.Message}", ex);
            }
        }
    }
}
