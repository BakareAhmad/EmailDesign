using System.Net;
using DeChimex.Infrastructure.Options;
using DeChimex.Infrastructure.Payments;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
namespace DeChimex.UnitTests.Infrastructure;
public class PaystackRefundAcceptanceTests
{
 private sealed class Response(string body,HttpStatusCode status):HttpMessageHandler
 {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(status){Content=new StringContent(body)});}
 [Theory]
 [InlineData("{\"status\":true,\"data\":{\"status\":\"pending\",\"reference\":\"r1\"}}",true)]
 [InlineData("{\"status\":true,\"data\":{\"status\":\"processed\"}}",true)]
 [InlineData("{\"status\":true,\"data\":{\"status\":\"failed\"}}",false)]
 [InlineData("{\"status\":false,\"data\":{}}",false)]
 [InlineData("{\"status\":true}",false)]
 [InlineData("{\"status\":true,\"data\":null}",false)]
 [InlineData("not-json",false)]
 public async Task HttpSuccessMustContainVerifiedAcceptance_NotSettlement(string body,bool success)
 {
  var gw=new PaystackGateway(new HttpClient(new Response(body,HttpStatusCode.OK)),Options.Create(new PaystackOptions{SecretKey="sk_test_placeholder",ApiBaseUrl="https://gateway.invalid"}),NullLogger<PaystackGateway>.Instance);
  var result=await gw.RefundAsync("payment1",100);result.Success.Should().Be(success);
  if(success)result.Message.Should().Contain("settlement is not confirmed");
 }
}
