using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Domain.Entities;

public class OrderTests
{
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly Address _shippingAddress = Address.Create("123 Main St, Apt 4", "Lagos", "Lagos", "Nigeria", "100001");
    private readonly Address _billingAddress = Address.Create("123 Main St, Apt 4", "Lagos", "Lagos", "Nigeria", "100001");

    private Order CreateTestOrder()
    {
        var order = Order.CreateDraft("ORD-TEST-001", _customerId);
        order.SetAddresses(_shippingAddress, _billingAddress);
        return order;
    }

    [Fact]
    public void CreateDraft_WithValidData_ShouldCreateOrder()
    {
        // Arrange & Act
        var order = Order.CreateDraft("ORD-001", _customerId);

        // Assert
        order.CustomerId.Should().Be(_customerId);
        order.OrderNumber.Should().Be("ORD-001");
        order.Status.Should().Be(OrderStatus.Draft);
        order.ItemsSubtotal.Amount.Should().Be(0m);
        order.Items.Should().BeEmpty();
        order.Services.Should().BeEmpty();
    }

    [Fact]
    public void CreateDraft_WithEmptyCustomerId_ShouldThrowException()
    {
        // Act
        var act = () => Order.CreateDraft("ORD-001", Guid.Empty);

        // Assert
        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*customer is required*");
    }

    [Fact]
    public void CreateDraft_WithEmptyOrderNumber_ShouldThrowException()
    {
        // Act
        var act = () => Order.CreateDraft("", _customerId);

        // Assert
        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*Order number is required*");
    }

    [Fact]
    public void AddProduct_WithValidData_ShouldAddItem()
    {
        // Arrange
        var order = CreateTestOrder();
        var productId = Guid.NewGuid();
        var productName = "Test Product";
        var sku = "SKU-001";

        // Act
        var item = order.AddProduct(productId, productName, sku, 100m, 2);

        // Assert
        order.Items.Should().HaveCount(1);
        item.ProductId.Should().Be(productId);
        item.ProductName.Should().Be(productName);
        item.Sku.Should().Be(sku);
        item.UnitPrice.Amount.Should().Be(100m);
        item.Quantity.Should().Be(2);
        item.LineTotal.Amount.Should().Be(200m);
        order.ItemsSubtotal.Amount.Should().Be(200m);
    }

    [Fact]
    public void AddProduct_WithZeroQuantity_ShouldThrowException()
    {
        // Arrange
        var order = CreateTestOrder();

        // Act
        var act = () => order.AddProduct(Guid.NewGuid(), "Product", "SKU", 100m, 0);

        // Assert
        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*Quantity must be positive*");
    }

    [Fact]
    public void AddProduct_WithNegativeQuantity_ShouldThrowException()
    {
        // Arrange
        var order = CreateTestOrder();

        // Act
        var act = () => order.AddProduct(Guid.NewGuid(), "Product", "SKU", 100m, -1);

        // Assert
        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*Quantity must be positive*");
    }

    [Fact]
    public void AddService_WithValidData_ShouldAddLine()
    {
        // Arrange
        var order = CreateTestOrder();
        var serviceId = Guid.NewGuid();
        var serviceName = "Custom Printing";

        // Act
        var line = order.AddService(serviceId, serviceName, 50m, 1, "Print logo on back");

        // Assert
        order.Services.Should().HaveCount(1);
        line.ServiceId.Should().Be(serviceId);
        line.ServiceName.Should().Be(serviceName);
        line.UnitPrice.Amount.Should().Be(50m);
        line.Quantity.Should().Be(1);
        line.Notes.Should().Be("Print logo on back");
        line.LineTotal.Amount.Should().Be(50m);
        order.ItemsSubtotal.Amount.Should().Be(50m);
    }

