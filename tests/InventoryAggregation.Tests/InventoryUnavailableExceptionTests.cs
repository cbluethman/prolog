using InventoryAggregation.Api.Inventory;

namespace InventoryAggregation.Tests;

public class InventoryUnavailableExceptionTests
{
    [Fact]
    public void Invalid_data_factory_keeps_the_message_and_the_cause()
    {
        var cause = new FormatException("the underlying problem");

        var exception = InventoryUnavailableException.InvalidData("The data cannot be trusted.", cause);

        Assert.Equal(InventoryFailureReason.InvalidData, exception.Reason);
        Assert.Equal("The data cannot be trusted.", exception.Message);
        Assert.Same(cause, exception.InnerException);
    }

    [Fact]
    public void Invalid_data_factory_has_no_cause_unless_one_is_given()
    {
        var exception = InventoryUnavailableException.InvalidData("The data cannot be trusted.");

        Assert.Equal(InventoryFailureReason.InvalidData, exception.Reason);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Invalid_data_never_suggests_a_time_to_retry()
    {
        var exception = InventoryUnavailableException.InvalidData("The data cannot be trusted.", new FormatException());

        Assert.Null(exception.RetryAfter);
    }
}
