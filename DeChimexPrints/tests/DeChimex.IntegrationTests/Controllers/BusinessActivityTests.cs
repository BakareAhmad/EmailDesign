using System.Net;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Activity;
using DeChimex.Application.Features.Orders.Commands;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Events;
using DeChimex.IntegrationTests.Fakes;
using DeChimex.Persistence.Context;
using DeChimex.Host.Realtime;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace DeChimex.IntegrationTests.Controllers;
public class BusinessActivityTests : IClassFixture<CustomWebApplicationFactory>
{
 private readonly CustomWebApplicationFactory factory;
 public BusinessActivityTests(CustomWebApplicationFactory factory)=>this.factory=factory;
 private static string Unique(string prefix)=>prefix+Guid.NewGuid().ToString("N")[..10];
 private HttpClient Browser()=>factory.CreateClient(new WebApplicationFactoryClientOptions{AllowAutoRedirect=false,HandleCookies=true});
 private Task<HttpClient> Owner()=>TestSeed.LoginOwnerMvcAsync(factory.Services,Browser);
 [Fact] public void DomainEventIdentityAndOccurrenceAreStable()
 {
  DeChimex.Domain.Common.IDomainEvent e=new OrderPlacedEvent(Guid.NewGuid(),"O1",Guid.NewGuid(),10);
  e.EventId.Should().Be(e.EventId);e.OccurredOnUtc.Should().Be(e.OccurredOnUtc);
 }
 [Fact] public async Task TypedRowsArePrivate_AndNoOpEditsDoNotDuplicate()
 {
  using var scope=factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
  var cat=Category.Create(Unique("ActivityCat"),"PRIVATE-DESCRIPTION");db.Add(cat);await db.SaveChangesAsync();
  var product=Product.Create(Unique("ActivityProduct"),Unique("SKU"),"PRIVATE-PRODUCT-DESCRIPTION",cat.Id,100,true,false);db.Add(product);await db.SaveChangesAsync();
  product.Update(product.Name,product.Description,cat.Id,250,true,false);await db.SaveChangesAsync();
  var rows=await db.AuditLogs.Where(a=>a.EntityId==product.Id.ToString()&&a.ActivityType!=null).ToListAsync();
  rows.Should().HaveCount(2);rows.Should().ContainSingle(a=>a.ActivityType=="product.price_changed"&&a.Summary!.Contains("100")&&a.Summary.Contains("250"));
  product.Update(product.Name,product.Description,cat.Id,250,true,false);await db.SaveChangesAsync();
  (await db.AuditLogs.CountAsync(a=>a.EntityId==product.Id.ToString()&&a.ActivityType!=null)).Should().Be(2);
  var user=User.Register(Unique("secret")+"@example.com","SECRET-PASSWORD-HASH","Private","Person","SECRET-VERIFICATION-TOKEN");db.Add(user);await db.SaveChangesAsync();
  var raw=await db.AuditLogs.Where(a=>a.EntityId==user.Id.ToString()).ToListAsync();raw.Should().NotBeEmpty();
  raw.Should().OnlyContain(a=>a.OldValues==null&&a.NewValues==null);
  (await db.AuditLogs.Where(a=>a.EntityId==cat.Id.ToString()).SingleAsync()).NewValues.Should().NotContain("PRIVATE-DESCRIPTION");
 }
 [Fact] public async Task PaymentOutcomesAreCanonicalAndProviderFailureDoesNotCreateRefundSuccess()
 {
  using var scope=factory.Services.CreateScope();var sp=scope.ServiceProvider;var db=sp.GetRequiredService<DeChimexDbContext>();
  var customer=Customer.Create("Activity","Customer");db.Add(customer);
  var order=Order.CreateDraft(Unique("ACT-"),customer.Id);order.AddProduct(Guid.NewGuid(),"Test","ACT-00001",100,1);order.PlaceOrder();db.Add(order);await db.SaveChangesAsync();
  var payment=Payment.Initialize(order.Id,customer.Id,100,PaymentMethod.Paystack,Unique("PAY-"));db.Add(payment);await db.SaveChangesAsync();
  payment.MarkSucceeded("GW-ACT","SECRET-RAW-RESPONSE");order.MarkPaid();await db.SaveChangesAsync();
  payment.MarkSucceeded("GW-ACT","SECRET-RAW-RESPONSE");await db.SaveChangesAsync();
  (await db.AuditLogs.CountAsync(a=>a.EntityId==payment.Id.ToString()&&a.ActivityType=="payment.succeeded")).Should().Be(1);
  var fake=(FakePaymentGateway)sp.GetRequiredService<IPaymentGateway>();fake.RefundSucceeds=false;
  Func<Task> refund=()=>sp.GetRequiredService<ISender>().Send(new RefundOrderCommand(order.Id));
  await refund.Should().ThrowAsync<DeChimex.Application.Common.Exceptions.ConflictException>();
  (await db.AuditLogs.CountAsync(a=>a.OrderId==order.Id&&a.ActivityType=="payment.refund_recorded")).Should().Be(0);
  order.Status.Should().Be(OrderStatus.Paid);
  fake.RefundSucceeds=true;await refund();
  var row=await db.AuditLogs.SingleAsync(a=>a.EntityId==payment.Id.ToString()&&a.ActivityType=="payment.refund_recorded");
  row.Title.Should().Be("Gateway refund request accepted");row.Summary.Should().Contain("not confirmed");
  row.NewValues.Should().NotContain("SECRET");order.Status.Should().Be(OrderStatus.Refunded);
  await refund.Should().ThrowAsync<DeChimex.Domain.Exceptions.BusinessRuleValidationException>();
  (await db.AuditLogs.CountAsync(a=>a.EntityId==payment.Id.ToString()&&a.ActivityType=="payment.refund_recorded")).Should().Be(1);
 }
 [Fact] public async Task FailedVerificationIsNotReportedAsPaymentReceived()
 {
  using var scope=factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
  var p=Payment.Initialize(Guid.NewGuid(),Guid.NewGuid(),99,PaymentMethod.Paystack,Unique("FAILED-"));db.Add(p);await db.SaveChangesAsync();
  p.MarkFailed("SECRET-GATEWAY-DETAILS","SECRET-BODY");await db.SaveChangesAsync();
  p.MarkFailed("SECRET-GATEWAY-DETAILS","SECRET-BODY");await db.SaveChangesAsync();
  var rows=await db.AuditLogs.Where(a=>a.EntityId==p.Id.ToString()&&a.ActivityType!=null).ToListAsync();
  rows.Count(a=>a.ActivityType=="payment.failed").Should().Be(1);rows.Should().NotContain(a=>a.ActivityType=="payment.succeeded");
  rows.Should().OnlyContain(a=>!a.Summary!.Contains("SECRET")&&(a.NewValues==null||!a.NewValues.Contains("SECRET")));
 }
 [Fact] public async Task DesignDecisionsAndConversationMilestones_ExcludeMessagesAndFeedback()
 {
  using var scope=factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
  var o=Order.CreateDraft(Unique("DESIGN-"),Guid.NewGuid());db.Add(o);await db.SaveChangesAsync();
  var staff=Guid.NewGuid();
  var review=o.AddDesignReview("saved.png","private-file.png","/uploads/private.png","image/png",100,staff,"staff",null);
  await db.SaveChangesAsync();
  o.ApproveDesignReview(review.Id,staff);await db.SaveChangesAsync();
  o.AddTimelineEntry("discussion","SECRET-CHAT",staff,"Private");
  for(int i=0;i<20;i++)o.AddTimelineEntry("message","SECRET-CHAT",staff,"Private");
  await db.SaveChangesAsync();
  var rows=await db.AuditLogs.Where(a=>a.OrderId==o.Id&&a.ActivityType!=null).ToListAsync();
  rows.Should().Contain(a=>a.ActivityType=="design.review_uploaded");rows.Should().Contain(a=>a.ActivityType=="design.review_approved");
  rows.Count(a=>a.Category=="communication").Should().Be(1);
  rows.Should().OnlyContain(a=>!a.Summary!.Contains("SECRET")&&!a.Summary.Contains("private-file"));
 }
 [Fact] public async Task HistoryFilters_Pagination_AndRealtimeEligibilityAreConsistent()
 {
  using var scope=factory.Services.CreateScope();var sp=scope.ServiceProvider;var db=sp.GetRequiredService<DeChimexDbContext>();var prefix=Unique("FILTER-");
  for(int i=0;i<55;i++)db.Add(MajorCategory.Create(prefix+i));await db.SaveChangesAsync();
  var q=sp.GetRequiredService<IActivityQueries>();
  var first=await q.SearchAsync(new ActivityFilter{Category="categories",Search=prefix},default);
  var next=await q.SearchAsync(new ActivityFilter{Category="categories",Search=prefix,Page=2},default);
  first.Items.Should().HaveCount(50);first.HasNext.Should().BeTrue();next.Items.Should().HaveCount(5);
  first.Items.Should().OnlyContain(a=>!a.IsRealtime);first.Items.Select(a=>a.Id).Intersect(next.Items.Select(a=>a.Id)).Should().BeEmpty();
  (await q.SearchAsync(new ActivityFilter{Category="payments",Search=prefix},default)).Items.Should().BeEmpty();
  (await q.SearchAsync(new ActivityFilter{Category="categories",Search=prefix,From=DateTime.UtcNow.AddDays(1)},default)).Items.Should().BeEmpty();
 }
 [Fact] public async Task ActivityRowsEmitUnambiguousUtcIso_EvenWhenTimestampKindIsUnspecified()
 {
  // REGRESSION PIN for the owner-reported "1 hours ago" skew: SQL Server/SQLite
  // materialise DateTime as Kind=Unspecified, so ToString("O") emits no zone
  // suffix and browsers parse the stamp as LOCAL time (Africa/Lagos UTC+1 made
  // every fresh row read "1 hours"). Rows markup must emit explicit UTC
  // regardless of the CLR Kind — exactly the SQL-materialised shape emulated
  // below (InMemory keeps Kind=Utc, which previously masked the defect).
  var marker=Unique("TSMARK-");
  var occurredUnspecified=System.DateTime.SpecifyKind(System.DateTime.UtcNow.AddMinutes(-30),System.DateTimeKind.Unspecified);
  using(var scope=factory.Services.CreateScope())
  {
   var db=scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
   var log=AuditLog.Create("create","Category",Guid.NewGuid().ToString(),"ts@example.com");
   log.Describe("category.test_timestamp","categories",marker+" title",marker+" summary",
    null,"Time Tester","Owner","ts-"+marker,false,occurredUtc:occurredUnspecified);
   db.Add(log);await db.SaveChangesAsync();
  }
  var owner=await Owner();
  var html=await owner.GetStringAsync("/admin/activity?format=rows&Search="+marker);
  html.Should().Contain("data-activity-id");
  var stamps=System.Text.RegularExpressions.Regex.Matches(html,"datetime=\"([^\"]+)\"");
  stamps.Should().NotBeEmpty();
  foreach(System.Text.RegularExpressions.Match s in stamps)
   s.Groups[1].Value.Should().EndWith("Z","activity timestamps must be unambiguous UTC, got {0}",s.Groups[1].Value);
  var cutoff=System.Text.RegularExpressions.Regex.Match(html,"data-history-cutoff=\"([^\"]+)\"");
  cutoff.Success.Should().BeTrue();cutoff.Groups[1].Value.Should().EndWith("Z");
  stamps[0].Groups[1].Value.Should().StartWith(occurredUnspecified.ToString("yyyy-MM-ddTHH:mm:ss"));
 }
 [Fact] public async Task AccessDeniedForAnonymousCustomerAndStaff_RevocationWorksWithOldCookie()
 {
  var anon=Browser();(await anon.GetAsync("/admin/activity")).StatusCode.Should().Be(HttpStatusCode.Redirect);
  var email=Unique("customer")+"@example.com";await TestSeed.RegisterCustomerAsync(factory.Services,email,"Customer!2026abc");await TestSeed.VerifyCustomerAsync(factory.Services,email);
  var c=Browser();await TestSeed.LoginMvcAsync(c,email,"Customer!2026abc");var denied=await c.GetAsync("/admin/activity?format=rows");denied.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect,HttpStatusCode.Forbidden);
  var staffEmail=Unique("staff")+"@example.com";await TestSeed.CreateStaffUserAsync(factory.Services,staffEmail,"Staff!2026abc","SalesStaff");
  var staff=Browser();await TestSeed.LoginMvcAsync(staff,staffEmail,"Staff!2026abc");(await staff.GetAsync("/admin/activity")).StatusCode.Should().BeOneOf(HttpStatusCode.Redirect,HttpStatusCode.Forbidden);
  var owner=await Owner();(await owner.GetAsync("/admin/activity")).StatusCode.Should().Be(HttpStatusCode.OK);
  (await owner.GetAsync("/admin/activity?From=2026-10-01&To=2026-01-01")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
  (await owner.GetAsync("/admin/activity?Page=-1")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
  using var scope=factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
  var role=await db.Roles.Include(r=>r.Permissions).SingleAsync(r=>r.Name=="Owner");
  var permission=await db.Permissions.SingleAsync(p=>p.Name=="activity.view");var original=role.Permissions.Select(p=>p.PermissionId).ToArray();
  try{role.SetPermissions(original.Where(p=>p!=permission.Id));await db.SaveChangesAsync();
    (await owner.GetAsync("/admin/activity?format=rows")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    (await ActivityAccess.AuthorizedUsers(db).ToListAsync()).Should().BeEmpty();}
  finally{role.SetPermissions(original);await db.SaveChangesAsync();}
 }
 [Fact] public async Task PartialProviderAcceptanceIsCheckpointed_AndRetryDoesNotResendAcceptedRequest()
 {
  using var scope=factory.Services.CreateScope();var sp=scope.ServiceProvider;var db=sp.GetRequiredService<DeChimexDbContext>();
  var customer=Customer.Create("Checkpoint","Test");db.Add(customer);
  var order=Order.CreateDraft(Unique("SPLIT-"),customer.Id);order.AddProduct(Guid.NewGuid(),"Test","SPLIT-0001",100,1);order.PlaceOrder();order.MarkPaid();db.Add(order);
  var one=Payment.Initialize(order.Id,customer.Id,50,PaymentMethod.Paystack,Unique("ONE-"));one.MarkSucceeded("GW1",null);
  var two=Payment.Initialize(order.Id,customer.Id,50,PaymentMethod.Paystack,Unique("TWO-"));two.MarkSucceeded("GW2",null);
  db.AddRange(one,two);await db.SaveChangesAsync();
  var fake=(FakePaymentGateway)sp.GetRequiredService<IPaymentGateway>();fake.RefundSuccessByReference[two.GatewayReference!]=false;
  Func<Task> refund=()=>sp.GetRequiredService<ISender>().Send(new RefundOrderCommand(order.Id));
  await refund.Should().ThrowAsync<DeChimex.Application.Common.Exceptions.ConflictException>();
  one.Status.Should().Be(PaymentStatus.Refunded);two.Status.Should().Be(PaymentStatus.Succeeded);order.Status.Should().Be(OrderStatus.Paid);
  (await db.AuditLogs.CountAsync(a=>a.OrderId==order.Id&&a.ActivityType=="payment.refund_recorded")).Should().Be(1);
  // Verify a new context sees the checkpoint, not just the existing change tracker.
  using(var read=factory.Services.CreateScope())
   (await read.ServiceProvider.GetRequiredService<DeChimexDbContext>().Payments.FindAsync(one.Id))!.Status.Should().Be(PaymentStatus.Refunded);
  fake.RefundSuccessByReference[two.GatewayReference!]=true;await refund();order.Status.Should().Be(OrderStatus.Refunded);
  fake.RefundCalls.Count(r=>r==one.GatewayReference).Should().Be(1);fake.RefundCalls.Count(r=>r==two.GatewayReference).Should().Be(2);
  var before=await db.AuditLogs.CountAsync(a=>a.OrderId==order.Id&&a.ActivityType!=null);
  await sp.GetRequiredService<ISender>().Send(new DeChimex.Application.Features.Payments.Commands.VerifyPaymentCommand(one.Reference));
  (await db.AuditLogs.CountAsync(a=>a.OrderId==order.Id&&a.ActivityType!=null)).Should().Be(before);
  one.Status.Should().Be(PaymentStatus.Refunded);
 }
 [Fact] public async Task InvalidRefundStateIsRejectedBeforeProviderCall()
 {
  using var scope=factory.Services.CreateScope();var sp=scope.ServiceProvider;var db=sp.GetRequiredService<DeChimexDbContext>();
  var o=Order.CreateDraft(Unique("INVALID-"),Guid.NewGuid());db.Add(o);
  var p=Payment.Initialize(o.Id,o.CustomerId,50,PaymentMethod.Paystack,Unique("INVALID-"));p.MarkSucceeded("GW",null);db.Add(p);await db.SaveChangesAsync();
  var fake=(FakePaymentGateway)sp.GetRequiredService<IPaymentGateway>();var calls=fake.RefundCalls.Count;
  Func<Task> refund=()=>sp.GetRequiredService<ISender>().Send(new RefundOrderCommand(o.Id));
  await refund.Should().ThrowAsync<DeChimex.Domain.Exceptions.BusinessRuleValidationException>();
  fake.RefundCalls.Count.Should().Be(calls);p.Status.Should().Be(PaymentStatus.Succeeded);
 }
 [Fact] public async Task IdenticalOptionReplacementIsSilent_ChangedRequirementsAreHistoryOnly()
 {
  using var scope=factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
  var c=Category.Create(Unique("OPTIONS-"));db.Add(c);await db.SaveChangesAsync();
  var spec=new CategoryOptionSpec("size","Choose size",CategoryOptionType.SingleChoice,true,null,null,new[]{"Small","Large"});
  c.ReplaceOptions(new[]{spec});await db.SaveChangesAsync();
  c.ReplaceOptions(new[]{spec});await db.SaveChangesAsync();
  (await db.AuditLogs.CountAsync(a=>a.EntityId==c.Id.ToString()&&a.ActivityType=="category.options_changed")).Should().Be(1);
  c.ReplaceOptions(new[]{spec with {IsRequired=false}});await db.SaveChangesAsync();
  var rows=await db.AuditLogs.Where(a=>a.EntityId==c.Id.ToString()&&a.ActivityType=="category.options_changed").ToListAsync();
  rows.Should().HaveCount(2);rows.Should().OnlyContain(a=>!a.IsRealtime);
 }

 [Fact] public async Task NewlyGrantedPermissionAndDisabledAccountUseCurrentDatabase_NotStaleCookieClaims()
 {
  var email=Unique("delegated")+"@example.com";await TestSeed.CreateStaffUserAsync(factory.Services,email,"Staff!2026abc","Manager");
  var browser=Browser();await TestSeed.LoginMvcAsync(browser,email,"Staff!2026abc");
  (await browser.GetAsync("/admin/activity")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
  using var scope=factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
  var role=await db.Roles.Include(r=>r.Permissions).SingleAsync(r=>r.Name=="Manager");
  var ids=role.Permissions.Select(p=>p.PermissionId).ToArray();var permission=await db.Permissions.SingleAsync(p=>p.Name=="activity.view");
  var account=await db.Users.SingleAsync(u=>u.Email.Value==email);
  try{
   role.SetPermissions(ids.Append(permission.Id));await db.SaveChangesAsync();
   (await browser.GetAsync("/admin/activity")).StatusCode.Should().Be(HttpStatusCode.OK);
   var before=await db.AuditLogs.CountAsync(a=>a.ActivityType=="role.permissions_changed");
   role.SetPermissions(ids.Append(permission.Id));await db.SaveChangesAsync();
   (await db.AuditLogs.CountAsync(a=>a.ActivityType=="role.permissions_changed")).Should().Be(before);
   account.Disable();await db.SaveChangesAsync();
   // AUD-006: the session-freshness middleware consults the CURRENT database before any
   // controller runs — a disabled account's next request is signed out and redirected to
   // the login page (stronger than the old endpoint-level 403, same root cause covered).
   var denied=await browser.GetAsync("/admin/activity?format=rows");
   denied.StatusCode.Should().Be(HttpStatusCode.Found);
   denied.Headers.Location!.OriginalString.Should().StartWith("/Account/Login");
   (await ActivityAccess.AuthorizedUsers(db).ContainsAsync(account.Id)).Should().BeFalse();
  }finally{role.SetPermissions(ids);account.Activate();await db.SaveChangesAsync();}
 }
 [Fact] public async Task RepeatedSignedWebhookAndVerificationProduceOneCanonicalSuccess()
 {
  using var scope=factory.Services.CreateScope();var sp=scope.ServiceProvider;var db=sp.GetRequiredService<DeChimexDbContext>();
  var cat=Category.Create(Unique("CALLBACK-"));var product=Product.Create(Unique("Webhook"),Unique("SKU"),null,cat.Id,100,false,false);
  var customer=Customer.Create("Callback","Customer");db.AddRange(cat,product,customer);
  var order=Order.CreateDraft(Unique("CALLBACK-"),customer.Id);order.AddProduct(product.Id,product.Name,product.Sku,100,1);order.PlaceOrder();db.Add(order);
  var payment=Payment.Initialize(order.Id,customer.Id,100,PaymentMethod.Paystack,Unique("CALLBACK-"));db.Add(payment);await db.SaveChangesAsync();
  var fake=(FakePaymentGateway)sp.GetRequiredService<IPaymentGateway>();fake.SignatureValid=true;
  var body=System.Text.Json.JsonSerializer.Serialize(new{ @event="charge.success",data=new{reference=payment.Reference,amount=10000,status="success"}});
  var client=Browser();
  for(int i=0;i<2;i++){
   var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/payments/webhook/paystack"){Content=new StringContent(body,System.Text.Encoding.UTF8,"application/json")};
   request.Headers.Add("x-paystack-signature","fake-valid-signature");(await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
  }
  using var read=factory.Services.CreateScope();var fresh=read.ServiceProvider.GetRequiredService<DeChimexDbContext>();
  await read.ServiceProvider.GetRequiredService<ISender>().Send(new DeChimex.Application.Features.Payments.Commands.VerifyPaymentCommand(payment.Reference));
  (await fresh.AuditLogs.CountAsync(a=>a.EntityId==payment.Id.ToString()&&a.ActivityType=="payment.succeeded")).Should().Be(1);
  (await fresh.Orders.FindAsync(order.Id))!.Status.Should().Be(OrderStatus.Paid);
 }

}
