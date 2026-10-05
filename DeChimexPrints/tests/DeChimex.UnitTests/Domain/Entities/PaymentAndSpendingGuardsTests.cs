using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Events;
using DeChimex.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Domain.Entities;

/// <summary>
/// Payment attempt guards: idempotent state transitions (a repeated event must never
/// double-apply side effects) and lifetime-spending bookkeeping (refunds reverse exactly
/// the recorded amount, never below zero).
/// </summary>
public class PaymentAndSpendingGuardsTests
{
    private static Payment NewPayment(decimal amount = 2500m)
        => Payment.Initialize(Guid.NewGuid(), Guid.NewGuid(), amount, PaymentMethod.Paystack, "PAY-TEST-1", 0m);

    [Fact]
    public void MarkFailed_raises_the_failure_event_exactly_once_per_attempt()
    {
        var payment = NewPayment();

        payment.MarkFailed("Card declined", null);
        payment.MarkFailed("Card declined again", null);

        payment.Status.Should().Be(PaymentStatus.Failed);
        payment.FailureReason.Should().Be("Card declined again", "the latest reason is kept");
        payment.DomainEvents.OfType<PaymentFailedEvent>().Should().HaveCount(1,
            "repeat verifications of a failed attempt must not re-notify the customer");
    }

    [Fact]
    public void MarkFailed_never_overrides_a_confirmed_payment()
    {
        var payment = NewPayment();
        payment.MarkSucceeded("gw-1", null).Should().BeTrue();

        payment.MarkFailed("late failure", null);

        payment.Status.Should().Be(PaymentStatus.Succeeded, "a settled payment cannot be flipped to failed");
        payment.DomainEvents.OfType<PaymentFailedEvent>().Should().BeEmpty();
    }

    [Fact]
    public void MarkSucceeded_is_idempotent_for_duplicate_confirmations()
    {
        var payment = NewPayment();

        payment.MarkSucceeded("gw-1", null).Should().BeTrue();
        payment.MarkSucceeded("gw-1", null).Should().BeFalse("a duplicate webhook/verify must not double-confirm");
        payment.Status.Should().Be(PaymentStatus.Succeeded);
    }

    [Fact]
    public void RecordSpending_then_ReverseSpending_returns_to_the_original_value()
    {
        var customer = Customer.Create("Ada", "Refund", "ada.refund@example.com");
        var before = customer.LifetimeSpending.Amount;

        customer.RecordSpending(Money.Of(4000m));
        customer.LifetimeSpending.Amount.Should().Be(before + 4000m);

        customer.ReverseSpending(Money.Of(4000m));
        customer.LifetimeSpending.Amount.Should().Be(before, "a full refund reverses exactly what was recorded");
    }

    [Fact]
    public void ReverseSpending_never_goes_below_zero()
    {
        var customer = Customer.Create("Bob", "Floor", "bob.floor@example.com");

        customer.ReverseSpending(Money.Of(100m));

        customer.LifetimeSpending.Amount.Should().Be(0m);
    }
}
