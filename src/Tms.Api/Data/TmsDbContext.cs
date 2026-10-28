using Microsoft.EntityFrameworkCore;
using Tms.Api.Modules.Ai;
using Tms.Api.Modules.Assessments;
using Tms.Api.Modules.Careers;
using Tms.Api.Modules.Catalog;
using Tms.Api.Modules.Certificates;
using Tms.Api.Modules.Commerce;
using Tms.Api.Modules.Commissions;
using Tms.Api.Modules.Gamification;
using Tms.Api.Modules.Identity;
using Tms.Api.Modules.Notifications;
using Tms.Api.Modules.Learning;
using Tms.Api.Modules.Referrals;

namespace Tms.Api.Data;

public class TmsDbContext(DbContextOptions<TmsDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<Course> Courses => Set<Course>();
    public DbSet<CourseVersion> CourseVersions => Set<CourseVersion>();
    public DbSet<CourseModule> CourseModules => Set<CourseModule>();
    public DbSet<Lesson> Lessons => Set<Lesson>();
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<CourseInterest> CourseInterests => Set<CourseInterest>();

    public DbSet<Enrollment> Enrollments => Set<Enrollment>();
    public DbSet<LessonProgress> LessonProgress => Set<LessonProgress>();
    public DbSet<ClassSession> ClassSessions => Set<ClassSession>();
    public DbSet<SessionAttendance> SessionAttendance => Set<SessionAttendance>();

    public DbSet<Assessment> Assessments => Set<Assessment>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<QuestionOption> QuestionOptions => Set<QuestionOption>();
    public DbSet<AssessmentAttempt> AssessmentAttempts => Set<AssessmentAttempt>();
    public DbSet<AttemptAnswer> AttemptAnswers => Set<AttemptAnswer>();

    public DbSet<Certificate> Certificates => Set<Certificate>();
    public DbSet<CertificateEvent> CertificateEvents => Set<CertificateEvent>();

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentWebhookEvent> PaymentWebhookEvents => Set<PaymentWebhookEvent>();
    public DbSet<Refund> Refunds => Set<Refund>();

    public DbSet<ReferralCode> ReferralCodes => Set<ReferralCode>();
    public DbSet<ReferralConversion> ReferralConversions => Set<ReferralConversion>();

    public DbSet<CommissionPlan> CommissionPlans => Set<CommissionPlan>();
    public DbSet<CommissionEntry> CommissionEntries => Set<CommissionEntry>();
    public DbSet<Payout> Payouts => Set<Payout>();

    public DbSet<AiMessage> AiMessages => Set<AiMessage>();

    public DbSet<CareerPath> CareerPaths => Set<CareerPath>();
    public DbSet<CareerStep> CareerSteps => Set<CareerStep>();
    public DbSet<CareerEnrollment> CareerEnrollments => Set<CareerEnrollment>();

    public DbSet<XpEvent> XpEvents => Set<XpEvent>();
    public DbSet<UserBadge> UserBadges => Set<UserBadge>();
    public DbSet<PracticeRound> PracticeRounds => Set<PracticeRound>();
    public DbSet<VivaSession> VivaSessions => Set<VivaSession>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Tenant>().HasIndex(x => x.Slug).IsUnique();
        b.Entity<User>().HasIndex(x => x.NormalizedEmail).IsUnique();
        b.Entity<TenantMembership>().HasIndex(x => new { x.TenantId, x.UserId, x.Role }).IsUnique();
        b.Entity<TenantMembership>().HasIndex(x => x.UserId);
        b.Entity<AuditLog>().HasIndex(x => new { x.EntityType, x.EntityId });

        b.Entity<Course>().HasIndex(x => x.Slug).IsUnique();
        b.Entity<Course>().HasIndex(x => new { x.TenantId, x.Status });
        b.Entity<Course>().HasOne(x => x.Instructor).WithMany().HasForeignKey(x => x.InstructorId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CourseVersion>().HasIndex(x => new { x.CourseId, x.Number }).IsUnique();
        b.Entity<CourseModule>().HasMany(x => x.Lessons).WithOne(x => x.Module).HasForeignKey(x => x.ModuleId);
        b.Entity<CourseVersion>().HasMany(x => x.Modules).WithOne().HasForeignKey(x => x.CourseVersionId);
        b.Entity<CourseVersion>().HasMany(x => x.Assessments).WithOne().HasForeignKey(x => x.CourseVersionId);
        b.Entity<CourseInterest>().HasIndex(x => new { x.CourseId, x.Email }).IsUnique();
        b.Entity<CourseVersion>().HasMany(x => x.Milestones).WithOne().HasForeignKey(x => x.CourseVersionId);

        b.Entity<Enrollment>().HasIndex(x => new { x.UserId, x.CourseId }).IsUnique();
        b.Entity<Enrollment>().HasOne(x => x.CourseVersion).WithMany().HasForeignKey(x => x.CourseVersionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LessonProgress>().HasIndex(x => new { x.EnrollmentId, x.LessonId }).IsUnique();
        b.Entity<SessionAttendance>().HasIndex(x => new { x.SessionId, x.UserId }).IsUnique();
        b.Entity<ClassSession>().HasMany(x => x.Attendance).WithOne().HasForeignKey(x => x.SessionId);

        b.Entity<Question>().HasMany(x => x.Options).WithOne().HasForeignKey(x => x.QuestionId);
        b.Entity<Assessment>().HasMany(x => x.Questions).WithOne().HasForeignKey(x => x.AssessmentId);
        b.Entity<AssessmentAttempt>().HasMany(x => x.Answers).WithOne().HasForeignKey(x => x.AttemptId);
        b.Entity<AssessmentAttempt>().HasIndex(x => new { x.AssessmentId, x.UserId });

        b.Entity<Certificate>().HasIndex(x => x.CredentialCode).IsUnique();
        b.Entity<Certificate>().HasIndex(x => x.EnrollmentId);
        b.Entity<CertificateEvent>().HasIndex(x => x.CertificateId);

        b.Entity<Order>().HasMany(x => x.Items).WithOne().HasForeignKey(x => x.OrderId);
        b.Entity<Order>().HasIndex(x => new { x.Provider, x.ProviderOrderId });
        b.Entity<Order>().HasIndex(x => x.BuyerUserId);
        b.Entity<Payment>().HasIndex(x => new { x.Provider, x.ProviderPaymentId }).IsUnique();
        b.Entity<PaymentWebhookEvent>().HasIndex(x => new { x.Provider, x.EventId }).IsUnique();
        b.Entity<Refund>().HasIndex(x => x.OrderId);

        b.Entity<ReferralCode>().HasIndex(x => x.Code).IsUnique();
        b.Entity<ReferralConversion>().HasIndex(x => x.OrderId).IsUnique();

        b.Entity<CommissionPlan>().HasIndex(x => new { x.PlanKey, x.Version }).IsUnique();
        b.Entity<CommissionEntry>().HasIndex(x => x.OrderId);
        b.Entity<CommissionEntry>().HasIndex(x => new { x.RecipientKind, x.RecipientUserId, x.Status });
        b.Entity<CommissionEntry>().HasIndex(x => new { x.RecipientKind, x.RecipientTenantId, x.Status });
        b.Entity<Payout>().HasIndex(x => x.IdempotencyKey).IsUnique();

        b.Entity<AiMessage>().HasIndex(x => new { x.UserId, x.CourseId });

        b.Entity<CareerPath>().HasIndex(x => x.Slug).IsUnique();
        b.Entity<CareerPath>().HasMany(x => x.Steps).WithOne().HasForeignKey(x => x.PathId);
        b.Entity<CareerStep>().HasOne(x => x.Course).WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CareerEnrollment>().HasIndex(x => new { x.PathId, x.UserId }).IsUnique();
        b.Entity<CareerEnrollment>().HasIndex(x => x.UserId);

        b.Entity<XpEvent>().HasIndex(x => new { x.UserId, x.Source, x.RefKey }).IsUnique();
        b.Entity<XpEvent>().HasIndex(x => x.OccurredAt);
        b.Entity<UserBadge>().HasIndex(x => new { x.UserId, x.BadgeKey }).IsUnique();
        b.Entity<PracticeRound>().HasIndex(x => new { x.EnrollmentId, x.UserId });
        b.Entity<VivaSession>().HasIndex(x => new { x.EnrollmentId, x.UserId });
        b.Entity<Notification>().HasIndex(x => new { x.UserId, x.DedupeKey }).IsUnique();
        b.Entity<Notification>().HasIndex(x => new { x.UserId, x.ReadAt });

        // Store enums as readable strings — easier to audit and safe to reorder.
        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var prop in entity.GetProperties().Where(p => (Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType).IsEnum))
                prop.SetProviderClrType(typeof(string));
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        // All timestamps are UTC. SQLite drops DateTime.Kind, so restore it on read.
        c.Properties<DateTime>().HaveConversion<UtcConverter>();
        c.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    private class UtcConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private class NullableUtcConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime?, DateTime?>(
        v => v == null ? v : v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime(),
        v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
}
