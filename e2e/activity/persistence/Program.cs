using DeChimex.Application.Features.Activity;
using DeChimex.Domain.Entities;
using DeChimex.Host.Realtime;
using DeChimex.IntegrationTests;
using DeChimex.Persistence.Context;
using DeChimex.Persistence.Interceptors;
using DeChimex.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
var options=new DbContextOptionsBuilder<DeChimexDbContext>()
 .UseSqlite($"Data Source={args[1]};Foreign Keys=True;Pooling=False")
 .ReplaceService<IModelCustomizer,ActivitySqliteModelCustomizer>()
 .AddInterceptors(new AuditableSoftDeleteInterceptor(new ActivitySystemUser(),new ActivityClock())).Options;
await using var db=new DeChimexDbContext(options);
if(args[0]=="write"){
 await db.Database.EnsureCreatedAsync();db.Add(MajorCategory.Create("Process restart durable activity"));await db.SaveChangesAsync();
 Console.WriteLine("WRITE: committed real business aggregate and audit row; process exiting.");
}else{
 var result=await new ActivityQueries(db).SearchAsync(new ActivityFilter{Search="Process restart durable activity"},default);
 if(result.Items.Count!=1||result.Items[0].Type!="major_category.created")throw new Exception("History did not survive process restart");
 // Exercise relational translation of the authorization and all relationship filters.
 await ActivityAccess.AuthorizedUsers(db).ToListAsync();
 await new ActivityQueries(db).SearchAsync(new ActivityFilter{Customer="Nobody",Product="Nothing",Staff="None",Order="NO",Actor="System"},default);
 Console.WriteLine("READ: new process recovered the persisted activity; relational access/filter queries executed successfully.");
}
