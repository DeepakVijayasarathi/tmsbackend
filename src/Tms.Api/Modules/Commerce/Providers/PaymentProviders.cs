using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tms.Api.Common;

namespace Tms.Api.Modules.Commerce.Providers;

/// <summary>What the browser needs to start paying; the order is only fulfilled from a verified webhook.</summary>
public record CheckoutStart(string ProviderOrderId, string? RedirectUrl, object? ClientPayload);

public enum PaymentEventKind { Captured, Failed, Ignored }

public record PaymentEvent(string EventId, string EventType, PaymentEventKind Kind, string? ProviderOrderId,
    string? ProviderPaymentId, long AmountMinor);

public interface IPaymentProvider
{
    string Name { get; }
    Task<CheckoutStart> StartCheckoutAsync(Order order, CancellationToken ct);
    bool VerifySignature(IHeaderDictionary headers, string rawBody);
    PaymentEvent Parse(IHeaderDictionary headers, string rawBody);
    Task<string> RefundAsync(Order order, Payment payment, long amountMinor, CancellationToken ct);
}

public class PaymentProviderRegistry(IEnumerable<IPaymentProvider> providers, IConfiguration config)
{
    public IPaymentProvider Get(string name) =>
        providers.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw AppException.NotFound($"Payment provider '{name}'");

    public IPaymentProvider Default => Get(config["Payments:DefaultProvider"] ?? "sandbox");
}

/// <summary>
/// Local development provider. Its "hosted checkout" is a page in the web app, and payment
/// confirmation still arrives as an HMAC-signed webhook so the real fulfilment path is exercised.
/// </summary>
public class SandboxPaymentProvider(IConfiguration config) : IPaymentProvider
{
    public const string SignatureHeader = "X-Sandbox-Signature";
    public string Name => "sandbox";
    private string Secret => config["Payments:Sandbox:WebhookSecret"] is { Length: > 0 } s
        ? s : throw new InvalidOperationException("Sandbox webhook secret is not configured.");

    public bool Enabled => config.GetValue("Payments:Sandbox:Enabled", false);

    public Task<CheckoutStart> StartCheckoutAsync(Order order, CancellationToken ct)
    {
        if (!Enabled) throw AppException.Invalid("Sandbox payments are disabled in this environment.");
        var providerOrderId = "sbx_order_" + Text.RandomCode(12);
        var web = config["App:PublicWebUrl"]?.TrimEnd('/');
        return Task.FromResult(new CheckoutStart(providerOrderId, $"{web}/checkout/sandbox/{order.Id}", null));
    }

    public string Sign(string rawBody) => Text.HmacSha256Hex(Secret, rawBody);

    public bool VerifySignature(IHeaderDictionary headers, string rawBody) =>
        Enabled && headers.TryGetValue(SignatureHeader, out var sig) && Text.FixedTimeEqualsHex(sig.ToString(), Sign(rawBody));

    public PaymentEvent Parse(IHeaderDictionary headers, string rawBody)
    {
        var json = JsonNode.Parse(rawBody)!;
        var type = json["type"]!.GetValue<string>();
        return new PaymentEvent(
            json["id"]!.GetValue<string>(), type,
            type switch { "payment.captured" => PaymentEventKind.Captured, "payment.failed" => PaymentEventKind.Failed, _ => PaymentEventKind.Ignored },
            json["orderId"]?.GetValue<string>(), json["paymentId"]?.GetValue<string>(), json["amount"]?.GetValue<long>() ?? 0);
    }

    public Task<string> RefundAsync(Order order, Payment payment, long amountMinor, CancellationToken ct) =>
        Task.FromResult("sbx_rfnd_" + Text.RandomCode(12));
}

/// <summary>Razorpay (India): Orders API for checkout, X-Razorpay-Signature HMAC for webhooks.</summary>
public class RazorpayPaymentProvider(IConfiguration config, IHttpClientFactory http) : IPaymentProvider
{
    public string Name => "razorpay";

    private HttpClient Client()
    {
        var keyId = config["Payments:Razorpay:KeyId"];
        var secret = config["Payments:Razorpay:KeySecret"];
        if (string.IsNullOrEmpty(keyId) || string.IsNullOrEmpty(secret))
            throw AppException.Invalid("Razorpay is not configured.");
        var client = http.CreateClient("razorpay");
        client.BaseAddress = new Uri("https://api.razorpay.com/v1/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{keyId}:{secret}")));
        return client;
    }

    public async Task<CheckoutStart> StartCheckoutAsync(Order order, CancellationToken ct)
    {
        using var res = await Client().PostAsJsonAsync("orders", new
        {
            amount = order.TotalMinor,
            currency = order.Currency,
            receipt = order.Id.ToString("N")[..32],
            notes = new { tmsOrderId = order.Id },
        }, ct);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
        var providerOrderId = body!["id"]!.GetValue<string>();
        return new CheckoutStart(providerOrderId, null, new
        {
            keyId = config["Payments:Razorpay:KeyId"],
            orderId = providerOrderId,
            amount = order.TotalMinor,
            currency = order.Currency,
        });
    }

    public bool VerifySignature(IHeaderDictionary headers, string rawBody)
    {
        var secret = config["Payments:Razorpay:WebhookSecret"];
        return !string.IsNullOrEmpty(secret)
            && headers.TryGetValue("X-Razorpay-Signature", out var sig)
            && Text.FixedTimeEqualsHex(sig.ToString(), Text.HmacSha256Hex(secret, rawBody));
    }

    public PaymentEvent Parse(IHeaderDictionary headers, string rawBody)
    {
        var json = JsonNode.Parse(rawBody)!;
        var type = json["event"]!.GetValue<string>();
        var payment = json["payload"]?["payment"]?["entity"];
        var eventId = headers.TryGetValue("X-Razorpay-Event-Id", out var id) && id.Count > 0
            ? id.ToString()
            : $"{type}:{payment?["id"]?.GetValue<string>()}";
        return new PaymentEvent(eventId, type,
            type switch { "payment.captured" => PaymentEventKind.Captured, "payment.failed" => PaymentEventKind.Failed, _ => PaymentEventKind.Ignored },
            payment?["order_id"]?.GetValue<string>(), payment?["id"]?.GetValue<string>(), payment?["amount"]?.GetValue<long>() ?? 0);
    }

    public async Task<string> RefundAsync(Order order, Payment payment, long amountMinor, CancellationToken ct)
    {
        using var res = await Client().PostAsJsonAsync($"payments/{payment.ProviderPaymentId}/refund", new { amount = amountMinor }, ct);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
        return body!["id"]!.GetValue<string>();
    }
}

public static class SandboxEvents
{
    public static string Captured(Order order) => JsonSerializer.Serialize(new
    {
        id = "sbx_evt_" + Text.RandomCode(16),
        type = "payment.captured",
        orderId = order.ProviderOrderId,
        paymentId = "sbx_pay_" + Text.RandomCode(12),
        amount = order.TotalMinor,
    });
}
