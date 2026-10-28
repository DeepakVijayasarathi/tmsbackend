using Tms.Api.Common;
using Tms.Api.Modules.Assessments;
using Tms.Api.Modules.Careers;
using Tms.Api.Modules.Catalog;
using Tms.Api.Modules.Certificates;
using Tms.Api.Modules.Classroom;
using Tms.Api.Modules.Gamification;
using Tms.Api.Modules.Identity;
using Tms.Api.Modules.Learning;

namespace Tms.Api.Data;

/// <summary>
/// Extra demo content so every page looks lived-in: AI and data courses from more partner companies,
/// a second career path, a cohort of learners with progress and certificates, live classes this week,
/// and an activity history for the main demo learner. All companies and people are fictional.
/// </summary>
public static class DemoShowcase
{
    private record LessonSpec(string Title, int Minutes, string Content);
    private record QuestionSpec(string Prompt, string Explanation, string Right, params string[] Wrong);

    public static void Add(TmsDbContext db, User learner, User prof, Guid collegeId)
    {
        var rng = new Random(42);
        var now = DateTime.UtcNow;

        User NewUser(string email, string name, string? headline = null)
        {
            var u = new User { Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = name, Headline = headline };
            u.PasswordHash = IdentityEndpoints.HashPassword(u, DemoSeed.Password);
            db.Users.Add(u);
            return u;
        }

        // Partner companies.
        Tenant Partner(string name, string slug, string color, string adminEmail, string adminName, string headline, out User admin)
        {
            var t = new Tenant { Name = name, Slug = slug, Kind = TenantKind.TrainingProvider, IsVerified = true, BrandColor = color };
            db.Tenants.Add(t);
            admin = NewUser(adminEmail, adminName, headline);
            db.TenantMemberships.AddRange(
                new TenantMembership { TenantId = t.Id, UserId = admin.Id, Role = Roles.ProviderAdmin },
                new TenantMembership { TenantId = t.Id, UserId = admin.Id, Role = Roles.Professor });
            return t;
        }
        var bright = Partner("BrightPath AI Academy", "brightpath-ai-academy", "#2563eb", "brightpath@tms.local", "Sara Thomas", "Lead AI Trainer, BrightPath AI Academy", out var sara);
        var forge = Partner("DataForge Analytics", "dataforge-analytics", "#7c3aed", "dataforge@tms.local", "Arjun Mehta", "Principal Analyst, DataForge Analytics", out var arjun);

        var genAi = Course(bright.Id, sara.Id, "Generative AI for Everyday Work", "AI & Machine Learning", CourseLevel.Beginner, 0,
            "Use AI assistants to write, summarise and plan faster, safely. Free course with a BrightPath certificate.",
            [
                new("What generative AI is good at", 12, "Generative AI predicts likely text, so it is great at drafting, rewriting, summarising and brainstorming. It is weak at facts it has not been given and at precise arithmetic.\n\nTreat its output as a first draft from a fast, confident intern: useful, but always checked."),
                new("Working safely with AI at work", 12, "Never paste confidential customer data or passwords into a public AI tool. Check your company's policy first.\n\nVerify every fact, number and quote before you share AI-written work, and say when content was AI-assisted."),
            ],
            [
                new("Which task is generative AI most reliable at?", "Drafting and rewriting play to its strengths.", "Drafting a first version of an email", "Quoting exact sales figures from memory", "Doing precise tax calculations"),
                new("What should you do before sharing AI-written facts?", "Always verify facts, numbers and quotes.", "Verify them against a trusted source", "Nothing, AI is always right", "Add more emojis"),
            ]);
        var prompts = Course(bright.Id, sara.Id, "Prompt Engineering Fundamentals", "AI & Machine Learning", CourseLevel.Intermediate, 99900,
            "Write prompts that get reliable, well-structured answers: roles, context, examples and output formats.",
            [
                new("Anatomy of a great prompt", 15, "A strong prompt gives the model a role, the context it needs, a clear task and the format you want back.\n\nCompare \"Summarise this\" with \"You are a support lead. Summarise this ticket thread in three bullet points for an engineer: the problem, what was tried, and the next step.\""),
                new("Examples and output formats", 15, "Showing one or two examples of the output you want is often more effective than describing it.\n\nAsk for structured output, such as a table or JSON with named fields, when the answer feeds another tool."),
            ],
            [
                new("Which prompt is likely to give the most useful summary?", "Role, audience, length and structure make the request precise.", "\"You are a support lead. Summarise this thread in 3 bullets for an engineer.\"", "\"Summarise\"", "\"Tell me stuff\""),
                new("Why include an example of the output you want?", "Examples show the model the exact shape of answer you expect.", "It shows the exact format expected", "It makes the prompt shorter", "Models ignore examples"),
            ]);
        var sql = Course(forge.Id, arjun.Id, "SQL for Data Analysts", "Data & Analytics", CourseLevel.Beginner, 149900,
            "Query real business data with SELECT, JOIN and GROUP BY, and answer questions your team actually asks.",
            [
                new("Selecting and filtering", 15, "SELECT picks columns, FROM names the table and WHERE filters rows. Start every analysis by looking at a few rows: SELECT * FROM orders LIMIT 10.\n\nFilter early so you only aggregate the rows you mean to."),
                new("Joins and group-bys", 18, "JOIN combines tables on a shared key, such as orders.customer_id = customers.id. GROUP BY then summarises rows per group, for example total revenue per region.\n\nAlways check row counts after a join; an unexpected jump usually means a duplicate key."),
            ],
            [
                new("Which clause filters rows before aggregation?", "WHERE filters rows; HAVING filters groups afterwards.", "WHERE", "ORDER BY", "LIMIT"),
                new("Row count jumps after a JOIN. What is the likely cause?", "Duplicate keys multiply matching rows.", "A duplicate key on one side", "The database is slow", "Too few columns selected"),
            ]);
        var dashboards = Course(forge.Id, arjun.Id, "Dashboards that Drive Decisions", "Data & Analytics", CourseLevel.Beginner, 0,
            "Design dashboards people actually use: one question per chart, honest scales and clear calls to action.",
            [
                new("Start from the decision", 10, "Before building a dashboard, write down the decision it supports and who makes it. Every chart should help answer that decision.\n\nIf a chart doesn't change what someone would do, remove it."),
                new("Charts that don't mislead", 12, "Start bar charts at zero, label units, and avoid 3D effects. Use one highlight colour for the thing that matters and grey for context."),
            ],
            [
                new("What should you define before building a dashboard?", "Dashboards exist to support a decision.", "The decision it supports and who makes it", "The colour palette", "The font"),
                new("Where should a bar chart's value axis start?", "Truncated bar axes exaggerate differences.", "At zero", "At the smallest value", "Anywhere"),
            ]);
        var ml = Course(collegeId, prof.Id, "Machine Learning Foundations", "AI & Machine Learning", CourseLevel.Intermediate, 249900,
            "Train, evaluate and explain your first models, from train/test splits to avoiding overfitting.",
            [
                new("Training and testing", 18, "Split your data into a training set and a test set the model never sees while learning. The test score tells you how the model will do on new data.\n\nNever tune your model on the test set; use a separate validation set or cross-validation."),
                new("Overfitting and how to spot it", 18, "A model that scores far better on training data than on test data has memorised noise: it is overfitting.\n\nSimpler models, more data and regularisation all help it generalise."),
            ],
            [
                new("Why keep a test set the model never sees?", "It estimates performance on new data.", "To estimate how it performs on new data", "To make training faster", "Because data is expensive"),
                new("Training accuracy 99%, test accuracy 70%. What is happening?", "A big train/test gap is the classic sign of overfitting.", "The model is overfitting", "The model is underfitting", "The test set is too easy"),
            ]);
        var courses = new[] { genAi, prompts, sql, dashboards, ml };
        db.Courses.AddRange(courses);

        // Second career path built from the new courses.
        var analyst = new CareerPath
        {
            Title = "Become an AI Product Analyst",
            Slug = "ai-product-analyst",
            Role = "AI Product Analyst",
            CredentialPrefix = "AIPA",
            Summary = "Turn data and AI tools into product decisions: query the data, prompt the models and tell the story.",
            Description = "AI Product Analysts sit between data, AI and the product team. This path covers everyday generative AI, prompt engineering, SQL and decision-ready dashboards, with live classes alongside working analysts. Finish every step to earn the AI Product Analyst credential.",
            Skills = string.Join("\n", ["Generative AI at work", "Prompt engineering", "SQL", "Dashboard design", "Data storytelling"]),
        };
        analyst.Steps =
        [
            new CareerStep { PathId = analyst.Id, Order = 1, Kind = CareerStepKind.CompleteCourse, CourseId = genAi.Id, Title = "Work smarter with AI", Description = "Use AI assistants safely for everyday work." },
            new CareerStep { PathId = analyst.Id, Order = 2, Kind = CareerStepKind.CompleteCourse, CourseId = prompts.Id, Title = "Master prompting", Description = "Get reliable, structured answers from AI models." },
            new CareerStep { PathId = analyst.Id, Order = 3, Kind = CareerStepKind.CompleteCourse, CourseId = sql.Id, Title = "Query the data", Description = "Answer product questions with SQL." },
            new CareerStep { PathId = analyst.Id, Order = 4, Kind = CareerStepKind.CompleteCourse, CourseId = dashboards.Id, Title = "Tell the story", Description = "Build dashboards that drive decisions." },
            new CareerStep { PathId = analyst.Id, Order = 5, Kind = CareerStepKind.AttendClasses, RequiredCount = 2, Title = "Learn live with analysts", Description = "Attend two live classes from courses on this path." },
        ];
        db.CareerPaths.Add(analyst);

        // The main demo learner is also taking the free AI course.
        db.Enrollments.Add(new Enrollment { UserId = learner.Id, CourseId = genAi.Id, CourseVersionId = genAi.PublishedVersionId!.Value, Source = EnrollmentSource.Free, Status = EnrollmentStatus.Active });

        // Live classes today and this week.
        ClassSession Live(Course c, Guid host, string title, string description, DateTime starts, int minutes) => new()
        {
            CourseId = c.Id, InstructorId = host, Title = title, Description = description, StartsAt = starts, EndsAt = starts.AddMinutes(minutes),
            MeetingRoom = JitsiTokenService.NewRoomName(title), Capacity = 50,
        };
        var today = now.Date;
        db.ClassSessions.AddRange(
            Live(genAi, sara.Id, "Live demo: AI assistants for your inbox", "Watch an AI assistant draft, summarise and triage a real inbox, then try it yourself.", now.AddHours(2).AddMinutes(-now.Minute), 60),
            Live(genAi, sara.Id, "Ask me anything: AI at work", "Bring your questions about using AI safely and productively at work.", today.AddDays(1).AddHours(13), 45),
            Live(prompts, sara.Id, "Prompt clinic: fix my prompt", "Share a prompt that isn't working and we'll improve it live.", today.AddDays(2).AddHours(15), 60),
            Live(sql, arjun.Id, "Live lab: SQL joins on real sales data", "Join orders, customers and products to answer three business questions.", today.AddDays(4).AddHours(11), 90),
            Live(ml, prof.Id, "Workshop: spotting overfitting", "Train two models live and compare their train and test scores.", today.AddDays(6).AddHours(10), 75));

        // A cohort of fictional learners with progress and certificates.
        string[] names = ["Ananya Rao", "Vikram Singh", "Meera Pillai", "Rahul Verma", "Fatima Shaikh", "Karthik Nair", "Divya Menon", "Arjun Kapoor", "Neha Gupta", "Sameer Khan", "Priyanka Das", "Rohan Joshi"];
        var all = courses.Append(db.Courses.Local.First(c => c.Slug == "cloud-foundations-by-nimbus")).ToArray();
        foreach (var (name, i) in names.Select((n, i) => (n, i)))
        {
            var u = NewUser($"{name.Split(' ')[0].ToLowerInvariant()}.{name.Split(' ')[1].ToLowerInvariant()}@example.com", name);
            u.Interests = i % 2 == 0 ? "AI & Machine Learning|Data & Analytics" : "Software Engineering|Cloud & DevOps";
            foreach (var c in all.OrderBy(_ => rng.Next()).Take(2 + i % 3))
            {
                var done = rng.NextDouble() < 0.45;
                var e = new Enrollment
                {
                    UserId = u.Id, CourseId = c.Id, CourseVersionId = c.PublishedVersionId!.Value, Source = EnrollmentSource.Free,
                    Status = done ? EnrollmentStatus.Completed : EnrollmentStatus.Active, CompletedAt = done ? now.AddDays(-rng.Next(1, 40)) : null,
                };
                db.Enrollments.Add(e);
                if (done)
                    db.Certificates.Add(new Certificate
                    {
                        CredentialCode = $"TMS-{Text.RandomCode(4)}-{Text.RandomCode(4)}-{Text.RandomCode(4)}", UserId = u.Id, CourseId = c.Id,
                        CourseVersionId = e.CourseVersionId, EnrollmentId = e.Id, IssuerTenantId = c.TenantId, RecipientName = name, CourseTitle = c.Title,
                        IssuerName = db.Tenants.Local.First(t => t.Id == c.TenantId).Name, ScorePercent = 70 + rng.Next(0, 31),
                        IssuedAt = e.CompletedAt!.Value, Status = CertificateStatus.Active,
                    });
            }
            for (var d = 0; d < 20; d++)
                if (rng.NextDouble() < 0.5)
                    db.XpEvents.Add(new XpEvent { UserId = u.Id, Source = XpSource.Practice, RefKey = $"seed:{d}", Points = 10 + rng.Next(0, 40), Label = "Practice round", OccurredAt = now.AddDays(-d).AddHours(-rng.Next(1, 8)) });
            if (i < 4)
                db.CourseInterests.Add(new CourseInterest
                {
                    CourseId = (i % 2 == 0 ? prompts : sql).Id, Name = name, Email = u.Email, Phone = $"+91 98{rng.Next(10000000, 99999999)}",
                    Goal = i % 2 == 0 ? "Get a job" : "Grow in my current role", PreferredTime = i < 2 ? "Weekends" : "Evenings",
                });
        }

        // Activity history for the main learner: a live streak and a fuller heatmap.
        for (var d = 1; d < 70; d++)
            if (d < 6 || rng.NextDouble() < 0.4)
                db.XpEvents.Add(new XpEvent { UserId = learner.Id, Source = XpSource.Practice, RefKey = $"seed:{d}", Points = 15 + rng.Next(0, 45), Label = "Practice round", OccurredAt = now.AddDays(-d).AddHours(-2) });
    }

