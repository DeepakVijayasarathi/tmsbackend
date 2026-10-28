using Microsoft.EntityFrameworkCore;
using Tms.Api.Modules.Assessments;
using Tms.Api.Modules.Careers;
using Tms.Api.Modules.Catalog;
using Tms.Api.Modules.Certificates;
using Tms.Api.Modules.Classroom;
using Tms.Api.Modules.Commissions;
using Tms.Api.Modules.Identity;
using Tms.Api.Modules.Learning;
using Tms.Api.Modules.Referrals;

namespace Tms.Api.Data;

/// <summary>Demo tenants, users, courses and a default commission plan for local development.</summary>
public static class DemoSeed
{
    public const string Password = "Passw0rd!";

    public static async Task RunAsync(TmsDbContext db)
    {
        if (await db.Tenants.AnyAsync()) return;

        var platform = new Tenant { Name = "TMS Platform", Slug = "platform", Kind = TenantKind.Platform, IsVerified = true };
        var college = new Tenant { Name = "Northfield College", Slug = "northfield-college", Kind = TenantKind.College, IsVerified = true, BrandColor = "#1d4ed8" };
        db.Tenants.AddRange(platform, college);

        User NewUser(string email, string name, string? headline = null)
        {
            var u = new User { Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = name, Headline = headline };
            u.PasswordHash = IdentityEndpoints.HashPassword(u, Password);
            db.Users.Add(u);
            return u;
        }

        var admin = NewUser("admin@tms.local", "Platform Admin");
        var finance = NewUser("finance@tms.local", "Finance Admin");
        var collegeAdmin = NewUser("college@tms.local", "Northfield Admin");
        var prof = NewUser("prof@tms.local", "Dr. Meera Iyer", "Associate Professor of Data Science");
        var learner = NewUser("learner@tms.local", "Ravi Kumar");
        var affiliate = NewUser("affiliate@tms.local", "Sam Fernandes");

        db.TenantMemberships.AddRange(
            new TenantMembership { TenantId = platform.Id, UserId = admin.Id, Role = Roles.SuperAdmin },
            new TenantMembership { TenantId = platform.Id, UserId = finance.Id, Role = Roles.FinanceAdmin },
            new TenantMembership { TenantId = college.Id, UserId = collegeAdmin.Id, Role = Roles.CollegeAdmin },
            new TenantMembership { TenantId = college.Id, UserId = prof.Id, Role = Roles.Professor },
            new TenantMembership { TenantId = college.Id, UserId = learner.Id, Role = Roles.Learner });

        db.CommissionPlans.Add(new CommissionPlan
        {
            Name = "Platform default",
            ProfessorBp = 4000,
            CollegeBp = 1000,
            ReferralBp = 500,
            // Referrers are paid in stages as the learner they referred progresses.
            ReferralSchedule = "Purchase:30,FirstMilestone:30,CourseCompleted:40",
            Stacking = StackingPolicy.NoInstructorReferral,
            HoldDays = 0, // production should hold earnings through the refund window
            EffectiveFrom = DateTime.UtcNow.AddDays(-1),
            Status = PlanStatus.Active,
        });

        db.ReferralCodes.Add(new ReferralCode { Code = "FRIEND10", OwnerUserId = affiliate.Id, Campaign = "Launch" });

        var data = DataAnalysisCourse(college.Id, prof.Id);
        var comms = CommunicationCourse(college.Id, prof.Id);
        var deploy = DeploymentCourse(college.Id, prof.Id);
        db.Courses.AddRange(data, comms, deploy);
        AddSyllabus(data,
            outcomes: ["Frame a narrow, answerable analysis question", "Load and inspect CSV data with pandas", "Clean missing values, duplicates and bad types defensibly", "Summarise groups with named aggregations", "Report findings with honest caveats"],
            prerequisites: ["Basic Python: variables, lists and functions", "Comfort opening a CSV in a spreadsheet"],
            objectives: [["State the measure, comparison and population", "Load, inspect and clean a raw export"], ["Aggregate by group and read group sizes", "Lead with the answer and name alternative explanations"]],
            milestones: v => [
                Milestone(v, 1, "Data in shape", "Finish the first module: framing, loading and cleaning.", MilestoneKind.CompleteModule, module: 0, dueDays: 7),
                Milestone(v, 2, "Join a live lab", "Attend one live class to practise with the instructor.", MilestoneKind.AttendClasses, count: 1, dueDays: 14),
                Milestone(v, 3, "Exploring and reporting", "Finish the second module.", MilestoneKind.CompleteModule, module: 1, dueDays: 21),
                Milestone(v, 4, "Pass the final assessment", "Score at least the pass mark to unlock your certificate.", MilestoneKind.PassAssessment, assessment: 0, dueDays: 28),
            ]);
        AddSyllabus(comms,
            outcomes: ["Write emails whose request is clear in the first line", "Plan meetings that end with decisions, owners and dates"],
            prerequisites: ["None — open to everyone"],
            objectives: [["Lead with the ask and use the subject as a summary", "Run a meeting with an agenda and send notes the same day"]],
            milestones: v => [
                Milestone(v, 1, "Finish the lessons", "Complete both lessons on clear communication.", MilestoneKind.CompleteModule, module: 0, dueDays: 3),
                Milestone(v, 2, "Attend the live workshop", "Practise with your cohort in a live class.", MilestoneKind.AttendClasses, count: 1, dueDays: 7),
                Milestone(v, 3, "Pass the quick check", null, MilestoneKind.PassAssessment, assessment: 0, dueDays: 10),
            ]);
        AddSyllabus(deploy,
            outcomes: ["Plan a customer deployment from discovery to sign-off", "Integrate with customer systems safely", "Debug issues in a customer's production environment", "Hand over with runbooks the customer can own"],
            prerequisites: ["Comfort with the command line", "Basic Python or another scripting language"],
            objectives: [["Run a discovery call and write a deployment plan", "Map customer systems and access needs"], ["Ship behind a feature flag and verify with the customer", "Write a runbook and hand over"]],
            milestones: v => [
                Milestone(v, 1, "Deployment plan ready", "Finish the discovery and planning module.", MilestoneKind.CompleteModule, module: 0, dueDays: 7),
                Milestone(v, 2, "Shipped and handed over", "Finish the go-live module.", MilestoneKind.CompleteModule, module: 1, dueDays: 14),
                Milestone(v, 3, "Pass the deployment check", null, MilestoneKind.PassAssessment, assessment: 0, dueDays: 21),
            ]);

        // Vendor partner example: a company that publishes its own course and issues certificates under its name.
        var vendor = new Tenant { Name = "Nimbus Cloud Labs", Slug = "nimbus-cloud-labs", Kind = TenantKind.TrainingProvider, IsVerified = true, BrandColor = "#0d9488" };
        var vendorAdmin = NewUser("vendor@tms.local", "Priya Nair", "Head of Training, Nimbus Cloud Labs");
        db.Tenants.Add(vendor);
        db.TenantMemberships.AddRange(
            new TenantMembership { TenantId = vendor.Id, UserId = vendorAdmin.Id, Role = Roles.ProviderAdmin },
            new TenantMembership { TenantId = vendor.Id, UserId = vendorAdmin.Id, Role = Roles.Professor });
        var cloud = CloudCourse(vendor.Id, vendorAdmin.Id);
        db.Courses.Add(cloud);
        AddSyllabus(cloud,
            outcomes: ["Explain regions, zones and the shared-responsibility model", "Choose between VMs, containers and serverless for a workload"],
            prerequisites: ["None — open to everyone"],
            objectives: [["Describe how cloud infrastructure is organised", "Pick the right compute option and justify it"]],
            milestones: v => [
                Milestone(v, 1, "Finish the lessons", "Complete both cloud basics lessons.", MilestoneKind.CompleteModule, module: 0, dueDays: 5),
                Milestone(v, 2, "Pass the cloud check", "Score the pass mark to earn your Nimbus certificate.", MilestoneKind.PassAssessment, assessment: 0, dueDays: 10),
            ]);
        // A completed enrolment with an issued certificate, so the partner credential can be verified at /verify/TMS-NMBS-CERT-2345.
        var cloudEnrollment = new Enrollment
        {
            UserId = learner.Id, CourseId = cloud.Id, CourseVersionId = cloud.PublishedVersionId!.Value,
            Source = EnrollmentSource.Free, Status = EnrollmentStatus.Completed, CompletedAt = DateTime.UtcNow.AddDays(-3),
        };
        db.Enrollments.Add(cloudEnrollment);
        db.Certificates.Add(new Certificate
        {
            CredentialCode = "TMS-NMBS-CERT-2345", UserId = learner.Id, CourseId = cloud.Id, CourseVersionId = cloud.PublishedVersionId!.Value,
            EnrollmentId = cloudEnrollment.Id, IssuerTenantId = vendor.Id, RecipientName = learner.FullName, CourseTitle = cloud.Title,
            IssuerName = vendor.Name, ScorePercent = 90, IssuedAt = DateTime.UtcNow.AddDays(-3), Status = CertificateStatus.Active,
        });

        // Meet to Career: the Forward Deployed Engineer path strings the three courses and live practice together.
        var fde = new CareerPath
        {
            Title = "Become a Forward Deployed Engineer",
            Slug = "forward-deployed-engineer",
            Role = "Forward Deployed Engineer",
            CredentialPrefix = "FDE",
            Summary = "Work at the customer's side: understand their problem, analyse their data and ship software that works in their world.",
            Description = "Forward Deployed Engineers combine engineering with consulting. This path takes you from clear client communication, through hands-on data analysis, to planning and running real customer deployments — with live classes alongside practitioners. Finish every milestone to earn the FDE career credential.",
            Skills = string.Join("\n", ["Client communication", "Python & data analysis", "Deploying and integrating software", "Debugging in production", "Turning business problems into technical plans"]),
        };
        fde.Steps =
        [
            new CareerStep { PathId = fde.Id, Order = 1, Kind = CareerStepKind.CompleteCourse, CourseId = comms.Id, Title = "Communicate like a consultant", Description = "Clear emails and decisive meetings — the basis of every customer relationship." },
            new CareerStep { PathId = fde.Id, Order = 2, Kind = CareerStepKind.CompleteCourse, CourseId = data.Id, Title = "Analyse customer data", Description = "Load, clean and explore real datasets to answer the customer's question." },
            new CareerStep { PathId = fde.Id, Order = 3, Kind = CareerStepKind.CompleteCourse, CourseId = deploy.Id, Title = "Ship at the customer site", Description = "Plan, integrate, go live and hand over." },
            new CareerStep { PathId = fde.Id, Order = 4, Kind = CareerStepKind.AttendClasses, RequiredCount = 3, Title = "Learn live with practitioners", Description = "Attend three live classes from courses on this path." },
        ];
        db.CareerPaths.Add(fde);
        db.CareerEnrollments.Add(new CareerEnrollment { PathId = fde.Id, UserId = learner.Id });
        learner.Interests = "Forward Deployed Engineering|Data & Analytics|Customer Success";

        // The learner is enrolled in the free course so the live-classroom flow works out of the box.
        db.Enrollments.Add(new Enrollment
        {
            UserId = learner.Id, CourseId = comms.Id, CourseVersionId = comms.PublishedVersionId!.Value,
            Source = EnrollmentSource.Free, Status = EnrollmentStatus.Active,
        });

        var now = DateTime.UtcNow;
        var soon = now.AddMinutes(10 - now.Minute % 5); // joinable right after seeding
        ClassSession Live(Course course, string title, string description, DateTime starts, int minutes) => new()
        {
            CourseId = course.Id,
            Title = title,
            Description = description,
            StartsAt = starts,
            EndsAt = starts.AddMinutes(minutes),
            MeetingRoom = JitsiTokenService.NewRoomName(title),
            Capacity = 30,
            InstructorId = prof.Id,
        };
        var past = Live(comms, "Kick-off: how this course works", "Introductions, course map and how live classes count towards your certificate.", now.Date.AddDays(-7).AddHours(10), 45);
        past.Status = SessionStatus.Ended;
        past.StartedAt = past.StartsAt;
        past.EndedAt = past.EndsAt;
        db.ClassSessions.AddRange(
            Live(comms, "Live workshop: emails people act on", "Bring a real email you need to send this week — we'll rewrite it together.", soon, 60),
            Live(comms, "Practice round: running a 15-minute meeting", "Role-play an agenda, a decision and the follow-up notes.", now.Date.AddDays(3).AddHours(11), 60),
            Live(data, "Office hours: group-bys and aggregation", "Drop-in Q&A on named aggregations, medians and group sizes.", now.Date.AddDays(2).AddHours(12), 45),
            Live(data, "Live lab: cleaning a messy sales dataset", "Hands-on lab — fix types, missing values and duplicates in a real export.", now.Date.AddDays(5).AddHours(10), 120),
            past);
        db.SessionAttendance.Add(new SessionAttendance
        {
            SessionId = past.Id, UserId = learner.Id, Status = AttendanceStatus.Present,
            JoinedAt = past.StartsAt.AddMinutes(2), LeftAt = past.EndsAt,
        });

        DemoShowcase.Add(db, learner, prof, college.Id);
        await db.SaveChangesAsync();
    }

