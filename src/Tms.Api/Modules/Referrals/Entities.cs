using Tms.Api.Common;

namespace Tms.Api.Modules.Referrals;

public enum ReferralConversionStatus { Credited, Rejected, Reversed }

public class ReferralCode : Entity
{
    public required string Code { get; set; }
    public Guid OwnerUserId { get; set; }
    public string? Campaign { get; set; }
    public bool IsActive { get; set; } = true;
}

public class ReferralConversion : Entity
{
    public Guid ReferralCodeId { get; set; }
    public Guid ReferrerUserId { get; set; }
    public Guid OrderId { get; set; }
    public Guid BuyerUserId { get; set; }
    public ReferralConversionStatus Status { get; set; }

    /// <summary>Why a conversion earned nothing (self-referral, instructor stacking policy…).</summary>
    public string? Note { get; set; }
}