    [Fact]
    public void AddService_WithZeroQuantity_ShouldThrowException()
    {
        // Arrange
        var order = CreateTestOrder();

        // Act
        var act = () => order.AddService(Guid.NewGuid(), "Service", 50m, 0, null);

        // Assert
        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*Quantity must be positive*");
    }

    [Fact]
    public void SetPricing_ShouldUpdateTotals()
    {
        // Arrange
        var order = CreateTestOrder();
        order.AddProduct(Guid.NewGuid(), "Product 1", "SKU1", 100m, 2); // 200
        order.AddProduct(Guid.NewGuid(), "Product 2", "SKU2", 50m, 3);  // 150
        order.AddService(Guid.NewGuid(), "Service", 75m, 1, null);      // 75

        // Act
        order.SetPricing(discount: 25m, deliveryFee: 1000m, tax: 42.50m);

        // Assert
        order.DiscountAmount.Amount.Should().Be(25m);
        order.DeliveryFee.Amount.Should().Be(1000m);
        order.TaxAmount.Amount.Should().Be(42.50m);
        order.ItemsSubtotal.Amount.Should().Be(425m); // 200 + 150 + 75
        // GrandTotal = ItemsSubtotal + DeliveryFee + Tax - Discount = 425 + 1000 + 42.50 - 25 = 1442.50
        order.GrandTotal.Amount.Should().Be(1442.50m);
    }

    [Fact]
    public void PlaceOrder_WhenDraft_ShouldTransitionToPendingPayment()
    {
        // Arrange
        var order = CreateTestOrder();
        order.AddProduct(Guid.NewGuid(), "Product", "SKU", 100m, 1);

        // Act
        order.PlaceOrder();

        // Assert
        order.Status.Should().Be(OrderStatus.PendingPayment);
    }

    [Fact]
    public void PlaceOrder_WhenEmpty_ShouldThrowException()
    {
        // Arrange
        var order = CreateTestOrder();

        // Act
        var act = () => order.PlaceOrder();

        // Assert
        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*must contain at least one product or service*");
    }