    private static void AddSyllabus(Course course, string[] outcomes, string[] prerequisites, string[][] objectives, Func<CourseVersion, Milestone[]> milestones)
    {
        var v = course.Versions[0];
        v.LearningOutcomes = string.Join("\n", outcomes);
        v.Prerequisites = string.Join("\n", prerequisites);
        for (var i = 0; i < v.Modules.Count; i++)
        {
            v.Modules[i].Week = i + 1;
            if (i < objectives.Length) v.Modules[i].Objectives = string.Join("\n", objectives[i]);
        }
        v.Milestones = milestones(v).ToList();
    }

    private static Milestone Milestone(CourseVersion v, int order, string title, string? description, MilestoneKind kind,
        int? module = null, int? assessment = null, int count = 0, int? dueDays = null) => new()
    {
        Title = title, Description = description, Order = order, Kind = kind, DueDays = dueDays, RequiredCount = count,
        ModuleId = module is { } m ? v.Modules[m].Id : null,
        AssessmentId = assessment is { } a ? v.Assessments[a].Id : null,
    };

    private static Course Publish(Course course)
    {
        var v = course.Versions[0];
        v.IsDraft = false;
        v.PublishedAt = DateTime.UtcNow;
        course.PublishedVersionId = v.Id;
        course.Status = CourseStatus.Published;
        return course;
    }

