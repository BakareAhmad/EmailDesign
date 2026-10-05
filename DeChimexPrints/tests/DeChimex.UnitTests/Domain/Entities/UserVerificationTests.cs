using DeChimex.Domain.Entities;
using DeChimex.Domain.Exceptions;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Domain.Entities;

/// <summary>
/// Email verification uses a 6-digit OTP (small keyspace), so the User aggregate caps
/// failed attempts at MaxEmailVerificationAttempts and invalidates the code, forcing a
/// resend. These tests pin that brute-force protection.
/// </summary>
public class UserVerificationTests
{
    private static User NewUser(string token = "123456")
        => User.Register("user@example.com", "hash", "First", "Last", token);

    [Fact]
    public void CorrectToken_VerifiesEmail_And_ClearsState()
    {
        var user = NewUser("654321");
        user.VerifyEmail("654321");
        user.IsEmailVerified.Should().BeTrue();
        user.EmailVerificationToken.Should().BeNull();
        user.EmailVerificationAttempts.Should().Be(0);
    }

    [Fact]
    public void WrongToken_Throws_And_CountsAttempt()
    {
        var user = NewUser("111111");
        var act = () => user.VerifyEmail("999999");
        act.Should().Throw<BusinessRuleValidationException>()
            .Which.Code.Should().Be("user.verify.token");
        user.EmailVerificationAttempts.Should().Be(1);
        user.IsEmailVerified.Should().BeFalse();
    }

    [Fact]
    public void TenWrongAttempts_InvalidateCode_Even_The_Correct_One()
    {
        var user = NewUser("222222");
        for (var i = 1; i <= User.MaxEmailVerificationAttempts; i++)
        {
            var act = () => user.VerifyEmail("000000");
            var expected = i < User.MaxEmailVerificationAttempts ? "user.verify.token" : "user.verify.locked";
            act.Should().Throw<BusinessRuleValidationException>().Which.Code.Should().Be(expected);
        }

        user.EmailVerificationToken.Should().BeNull("the code must be invalidated after the cap");

        // Even the CORRECT code no longer works - a resend is required.
        var again = () => user.VerifyEmail("222222");
        again.Should().Throw<BusinessRuleValidationException>().Which.Code.Should().Be("user.verify.locked");
        user.IsEmailVerified.Should().BeFalse();
    }

    [Fact]
    public void Further_After_Lockout_Still_Throws_Locked()
    {
        var user = NewUser("555555");
        for (var i = 0; i < User.MaxEmailVerificationAttempts; i++)
            ((Action)(() => user.VerifyEmail("000000")))
                .Should().Throw<BusinessRuleValidationException>();

        var act = () => user.VerifyEmail("555555");
        act.Should().Throw<BusinessRuleValidationException>().Which.Code.Should().Be("user.verify.locked");
    }

    [Fact]
    public void Resend_IssuesNewCode_And_ResetsAttemptBudget()
    {
        var user = NewUser("333333");
        for (var i = 0; i < User.MaxEmailVerificationAttempts - 1; i++)
            ((Action)(() => user.VerifyEmail("000000")))
                .Should().Throw<BusinessRuleValidationException>();
        user.EmailVerificationAttempts.Should().Be(User.MaxEmailVerificationAttempts - 1);

        user.RequestEmailVerification("444444");
        user.EmailVerificationAttempts.Should().Be(0, "a fresh code must come with a fresh attempt budget");
        user.EmailVerificationToken.Should().Be("444444");

        user.VerifyEmail("444444");
        user.IsEmailVerified.Should().BeTrue();
    }
}
