using System.ServiceModel;
using System.ServiceModel.Description;
using YardTracker.Contracts;

namespace YardTracker.Tests;

public class ScanCodeTests
{
    [Theory]
    [InlineData("yt-000123\r\n", ScanCodeKind.ItemTag, "YT-000123")]
    [InlineData("  E28011606000A1B2C3D4E5F6 ", ScanCodeKind.ItemTag, "E28011606000A1B2C3D4E5F6")]
    [InlineData("LOC:nyd-r01", ScanCodeKind.Location, "NYD-R01")]
    [InlineData("BADGE:1001", ScanCodeKind.Badge, "1001")]
    [InlineData("YT-004211", ScanCodeKind.ItemTag, "YT-004211")]
    [InlineData("", ScanCodeKind.Empty, "")]
    [InlineData("   ", ScanCodeKind.Empty, "")]
    [InlineData("LOC:", ScanCodeKind.Invalid, "")]
    [InlineData("#@!", ScanCodeKind.Invalid, "#@!")]
    [InlineData("YT", ScanCodeKind.Invalid, "YT")]
    public void Classifies_scanner_input(string raw, ScanCodeKind kind, string value)
    {
        var code = ScanCodes.Parse(raw);

        Assert.Equal(kind, code.Kind);
        Assert.Equal(value, code.Value);
    }

    [Fact]
    public void NormalizeTag_rejects_location_labels() =>
        Assert.Throws<ArgumentException>(() => ScanCodes.NormalizeTag("LOC:NYD-R01"));
}

public class ServiceContractTests
{
    public static TheoryData<Type> Contracts => [typeof(IInventoryService), typeof(ILogisticsService), typeof(ITelemetryService)];

    [Theory]
    [MemberData(nameof(Contracts))]
    public void Every_operation_declares_the_service_fault(Type contractType)
    {
        var contract = ContractDescription.GetContract(contractType);

        Assert.NotEmpty(contract.Operations);
        Assert.All(contract.Operations, operation =>
            Assert.Contains(operation.Faults, fault => fault.DetailType == typeof(ServiceFault)));
    }

    [Theory]
    [MemberData(nameof(Contracts))]
    public void Wire_operation_names_do_not_carry_the_async_suffix(Type contractType)
    {
        var contract = ContractDescription.GetContract(contractType);

        Assert.Equal(ContractNamespaces.Service, contract.Namespace);
        Assert.All(contract.Operations, operation => Assert.False(operation.Name.EndsWith("Async", StringComparison.Ordinal), operation.Name));
    }

    [Fact]
    public void Scan_operations_are_named_after_the_yard_actions()
    {
        var names = ContractDescription.GetContract(typeof(IInventoryService)).Operations.Select(o => o.Name).ToHashSet();

        Assert.Superset(new HashSet<string> { "CheckInItem", "CheckOutItem", "MoveItem", "ReceiveItem", "GetItemHistory" }, names);
    }

    [Fact]
    public void Connectivity_failures_are_distinguished_from_faults()
    {
        Assert.True(YardTracker.Client.YardTrackerClient.IsConnectivityFailure(new EndpointNotFoundException("down")));
        Assert.True(YardTracker.Client.YardTrackerClient.IsConnectivityFailure(new TimeoutException()));
        Assert.False(YardTracker.Client.YardTrackerClient.IsConnectivityFailure(new FaultException<ServiceFault>(new ServiceFault { Message = "bad" })));
    }
}