    private static Question Q(QuestionKind kind, string prompt, string explanation, params (string Text, bool Correct)[] options) => new()
    {
        Kind = kind,
        Prompt = prompt,
        Explanation = explanation,
        Options = options.Select((o, i) => new QuestionOption { Text = o.Text, IsCorrect = o.Correct, Order = i }).ToList(),
    };

    private static Course DataAnalysisCourse(Guid tenantId, Guid instructorId) => Publish(new Course
    {
        TenantId = tenantId,
        InstructorId = instructorId,
        Title = "Practical Data Analysis with Python",
        Slug = "practical-data-analysis-with-python",
        Summary = "Load, clean, explore and summarise real datasets with pandas — from raw CSV to a defensible answer.",
        Description = "A hands-on course for analysts and students who know basic Python. You will work through the full analysis loop: framing a question, loading data, fixing quality problems, exploring with group-bys and plots, and reporting what the data does and does not support.",
        Category = "Data & Analytics",
        Level = CourseLevel.Intermediate,
        PriceMinor = 100_000,
        Versions =
        [
            new CourseVersion
            {
                Number = 1,
                MinSessionsAttended = 0,
                CertificateValidityMonths = 36,
                Modules =
                [
                    new CourseModule
                    {
                        Title = "Getting data in shape", Order = 1,
                        Lessons =
                        [
                            new Lesson
                            {
                                Title = "Framing an analysis question", Order = 1, Kind = LessonKind.Text, DurationMinutes = 12, IsPreview = true,
                                Content = """
                                    Every useful analysis starts with a question narrow enough to answer. "How are sales?" is not a question; "Did average order value change after the March price increase, and for which regions?" is.

                                    A good analysis question names the measure (average order value), the comparison (before vs after March), and the population (orders, broken down by region). Write it down before you open the data. It tells you which columns you need and stops you wandering.

                                    Next, list your assumptions. If you compare March to February, you are assuming nothing else changed between them — no holiday, no marketing campaign. Writing assumptions down makes it obvious which ones you need to check.
                                    """,
                            },
                            new Lesson
                            {
                                Title = "Loading data with pandas", Order = 2, Kind = LessonKind.Text, DurationMinutes = 18,
                                Content = """
                                    pandas reads tabular data into a DataFrame. For a CSV file use pd.read_csv("orders.csv"). Pass parse_dates=["order_date"] so date columns become real datetimes instead of strings, and dtype={"pincode": "string"} for identifiers that look numeric but are not numbers — otherwise leading zeros are silently dropped.

                                    After loading, always inspect: df.shape gives rows and columns, df.head() shows the first rows, and df.info() lists each column's type and how many non-null values it has. df.describe() summarises numeric columns with count, mean, standard deviation and quartiles.

                                    Large files can be read in pieces with the chunksize argument, which returns an iterator of DataFrames. Use it when the file does not fit comfortably in memory.
                                    """,
                            },
                            new Lesson
                            {
                                Title = "Cleaning: missing values and duplicates", Order = 3, Kind = LessonKind.Text, DurationMinutes = 22,
                                Content = """
                                    Missing values appear as NaN. df.isna().sum() counts them per column. You have three honest options: drop the rows (df.dropna(subset=["amount"])), fill them with a justified value (df["discount"].fillna(0) when a missing discount genuinely means no discount), or keep them and report how many there were. Filling with the mean is rarely justified — it hides uncertainty and shrinks the spread of the data.

                                    Duplicates inflate totals. df.duplicated(subset=["order_id"]).sum() counts repeated order ids; df.drop_duplicates(subset=["order_id"], keep="first") removes them. Decide which columns define a duplicate before you drop anything.

                                    Fix types early: pd.to_numeric(df["amount"], errors="coerce") turns unparseable values into NaN instead of raising, so you can count and inspect them rather than lose them silently.
                                    """,
                            },
                        ],
                    },
                    new CourseModule
                    {
                        Title = "Exploring and reporting", Order = 2,
                        Lessons =
                        [
                            new Lesson
                            {
                                Title = "Group-by and aggregation", Order = 1, Kind = LessonKind.Text, DurationMinutes = 20,
                                Content = """
                                    groupby splits data into groups, applies a function to each, and combines the results. df.groupby("region")["amount"].mean() gives average order value per region.

                                    Use agg to compute several statistics at once: df.groupby("region").agg(orders=("order_id", "count"), revenue=("amount", "sum"), aov=("amount", "mean")). Named aggregations keep column names readable.

                                    Always look at group sizes alongside averages. An average computed from four orders is not comparable to one computed from four thousand. The median is more robust than the mean when a few very large orders skew a group.
                                    """,
                            },
                            new Lesson
                            {
                                Title = "Communicating findings", Order = 2, Kind = LessonKind.Text, DurationMinutes = 15,
                                Content = """
                                    Lead with the answer to the question you framed, then the evidence, then the caveats. "Average order value rose 8% after the March price change, driven by the North and West regions; the South was flat" is a finding. A table of every group-by you ran is not.

                                    Say what the data cannot tell you. A before/after comparison shows association, not cause — a competitor closing in March would produce the same pattern. Name the main alternative explanations you could not rule out.

                                    Keep one chart per message and label axes with units. If a reader needs the notebook to understand the chart, the chart is not finished.
                                    """,
                            },
                        ],
                    },
                ],
                Assessments =
                [
                    new Assessment
                    {
                        Title = "Final assessment", PassMarkPercent = 70, TimeLimitMinutes = 20, MaxAttempts = 3,
                        Questions =
                        [
                            Q(QuestionKind.SingleChoice, "Why pass dtype={\"pincode\": \"string\"} when loading a CSV?",
                                "Identifiers that look numeric lose leading zeros if parsed as numbers (Lesson: Loading data with pandas).",
                                ("To keep leading zeros in identifiers that are not really numbers", true),
                                ("To make the file load faster", false),
                                ("To convert the column to a datetime", false),
                                ("To drop rows with missing pincodes", false)),
                            Q(QuestionKind.SingleChoice, "Which call counts missing values in each column?",
                                "isna() marks missing cells and sum() counts them per column (Lesson: Cleaning).",
                                ("df.isna().sum()", true), ("df.describe()", false), ("df.dropna()", false), ("df.shape", false)),
                            Q(QuestionKind.TrueFalse, "Filling missing amounts with the column mean is usually the safest default.",
                                "Mean-filling hides uncertainty and shrinks the spread; it is rarely justified (Lesson: Cleaning).",
                                ("True", false), ("False", true)),
                            Q(QuestionKind.MultipleChoice, "Which should you report alongside a per-group average? Select all that apply.",
                                "Group size tells you how much to trust an average, and the median resists skew from a few large values (Lesson: Group-by and aggregation).",
                                ("The number of rows in each group", true), ("The median for skewed groups", true),
                                ("The DataFrame's memory usage", false), ("The column data types", false)),
                            Q(QuestionKind.SingleChoice, "A before/after comparison shows average order value rose after a price change. What can you conclude?",
                                "Before/after comparisons show association; other changes in the same period could explain it (Lesson: Communicating findings).",
                                ("The values are associated; other explanations still need ruling out", true),
                                ("The price change caused the rise", false),
                                ("Nothing, because averages are unreliable", false),
                                ("The rise will continue next quarter", false)),
                        ],
                    },
                ],
            },
        ],
    });

