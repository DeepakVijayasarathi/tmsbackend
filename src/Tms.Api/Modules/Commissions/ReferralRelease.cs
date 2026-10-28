using Microsoft.EntityFrameworkCore;
using Tms.Api.Data;
using Tms.Api.Modules.Catalog;
using Tms.Api.Modules.Identity;
using Tms.Api.Modules.Learning;

namespace Tms.Api.Modules.Commissions;

/// <summary>
/// Releases staged referral commission as the referred learner progresses. Held entries move from
/// OnHold to Pending (then through the normal approval and payout workflow) once their stage is reached.
/// </summary>
public class ReferralRelease(TmsDbContext db, MilestoneService milestones)
{
    public async Task ReleaseAsync(Enrollment enrollment)
    {
        if (enrollment.OrderId is not { } orderId) return;
        var itemIds = await db.OrderItems.Where(i => i.OrderId == orderId && i.CourseId == enrollment.CourseId).Select(i => i.Id).ToListAsync();
        var held = await db.CommissionEntries
            .Where(e => e.OrderId == orderId && itemIds.Contains(e.OrderItemId) && e.RecipientKind == RecipientKind.Referrer
                && e.EntryType == CommissionEntryType.Accrual && e.Status == CommissionStatus.OnHold && e.ReleaseStage != null)
            .ToListAsync();
        if (held.Count == 0) return;

        var reached = await ReachedAsync(enrollment);
        var now = DateTime.UtcNow;
        var released = held.Where(e => reached.Contains(e.ReleaseStage!.Value)).ToList();
        if (released.Count == 0) return;

        var releasedIds = released.Select(e => e.Id).ToList();
        // A partial refund before release leaves an OnHold reversal; it must move with its accrual.
        var reversals = await db.CommissionEntries.Where(e => e.ReversesEntryId != null && releasedIds.Contains(e.ReversesEntryId.Value) && e.Status == CommissionStatus.OnHold).ToListAsync();
        foreach (var e in released.Concat(reversals))
        {
            e.Status = CommissionStatus.Pending;
            e.ReleasedAt = now;
            if (e.AvailableAt < now) e.AvailableAt = now;
        }
        Audit.Record(db, null, "commission.referral_released", nameof(Enrollment), enrollment.Id,
            new { stages = released.Select(e => e.ReleaseStage.ToString()), amount = released.Sum(e => e.AmountMinor) });
        await db.SaveChangesAsync();
    }

    /// <summary>Stages the learner has reached. Courses without milestones fall back to lesson progress.</summary>
    private async Task<HashSet<ReferralStage>> ReachedAsync(Enrollment e)
    {
        var stages = new HashSet<ReferralStage> { ReferralStage.Purchase };
        if (e.Status == EnrollmentStatus.Completed) stages.UnionWith([ReferralStage.FirstMilestone, ReferralStage.HalfwayMilestones, ReferralStage.CourseCompleted]);

        var progress = await milestones.ProgressAsync(e);
        int done, total;
        if (progress.Count > 0)
        {
            done = progress.Count(m => m.Achieved);
            total = progress.Count;
        }
        else
        {
            total = await db.Lessons.CountAsync(l => l.Module!.CourseVersionId == e.CourseVersionId);
            done = await db.LessonProgress.CountAsync(p => p.EnrollmentId == e.Id);
        }
        if (done >= 1) stages.Add(ReferralStage.FirstMilestone);
        if (total > 0 && done * 2 >= total) stages.Add(ReferralStage.HalfwayMilestones);
        return stages;
    }
}
