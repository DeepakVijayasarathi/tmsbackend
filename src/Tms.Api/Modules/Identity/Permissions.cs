namespace Tms.Api.Modules.Identity;

public static class Roles
{
    // Platform tenant roles — grant global scope.
    public const string SuperAdmin = "SuperAdmin";
    public const string FinanceAdmin = "FinanceAdmin";
    public const string SupportAdmin = "SupportAdmin";
    public const string ContentModerator = "ContentModerator";

    // Institution roles — scoped to their tenant.
    public const string CollegeAdmin = "CollegeAdmin";
    public const string Professor = "Professor";
    public const string CourseManager = "CourseManager";
    public const string FinanceManager = "FinanceManager";
    public const string Learner = "Learner";
    public const string CorporateAdmin = "CorporateAdmin";
    public const string ProviderAdmin = "ProviderAdmin";

    public static readonly string[] All =
    [
        SuperAdmin, FinanceAdmin, SupportAdmin, ContentModerator,
        CollegeAdmin, Professor, CourseManager, FinanceManager, Learner, CorporateAdmin, ProviderAdmin,
    ];
}

public static class Permissions
{
    public const string CourseCreate = "course.create";
    public const string CourseEditAny = "course.edit_any";
    public const string CoursePublish = "course.publish";
    public const string AssessmentGrade = "assessment.grade";
    public const string CertificateIssue = "certificate.issue";
    public const string CertificateRevoke = "certificate.revoke";
    public const string CommissionConfigure = "commission.configure";
    public const string CommissionApprove = "commission.approve";
    public const string PayoutApprove = "payout.approve";
    public const string BillingManage = "billing.manage";
    public const string AnalyticsView = "analytics.view";
    public const string MembersManage = "members.manage";

    private static readonly Dictionary<string, string[]> ByRole = new()
    {
        [Roles.SuperAdmin] =
        [
            CourseCreate, CourseEditAny, CoursePublish, AssessmentGrade, CertificateIssue, CertificateRevoke,
            CommissionConfigure, CommissionApprove, PayoutApprove, BillingManage, AnalyticsView, MembersManage,
        ],
        [Roles.FinanceAdmin] = [CommissionConfigure, CommissionApprove, PayoutApprove, BillingManage, AnalyticsView],
        [Roles.SupportAdmin] = [AnalyticsView],
        [Roles.ContentModerator] = [CourseEditAny, CoursePublish],
        [Roles.CollegeAdmin] = [CourseCreate, CourseEditAny, CoursePublish, AssessmentGrade, CertificateRevoke, AnalyticsView, MembersManage],
        [Roles.ProviderAdmin] = [CourseCreate, CourseEditAny, CoursePublish, AssessmentGrade, CertificateRevoke, AnalyticsView, MembersManage],
        [Roles.Professor] = [CourseCreate, CoursePublish, AssessmentGrade],
        [Roles.CourseManager] = [CourseCreate, CourseEditAny, CoursePublish],
        [Roles.FinanceManager] = [AnalyticsView],
        [Roles.CorporateAdmin] = [AnalyticsView, MembersManage],
        [Roles.Learner] = [],
    };

    public static IEnumerable<string> For(string role) => ByRole.TryGetValue(role, out var p) ? p : [];

    public static bool IsPlatformRole(string role) =>
        role is Roles.SuperAdmin or Roles.FinanceAdmin or Roles.SupportAdmin or Roles.ContentModerator;
}
