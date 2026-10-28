using Tms.Api.Common;

namespace Tms.Api.Modules.Commerce;

public enum OrderStatus { Pending, Paid, PartiallyRefunded, Refunded, Failed }
public enum PaymentStatus { Captured, Failed }
public enum RefundStatus { Processed, Failed }

/// <summary>All money is stored as integer minor units (paise, cents) to avoid rounding drift.</summary>
public class Order : Entity
{
    public Guid BuyerUserId { get; set; }
    public OrderStatus Status { get; set; }
    public required string Currency { get; set; }
    public long SubtotalMinor { get; set; }
    public long DiscountMinor { get; set; }

    /// <summary>Tax contained in the total (prices are tax-inclusive).</summary>
    public long TaxMinor { get; set; }
    public long TotalMinor { get; set; }
    public long RefundedMinor { get; set; }
    public required string Provider { get; set; }
    public string? ProviderOrderId { get; set; }
    public Guid? ReferralCodeId { get; set; }
    public DateTime? PaidAt { get; set; }
    public List<OrderItem> Items { get; set; } = [];
}

public class OrderItem : Entity
{
    public Guid OrderId { get; set; }
    public Guid CourseId { get; set; }
    public required string CourseTitle { get; set; }
    public Guid CourseTenantId { get; set; }
    public Guid InstructorId { get; set; }
    public long UnitPriceMinor { get; set; }
    public long TaxMinor { get; set; }
}

public class Payment : Entity
{
    public Guid OrderId { get; set; }
    public required string Provider { get; set; }
    public required string ProviderPaymentId { get; set; }
    public long AmountMinor { get; set; }
    public PaymentStatus Status { get; set; }
}

/// <summary>Every inbound provider event, stored once per (provider, event id) for idempotency.</summary>
public class PaymentWebhookEvent : Entity
{
    public required string Provider { get; set; }
    public required string EventId { get; set; }
    public required string EventType { get; set; }
    public required string Payload { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? Outcome { get; set; }
}

public class Refund : Entity
{
    public Guid OrderId { get; set; }
    public long AmountMinor { get; set; }
    public required string Reason { get; set; }
    public RefundStatus Status { get; set; }
    public string? ProviderRefundId { get; set; }
    public Guid? RequestedBy { get; set; }
}