    private static Course DeploymentCourse(Guid tenantId, Guid instructorId) => Publish(new Course
    {
        TenantId = tenantId,
        InstructorId = instructorId,
        Title = "Customer Deployments: Shipping Software On-Site",
        Slug = "customer-deployments",
        Summary = "Plan, integrate and hand over software in a customer's environment — the core craft of a Forward Deployed Engineer.",
        Description = "A practical course on running deployments with customers: discovery, access and integration planning, shipping safely behind flags, debugging in production and handing over with runbooks the customer can own.",
        Category = "Software Engineering",
        Level = CourseLevel.Intermediate,
        PriceMinor = 150_000,
        Versions =
        [
            new CourseVersion
            {
                Number = 1,
                MinSessionsAttended = 0,
                CertificateValidityMonths = 36,
                VivaEnabled = true, // ends with a short AI interview, like a real FDE client conversation
                VivaPassMarkPercent = 65,
                VivaQuestionCount = 3,
                Modules =
                [
                    new CourseModule
                    {
                        Title = "Discovery and planning", Order = 1,
                        Lessons =
                        [
                            new Lesson
                            {
                                Title = "Running a discovery call", Order = 1, Kind = LessonKind.Text, DurationMinutes = 15, IsPreview = true,
                                Content = """
                                    A deployment succeeds or fails in discovery. Before writing any code, learn what the customer is trying to achieve, how they measure success, and who signs off.

                                    Ask about the current workflow and where it hurts, the systems involved, data volumes, security rules and deadlines. Write the answers down and play them back at the end of the call.

                                    Close with a short written summary: goal, success measure, constraints, open questions and next steps with owners.
                                    """,
                            },
                            new Lesson
                            {
                                Title = "Mapping systems and access", Order = 2, Kind = LessonKind.Text, DurationMinutes = 18,
                                Content = """
                                    Draw the customer's systems as boxes and the data flowing between them as arrows. Mark which systems you must read from, which you must write to, and who owns each.

                                    List the access you need — network routes, service accounts, API keys — and request it early. Access requests are the most common cause of slipped go-live dates.

                                    Agree a test environment that mirrors production closely enough to catch integration problems before go-live.
                                    """,
                            },
                        ],
                    },
                    new CourseModule
                    {
                        Title = "Go-live and handover", Order = 2,
                        Lessons =
                        [
                            new Lesson
                            {
                                Title = "Shipping safely", Order = 1, Kind = LessonKind.Text, DurationMinutes = 16,
                                Content = """
                                    Release behind a feature flag so you can turn the change on for a small group first and off instantly if something breaks.

                                    Verify with the customer using their real workflow, not just your tests. Watch logs and metrics during the first hours, and agree in advance what would trigger a rollback.

                                    When something fails in production, reproduce it, find the smallest fix, and tell the customer what happened in plain language.
                                    """,
                            },
                            new Lesson
                            {
                                Title = "Runbooks and handover", Order = 2, Kind = LessonKind.Text, DurationMinutes = 12,
                                Content = """
                                    A deployment is finished when the customer can run it without you. Write a runbook: how to start and stop it, where the logs are, the common errors and their fixes, and who to call.

                                    Walk the customer's team through the runbook and have them perform one routine task while you watch. Then hand over credentials and close out access you no longer need.
                                    """,
                            },
                        ],
                    },
                ],
                Assessments =
                [
                    new Assessment
                    {
                        Title = "Deployment check", PassMarkPercent = 70, MaxAttempts = 3,
                        Questions =
                        [
                            Q(QuestionKind.SingleChoice, "What is the most common cause of slipped go-live dates?",
                                "Access requests take time; request them early (Lesson: Mapping systems and access).",
                                ("Late access requests", true), ("Too many tests", false), ("Feature flags", false)),
                            Q(QuestionKind.TrueFalse, "A deployment is finished when your code is in production.",
                                "It is finished when the customer can run it without you (Lesson: Runbooks and handover).",
                                ("True", false), ("False", true)),
                        ],
                    },
                ],
            },
        ],
    });

