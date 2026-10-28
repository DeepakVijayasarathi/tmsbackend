using Tms.Api.Common;

namespace Tms.Api.Modules.Commissions;

public enum RecipientKind { Professor, College, Referrer, Platform }
public enum CommissionEntryType { Accrual, Reversal }
public enum CommissionStatus { Pending, Approved, Payable, Paid, OnHold, Rejected, Reversed }
public enum StackingPolicy
{
    /// <summary>When the instructor also owns the referral code, the referral share goes to the platform.</summary>
    NoInstructorReferral,

    /// <summary>The instructor may earn both their instructor share and the referral share.</summary>
    AllowStacking,
}
public enum PlanStatus { Active, Superseded }
public enum PayoutStatus { Requested, Approved, Paid, Rejected }

/// <summary>When a staged referral payout is released: at purchase, or as the referred learner progresses.</summary>
public enum ReferralStage { Purchase, FirstMilestone, HalfwayMilestones, CourseCompleted }

/// <summary>
/// A revenue-sharing agreement. Plans are versioned: editing a plan supersedes it with a new
/// version, and every ledger entry records the exact plan version it was calculated under.
/// Scope precedence: course-specific → tenant-specific → platform default.
/// </summary>
public class CommissionPlan : Entity
{
    public Guid PlanKey { get; set; } = Guid.NewGuid();
    public int Version { get; set; } = 1;
    public required string Name { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? CourseId { get; set; }

    // Rates in basis points (1% = 100bp) of the eligible revenue base.
    public int ProfessorBp { get; set; }
    public int CollegeBp { get; set; }
    public int ReferralBp { get; set; }
    /// <summary>
    /// How the referral share is released, e.g. "Purchase:30,FirstMilestone:30,CourseCompleted:40" (percent of the
    /// referral share per stage). Empty means the whole share accrues at purchase.
    /// </summary>
    public string ReferralSchedule { get; set; } = "";
    public StackingPolicy Stacking { get; set; }

    /// <summary>Days an approved earning is held before it becomes payable (covers the refund window).</summary>
    public int HoldDays { get; set; }
    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
    public DateTime? EffectiveTo { get; set; }
    public PlanStatus Status { get; set; }
}

/// <summary>
/// Immutable commission ledger. Amount fields are never edited after posting; corrections
/// (refunds, chargebacks) are new Reversal entries pointing at the entry they offset.
/// Only the workflow status moves (Pending → Approved → Payable → Paid).
/// </summary>
public class CommissionEntry : Entity
{
    public Guid OrderId { get; set; }
    public Guid OrderItemId { get; set; }
    public RecipientKind RecipientKind { get; set; }
    public Guid? RecipientUserId { get; set; }
    public Guid? RecipientTenantId { get; set; }
    public CommissionEntryType EntryType { get; set; }
    public Guid? ReversesEntryId { get; set; }
    public long BaseMinor { get; set; }
    public int RateBp { get; set; }
    public long AmountMinor { get; set; }
    public required string Currency { get; set; }
    public Guid PlanId { get; set; }
    public int PlanVersion { get; set; }

    /// <summary>JSON explanation of how the amount was derived.</summary>
    public required string Calculation { get; set; }
    public CommissionStatus Status { get; set; }
    /// <summary>For staged referral payouts: the learner progress that releases this entry from OnHold.</summary>
    public ReferralStage? ReleaseStage { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public DateTime AvailableAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public Guid? ApprovedBy { get; set; }
    public Guid? PayoutId { get; set; }
}

public class Payout : Entity
{
    public RecipientKind RecipientKind { get; set; }
    public Guid? RecipientUserId { get; set; }
    public Guid? RecipientTenantId { get; set; }
    public long AmountMinor { get; set; }
    public required string Currency { get; set; }
    public PayoutStatus Status { get; set; }
    public required string IdempotencyKey { get; set; }
    public Guid RequestedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public string? Reference { get; set; }
}