    [Fact]
    public void MarkPaid_WhenPendingPayment_ShouldChangeStatus()
    {
        // Arrange
        var order = CreateTestOrder();
        order.AddProduct(Guid.NewGuid(), "Product", "SKU", 100m, 1);
        order.PlaceOrder();

        // Act
        order.MarkPaid();

        // Assert
        order.Status.Should().Be(OrderStatus.Paid);
        order.PaidAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void MarkPaid_WhenAlreadyPaid_ShouldThrowException()
    {
        // Arrange
        var order = CreateTestOrder();
        order.AddProduct(Guid.NewGuid(), "Product", "SKU", 100m, 1);
        order.PlaceOrder();
        order.MarkPaid();

        // Act
        var act = () => order.MarkPaid();

        // Assert
        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*not awaiting payment*");
    }

    [Fact]
    public void StartProduction_WhenPaid_ShouldChangeStatus()
    {
        // Arrange
        var order = CreateTestOrder();
        order.AddProduct(Guid.NewGuid(), "Product", "SKU", 100m, 1);
        order.PlaceOrder();
        order.MarkPaid();

        // Act
        order.StartProduction();

        // Assert
        order.Status.Should().Be(OrderStatus.InProduction);
    }

    [Fact]
    public void StartProduction_WhenNotPaid_ShouldThrowException()
    {
        // Arrange
        var order = CreateTestOrder();
        order.AddProduct(Guid.NewGuid(), "Product", "SKU", 100m, 1);
        order.PlaceOrder();

        // Act
        var act = () => order.StartProduction();

        // Assert
        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*must be paid*");
    }

    [Fact]
    public void MarkReady_WhenInProduction_ShouldChangeStatus()
    {
        // Arrange
        var order = CreateTestOrder();
        order.AddProduct(Guid.NewGuid(), "Product", "SKU", 100m, 1);
        order.PlaceOrder();
        order.MarkPaid();
        order.StartProduction();

        // Act
        order.MarkReady();

        // Assert
        order.Status.Should().Be(OrderStatus.Ready);
    }

    [Fact]
    public void MarkCompleted_WhenReady_ShouldChangeStatus()
    {
        // Arrange
        var order = CreateTestOrder();
        order.AddProduct(Guid.NewGuid(), "Product", "SKU", 100m, 1);
        order.PlaceOrder();
        order.MarkPaid();
        order.StartProduction();
        order.MarkReady();

        // Act
        order.MarkCompleted();

        // Assert
        order.Status.Should().Be(OrderStatus.Completed);
        order.CompletedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void Cancel_WhenPendingPayment_ShouldChangeStatus()
    {
        // Arrange
        var order = CreateTestOrder();
        order.AddProduct(Guid.NewGuid(), "Product", "SKU", 100m, 1);
        order.PlaceOrder();

        // Act
        order.Cancel("Customer requested cancellation");

        // Assert
        order.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public void Cancel_WhenCompleted_ShouldThrowException()
    {
        // Arrange
        var order = CreateTestOrder();
        order.AddProduct(Guid.NewGuid(), "Product", "SKU", 100m, 1);
        order.PlaceOrder();
        order.MarkPaid();
        order.StartProduction();
        order.MarkReady();
        order.MarkCompleted();

        // Act
        var act = () => order.Cancel("Cancellation");

        // Assert
        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*cannot be cancelled*");
    }

    [Fact]
    public void Refund_WhenPaid_ShouldChangeStatus()
    {
        // Arrange
        var order = CreateTestOrder();
        order.AddProduct(Guid.NewGuid(), "Product", "SKU", 100m, 1);
        order.PlaceOrder();
        order.MarkPaid();

        // Act
        order.Refund();

        // Assert
        order.Status.Should().Be(OrderStatus.Refunded);
    }

    [Fact]
    public void Refund_WhenNotPaid_ShouldThrowException()
    {
        // Arrange
        var order = CreateTestOrder();
        order.AddProduct(Guid.NewGuid(), "Product", "SKU", 100m, 1);
        order.PlaceOrder();

        // Act
        var act = () => order.Refund();

        // Assert
        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*Only a paid order can be refunded*");
    }

    [Fact]
    public void SetAddresses_ShouldUpdateAddresses()
    {
        // Arrange
        var order = Order.CreateDraft("ORD-001", _customerId);

        // Act
        order.SetAddresses(_shippingAddress, _billingAddress);

        // Assert
        order.ShippingAddress.Should().Be(_shippingAddress);
        order.BillingAddress.Should().Be(_billingAddress);
    }

    [Fact]
    public void SetCustomerNotes_ShouldUpdateNotes()
    {
        // Arrange
        var order = CreateTestOrder();

        // Act
        order.SetCustomerNotes("Rush delivery");

        // Assert
        order.CustomerNotes.Should().Be("Rush delivery");
    }

    [Fact]
    public void Type_WithOnlyProducts_ShouldBeInventory()
    {
        // Arrange
        var order = CreateTestOrder();
        order.AddProduct(Guid.NewGuid(), "Product", "SKU", 100m, 1);

        // Assert
        order.Type.Should().Be(OrderType.Inventory);
    }

    [Fact]
    public void Type_WithOnlyServices_ShouldBeService()
    {
        // Arrange
        var order = CreateTestOrder();
        order.AddService(Guid.NewGuid(), "Service", 50m, 1, null);

        // Assert
        order.Type.Should().Be(OrderType.Service);
    }

    [Fact]
    public void Type_WithBothProductsAndServices_ShouldBeMixed()
    {
        // Arrange
        var order = CreateTestOrder();
        order.AddProduct(Guid.NewGuid(), "Product", "SKU", 100m, 1);
        order.AddService(Guid.NewGuid(), "Service", 50m, 1, null);

        // Assert
        order.Type.Should().Be(OrderType.Mixed);
    }
}
