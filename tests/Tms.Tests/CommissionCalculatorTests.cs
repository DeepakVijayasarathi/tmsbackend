using Tms.Api.Modules.Commissions;

namespace Tms.Tests;

public class CommissionCalculatorTests
{
    private static readonly Guid Buyer = Guid.NewGuid(), Instructor = Guid.NewGuid(), Affiliate = Guid.NewGuid(), College = Guid.NewGuid();

    private static CommissionPlan Plan(StackingPolicy stacking = StackingPolicy.NoInstructorReferral) => new()
    {
        Name = "test", ProfessorBp = 4000, CollegeBp = 1000, ReferralBp = 500, Stacking = stacking,
    };

    private static long Amount(IEnumerable<Allocation> a, RecipientKind k) => a.Where(x => x.Kind == k).Sum(x => x.AmountMinor);

    [Fact]
    public void Matches_the_spec_example_for_a_1000_rupee_sale()
    {
        var result = CommissionCalculator.Allocate(new SaleContext(100_000, Buyer, Instructor, College, true, Affiliate), Plan());

        Assert.Equal(40_000, Amount(result, RecipientKind.Professor));
        Assert.Equal(10_000, Amount(result, RecipientKind.College));
        Assert.Equal(5_000, Amount(result, RecipientKind.Referrer));
        Assert.Equal(45_000, Amount(result, RecipientKind.Platform));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(84_746)]
    [InlineData(333_333)]
    [InlineData(0)]
    public void Allocations_always_sum_to_the_base(long baseMinor)
    {
        var result = CommissionCalculator.Allocate(new SaleContext(baseMinor, Buyer, Instructor, College, true, Affiliate), Plan());
        Assert.Equal(baseMinor, result.Sum(a => a.AmountMinor));
        Assert.All(result, a => Assert.True(a.AmountMinor >= 0));
    }

    [Fact]
    public void Instructor_referring_their_own_course_does_not_double_dip_by_default()
    {
        var result = CommissionCalculator.Allocate(new SaleContext(100_000, Buyer, Instructor, College, true, Instructor), Plan());

        Assert.DoesNotContain(result, a => a.Kind == RecipientKind.Referrer);
        Assert.Equal(40_000, Amount(result, RecipientKind.Professor));
        Assert.Equal(50_000, Amount(result, RecipientKind.Platform));
        Assert.Contains("stacking disabled", result.Single(a => a.Kind == RecipientKind.Platform).Note);
    }

    [Fact]
    public void Stacking_policy_can_allow_instructor_referral()
    {
        var result = CommissionCalculator.Allocate(new SaleContext(100_000, Buyer, Instructor, College, true, Instructor), Plan(StackingPolicy.AllowStacking));
        Assert.Equal(45_000, result.Where(a => a.UserId == Instructor).Sum(a => a.AmountMinor));
    }

    [Fact]
    public void Self_referral_earns_nothing()
    {
        var result = CommissionCalculator.Allocate(new SaleContext(100_000, Buyer, Instructor, College, true, Buyer), Plan());
        Assert.DoesNotContain(result, a => a.Kind == RecipientKind.Referrer);
    }

    [Fact]
    public void College_share_goes_to_platform_when_course_is_not_college_owned()
    {
        var result = CommissionCalculator.Allocate(new SaleContext(100_000, Buyer, Instructor, College, false, null), Plan());
        Assert.DoesNotContain(result, a => a.Kind == RecipientKind.College);
        Assert.Equal(60_000, Amount(result, RecipientKind.Platform));
    }

    [Fact]
    public void Plan_over_100_percent_is_rejected()
    {
        var plan = new CommissionPlan { Name = "bad", ProfessorBp = 9000, CollegeBp = 1000, ReferralBp = 500 };
        Assert.Throws<InvalidOperationException>(() =>
            CommissionCalculator.Allocate(new SaleContext(100, Buyer, Instructor, College, true, Affiliate), plan));
    }

    [Fact]
    public void Repeated_partial_refunds_reverse_exactly_the_original_amount()
    {
        const long original = 33_898, total = 100_000;
        long reversed = 0, refunded = 0;
        foreach (var refund in new long[] { 33_333, 33_333, 33_334 })
        {
            refunded += refund;
            reversed += CommissionCalculator.ReversalAmount(original, reversed, refunded, total);
            Assert.True(reversed <= original);
        }
        Assert.Equal(original, reversed);
    }

    [Theory]
    [InlineData(100_000, 18, 15_254)]
    [InlineData(118, 18, 18)]
    [InlineData(100_000, 0, 0)]
    public void Inclusive_tax_is_extracted_from_the_gross_price(long gross, int rate, long expectedTax) =>
        Assert.Equal(expectedTax, CommissionCalculator.InclusiveTax(gross, rate));
}
