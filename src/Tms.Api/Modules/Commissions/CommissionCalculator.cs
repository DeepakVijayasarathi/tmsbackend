namespace Tms.Api.Modules.Commissions;

public record SaleContext(
    long BaseMinor,
    Guid BuyerUserId,
    Guid InstructorUserId,
    Guid CourseTenantId,
    bool CourseTenantIsCollege,
    Guid? ReferrerUserId);

public record Allocation(
    RecipientKind Kind,
    Guid? UserId,
    Guid? TenantId,
    int RateBp,
    long AmountMinor,
    string Note);

/// <summary>
/// Pure, deterministic revenue-split maths. Each non-platform recipient receives
/// floor(base × rate). The platform receives the exact remainder, so allocations always sum to
/// the base and any share that is not payable (no college, blocked referral) is never lost or doubled.
/// </summary>
public static class CommissionCalculator
{
    public static IReadOnlyList<Allocation> Allocate(SaleContext sale, CommissionPlan plan)
    {
        if (sale.BaseMinor < 0) throw new ArgumentOutOfRangeException(nameof(sale), "Revenue base cannot be negative.");
        if (plan.ProfessorBp + plan.CollegeBp + plan.ReferralBp > 10_000)
            throw new InvalidOperationException($"Plan {plan.Name} v{plan.Version} allocates more than 100%.");

        var result = new List<Allocation>();
        var redirected = new List<string>();

        result.Add(new Allocation(RecipientKind.Professor, sale.InstructorUserId, null, plan.ProfessorBp,
            Share(sale.BaseMinor, plan.ProfessorBp), "Instructor share"));

        if (sale.CourseTenantIsCollege)
            result.Add(new Allocation(RecipientKind.College, null, sale.CourseTenantId, plan.CollegeBp,
                Share(sale.BaseMinor, plan.CollegeBp), "Institution share"));
        else if (plan.CollegeBp > 0)
            redirected.Add("college share (course is not owned by a college)");

        if (sale.ReferrerUserId is { } referrer && plan.ReferralBp > 0)
        {
            if (referrer == sale.BuyerUserId)
                redirected.Add("referral share (self-referral)");
            else if (referrer == sale.InstructorUserId && plan.Stacking == StackingPolicy.NoInstructorReferral)
                redirected.Add("referral share (instructor referred their own course; stacking disabled)");
            else
                result.Add(new Allocation(RecipientKind.Referrer, referrer, null, plan.ReferralBp,
                    Share(sale.BaseMinor, plan.ReferralBp), "Referral share"));
        }

        var platformAmount = sale.BaseMinor - result.Sum(a => a.AmountMinor);
        var platformNote = redirected.Count == 0
            ? "Platform share (remainder)"
            : "Platform share (remainder, includes " + string.Join("; ", redirected) + ")";
        result.Add(new Allocation(RecipientKind.Platform, null, null,
            sale.BaseMinor == 0 ? 0 : (int)(platformAmount * 10_000 / sale.BaseMinor), platformAmount, platformNote));

        return result;
    }

    /// <summary>
    /// Splits a referral amount into release stages from a plan schedule ("Purchase:30,CourseCompleted:70").
    /// Each stage gets floor(amount × percent); the last stage takes the remainder so the parts always sum
    /// to the amount. An empty or invalid schedule releases everything at purchase.
    /// </summary>
    public static IReadOnlyList<(ReferralStage Stage, int Percent, long AmountMinor)> SplitReferral(long amountMinor, string? schedule)
    {
        var parts = new List<(ReferralStage Stage, int Percent)>();
        foreach (var token in (schedule ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = token.Split(':', StringSplitOptions.TrimEntries);
            if (kv.Length != 2 || !Enum.TryParse<ReferralStage>(kv[0], true, out var stage) || !int.TryParse(kv[1], out var pct) || pct <= 0)
                return [(ReferralStage.Purchase, 100, amountMinor)];
            parts.Add((stage, pct));
        }
        if (parts.Count == 0 || parts.Sum(p => p.Percent) != 100) return [(ReferralStage.Purchase, 100, amountMinor)];

        var result = new List<(ReferralStage, int, long)>();
        long allocated = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            var amount = i == parts.Count - 1 ? amountMinor - allocated : amountMinor * parts[i].Percent / 100;
            allocated += amount;
            result.Add((parts[i].Stage, parts[i].Percent, amount));
        }
        return result;
    }

    /// <summary>
    /// Amount to reverse from an accrual after a refund. Uses the cumulative refunded fraction so
    /// repeated partial refunds never over-reverse, and a full refund always reverses exactly.
    /// </summary>
    public static long ReversalAmount(long originalAmount, long alreadyReversed, long cumulativeRefundedMinor, long orderTotalMinor)
    {
        if (orderTotalMinor <= 0) return 0;
        var refunded = Math.Min(cumulativeRefundedMinor, orderTotalMinor);
        var targetReversed = (long)Math.Floor((decimal)originalAmount * refunded / orderTotalMinor);
        return Math.Max(0, targetReversed - alreadyReversed);
    }

    /// <summary>Tax contained in a tax-inclusive price.</summary>
    public static long InclusiveTax(long grossMinor, decimal ratePercent)
    {
        if (ratePercent <= 0) return 0;
        var net = Math.Round(grossMinor * 100m / (100m + ratePercent), MidpointRounding.AwayFromZero);
        return grossMinor - (long)net;
    }

    private static long Share(long baseMinor, int bp) => (long)Math.Floor((decimal)baseMinor * bp / 10_000m);
}