    private static Course CommunicationCourse(Guid tenantId, Guid instructorId) => Publish(new Course
    {
        TenantId = tenantId,
        InstructorId = instructorId,
        Title = "Workplace Communication Essentials",
        Slug = "workplace-communication-essentials",
        Summary = "Write clearer emails and run shorter, more useful meetings. Free introductory course.",
        Description = "Two short lessons on the habits that make written and spoken workplace communication easier to act on.",
        Category = "Professional Skills",
        Level = CourseLevel.Beginner,
        PriceMinor = 0,
        Versions =
        [
            new CourseVersion
            {
                Number = 1,
                Modules =
                [
                    new CourseModule
                    {
                        Title = "Clear communication", Order = 1,
                        Lessons =
                        [
                            new Lesson
                            {
                                Title = "Emails people act on", Order = 1, Kind = LessonKind.Text, DurationMinutes = 10, IsPreview = true,
                                Content = """
                                    Put the request or decision in the first line. Readers skim; if the ask is in the fourth paragraph, it will be missed.

                                    Use the subject line as a summary: "Approval needed by Friday: Q3 training budget" beats "Budget". One email, one topic — separate topics get separate threads so each can be answered and closed.

                                    End with a specific next step and an owner: who does what by when.
                                    """,
                            },
                            new Lesson
                            {
                                Title = "Meetings with a purpose", Order = 2, Kind = LessonKind.Text, DurationMinutes = 10,
                                Content = """
                                    A meeting needs a stated purpose and a decision or output. If the purpose is only to share information, send a written update instead.

                                    Share the agenda in advance, invite only the people needed for the decision, and close by reading back decisions, owners and dates. Send those notes within the day.
                                    """,
                            },
                        ],
                    },
                ],
                Assessments =
                [
                    new Assessment
                    {
                        Title = "Quick check", PassMarkPercent = 60, MaxAttempts = 5, ShuffleQuestions = false,
                        Questions =
                        [
                            Q(QuestionKind.SingleChoice, "Where should the main request in an email go?",
                                "Readers skim, so lead with the ask (Lesson: Emails people act on).",
                                ("In the first line", true), ("In the closing paragraph", false), ("In the signature", false)),
                            Q(QuestionKind.TrueFalse, "A meeting whose only purpose is sharing information is often better as a written update.",
                                "Meetings should produce a decision or output (Lesson: Meetings with a purpose).",
                                ("True", true), ("False", false)),
                        ],
                    },
                ],
            },
        ],
    });