    private static Course Course(Guid tenantId, Guid instructorId, string title, string category, CourseLevel level, long price, string summary,
        LessonSpec[] lessons, QuestionSpec[] questions)
    {
        var course = new Course
        {
            TenantId = tenantId, InstructorId = instructorId, Title = title, Slug = Text.Slugify(title), Summary = summary, Description = summary,
            Category = category, Level = level, PriceMinor = price,
            Versions =
            [
                new CourseVersion
                {
                    Number = 1,
                    Modules = [new CourseModule { Title = "Core skills", Order = 1, Lessons = lessons.Select((l, i) => new Lesson { Title = l.Title, Order = i + 1, Kind = LessonKind.Text, DurationMinutes = l.Minutes, IsPreview = i == 0, Content = l.Content }).ToList() }],
                    Assessments =
                    [
                        new Assessment
                        {
                            Title = "Skills check", PassMarkPercent = 60, MaxAttempts = 5,
                            Questions = questions.Select((q, i) => new Question
                            {
                                Kind = QuestionKind.SingleChoice, Prompt = q.Prompt, Explanation = q.Explanation, Order = i + 1, Status = QuestionStatus.Approved,
                                Options = [new QuestionOption { Text = q.Right, IsCorrect = true, Order = 1 }, .. q.Wrong.Select((w, j) => new QuestionOption { Text = w, Order = j + 2 })],
                            }).ToList(),
                        },
                    ],
                },
            ],
        };
        var v = course.Versions[0];
        v.IsDraft = false;
        v.PublishedAt = DateTime.UtcNow;
        v.LearningOutcomes = string.Join("\n", lessons.Select(l => l.Title));
        v.Prerequisites = "None — open to everyone";
        v.Modules[0].Week = 1;
        v.Milestones =
        [
            new Milestone { Title = "Finish the lessons", Order = 1, Kind = MilestoneKind.CompleteModule, ModuleId = v.Modules[0].Id, DueDays = 7 },
            new Milestone { Title = "Pass the skills check", Order = 2, Kind = MilestoneKind.PassAssessment, AssessmentId = v.Assessments[0].Id, DueDays = 14 },
        ];
        course.PublishedVersionId = v.Id;
        course.Status = CourseStatus.Published;
        return course;
    }
}
