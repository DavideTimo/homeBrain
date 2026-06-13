using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CasaTimo.Core.Models;
using CasaTimo.Infrastructure.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Microsoft.EntityFrameworkCore;

namespace CasaTimo.Workers;

public class BillWatcherConfig
{
    public string Sender { get; set; } = "";
    public BillType Type { get; set; }
}

public class BillWatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<BillWatcher> _logger;
    private readonly HttpClient _http;

    public BillWatcher(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<BillWatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
        _http = new HttpClient();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var clientId = _config["GOOGLE_CLIENT_ID"];
        var clientSecret = _config["GOOGLE_CLIENT_SECRET"];

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            _logger.LogWarning("Gmail credentials not configured. BillWatcher disabled.");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckGmailAsync(clientId, clientSecret, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in BillWatcher");
            }

            await Task.Delay(TimeSpan.FromHours(6), ct);
        }
    }

    private async Task CheckGmailAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
            [GmailService.Scope.GmailReadonly],
            "casatimo",
            ct);

        var gmail = new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "CasaTimo"
        });

        var watchers = _config.GetSection("BillWatchers").Get<List<BillWatcherConfig>>() ?? [];

        foreach (var watcher in watchers)
        {
            await ProcessSenderAsync(gmail, watcher, ct);
        }
    }

    private async Task ProcessSenderAsync(GmailService gmail, BillWatcherConfig watcher, CancellationToken ct)
    {
        var listRequest = gmail.Users.Messages.List("me");
        listRequest.Q = $"from:{watcher.Sender} has:attachment newer_than:30d";
        var response = await listRequest.ExecuteAsync(ct);

        if (response.Messages == null) return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CasaTimoDbContext>();

        foreach (var msgRef in response.Messages)
        {
            var existing = await db.Bills.AnyAsync(b => b.EmailId == msgRef.Id, ct);
            if (existing) continue;

            try
            {
                var message = await gmail.Users.Messages.Get("me", msgRef.Id).ExecuteAsync(ct);
                await ProcessMessageAsync(gmail, message, watcher, db, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message {Id}", msgRef.Id);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task ProcessMessageAsync(GmailService gmail, Message message, BillWatcherConfig watcher,
        CasaTimoDbContext db, CancellationToken ct)
    {
        var pdfPart = message.Payload?.Parts?.FirstOrDefault(p =>
            p.MimeType == "application/pdf" || p.Filename?.EndsWith(".pdf") == true);

        if (pdfPart?.Body?.AttachmentId == null) return;

        var attachment = await gmail.Users.Messages.Attachments
            .Get("me", message.Id, pdfPart.Body.AttachmentId)
            .ExecuteAsync(ct);

        var pdfBytes = Convert.FromBase64String(attachment.Data.Replace('-', '+').Replace('_', '/'));

        var nasPath = _config["NAS_PDF_PATH"] ?? "/tmp/bollette";
        var year = DateTime.Now.Year;
        var dir = Path.Combine(nasPath, year.ToString(), watcher.Type.ToString().ToLower());
        Directory.CreateDirectory(dir);

        var filename = $"{DateTime.Now:yyyyMMdd_HHmmss}_{pdfPart.Filename}";
        var filePath = Path.Combine(dir, filename);
        await File.WriteAllBytesAsync(filePath, pdfBytes, ct);

        var billData = await ExtractBillDataAsync(pdfBytes, watcher.Type, ct);
        if (billData == null) return;

        var bill = new Bill
        {
            Type = watcher.Type,
            Issuer = ExtractSenderName(message),
            Amount = billData.Amount,
            DueDate = billData.DueDate,
            PeriodFrom = billData.PeriodFrom,
            PeriodTo = billData.PeriodTo,
            ConsumptionKwh = billData.ConsumptionKwh,
            PdfPath = filePath,
            EmailId = message.Id,
            CreatedAt = DateTime.UtcNow
        };

        db.Bills.Add(bill);
        await db.SaveChangesAsync(ct);

        var reminder = new Reminder
        {
            BillId = bill.Id,
            DueDate = bill.DueDate.AddDays(-7),
            DaysBefore = 7,
            Message = $"Bolletta {watcher.Type} di €{bill.Amount:F2} scade il {bill.DueDate:dd/MM/yyyy}"
        };
        db.Reminders.Add(reminder);

        _logger.LogInformation("Saved bill: {Type} €{Amount} due {Due}", watcher.Type, bill.Amount, bill.DueDate);
    }

    private async Task<BillExtract?> ExtractBillDataAsync(byte[] pdfBytes, BillType type, CancellationToken ct)
    {
        var apiKey = _config["ANTHROPIC_API_KEY"];
        if (string.IsNullOrEmpty(apiKey))
            return null;

        var pdfText = ExtractTextFromPdf(pdfBytes);
        if (string.IsNullOrEmpty(pdfText)) return null;

        var prompt = $$"""
            Analizza il testo di questa bolletta italiana e rispondi SOLO con JSON valido nel formato:
            {"amount": 123.45, "due_date": "2024-03-15", "period_from": "2024-01-01", "period_to": "2024-02-28", "consumption_kwh": 250.5}

            Se un campo non è presente usa null. Le date devono essere in formato ISO 8601.

            Testo bolletta:
            {{pdfText[..Math.Min(3000, pdfText.Length)]}}
            """;

        var request = new
        {
            model = "claude-haiku-4-5-20251001",
            max_tokens = 256,
            messages = new[] { new { role = "user", content = prompt } }
        };

        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        _http.DefaultRequestHeaders.Remove("x-api-key");
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Remove("anthropic-version");
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var resp = await _http.PostAsync("https://api.anthropic.com/v1/messages", content, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var text = json.GetProperty("content")[0].GetProperty("text").GetString() ?? "";

        try
        {
            var extract = JsonSerializer.Deserialize<BillExtractRaw>(text);
            if (extract == null) return null;

            return new BillExtract
            {
                Amount = (decimal)(extract.amount ?? 0),
                DueDate = DateTime.TryParse(extract.due_date, out var d) ? d : DateTime.Now.AddDays(30),
                PeriodFrom = DateTime.TryParse(extract.period_from, out var pf) ? pf : null,
                PeriodTo = DateTime.TryParse(extract.period_to, out var pt) ? pt : null,
                ConsumptionKwh = extract.consumption_kwh
            };
        }
        catch { return null; }
    }

    private static string ExtractTextFromPdf(byte[] pdfBytes)
    {
        // Basic text extraction - in production use iTextSharp or PdfPig
        var text = Encoding.UTF8.GetString(pdfBytes);
        var sb = new StringBuilder();
        var inText = false;
        for (int i = 0; i < text.Length - 3; i++)
        {
            if (text[i] == 'B' && text[i + 1] == 'T') { inText = true; continue; }
            if (text[i] == 'E' && text[i + 1] == 'T') { inText = false; continue; }
            if (inText && text[i] == '(' && text[i - 1] != '\\')
            {
                var end = text.IndexOf(')', i + 1);
                if (end > i) { sb.Append(text.Substring(i + 1, end - i - 1)); sb.Append(' '); i = end; }
            }
        }
        return sb.ToString();
    }

    private static string ExtractSenderName(Message message)
    {
        var fromHeader = message.Payload?.Headers?
            .FirstOrDefault(h => h.Name == "From")?.Value ?? "";
        var match = System.Text.RegularExpressions.Regex.Match(fromHeader, @"^([^<]+)");
        return match.Success ? match.Groups[1].Value.Trim() : fromHeader;
    }

    private record BillExtract
    {
        public decimal Amount { get; init; }
        public DateTime DueDate { get; init; }
        public DateTime? PeriodFrom { get; init; }
        public DateTime? PeriodTo { get; init; }
        public double? ConsumptionKwh { get; init; }
    }

    private record BillExtractRaw
    {
        public double? amount { get; init; }
        public string? due_date { get; init; }
        public string? period_from { get; init; }
        public string? period_to { get; init; }
        public double? consumption_kwh { get; init; }
    }
}