    private static Course CloudCourse(Guid tenantId, Guid instructorId) => Publish(new Course
    {
        TenantId = tenantId,
        InstructorId = instructorId,
        Title = "Cloud Foundations by Nimbus",
        Slug = "cloud-foundations-by-nimbus",
        Summary = "A free starter course from our partner Nimbus Cloud Labs. Earn a Nimbus-issued certificate.",
        Description = "Nimbus Cloud Labs trains its own engineers on these basics. Two short lessons cover how cloud infrastructure is organised and how to pick the right compute for a workload.",
        Category = "Cloud & DevOps",
        Level = CourseLevel.Beginner,
        PriceMinor = 0,
        Versions =
        [
            new CourseVersion
            {
                Number = 1,
                Modules =
                [
                    new CourseModule
                    {
                        Title = "Cloud basics", Order = 1,
                        Lessons =
                        [
                            new Lesson
                            {
                                Title = "Regions, zones and who is responsible for what", Order = 1, Kind = LessonKind.Text, DurationMinutes = 12, IsPreview = true,
                                Content = """
                                    A region is a geographic area with its own data centres; a zone is an isolated data centre inside a region. Spread a service across zones so one failure doesn't take it down, and choose a region close to your users and inside the right legal boundary.

                                    In the shared-responsibility model the provider secures the hardware, network and hypervisor. You secure what you put on top: your data, access rules, operating-system patches on VMs and your application code.
                                    """,
                            },
                            new Lesson
                            {
                                Title = "VMs, containers or serverless?", Order = 2, Kind = LessonKind.Text, DurationMinutes = 12,
                                Content = """
                                    Virtual machines give full control and suit legacy or long-running software, but you patch and scale them yourself. Containers package an app with its dependencies and start in seconds, which suits services you deploy often.

                                    Serverless functions run only when called and scale to zero, so they suit spiky, event-driven work. They are a poor fit for long jobs or steady heavy load, where a VM or container is cheaper.
                                    """,
                            },
                        ],
                    },
                ],
                Assessments =
                [
                    new Assessment
                    {
                        Title = "Cloud check", PassMarkPercent = 60, MaxAttempts = 5, ShuffleQuestions = false,
                        Questions =
                        [
                            Q(QuestionKind.SingleChoice, "Why deploy a service across several availability zones?",
                                "Zones are isolated data centres, so spreading across them survives one failing (Lesson: Regions, zones…).",
                                ("So one data-centre failure doesn't take it down", true), ("To lower the price", false), ("Because one zone can only run one VM", false)),
                            Q(QuestionKind.TrueFalse, "Under the shared-responsibility model, the cloud provider patches the operating system on your VMs.",
                                "Patching the OS on a VM is the customer's job (Lesson: Regions, zones…).",
                                ("True", false), ("False", true)),
                        ],
                    },
                ],
            },
        ],
    });
}
